using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Inverge.Nexus.Telescope;

/// <summary>
/// Nexus Telescope for .NET — records every unit of work (an ASP.NET Core
/// request, a message consumer, a job or command you wrap) and everything it
/// did, and ships the journeys to the Nexus console.
/// </summary>
/// <remarks>
/// <para>
/// It listens to <see cref="Activity"/>, which the runtime and most libraries
/// already emit: ASP.NET Core requests, HttpClient calls (with W3C
/// <c>traceparent</c> propagation, so downstream services join the trace),
/// Npgsql, MassTransit, the Azure SDKs… EF Core and SqlClient queries come from
/// their <see cref="DiagnosticListener"/> events; log records from the ILogger
/// provider. Your own work: <see cref="Span"/>, <see cref="RunAsync{T}"/>.
/// </para>
/// <para>Nothing here throws into the application.</para>
/// </remarks>
public sealed class NexusTelescope : IDisposable
{
    /// <summary>The ActivitySource Telescope's own spans and jobs are created from.</summary>
    public static readonly ActivitySource Source = new ActivitySource("Inverge.Nexus.Telescope", NexusVersion.Current);

    internal const string TypeTag = "nexus.telescope.type";

    private readonly INexusClient _client;
    private readonly TelescopeOptions _options;
    private readonly ConditionalWeakTable<Activity, TelescopeBatch> _batches = new ConditionalWeakTable<Activity, TelescopeBatch>();
    private readonly Channel<JsonObject> _outbox = Channel.CreateBounded<JsonObject>(new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stop = new CancellationTokenSource();
    private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
    private readonly string _host = Environment.MachineName;
    private ActivityListener? _listener;
    private DiagnosticSubscriber? _diagnostics;
    private Task? _loop;
    private volatile RemoteSettings _remote = RemoteSettings.Default;
    private readonly Random _random = new Random();
    private int _disposed;

    /// <summary>Creates the recorder. Call <see cref="Start"/> to begin listening (the DI registration does).</summary>
    public NexusTelescope(INexusClient client, TelescopeOptions? options = null)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options ?? new TelescopeOptions();
        _options.Service ??= Environment.GetEnvironmentVariable("NEXUS_SERVICE_NAME") ?? System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name;
        _options.Release ??= Environment.GetEnvironmentVariable("NEXUS_RELEASE");
        if (_options.SampleRate is null && double.TryParse(Environment.GetEnvironmentVariable("NEXUS_TELESCOPE_SAMPLE_RATE"), NumberStyles.Float, CultureInfo.InvariantCulture, out var rate))
        {
            _options.SampleRate = rate;
        }
    }

    /// <summary>The options in effect.</summary>
    public TelescopeOptions Options => _options;

    /// <summary>Whether recording is on: options, the service switch, and the console's pause.</summary>
    public bool IsEnabled => _options.Enabled && ((_client as NexusClient)?.IsServiceEnabled(NexusServices.Telescope) ?? true) && _remote.Enabled;

    // ── lifecycle ──────────────────────────────────────────────────────────

    /// <summary>Starts listening and the background sender. Idempotent.</summary>
    public void Start()
    {
        if (_listener is not null)
        {
            return;
        }

        _listener = new ActivityListener
        {
            ShouldListenTo = source => !_options.IgnoreActivitySources.Contains(source.Name),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => IsEnabled ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => IsEnabled ? ActivitySamplingResult.AllDataAndRecorded : ActivitySamplingResult.None,
            ActivityStarted = OnStarted,
            ActivityStopped = OnStopped,
        };
        ActivitySource.AddActivityListener(_listener);
        _diagnostics = new DiagnosticSubscriber(this);
        _diagnostics.Subscribe();
        _loop = Task.Run(SendLoopAsync);
    }

    /// <summary>Ships everything finished so far.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var batch = new List<JsonObject>();
        while (_outbox.Reader.TryRead(out var item))
        {
            batch.Add(item);
            if (batch.Count == 100)
            {
                await SendAsync(batch, cancellationToken).ConfigureAwait(false);
                batch = new List<JsonObject>();
            }
        }

        if (batch.Count > 0)
        {
            await SendAsync(batch, cancellationToken).ConfigureAwait(false);
        }

        // Wait for a delivery the background loop may have in flight.
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        _sendLock.Release();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _listener?.Dispose();
        _diagnostics?.Dispose();
        _stop.Cancel();
        try
        {
            FlushAsync().Wait(TimeSpan.FromSeconds(3));
        }
#pragma warning disable CA1031 // shutdown must not throw
        catch (Exception)
#pragma warning restore CA1031
        {
        }

        _stop.Dispose();
        _sendLock.Dispose();
    }

    // ── your own work ──────────────────────────────────────────────────────

    /// <summary>
    /// A named span: everything recorded inside it (queries, HTTP calls, logs —
    /// across awaits) nests under it. <c>using (telescope.Span("pricing")) { … }</c>
    /// </summary>
    public IDisposable Span(string name)
    {
        var activity = IsEnabled ? Source.StartActivity(name ?? "span", ActivityKind.Internal) : null;
        return activity ?? (IDisposable)NoopScope.Instance;
    }

    /// <summary>
    /// Runs <paramref name="work"/> as its own journey — a job, command or
    /// scheduled task. Pass the <c>traceparent</c> the producer put on the message
    /// (see <see cref="Traceparent"/>) so the run joins the producer's trace.
    /// </summary>
    public async Task<T> RunAsync<T>(string type, string name, Func<Task<T>> work, string? traceparent = null, IDictionary<string, object?>? content = null)
    {
        if (work is null)
        {
            throw new ArgumentNullException(nameof(work));
        }

        ActivityContext.TryParse(traceparent, null, out var parent);
        using var activity = Source.StartActivity(name ?? type, ActivityKind.Consumer, parent, new[] { new KeyValuePair<string, object?>(TypeTag, type) });
        var batch = activity is null ? null : BatchFor(activity);
        if (batch is not null && content is not null)
        {
            Enrich(batch, content);
        }

        try
        {
            var result = await work().ConfigureAwait(false);
            batch?.SetStatus(type == "job" ? "processed" : "0");
            return result;
        }
        catch (Exception exception)
        {
            RecordException(exception, handled: false);
            batch?.SetStatus(type == "job" ? "failed" : "1");
            throw;
        }
    }

    /// <summary>Runs <paramref name="work"/> as its own journey (see <see cref="RunAsync{T}"/>).</summary>
    public Task RunAsync(string type, string name, Func<Task> work, string? traceparent = null, IDictionary<string, object?>? content = null)
    {
        if (work is null)
        {
            throw new ArgumentNullException(nameof(work));
        }

        return RunAsync(type, name, async () =>
        {
            await work().ConfigureAwait(false);
            return true;
        }, traceparent, content);
    }

    /// <summary>A W3C traceparent for the current point — put it on a message so its consumer joins the trace.</summary>
    public static string? Traceparent() => Activity.Current?.Id is { } id && Activity.Current.IdFormat == ActivityIdFormat.W3C ? id : null;

    // ── entries ────────────────────────────────────────────────────────────

    /// <summary>Records an entry in the current journey.</summary>
    public void Record(string type, IDictionary<string, object?> content, double? durationMs = null, string? status = null, string? name = null)
        => Record(type, ToObject(content), durationMs, status, name, null, null, null);

    /// <summary>Records a query you ran outside EF Core / ADO.NET instrumentation.</summary>
    public void RecordQuery(string sql, IEnumerable<object?>? bindings, double durationMs, string? connection = null)
        => Record("query", new JsonObject { ["sql"] = sql, ["bindings"] = ToNode(bindings?.ToList()), ["connection"] = connection }, durationMs, null, null, null, null, null);

    /// <summary>Records an exception in the current journey. Unhandled ones fail it.</summary>
    public void RecordException(Exception exception, bool handled = true)
    {
        if (exception is null || !_exceptions.TryAdd(exception, true))
        {
            return; // null, or already recorded (middleware + logger both see unhandled ones)
        }

        var frames = new JsonArray();
        foreach (var frame in new StackTrace(exception, true).GetFrames().Take(60))
        {
            var method = frame.GetMethod();
            frames.Add(new JsonObject
            {
                ["function"] = method is null ? "?" : $"{method.DeclaringType?.FullName}.{method.Name}",
                ["filename"] = frame.GetFileName() ?? method?.DeclaringType?.Assembly.GetName().Name ?? "?",
                ["lineno"] = frame.GetFileLineNumber(),
            });
        }

        Record("exception", new JsonObject
        {
            ["class"] = exception.GetType().FullName,
            ["message"] = exception.Message,
            ["handled"] = handled,
            ["stack"] = frames,
            ["previous"] = exception.InnerException is { } inner ? $"{inner.GetType().FullName}: {inner.Message}" : null,
        }, null, null, null, null, null, null);
    }

    private readonly ConditionalWeakTable<Exception, object> _exceptions = new ConditionalWeakTable<Exception, object>();

    /// <summary>Records a log line in the current journey.</summary>
    public void Log(string level, string message, IDictionary<string, object?>? context = null)
        => Record("log", new JsonObject { ["level"] = level, ["message"] = message, ["context"] = context is null ? null : ToObject(context) }, null, level, null, null, null, null);

    /// <summary>The signed-in user of the current journey: tags it <c>Auth:&lt;id&gt;</c> and links it to Nexus Sessions.</summary>
    public void SetUser(string id, string? email = null, string? name = null)
    {
        var batch = BatchFor(Activity.Current);
        if (batch is not null && !string.IsNullOrEmpty(id))
        {
            batch.User = new JsonObject { ["id"] = id, ["email"] = email, ["name"] = name };
        }
    }

    /// <summary>Tags the current journey.</summary>
    public void Tag(params string[] tags)
    {
        var batch = BatchFor(Activity.Current);
        if (batch is not null && tags is not null)
        {
            lock (batch.Gate)
            {
                batch.Tags.AddRange(tags);
            }
        }
    }

    /// <summary>Adds fields to the current unit of work's own record (the request, the job…).</summary>
    public void Enrich(IDictionary<string, object?> content)
    {
        var batch = BatchFor(Activity.Current);
        if (batch is not null && content is not null)
        {
            Enrich(batch, content);
        }
    }

    internal void Enrich(TelescopeBatch batch, IDictionary<string, object?> content)
    {
        var redacted = Redactor.Redact(ToObject(content), _options.RedactKeys.Concat(_remote.RedactKeys));
        lock (batch.Gate)
        {
            foreach (var pair in redacted.ToList())
            {
                redacted.Remove(pair.Key);
                batch.Content[pair.Key] = pair.Value;
            }
        }
    }

    /// <summary>Whether a unit of work is being recorded at the current point.</summary>
    public bool IsRecording => BatchFor(Activity.Current) is not null;

    /// <summary>Stops recording the current unit of work and drops it (e.g. an ignored path).</summary>
    public void Discard()
    {
        BatchFor(Activity.Current)?.MarkDone();
    }

    /// <summary>Sets the status of the current unit of work (HTTP status, <c>processed</c>, an exit code…).</summary>
    public void SetStatus(string status)
    {
        BatchFor(Activity.Current)?.SetStatus(status);
    }

    /// <summary>Whether a request path is ignored (options + console settings).</summary>
    public bool IgnoresPath(string path)
        => _options.IgnorePaths.Concat(_remote.IgnorePaths).Any(glob => Redactor.GlobMatch(glob, path));

    internal bool IgnoresType(string type) => _options.IgnoreTypes.Contains(type) || _remote.IgnoreTypes.Contains(type);

    /// <summary>The body capture limit in bytes.</summary>
    public int BodyLimitBytes => _options.BodySizeLimitKb * 1024;

    /// <summary>The journey the given activity belongs to (walks up to its local root).</summary>
    internal TelescopeBatch? BatchFor(Activity? activity)
    {
        for (var a = activity; a is not null; a = a.Parent)
        {
            if (_batches.TryGetValue(a, out var batch))
            {
                return batch.Done ? null : batch;
            }
        }

        return null;
    }

    internal void Record(string type, JsonObject content, double? durationMs, string? status, string? name, DateTime? startUtc, string? id, Activity? at)
    {
        try
        {
            var current = at ?? Activity.Current;
            var batch = BatchFor(current);
            if (batch is null || IgnoresType(type))
            {
                return;
            }

            var start = startUtc ?? (durationMs is double ms ? DateTime.UtcNow.AddMilliseconds(-ms) : DateTime.UtcNow);
            var entry = new JsonObject
            {
                ["id"] = id ?? ActivitySpanId.CreateRandom().ToHexString(),
                ["parentId"] = current is null || current == batch.Root ? batch.Id : current.SpanId.ToHexString(),
                ["type"] = type,
                ["offsetMs"] = Math.Max(0, Math.Round((start - batch.StartUtc).TotalMilliseconds, 3)),
                ["content"] = Redactor.Redact(content, _options.RedactKeys.Concat(_remote.RedactKeys)),
            };
            if (durationMs is double d)
            {
                entry["durationMs"] = Math.Round(d, 3);
            }

            if (status is not null)
            {
                entry["status"] = status;
            }

            if (name is not null)
            {
                entry["name"] = name;
            }

            batch.Add(entry, _options.MaxEntries);
            if (type == "exception" && content["handled"]?.GetValue<bool>() != true)
            {
                batch.Failed = true;
            }
        }
#pragma warning disable CA1031 // never throw into the app
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    // ── activities ─────────────────────────────────────────────────────────

    private void OnStarted(Activity activity)
    {
        if (activity.Parent is not null || !IsEnabled)
        {
            return;
        }

        var type = ActivityMapper.UnitOfWork(activity);
        if (type is null || IgnoresType(type))
        {
            return;
        }

        var batch = new TelescopeBatch(activity, type);
        if (type == "request" && activity.GetTagItem("url.path") is string p && IgnoresPath(p))
        {
            return;
        }

        _batches.AddOrUpdate(activity, batch);
    }

    private void OnStopped(Activity activity)
    {
        try
        {
            if (_batches.TryGetValue(activity, out var root))
            {
                Finish(root);
                return;
            }

            if (activity.Source == Source || ActivityMapper.HandledElsewhere(activity, _diagnostics))
            {
                if (activity.Source == Source)
                {
                    // One of our spans: an entry with its real duration.
                    Record("span", new JsonObject { ["name"] = activity.DisplayName }, activity.Duration.TotalMilliseconds, null, activity.DisplayName, activity.StartTimeUtc, activity.SpanId.ToHexString(), activity.Parent);
                }

                return;
            }

            if (BatchFor(activity.Parent) is null)
            {
                return;
            }

            var (type, content, status) = ActivityMapper.Entry(activity);
            Record(type, content, activity.Duration.TotalMilliseconds, status, null, activity.StartTimeUtc, activity.SpanId.ToHexString(), activity.Parent);
            foreach (var ev in activity.Events)
            {
                if (ev.Name == "exception")
                {
                    var tags = ev.Tags.ToDictionary(t => t.Key, t => t.Value);
                    Record("exception", new JsonObject
                    {
                        ["class"] = tags.TryGetValue("exception.type", out var t) ? t?.ToString() : "Exception",
                        ["message"] = tags.TryGetValue("exception.message", out var m) ? m?.ToString() : "",
                        ["stack"] = tags.TryGetValue("exception.stacktrace", out var s) ? s?.ToString() : null,
                    }, null, null, null, ev.Timestamp.UtcDateTime, null, activity.Parent);
                }
            }
        }
#pragma warning disable CA1031 // never throw into the app
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private void Finish(TelescopeBatch batch)
    {
        if (!batch.MarkDone())
        {
            return;
        }

        var root = batch.Root;
        batch.DurationMs = root.Duration.TotalMilliseconds;
        if (batch.Status is null && root.GetTagItem("http.response.status_code") is { } code)
        {
            batch.Status = Convert.ToString(code, CultureInfo.InvariantCulture);
        }

        if (root.Status == ActivityStatusCode.Error && batch.Type != "request")
        {
            batch.Failed = true;
        }

        if (Keep(batch))
        {
            _outbox.Writer.TryWrite(batch.ToWire());
        }
    }

    private bool Keep(TelescopeBatch b)
    {
        if (b.Failed || b.IsFailure())
        {
            return true;
        }

        var remote = _remote;
        if (b.DurationMs >= remote.SlowRequestMs)
        {
            return true;
        }

        var userTag = b.User?["id"]?.GetValue<string>() is { } id ? $"Auth:{id}" : null;
        if (remote.MonitoredTags.Any(t => b.Tags.Contains(t) || t == userTag))
        {
            return true;
        }

        var rate = _options.SampleRate ?? remote.SampleRate;
        if (rate >= 1)
        {
            return true;
        }

        lock (_random)
        {
#pragma warning disable CA5394 // sampling, not security
            return _random.NextDouble() < rate;
#pragma warning restore CA5394
        }
    }

    // ── delivery ───────────────────────────────────────────────────────────

    private async Task SendLoopAsync()
    {
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.FlushInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            var batch = new List<JsonObject>();
            while (batch.Count < 100 && _outbox.Reader.TryRead(out var item))
            {
                batch.Add(item);
            }

            if (batch.Count > 0)
            {
                await SendAsync(batch, token).ConfigureAwait(false);
            }
        }
    }

    private async Task SendAsync(List<JsonObject> batches, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var arr = new JsonArray();
            foreach (var b in batches)
            {
                arr.Add(b);
            }

            var body = new JsonObject
            {
                ["host"] = _host,
                ["sdk"] = new JsonObject { ["name"] = "nexus-dotnet", ["version"] = NexusVersion.Current },
                ["batches"] = arr,
            };
            if (_options.Service is not null)
            {
                body["service"] = _options.Service;
            }

            if (_options.Release is not null)
            {
                body["release"] = _options.Release;
            }

            using (SuppressInstrumentation())
            {
                var response = await _client.SendAsync(new NexusRequest("POST", "/partner/telescope", body, retryable: true), cancellationToken).ConfigureAwait(false);
                if (response?["config"] is JsonObject config)
                {
                    _remote = RemoteSettings.From(config);
                }
            }
        }
#pragma warning disable CA1031 // delivery must never break the app
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Our own delivery must not be recorded as an outgoing call of whatever journey triggered it.</summary>
    private static RestoreActivity SuppressInstrumentation()
    {
        var previous = Activity.Current;
        Activity.Current = null;
        return new RestoreActivity(previous);
    }

    // ── json helpers ───────────────────────────────────────────────────────

    internal static JsonObject ToObject(IDictionary<string, object?> values)
    {
        var obj = new JsonObject();
        foreach (var pair in values)
        {
            obj[pair.Key] = ToNode(pair.Value);
        }

        return obj;
    }

    internal static JsonNode? ToNode(object? value, int depth = 0)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonNode node:
                return node.Parent is null ? node : node.DeepClone();
            case string s:
                return s.Length > 4000 ? string.Concat(s.AsSpan(0, 4000), "…") : s;
            case bool b:
                return b;
            case int or long or short or byte or uint or ulong or ushort or sbyte:
                return JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture));
            case double or float or decimal:
                return JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            case DateTime dt:
                return dt.ToString("O", CultureInfo.InvariantCulture);
            case DateTimeOffset dto:
                return dto.ToString("O", CultureInfo.InvariantCulture);
            case Guid g:
                return g.ToString();
            case byte[] bytes:
                return $"[binary {bytes.Length} bytes]";
            case System.Collections.IDictionary dict when depth < 6:
                var obj = new JsonObject();
                foreach (System.Collections.DictionaryEntry e in dict)
                {
                    obj[Convert.ToString(e.Key, CultureInfo.InvariantCulture) ?? ""] = ToNode(e.Value, depth + 1);
                }

                return obj;
            case System.Collections.IEnumerable list when depth < 6:
                var arr = new JsonArray();
                foreach (var item in list)
                {
                    if (arr.Count >= 100)
                    {
                        arr.Add("…");
                        break;
                    }

                    arr.Add(ToNode(item, depth + 1));
                }

                return arr;
            default:
                return Convert.ToString(value, CultureInfo.InvariantCulture);
        }
    }

    private sealed class NoopScope : IDisposable
    {
        public static readonly NoopScope Instance = new NoopScope();

        public void Dispose()
        {
        }
    }

    private sealed class RestoreActivity : IDisposable
    {
        private readonly Activity? _previous;

        public RestoreActivity(Activity? previous) => _previous = previous;

        public void Dispose() => Activity.Current = _previous;
    }
}
