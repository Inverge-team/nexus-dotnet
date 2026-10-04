using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Internal;
using Inverge.Nexus.Models;

namespace Inverge.Nexus.Resources;

/// <summary>Product analytics — events correlated to a person and a session.</summary>
/// <remarks>
/// <see cref="Track"/> is buffered by default and shipped on a background loop,
/// so it costs microseconds inside a request handler and never fails it. Call
/// <see cref="FlushAsync"/> (or the client's) before a short-lived process exits.
/// </remarks>
public sealed class EventsResource : NexusResource
{
    /// <summary>The most events the ingest endpoint accepts in one request.</summary>
    public const int MaxBatchSize = 1000;

    internal EventsResource(NexusClient client) : base(client)
    {
    }

    /// <summary>
    /// Captures one event. Returns as soon as it is buffered.
    /// </summary>
    /// <param name="name">The event name, as it appears in the console.</param>
    /// <param name="properties">
    /// Properties — a dictionary, an anonymous object, or a <c>JsonNode</c>. These
    /// layer over the client's <c>DefaultProperties</c> and the ambient scope's
    /// properties, in that order.
    /// </param>
    /// <param name="timestamp">
    /// When it happened. Omit it and the server stamps arrival; set it when you
    /// are replaying events that already happened, or the timeline will be wrong.
    /// </param>
    /// <param name="context">Per-call identity and device overrides.</param>
    /// <example>
    /// <code>
    /// nexus.Events.Track("order_placed", new { total = 42, currency = "USD" });
    /// </code>
    /// </example>
    public void Track(
        string name,
        object? properties = null,
        DateTimeOffset? timestamp = null,
        NexusTelemetryContext? context = null)
    {
        Guard.NotBlank(name, nameof(name));

        var resolved = Resolve(context);
        var item = Payloads.EventItem(
            name,
            MergeProperties(resolved, properties),
            timestamp.HasValue ? JsonHelpers.Iso8601(timestamp.Value) : null);

        Client.EnqueueEvent(item, resolved);
    }

    /// <summary>
    /// Sends many events that share one identity immediately, bypassing the buffer.
    /// </summary>
    /// <returns>How many events the server wrote.</returns>
    /// <param name="events">The events. At most <see cref="MaxBatchSize"/>.</param>
    /// <param name="context">Per-call identity and device overrides.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<int> BatchAsync(
        IEnumerable<NexusEvent> events,
        NexusTelemetryContext? context = null,
        CancellationToken cancellationToken = default)
    {
        if (events is null)
        {
            throw new ArgumentNullException(nameof(events));
        }

        var resolved = Resolve(context);
        var items = new List<JsonObject>();

        foreach (var item in events)
        {
            items.Add(Payloads.EventItem(
                item.Name,
                MergeProperties(resolved, item.Properties),
                item.Timestamp.HasValue ? JsonHelpers.Iso8601(item.Timestamp.Value) : null));
        }

        if (items.Count == 0)
        {
            return 0;
        }

        Guard.AtMost(items.Count, MaxBatchSize, "events", nameof(events));

        var response = await SendAsync(
            Payloads.EventsBatch(items, resolved.ToWire()), cancellationToken).ConfigureAwait(false);

        return response is null ? 0 : response.Int("written", items.Count);
    }

    /// <summary>Ships every buffered event right now.</summary>
    public Task FlushAsync(CancellationToken cancellationToken = default)
        => Client.FlushEventsAsync(cancellationToken);

    private JsonObject? MergeProperties(NexusTelemetryContext resolved, object? properties)
    {
        var merged = new JsonObject();

        JsonHelpers.MergeInto(merged, Client.Options.DefaultProperties);
        JsonHelpers.MergeInto(merged, resolved.Properties);
        JsonHelpers.MergeInto(merged, properties);

        return merged.Count == 0 ? null : merged;
    }
}
