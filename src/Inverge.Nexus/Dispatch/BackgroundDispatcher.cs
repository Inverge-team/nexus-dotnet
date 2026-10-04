using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Internal;

namespace Inverge.Nexus.Dispatch;

/// <summary>
/// Hands every request to a worker and returns immediately, never throwing into
/// the caller.
/// </summary>
/// <remarks>
/// <para>
/// Use it when you want <em>every</em> call fire-and-forget, not just the
/// buffered ingest ones — <c>nexus.Background()</c> returns a sibling client
/// wired this way.
/// </para>
/// <para>
/// Calls that need a response (flag evaluation, Remote Config) cannot work
/// through this: there is nothing to return. Use the normal client for those.
/// </para>
/// </remarks>
internal sealed class BackgroundDispatcher : INexusDispatcher, IDisposable
{
    private readonly HttpEngine _engine;
    private readonly NexusDiagnostics _diagnostics;
    private readonly int _maxQueue;
    private readonly int _workers;

    private readonly object _gate = new object();
    private readonly Queue<NexusRequest> _queue = new Queue<NexusRequest>();
    private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
    private readonly List<Task> _running = new List<Task>();

    private long _dropped;
    private int _inFlight;
    private volatile bool _stopping;

    public BackgroundDispatcher(
        HttpEngine engine,
        NexusDiagnostics diagnostics,
        int workers = 1,
        int maxQueue = 5000)
    {
        _engine = engine;
        _diagnostics = diagnostics;
        _workers = Math.Max(1, workers);
        _maxQueue = Math.Max(1, maxQueue);
    }

    /// <summary>How many requests were dropped because the queue was full.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    public Task<JsonNode?> DispatchAsync(NexusRequest request, CancellationToken cancellationToken)
    {
        var accepted = false;

        lock (_gate)
        {
            if (!_stopping && _queue.Count < _maxQueue)
            {
                _queue.Enqueue(request);
                accepted = true;
            }
        }

        if (!accepted)
        {
            var dropped = Interlocked.Increment(ref _dropped);
            _diagnostics.Warning(
                null,
                "The background dispatch queue is full — dropped {0} {1} ({2} total).",
                request.Method,
                request.Path,
                dropped);

            return Task.FromResult<JsonNode?>(null);
        }

        EnsureWorkers();
        Release();

        return Task.FromResult<JsonNode?>(null);
    }

    public async Task FlushAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + (timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(3) : timeout);

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            bool idle;
            lock (_gate)
            {
                idle = _queue.Count == 0 && Volatile.Read(ref _inFlight) == 0;
            }

            if (idle)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(TimeSpan timeout)
    {
        List<Task> workers;
        lock (_gate)
        {
            _stopping = true;
            workers = new List<Task>(_running);
        }

        // One extra permit per worker so each wakes, sees the stop flag and exits
        // after draining, rather than sitting out the flush interval.
        for (var i = 0; i < workers.Count + _workers; i++)
        {
            Release();
        }

        if (workers.Count > 0)
        {
            var all = Task.WhenAll(workers);
            var finished = await Task
                .WhenAny(all, Task.Delay(timeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : timeout))
                .ConfigureAwait(false);

            if (finished != all)
            {
                _diagnostics.Warning("Background dispatch did not drain within the shutdown timeout.");
            }
        }

        _engine.Dispose();
    }

    /// <summary>
    /// Releases the wake signal. Call <see cref="StopAsync"/> first — disposing
    /// does not drain the queue.
    /// </summary>
    public void Dispose()
    {
        _stopping = true;
        _signal.Dispose();
    }

    private void EnsureWorkers()
    {
        lock (_gate)
        {
            if (_stopping || _running.Count >= _workers)
            {
                return;
            }

            while (_running.Count < _workers)
            {
                _running.Add(Task.Factory.StartNew(
                    RunAsync,
                    CancellationToken.None,
                    TaskCreationOptions.LongRunning | TaskCreationOptions.DenyChildAttach,
                    TaskScheduler.Default).Unwrap());
            }
        }
    }

    private void Release()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Over-signalled; the worker drains the queue either way.
        }
    }

    private async Task RunAsync()
    {
        while (true)
        {
            try
            {
                await _signal.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                return;
            }

            while (true)
            {
                NexusRequest? request;
                lock (_gate)
                {
                    request = _queue.Count > 0 ? _queue.Dequeue() : null;
                }

                if (request is null)
                {
                    break;
                }

                Interlocked.Increment(ref _inFlight);
                try
                {
                    await _engine.ExecuteAsync(request, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _diagnostics.Warning(
                        exception, "Background delivery of {0} {1} failed.", request.Method, request.Path);
                }
                finally
                {
                    Interlocked.Decrement(ref _inFlight);
                }
            }

            if (_stopping)
            {
                bool empty;
                lock (_gate)
                {
                    empty = _queue.Count == 0;
                }

                if (empty)
                {
                    return;
                }
            }
        }
    }
}
