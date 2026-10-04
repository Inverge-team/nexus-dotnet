using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Models;
using Inverge.Nexus.Resources;

namespace Inverge.Nexus;

/// <summary>
/// A configured connection to one Nexus environment.
/// </summary>
/// <remarks>
/// Register it as a singleton and share it. It is thread-safe, it owns the
/// background buffers, and building one per request would both leak buffers and
/// lose the batching that keeps telemetry cheap.
/// </remarks>
public interface INexusClient : IDisposable, IAsyncDisposable
{
    /// <summary>The options this client was built with.</summary>
    NexusOptions Options { get; }

    /// <summary>People and journey sessions.</summary>
    SessionsResource Sessions { get; }

    /// <summary>Product analytics.</summary>
    EventsResource Events { get; }

    /// <summary>Structured logging.</summary>
    LogsResource Logs { get; }

    /// <summary>Error monitoring.</summary>
    ErrorsResource Errors { get; }

    /// <summary>Feature flags.</summary>
    FlagsResource Flags { get; }

    /// <summary>Remote Config.</summary>
    RemoteConfigResource RemoteConfig { get; }

    /// <summary>Realtime rooms.</summary>
    RealtimeResource Realtime { get; }

    /// <summary>Deep links and attribution.</summary>
    LinksResource Links { get; }

    /// <summary>In-product surveys.</summary>
    SurveysResource Surveys { get; }

    /// <summary>Push notifications.</summary>
    PushResource Push { get; }

    /// <summary>In-app messages.</summary>
    InAppResource InApp { get; }

    /// <summary>Live Activities.</summary>
    LiveActivitiesResource LiveActivities { get; }

    /// <summary>Session replay ingestion.</summary>
    ReplayResource Replay { get; }

    /// <summary>Voice (CPaaS) call control.</summary>
    VoiceResource Voice { get; }

    /// <summary>How many buffered telemetry items are waiting to be sent.</summary>
    int Pending { get; }

    /// <summary>
    /// How many buffered items have been dropped because a buffer was full.
    /// </summary>
    /// <remarks>
    /// Non-zero means telemetry is being lost — either the backend is unreachable or
    /// the process is producing more than <see cref="NexusOptions.MaxQueue"/> can
    /// hold. Worth exposing as a metric.
    /// </remarks>
    long Dropped { get; }

    /// <summary>Sets the current person locally and creates or updates them server-side.</summary>
    Task<IdentifyResult> IdentifyAsync(
        string distinctId,
        string? email = null,
        string? name = null,
        string? phone = null,
        object? traits = null,
        CancellationToken cancellationToken = default);

    /// <summary>Forgets the person and starts a new session — what you want on logout.</summary>
    void Reset();

    /// <summary>Captures one event. Returns as soon as it is buffered.</summary>
    void Track(
        string name,
        object? properties = null,
        DateTimeOffset? timestamp = null,
        NexusTelemetryContext? context = null);

    /// <summary>Reports a caught exception.</summary>
    Task<ErrorCaptureResult> CaptureExceptionAsync(
        Exception exception,
        bool handled = true,
        object? errorContext = null,
        CancellationToken cancellationToken = default);

    /// <summary>Emits a named event to a realtime room.</summary>
    Task<EmitResult> EmitAsync(
        string room, string eventName, object? payload = null, CancellationToken cancellationToken = default);

    /// <summary>Whether a feature flag is on for a person.</summary>
    Task<bool> IsEnabledAsync(
        string key,
        string? distinctId = null,
        object? properties = null,
        bool fallback = false,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Executes a request against an endpoint the typed surface does not cover.
    /// </summary>
    Task<JsonNode?> SendAsync(NexusRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Client options, ambient context and per-call overrides, resolved in that order
    /// of increasing precedence.
    /// </summary>
    NexusTelemetryContext ResolveContext(NexusTelemetryContext? contextOverrides = null);

    /// <summary>
    /// Ships every buffered event and log line.
    /// </summary>
    /// <remarks>
    /// Call it before a short-lived process exits. A console app or a function that
    /// returns without flushing loses whatever is still buffered.
    /// </remarks>
    Task FlushAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// A sibling client on which every call is fire-and-forget.
    /// </summary>
    /// <remarks>
    /// Calls that need a response cannot work through it — there is nothing to
    /// return. Use the normal client for flags and Remote Config.
    /// </remarks>
    INexusClient Background(int workers = 1);
}
