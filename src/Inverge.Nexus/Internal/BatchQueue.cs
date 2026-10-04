using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Inverge.Nexus.Internal;

/// <summary>
/// A bounded, context-grouped, size-and-time triggered send buffer for
/// high-volume telemetry.
/// </summary>
/// <remarks>
/// <para>
/// Analytics must not sit on a request's critical path, so <c>Track</c> and
/// <c>Logs.Info</c> append here and return. A background loop ships the buffer
/// when it reaches <c>maxBatch</c> or the flush interval elapses, grouping items
/// that share an identity into one request — which is the shape the ingest
/// endpoints want.
/// </para>
/// <para>
/// The buffer is bounded on purpose. Under a flood, or while the backend is
/// unreachable, the newest items are dropped and counted rather than growing
/// memory until the process dies: a telemetry outage should cost the memory you
/// budgeted, not the application.
/// </para>
/// </remarks>
internal sealed class BatchQueue : IDisposable
{
    private readonly string _name;
    private readonly Func<IReadOnlyList<JsonObject>, JsonObject, CancellationToken, Task> _sender;
    private readonly NexusDiagnostics _diagnostics;
    private readonly int _maxBatch;
    private readonly int _maxQueue;
    private readonly TimeSpan _interval;

    private readonly object _gate = new object();
    private readonly List<Entry> _items = new List<Entry>();
    private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);

    private Task? _worker;
    private long _dropped;
    private volatile bool _stopping;

    public BatchQueue(
        string name,
        Func<IReadOnlyList<JsonObject>, JsonObject, CancellationToken, Task> sender,
        int maxBatch,
        TimeSpan interval,
        int maxQueue,
        NexusDiagnostics diagnostics)
    {
        _name = name;
        _sender = sender;
        _diagnostics = diagnostics;
        _maxBatch = Math.Max(1, maxBatch);
        _interval = interval < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : interval;
        _maxQueue = Math.Max(_maxBatch, maxQueue);
    }

    /// <summary>How many items are waiting to be sent.</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>How many items have been dropped because the buffer was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Queues one item. Returns immediately; delivery happens off-thread.</summary>
    public void Add(JsonObject item, NexusTelemetryContext context)
    {
        var key = context.BatchKey();
        var wire = context.ToWire();
        var full = false;
        var ready = false;

        lock (_gate)
        {
            if (_stopping)
            {
                // Past shutdown there is no worker left to ship this.
                full = true;
            }
            else if (_items.Count >= _maxQueue)
            {
                full = true;
            }
            else
            {
                _items.Add(new Entry(key, wire, item));
                ready = _items.Count >= _maxBatch;
            }
        }

        if (full)
        {
            ReportDrop();
            return;
        }

        EnsureWorker();

        if (ready)
        {
            Wake();
        }
    }

    /// <summary>Sends everything buffered right now, on the calling flow.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var batch = Drain();
            if (batch.Count == 0)
            {
                return;
            }

            await SendGroupedAsync(batch, cancellationToken).ConfigureAwait(false);

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Stops the worker and ships what is left. Idempotent.
    /// </summary>
    /// <remarks>
    /// The in-flight delivery is never cancelled here — a shutdown that threw
    /// away the batch it was already sending would lose exactly the telemetry
    /// about why the process is shutting down. <paramref name="timeout"/> bounds
    /// how long we wait for the worker to notice, after which the final flush
    /// happens on this flow.
    /// </remarks>
    public async Task StopAsync(TimeSpan timeout)
    {
        Task? worker;
        lock (_gate)
        {
            if (_stopping)
            {
                worker = _worker;
            }
            else
            {
                _stopping = true;
                worker = _worker;
            }
        }

        Wake();

        if (worker is not null && !worker.IsCompleted)
        {
            var finished = await Task
                .WhenAny(worker, Task.Delay(timeout <= TimeSpan.Zero ? TimeSpan.Zero : timeout))
                .ConfigureAwait(false);

            if (finished != worker)
            {
                _diagnostics.Warning(
                    "The " + _name + " buffer did not drain within the shutdown timeout; flushing inline.");
            }
        }

        using var deadline = new CancellationTokenSource(timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : timeout);
        await FlushAsync(deadline.Token).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases the wake signal. Call <see cref="StopAsync"/> first — disposing
    /// does not flush, and anything still buffered is lost.
    /// </summary>
    public void Dispose()
    {
        _stopping = true;
        _signal.Dispose();
    }

    private void EnsureWorker()
    {
        if (Volatile.Read(ref _worker) is not null)
        {
            return;
        }

        lock (_gate)
        {
            if (_worker is not null || _stopping)
            {
                return;
            }

            // LongRunning keeps a mostly-idle loop off the thread pool, where it
            // would otherwise occupy a worker that the application's own requests
            // want.
            _worker = Task.Factory.StartNew(
                RunAsync,
                CancellationToken.None,
                TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default).Unwrap();
        }
    }

    private void Wake()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signalled far more than needed; the worker will drain
            // everything on its next pass regardless.
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            try
            {
                await _signal.WaitAsync(_interval).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            // One wake can cover many queued items; drain the signal so the loop
            // does not spin once per item after a burst.
            DrainSignal();

            var batch = Drain();
            if (batch.Count > 0)
            {
                await SendGroupedAsync(batch, CancellationToken.None).ConfigureAwait(false);
            }

            if (_stopping)
            {
                var remaining = Drain();
                if (remaining.Count > 0)
                {
                    await SendGroupedAsync(remaining, CancellationToken.None).ConfigureAwait(false);
                }

                return;
            }
        }
    }

    private void DrainSignal()
    {
        while (_signal.CurrentCount > 0 && _signal.Wait(0))
        {
            // Consume the surplus permits.
        }
    }

    private List<Entry> Drain()
    {
        lock (_gate)
        {
            if (_items.Count == 0)
            {
                return EmptyBatch;
            }

            var batch = new List<Entry>(_items);
            _items.Clear();
            return batch;
        }
    }

    private static readonly List<Entry> EmptyBatch = new List<Entry>(0);

    private async Task SendGroupedAsync(List<Entry> batch, CancellationToken cancellationToken)
    {
        var groups = new Dictionary<string, Group>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var entry in batch)
        {
            if (!groups.TryGetValue(entry.Key, out var group))
            {
                group = new Group(entry.Context);
                groups[entry.Key] = group;
                order.Add(entry.Key);
            }

            group.Items.Add(entry.Item);
        }

        foreach (var key in order)
        {
            var group = groups[key];
            for (var start = 0; start < group.Items.Count; start += _maxBatch)
            {
                var size = Math.Min(_maxBatch, group.Items.Count - start);
                var chunk = group.Items.GetRange(start, size);

                try
                {
                    await _sender(chunk, group.Context, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    // Background delivery is always silent: a telemetry failure
                    // must never surface as an application failure.
                    _diagnostics.Warning(
                        exception,
                        "Delivery of {0} {1} item(s) failed.",
                        size,
                        _name);
                }
            }
        }
    }

    private void ReportDrop()
    {
        var dropped = Interlocked.Increment(ref _dropped);

        // Log the first few and then thin out, so a sustained outage does not
        // turn a dropped-telemetry problem into a log-volume problem.
        if (dropped is 1 or 10 or 100 || dropped % 1000 == 0)
        {
            _diagnostics.Warning(
                null,
                "The {0} buffer is full at {1} items — {2} dropped so far.",
                _name,
                _maxQueue,
                dropped);
        }
    }

    private readonly struct Entry
    {
        public Entry(string key, JsonObject context, JsonObject item)
        {
            Key = key;
            Context = context;
            Item = item;
        }

        public string Key { get; }

        public JsonObject Context { get; }

        public JsonObject Item { get; }
    }

    private sealed class Group
    {
        public Group(JsonObject context) => Context = context;

        public JsonObject Context { get; }

        public List<JsonObject> Items { get; } = new List<JsonObject>();
    }
}
