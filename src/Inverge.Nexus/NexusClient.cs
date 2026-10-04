using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Diagnostics;
using Inverge.Nexus.Dispatch;
using Inverge.Nexus.Internal;
using Inverge.Nexus.Models;
using Inverge.Nexus.Resources;
using Inverge.Nexus.Transport;

namespace Inverge.Nexus;

/// <summary>
/// One object, every Nexus product.
/// </summary>
/// <example>
/// <code>
/// await using var nexus = new NexusClient("nxs_live_xxx");
///
/// await nexus.IdentifyAsync("user_123", email: "a@b.com");
/// nexus.Track("order_placed", new { total = 42 });
/// await nexus.EmitAsync("orders:42", "status", new { state = "shipped" });
/// </code>
/// </example>
/// <remarks>
/// Build one per process and share it — see <see cref="INexusClient"/>. In an
/// ASP.NET Core or worker app, prefer <c>services.AddNexus(...)</c> from
/// <c>Inverge.Nexus.Extensions.Logging</c>, which registers it correctly and
/// flushes it on shutdown.
/// </remarks>
public sealed class NexusClient : INexusClient
{
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(3);

    private readonly NexusOptions _options;
    private readonly NexusDiagnostics _diagnostics;
    private readonly HttpEngine _engine;
    private readonly INexusDispatcher _dispatcher;
    private readonly INexusTransport? _sharedTransport;
    private readonly NexusTelemetryContext _baseline;
    private readonly BatchQueue? _events;
    private readonly BatchQueue? _logs;

    // Two flags, not one. `_shutdown` makes DisposeAsync idempotent the moment it
    // is entered; `_closed` is only set once the shutdown flush has finished. If a
    // single flag guarded both, marking the client disposed on entry would make the
    // shutdown flush itself look like a use-after-dispose and silently drop exactly
    // the telemetry the flush exists to deliver.
    private int _shutdown;
    private volatile bool _closed;

    /// <summary>Creates a client for an API key, layered over the <c>NEXUS_*</c> environment.</summary>
    /// <param name="apiKey">The tenant key (<c>nxs_...</c>).</param>
    /// <param name="configure">Further options, applied over the environment.</param>
    public NexusClient(string apiKey, Action<NexusOptions>? configure = null)
        : this(BuildOptions(apiKey, configure), transport: null, dispatcherFactory: null)
    {
    }

    /// <summary>Creates a client from options.</summary>
    public NexusClient(NexusOptions options)
        : this(options, transport: null, dispatcherFactory: null)
    {
    }

    /// <summary>
    /// Creates a client that sends through a caller-supplied <see cref="HttpClient"/>.
    /// </summary>
    /// <remarks>
    /// This is the <c>IHttpClientFactory</c> path: the app's own handler pipeline,
    /// resilience policies and connection lifetime apply, and the client is not
    /// disposed here.
    /// </remarks>
    public NexusClient(NexusOptions options, HttpClient httpClient)
        : this(
            options,
            new HttpClientTransport(httpClient ?? throw new ArgumentNullException(nameof(httpClient))),
            dispatcherFactory: null)
    {
    }

    /// <summary>Creates a client that sends through a custom transport.</summary>
    public NexusClient(NexusOptions options, INexusTransport transport)
        : this(options, transport ?? throw new ArgumentNullException(nameof(transport)), null)
    {
    }

    private NexusClient(
        NexusOptions options,
        INexusTransport? transport,
        Func<HttpEngine, NexusDiagnostics, INexusDispatcher>? dispatcherFactory)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        options.Validate();

        // Snapshot the options: a caller that mutates the instance afterwards must
        // not be able to change a running client's key or origin out from under it.
        _options = options.Clone();
        _sharedTransport = transport;
        _diagnostics = new NexusDiagnostics(_options.DiagnosticSink, _options.Debug);
        _engine = new HttpEngine(_options, transport, _diagnostics);
        _dispatcher = dispatcherFactory is null
            ? new DirectDispatcher(_engine)
            : dispatcherFactory(_engine, _diagnostics);

        _baseline = new NexusTelemetryContext
        {
            Release = _options.Release,
            AppVersion = _options.AppVersion,
            OsType = _options.OsType,
            OsVersion = _options.OsVersion,
        };

        if (_options.Batch)
        {
            _events = new BatchQueue(
                "events",
                DeliverEventsAsync,
                _options.MaxBatch,
                _options.FlushInterval,
                _options.MaxQueue,
                _diagnostics);

            _logs = new BatchQueue(
                "logs",
                DeliverLogsAsync,
                _options.MaxBatch,
                _options.FlushInterval,
                _options.MaxQueue,
                _diagnostics);
        }

        Sessions = new SessionsResource(this);
        Events = new EventsResource(this);
        Logs = new LogsResource(this);
        Errors = new ErrorsResource(this);
        Flags = new FlagsResource(this);
        RemoteConfig = new RemoteConfigResource(this);
        Realtime = new RealtimeResource(this);
        Links = new LinksResource(this);
        Surveys = new SurveysResource(this);
        Push = new PushResource(this);
        InApp = new InAppResource(this);
        LiveActivities = new LiveActivitiesResource(this);
        Replay = new ReplayResource(this);
        Voice = new VoiceResource(this);
    }

    /// <inheritdoc />
    public NexusOptions Options => _options;

    /// <inheritdoc />
    public SessionsResource Sessions { get; }

    /// <inheritdoc />
    public EventsResource Events { get; }

    /// <inheritdoc />
    public LogsResource Logs { get; }

    /// <inheritdoc />
    public ErrorsResource Errors { get; }

    /// <inheritdoc />
    public FlagsResource Flags { get; }

    /// <inheritdoc />
    public RemoteConfigResource RemoteConfig { get; }

    /// <inheritdoc />
    public RealtimeResource Realtime { get; }

    /// <inheritdoc />
    public LinksResource Links { get; }

    /// <inheritdoc />
    public SurveysResource Surveys { get; }

    /// <inheritdoc />
    public PushResource Push { get; }

    /// <inheritdoc />
    public InAppResource InApp { get; }

    /// <inheritdoc />
    public LiveActivitiesResource LiveActivities { get; }

    /// <inheritdoc />
    public ReplayResource Replay { get; }

    /// <inheritdoc />
    public VoiceResource Voice { get; }

    /// <inheritdoc />
    public int Pending => (_events?.Pending ?? 0) + (_logs?.Pending ?? 0);

    /// <inheritdoc />
    public long Dropped => (_events?.Dropped ?? 0) + (_logs?.Dropped ?? 0);

    /// <summary>Whether the client has been disposed.</summary>
    public bool IsDisposed => _closed;

    /// <summary>The person bound to the current execution flow, if any.</summary>
    public string? DistinctId => ResolveContext().DistinctId;

    /// <inheritdoc />
    public Task<IdentifyResult> IdentifyAsync(
        string distinctId,
        string? email = null,
        string? name = null,
        string? phone = null,
        object? traits = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(distinctId, nameof(distinctId));

        // Bind locally first, so telemetry captured between here and the response
        // already carries the identity.
        NexusContext.Identify(distinctId);

        return Sessions.IdentifyAsync(distinctId, email, name, phone, traits, cancellationToken);
    }

    /// <inheritdoc />
    public void Reset() => NexusContext.Reset();

    /// <inheritdoc />
    public void Track(
        string name,
        object? properties = null,
        DateTimeOffset? timestamp = null,
        NexusTelemetryContext? context = null)
        => Events.Track(name, properties, timestamp, context);

    /// <inheritdoc />
    public Task<ErrorCaptureResult> CaptureExceptionAsync(
        Exception exception,
        bool handled = true,
        object? errorContext = null,
        CancellationToken cancellationToken = default)
        => Errors.CaptureExceptionAsync(
            exception, handled, errorContext: errorContext, cancellationToken: cancellationToken);

    /// <inheritdoc />
    public Task<EmitResult> EmitAsync(
        string room, string eventName, object? payload = null, CancellationToken cancellationToken = default)
        => Realtime.EmitAsync(room, eventName, payload, cancellationToken);

    /// <inheritdoc />
    public Task<bool> IsEnabledAsync(
        string key,
        string? distinctId = null,
        object? properties = null,
        bool fallback = false,
        CancellationToken cancellationToken = default)
        => Flags.IsEnabledAsync(key, distinctId, properties, fallback, cancellationToken);

    /// <inheritdoc />
    public async Task<JsonNode?> SendAsync(
        NexusRequest request, CancellationToken cancellationToken = default)
    {
        if (request is null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (IsDisposed)
        {
            // Throwing here would turn a shutdown-ordering mistake into a crash on
            // the way out. Report it instead, loudly enough to be fixable.
            _diagnostics.Warning(
                null, "Request after dispose was dropped: {0} {1}.", request.Method, request.Path);
            return null;
        }

        try
        {
            return await _dispatcher.DispatchAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (NexusException exception) when (_options.Silent)
        {
            _diagnostics.Warning(
                exception, "{0} {1} failed and was swallowed (Silent).", request.Method, request.Path);
            return null;
        }
    }

    /// <inheritdoc />
    public NexusTelemetryContext ResolveContext(NexusTelemetryContext? contextOverrides = null)
        => _baseline.Merge(NexusContext.Current).Merge(contextOverrides);

    /// <inheritdoc />
    public async Task FlushAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        await FlushEventsAsync(cancellationToken).ConfigureAwait(false);
        await FlushLogsAsync(cancellationToken).ConfigureAwait(false);
        await _dispatcher
            .FlushAsync(timeout ?? DefaultShutdownTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public INexusClient Background(int workers = 1)
    {
        // The sibling shares this client's transport, so it does not open a second
        // connection pool, and it never disposes one it did not create.
        var transport = _sharedTransport;
        return new NexusClient(
            _options,
            transport,
            (engine, diagnostics) => new BackgroundDispatcher(engine, diagnostics, workers));
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _shutdown, 1) != 0)
        {
            return;
        }

        try
        {
            if (_events is not null)
            {
                await _events.StopAsync(DefaultShutdownTimeout).ConfigureAwait(false);
            }

            if (_logs is not null)
            {
                await _logs.StopAsync(DefaultShutdownTimeout).ConfigureAwait(false);
            }

            await _dispatcher.StopAsync(DefaultShutdownTimeout).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // Shutdown is the worst possible moment to throw; the host is already
            // on its way out and would turn this into a confusing crash.
            _diagnostics.Warning(exception, "Nexus shutdown did not complete cleanly.");
        }
        finally
        {
            _events?.Dispose();
            _logs?.Dispose();
            (_dispatcher as IDisposable)?.Dispose();
            _closed = true;
        }
    }

    /// <summary>
    /// Flushes and releases everything. Prefer <see cref="DisposeAsync"/>.
    /// </summary>
    /// <remarks>
    /// The flush is genuinely asynchronous, so this runs it on the thread pool and
    /// waits. Doing it inline would deadlock under a synchronization context that
    /// still has one thread. Hosts that support <see cref="IAsyncDisposable"/> —
    /// which includes the ASP.NET Core and Generic Host containers — take the async
    /// path and never reach this.
    /// </remarks>
    public void Dispose()
    {
        if (Volatile.Read(ref _shutdown) != 0)
        {
            return;
        }

        Task.Run(async () => await DisposeAsync().ConfigureAwait(false))
            .GetAwaiter()
            .GetResult();
    }

    internal void EnqueueEvent(JsonObject item, NexusTelemetryContext context)
    {
        if (_events is not null)
        {
            _events.Add(item, context);
            return;
        }

        // Batching off: send now, and never let a telemetry failure reach the caller
        // of a void Track().
        FireAndForget(DeliverEventsAsync(new[] { item }, context.ToWire(), CancellationToken.None), "events");
    }

    internal void EnqueueLog(JsonObject item, NexusTelemetryContext context)
    {
        if (_logs is not null)
        {
            _logs.Add(item, context);
            return;
        }

        FireAndForget(DeliverLogsAsync(new[] { item }, context.ToWire(), CancellationToken.None), "logs");
    }

    internal Task FlushEventsAsync(CancellationToken cancellationToken)
        => _events?.FlushAsync(cancellationToken) ?? Task.CompletedTask;

    internal Task FlushLogsAsync(CancellationToken cancellationToken)
        => _logs?.FlushAsync(cancellationToken) ?? Task.CompletedTask;

    /// <summary>Reports an SDK-level condition through the configured diagnostics sink.</summary>
    internal void Report(string message, Exception? exception = null)
        => _diagnostics.Warning(message, exception);

    private static NexusOptions BuildOptions(string apiKey, Action<NexusOptions>? configure)
    {
        var options = NexusOptions.FromEnvironment(configure);

        // The explicit key wins over NEXUS_API_KEY — passing one in code and
        // silently using another from the environment would be worse than either.
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            options.ApiKey = apiKey;
        }

        return options;
    }

    private Task<JsonNode?> DeliverEventsAsync(
        System.Collections.Generic.IReadOnlyList<JsonObject> items,
        JsonObject context,
        CancellationToken cancellationToken)
        => SendAsync(Payloads.EventsBatch(items, context), cancellationToken);

    private Task<JsonNode?> DeliverLogsAsync(
        System.Collections.Generic.IReadOnlyList<JsonObject> items,
        JsonObject context,
        CancellationToken cancellationToken)
        => SendAsync(Payloads.LogsBatch(items, context), cancellationToken);

    private void FireAndForget(Task task, string what)
    {
        _ = task.ContinueWith(
            completed => _diagnostics.Warning(
                completed.Exception, "Unbuffered {0} delivery failed.", what),
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
