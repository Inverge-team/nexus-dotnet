using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;

namespace Inverge.Nexus;

/// <summary>
/// The ambient telemetry context: bind identity once at the edge of a request,
/// and everything captured underneath inherits it.
/// </summary>
/// <remarks>
/// <para>
/// Backed by <see cref="AsyncLocal{T}"/>, so the context is correct per request
/// and flows across <c>await</c> boundaries into whatever the handler calls —
/// including work started on the thread pool.
/// </para>
/// <para>
/// One <see cref="AsyncLocal{T}"/> caveat worth knowing: a value set inside an
/// <c>async</c> method does not flow back out to its caller. Bind at the level
/// that should own the context — the middleware, the job handler — rather than
/// deep inside a helper, or use <see cref="Scope(NexusTelemetryContext)"/>,
/// whose lifetime is explicit.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// using (NexusContext.Scope(distinctId: "user_123", url: "/checkout"))
/// {
///     nexus.Track("order_placed", new { total = 42 });   // carries user_123
/// }
/// </code>
/// </example>
public static class NexusContext
{
    private static readonly AsyncLocal<NexusTelemetryContext?> Ambient
        = new AsyncLocal<NexusTelemetryContext?>();

    /// <summary>The context in force for the current execution flow.</summary>
    public static NexusTelemetryContext Current => Ambient.Value ?? NexusTelemetryContext.Empty;

    /// <summary>Replaces the context for the current execution flow.</summary>
    public static void Set(NexusTelemetryContext? context) => Ambient.Value = context;

    /// <summary>Merges values into the current context for the current flow.</summary>
    public static void Bind(NexusTelemetryContext context) => Ambient.Value = Current.Merge(context);

    /// <summary>
    /// Binds values for the lifetime of the returned scope, restoring the
    /// previous context when it is disposed.
    /// </summary>
    public static IDisposable Scope(NexusTelemetryContext context)
    {
        var previous = Ambient.Value;
        Ambient.Value = Current.Merge(context);
        return new ContextScope(previous);
    }

    /// <summary>Binds the fields you name for the lifetime of the returned scope.</summary>
    public static IDisposable Scope(
        string? distinctId = null,
        string? sessionKey = null,
        string? deviceKey = null,
        string? release = null,
        string? appVersion = null,
        string? osType = null,
        string? osVersion = null,
        string? browser = null,
        string? country = null,
        string? url = null,
        IReadOnlyDictionary<string, object?>? properties = null)
        => Scope(new NexusTelemetryContext
        {
            DistinctId = distinctId,
            SessionKey = sessionKey,
            DeviceKey = deviceKey,
            Release = release,
            AppVersion = appVersion,
            OsType = osType,
            OsVersion = osVersion,
            Browser = browser,
            Country = country,
            Url = url,
            Properties = properties,
        });

    /// <summary>
    /// Sets the current person for this flow.
    /// </summary>
    /// <remarks>
    /// This binds local context only. To also create or update the person
    /// server-side, call <c>client.IdentifyAsync(...)</c>.
    /// </remarks>
    public static void Identify(string? distinctId, IReadOnlyDictionary<string, object?>? properties = null)
        => Bind(new NexusTelemetryContext { DistinctId = distinctId, Properties = properties });

    /// <summary>
    /// Forgets the person and starts a new session — what you want on logout.
    /// </summary>
    /// <param name="keepDevice">Keep the device key, so the same device is still recognised.</param>
    public static void Reset(bool keepDevice = true)
    {
        var previous = Current;
        Ambient.Value = new NexusTelemetryContext
        {
            DeviceKey = keepDevice ? previous.DeviceKey : null,
            SessionKey = NewSessionKey(),
            Release = previous.Release,
            AppVersion = previous.AppVersion,
            OsType = previous.OsType,
            OsVersion = previous.OsVersion,
        };
    }

    /// <summary>Drops the whole context: no person, no session, no device.</summary>
    public static void Clear() => Ambient.Value = null;

    /// <summary>A fresh opaque session key.</summary>
    public static string NewSessionKey()
        => "sess_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    /// <summary>A fresh opaque device key.</summary>
    public static string NewDeviceKey()
        => "dev_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

    private sealed class ContextScope : IDisposable
    {
        private readonly NexusTelemetryContext? _previous;
        private bool _disposed;

        public ContextScope(NexusTelemetryContext? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Ambient.Value = _previous;
        }
    }
}
