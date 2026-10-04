using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Internal;
using Inverge.Nexus.Models;

namespace Inverge.Nexus.Resources;

/// <summary>Session replay ingestion.</summary>
/// <remarks>
/// Replay is normally recorded by the browser and mobile SDKs. This resource is
/// for proxying those chunks through your own backend — when the browser must not
/// talk to Nexus directly — and for replaying synthetic sessions from a worker.
/// </remarks>
public sealed class ReplayResource : NexusResource
{
    /// <summary>The most replay events the ingest endpoint accepts in one request.</summary>
    public const int MaxChunkSize = 5000;

    internal ReplayResource(NexusClient client) : base(client)
    {
    }

    /// <summary>Ships one chunk of replay events for a recording.</summary>
    /// <param name="recordingId">
    /// The recording these events belong to. It must stay the same across every
    /// chunk — a new id starts a new recording, splitting one session into several.
    /// </param>
    /// <param name="events">The recorder's events. At most <see cref="MaxChunkSize"/>.</param>
    /// <param name="href">The page or screen being recorded.</param>
    /// <param name="width">Viewport width, so playback scales correctly.</param>
    /// <param name="height">Viewport height.</param>
    /// <param name="context">Per-call identity and device overrides.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<NexusAck> IngestAsync(
        string recordingId,
        IEnumerable<object> events,
        string? href = null,
        int? width = null,
        int? height = null,
        NexusTelemetryContext? context = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(recordingId, nameof(recordingId));
        if (events is null)
        {
            throw new ArgumentNullException(nameof(events));
        }

        var items = new List<JsonObject>();
        foreach (var payload in events)
        {
            items.Add(JsonHelpers.ToObjectNode(payload));
        }

        if (items.Count == 0)
        {
            return NexusAck.From(null);
        }

        Guard.AtMost(items.Count, MaxChunkSize, "replay events", nameof(events));

        var resolved = Resolve(context);
        var response = await SendAsync(
            Payloads.ReplayIngest(resolved, recordingId, items, href, width, height),
            cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }
}
