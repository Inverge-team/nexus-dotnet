using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Http;

namespace Inverge.Nexus.AspNetCore;

/// <summary>How the middleware reads a request.</summary>
public sealed class NexusAspNetCoreOptions
{
    /// <summary>Header carrying the journey session key.</summary>
    public string SessionKeyHeader { get; set; } = "X-Nexus-Session";

    /// <summary>Header carrying the device key.</summary>
    public string DeviceKeyHeader { get; set; } = "X-Nexus-Device";

    /// <summary>
    /// Header carrying the visitor's country.
    /// </summary>
    /// <remarks>
    /// Defaults to Cloudflare's <c>CF-IPCountry</c>. Change it to match whatever
    /// proxy actually fronts your app — behind a different CDN this header is absent
    /// and country targeting silently never matches.
    /// </remarks>
    public string CountryHeader { get; set; } = "CF-IPCountry";

    /// <summary>
    /// Reads the current person's id from the request.
    /// </summary>
    /// <remarks>
    /// Defaults to the <c>sub</c> / <c>NameIdentifier</c> claim, falling back to
    /// <c>User.Identity.Name</c>. Replace it when your identity lives somewhere else
    /// — a tenant-scoped id, a header from an upstream gateway.
    /// </remarks>
    public Func<HttpContext, string?>? ResolveDistinctId { get; set; }

    /// <summary>Reads the session key. Defaults to <see cref="SessionKeyHeader"/>.</summary>
    public Func<HttpContext, string?>? ResolveSessionKey { get; set; }

    /// <summary>Extra properties attached to every event captured during the request.</summary>
    public Func<HttpContext, IReadOnlyDictionary<string, object?>?>? ResolveProperties { get; set; }

    /// <summary>
    /// Report unhandled exceptions to error monitoring.
    /// </summary>
    /// <remarks>
    /// The exception is reported and then rethrown unchanged, so your own error
    /// handling still runs and the response is unaffected.
    /// </remarks>
    public bool CaptureUnhandledExceptions { get; set; } = true;

    /// <summary>
    /// Capture one analytics event per request.
    /// </summary>
    /// <remarks>
    /// Off by default, deliberately. Events are metered, and a busy service would
    /// bill an event for every request — including health checks and static files —
    /// which is rarely what anyone wants from product analytics.
    /// </remarks>
    public bool TrackRequests { get; set; }

    /// <summary>The event name used when <see cref="TrackRequests"/> is on.</summary>
    public string RequestEventName { get; set; } = "http_request";

    /// <summary>
    /// Path prefixes the middleware ignores entirely.
    /// </summary>
    /// <remarks>
    /// Health and metrics endpoints are excluded by default: they are polled
    /// constantly, carry no user, and would otherwise dominate your telemetry.
    /// </remarks>
    public IList<string> IgnoredPathPrefixes { get; }
        = new List<string> { "/health", "/healthz", "/ready", "/live", "/metrics", "/favicon.ico" };

    /// <summary>
    /// Include the query string in the telemetry <c>url</c>.
    /// </summary>
    /// <remarks>
    /// Off by default. Query strings routinely carry tokens, invite codes and
    /// personal data, and telemetry is the last place that should be copied to.
    /// </remarks>
    public bool IncludeQueryString { get; set; }
}
