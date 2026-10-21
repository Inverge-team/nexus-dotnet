using System;
using System.Collections.Generic;
using System.Globalization;
using Inverge.Nexus.Diagnostics;

namespace Inverge.Nexus;

/// <summary>
/// Everything the client needs to talk to one Nexus environment.
/// </summary>
/// <remarks>
/// An API key belongs to a single environment, so one client writes to one
/// environment. The type is mutable so it works with the options pattern
/// (<c>services.AddNexus(o =&gt; ...)</c>); the client copies it at construction,
/// which means mutating it afterwards changes nothing already running.
/// </remarks>
public sealed class NexusOptions
{
    /// <summary>The production API origin, used when nothing else is configured.</summary>
    public const string DefaultBaseUrl = "https://services.inverge.net";

    private string _apiKey = string.Empty;
    private string _baseUrl = DefaultBaseUrl;
    private string? _realtimeUrl;

    /// <summary>The tenant API key (<c>nxs_...</c>). Required.</summary>
    public string ApiKey
    {
        get => _apiKey;
        set => _apiKey = value?.Trim() ?? string.Empty;
    }

    /// <summary>
    /// The API origin. Point it at a local backend while developing.
    /// </summary>
    public string BaseUrl
    {
        get => _baseUrl;
        set => _baseUrl = NormalizeOrigin(value) ?? DefaultBaseUrl;
    }

    /// <summary>
    /// The Socket.IO origin for the realtime listener. Defaults to <see cref="BaseUrl"/>.
    /// </summary>
    public string? RealtimeUrl
    {
        get => _realtimeUrl;
        set => _realtimeUrl = NormalizeOrigin(value);
    }

    /// <summary>Per-request timeout. Defaults to 10 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How many times to retry a <em>safe</em> request — telemetry ingest and
    /// reads — on a network error, 429, or 5xx. Calls with a side effect a user
    /// would notice twice are never retried, whatever this is set to.
    /// </summary>
    public int MaxRetries { get; set; } = 2;

    /// <summary>Base backoff delay; it doubles per attempt and carries jitter.</summary>
    public TimeSpan RetryBackoff { get; set; } = TimeSpan.FromMilliseconds(250);

    /// <summary>Headers merged into every request.</summary>
    public IDictionary<string, string> DefaultHeaders { get; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Default <c>appVersion</c> for telemetry that does not set one.</summary>
    public string? AppVersion { get; set; }

    /// <summary>Default <c>release</c> for telemetry that does not set one.</summary>
    public string? Release { get; set; }

    /// <summary>Default <c>osType</c> for telemetry that does not set one.</summary>
    public string? OsType { get; set; }

    /// <summary>Default <c>osVersion</c> for telemetry that does not set one.</summary>
    public string? OsVersion { get; set; }

    /// <summary>Properties merged into every captured event.</summary>
    public IDictionary<string, object?> DefaultProperties { get; }
        = new Dictionary<string, object?>(StringComparer.Ordinal);

    /// <summary>
    /// Buffer events and log lines and ship them in the background, keeping
    /// telemetry off the request's hot path. Turn it off in a short-lived process
    /// if you prefer explicit sends; <c>FlushAsync</c> works either way.
    /// </summary>
    public bool Batch { get; set; } = true;

    /// <summary>How long a partial batch waits before it is sent anyway.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum items per batched request. The ingest endpoints accept 1000; the
    /// default of 50 keeps individual requests small and failures cheap.
    /// </summary>
    public int MaxBatch { get; set; } = 50;

    /// <summary>
    /// Buffer capacity. Items beyond it are dropped and counted rather than
    /// growing memory without bound — a backend outage costs the memory you
    /// chose, not all of it.
    /// </summary>
    public int MaxQueue { get; set; } = 10_000;

    /// <summary>
    /// Never let a Nexus failure reach your code: calls return a default instead
    /// of throwing, and the failure goes to diagnostics. Background delivery is
    /// always silent regardless.
    /// </summary>
    public bool Silent { get; set; }

    /// <summary>Verbose SDK diagnostics.</summary>
    public bool Debug { get; set; }

    /// <summary>
    /// Where the SDK reports on itself — dropped batches, retries, delivery
    /// failures. Left unset, those messages go nowhere.
    /// </summary>
    /// <remarks>
    /// <c>Inverge.Nexus.Extensions.Logging</c> wires this to <c>ILogger</c>
    /// automatically. Set it yourself when you are not using Microsoft.Extensions.
    /// </remarks>
    public NexusDiagnosticSink? DiagnosticSink { get; set; }

    /// <summary>Overrides the default <c>Inverge.Nexus.NET/&lt;version&gt;</c> user agent.</summary>
    public string? UserAgent { get; set; }

    /// <summary>
    /// How long a resolved Remote Config template is reused in-process before it
    /// is revalidated. Defaults to 30 seconds.
    /// </summary>
    public TimeSpan RemoteConfigCacheTtl { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// <c>false</c> switches every Nexus service off without removing any code.
    /// Defaults to <c>true</c>: every service is on. Also <c>NEXUS_ENABLED=false</c>.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Per-service switches, keyed by <see cref="NexusServices"/> names, e.g.
    /// <c>Services[NexusServices.LiveActivities] = false</c>. Empty by default —
    /// <b>every service is on</b> unless switched off here or by its
    /// <c>NEXUS_&lt;SERVICE&gt;_ENABLED</c> environment variable (off wins).
    /// </summary>
    public IDictionary<string, bool> Services { get; } = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

    // Switched off by the environment — kept apart from Services so code applied
    // after ApplyEnvironment() can't switch them back on (off wins).
    private readonly HashSet<string> _environmentOff = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private bool _environmentAllOff;

    /// <summary>
    /// Whether a service is on: not switched off by <see cref="Enabled"/>,
    /// <see cref="Services"/> or the environment. Unknown names count as on.
    /// </summary>
    public bool IsServiceEnabled(string service) => ServiceOffReason(service) is null;

    /// <summary>Why a service is off (for diagnostics), or null when it is on.</summary>
    public string? ServiceOffReason(string service)
    {
        var name = NexusServices.Normalize(service);
        if (name is null)
        {
            return null;
        }

        if (!Enabled)
        {
            return "NexusOptions.Enabled=false";
        }

        if (_environmentAllOff)
        {
            return NexusServices.AllEnvironmentVariable + "=false";
        }

        foreach (var entry in Services)
        {
            if (!entry.Value && NexusServices.Normalize(entry.Key) == name)
            {
                return "NexusOptions.Services[\"" + name + "\"]=false";
            }
        }

        return _environmentOff.Contains(name) ? NexusServices.EnvironmentVariables[name] + "=false" : null;
    }

    /// <summary>The API origin without a trailing slash.</summary>
    public string HttpBase => _baseUrl;

    /// <summary>The Socket.IO origin: <see cref="RealtimeUrl"/>, or the API origin.</summary>
    public string SocketBase => _realtimeUrl ?? _baseUrl;

    /// <summary>The user agent actually sent.</summary>
    public string ResolvedUserAgent
        => string.IsNullOrWhiteSpace(UserAgent) ? NexusVersion.UserAgent : UserAgent!;

    /// <summary>Environment variable names, by the option each one fills.</summary>
    public static IReadOnlyDictionary<string, string> EnvironmentVariables { get; }
        = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [nameof(ApiKey)] = "NEXUS_API_KEY",
            [nameof(BaseUrl)] = "NEXUS_BASE_URL",
            [nameof(RealtimeUrl)] = "NEXUS_REALTIME_URL",
            [nameof(Timeout)] = "NEXUS_TIMEOUT",
            [nameof(MaxRetries)] = "NEXUS_MAX_RETRIES",
            [nameof(AppVersion)] = "NEXUS_APP_VERSION",
            [nameof(Release)] = "NEXUS_RELEASE",
            [nameof(Batch)] = "NEXUS_BATCH",
            [nameof(FlushInterval)] = "NEXUS_FLUSH_INTERVAL",
            [nameof(MaxBatch)] = "NEXUS_MAX_BATCH",
            [nameof(MaxQueue)] = "NEXUS_MAX_QUEUE",
            [nameof(Silent)] = "NEXUS_SILENT",
            [nameof(Debug)] = "NEXUS_DEBUG",
        };

    /// <summary>
    /// Builds options from the <c>NEXUS_*</c> environment, then applies
    /// <paramref name="configure"/> on top.
    /// </summary>
    /// <remarks>
    /// The ordering matters and is the whole point: explicit code wins over the
    /// environment for the values it sets, while the values it leaves alone stay
    /// environment-driven. That is what lets <c>NEXUS_BASE_URL</c> repoint a
    /// staging deploy whose API key is assigned in code — without it, passing a
    /// key in code silently sends staging traffic to production.
    /// </remarks>
    public static NexusOptions FromEnvironment(Action<NexusOptions>? configure = null)
    {
        var options = new NexusOptions();
        options.ApplyEnvironment();
        configure?.Invoke(options);
        return options;
    }

    /// <summary>Overlays any <c>NEXUS_*</c> variables that are set.</summary>
    /// <param name="read">
    /// Variable reader; defaults to <see cref="Environment.GetEnvironmentVariable(string)"/>.
    /// </param>
    public void ApplyEnvironment(Func<string, string?>? read = null)
    {
        read ??= Environment.GetEnvironmentVariable;

        Assign(read("NEXUS_API_KEY"), value => ApiKey = value);
        Assign(read("NEXUS_BASE_URL"), value => BaseUrl = value);
        Assign(read("NEXUS_REALTIME_URL"), value => RealtimeUrl = value);
        Assign(read("NEXUS_APP_VERSION"), value => AppVersion = value);
        Assign(read("NEXUS_RELEASE"), value => Release = value);
        Assign(read("NEXUS_USER_AGENT"), value => UserAgent = value);

        AssignSeconds(read("NEXUS_TIMEOUT"), value => Timeout = value);
        AssignSeconds(read("NEXUS_FLUSH_INTERVAL"), value => FlushInterval = value);
        AssignInt(read("NEXUS_MAX_RETRIES"), value => MaxRetries = value);
        AssignInt(read("NEXUS_MAX_BATCH"), value => MaxBatch = value);
        AssignInt(read("NEXUS_MAX_QUEUE"), value => MaxQueue = value);
        AssignBool(read("NEXUS_BATCH"), value => Batch = value);
        AssignBool(read("NEXUS_SILENT"), value => Silent = value);
        AssignBool(read("NEXUS_DEBUG"), value => Debug = value);

        // Service switches — only ever turn things OFF (every service defaults to on).
        if (!NexusServices.IsOn(read(NexusServices.AllEnvironmentVariable)))
        {
            _environmentAllOff = true;
        }

        foreach (var entry in NexusServices.EnvironmentVariables)
        {
            if (!NexusServices.IsOn(read(entry.Value)))
            {
                _environmentOff.Add(entry.Key);
            }
        }
    }

    /// <summary>Throws when the options cannot produce a working client.</summary>
    /// <exception cref="NexusConfigurationException">The key or origin is unusable.</exception>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            throw new NexusConfigurationException(
                "No Nexus API key. Set NexusOptions.ApiKey to your nxs_... key, or export NEXUS_API_KEY.");
        }

        if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new NexusConfigurationException(
                "NexusOptions.BaseUrl must be an absolute http:// or https:// origin (got '" + _baseUrl + "').");
        }

        if (Timeout <= TimeSpan.Zero)
        {
            throw new NexusConfigurationException("NexusOptions.Timeout must be greater than zero.");
        }

        if (MaxRetries < 0)
        {
            throw new NexusConfigurationException("NexusOptions.MaxRetries cannot be negative.");
        }

        if (MaxBatch < 1)
        {
            throw new NexusConfigurationException("NexusOptions.MaxBatch must be at least 1.");
        }

        foreach (var key in Services.Keys)
        {
            // A typo must not silently leave a service on.
            if (NexusServices.Normalize(key) is null)
            {
                throw new NexusConfigurationException(
                    "Unknown Nexus service '" + key + "' in NexusOptions.Services. Known: "
                    + string.Join(", ", NexusServices.EnvironmentVariables.Keys) + ".");
            }
        }
    }

    /// <summary>The absolute URL for a request path.</summary>
    public string UrlFor(string path)
        => _baseUrl + (path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path);

    /// <summary>A deep copy, so the client is immune to later mutation.</summary>
    public NexusOptions Clone()
    {
        var copy = new NexusOptions
        {
            ApiKey = ApiKey,
            BaseUrl = _baseUrl,
            RealtimeUrl = _realtimeUrl,
            Timeout = Timeout,
            MaxRetries = MaxRetries,
            RetryBackoff = RetryBackoff,
            AppVersion = AppVersion,
            Release = Release,
            OsType = OsType,
            OsVersion = OsVersion,
            Batch = Batch,
            FlushInterval = FlushInterval,
            MaxBatch = MaxBatch,
            MaxQueue = MaxQueue,
            Silent = Silent,
            Debug = Debug,
            UserAgent = UserAgent,
            RemoteConfigCacheTtl = RemoteConfigCacheTtl,
            DiagnosticSink = DiagnosticSink,
            Enabled = Enabled,
            _environmentAllOff = _environmentAllOff,
        };

        foreach (var service in Services)
        {
            copy.Services[service.Key] = service.Value;
        }

        copy._environmentOff.UnionWith(_environmentOff);

        foreach (var header in DefaultHeaders)
        {
            copy.DefaultHeaders[header.Key] = header.Value;
        }

        foreach (var property in DefaultProperties)
        {
            copy.DefaultProperties[property.Key] = property.Value;
        }

        return copy;
    }

    private static string? NormalizeOrigin(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value!.Trim().TrimEnd('/');

    private static void Assign(string? raw, Action<string> apply)
    {
        if (!string.IsNullOrWhiteSpace(raw))
        {
            apply(raw!.Trim());
        }
    }

    private static void AssignInt(string? raw, Action<int> apply)
    {
        if (!string.IsNullOrWhiteSpace(raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            apply(value);
        }
    }

    private static void AssignSeconds(string? raw, Action<TimeSpan> apply)
    {
        if (!string.IsNullOrWhiteSpace(raw)
            && double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            && value > 0)
        {
            apply(TimeSpan.FromSeconds(value));
        }
    }

    private static void AssignBool(string? raw, Action<bool> apply)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        switch (raw!.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
            case "y":
            case "t":
                apply(true);
                break;
            case "0":
            case "false":
            case "no":
            case "off":
            case "n":
            case "f":
                apply(false);
                break;
        }
    }
}
