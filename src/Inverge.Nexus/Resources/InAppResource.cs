using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Internal;
using Inverge.Nexus.Models;

namespace Inverge.Nexus.Resources;

/// <summary>In-app messages — banners and modals composed in the console.</summary>
/// <remarks>
/// The device SDKs render these. Server-side you fetch what is active (for a
/// headless or server-driven UI) and record impressions, clicks and dismissals so
/// the console's numbers mean something.
/// </remarks>
public sealed class InAppResource : NexusResource
{
    internal InAppResource(NexusClient client) : base(client)
    {
    }

    /// <summary>The environment's active in-app messages.</summary>
    public async Task<IReadOnlyList<JsonNode>> ActiveAsync(
        NexusTelemetryContext? context = null, CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(context);
        var response = await SendAsync(
            Payloads.InAppActive(resolved.DistinctId, resolved.DeviceKey),
            cancellationToken).ConfigureAwait(false);

        return response.Items("messages");
    }

    /// <summary>Records an <c>impression</c>, <c>click</c> or <c>dismiss</c>.</summary>
    public async Task<NexusAck> TrackEventAsync(
        string messageId,
        string type,
        string? buttonId = null,
        NexusTelemetryContext? context = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(messageId, nameof(messageId));
        Guard.NotBlank(type, nameof(type));

        var resolved = Resolve(context);
        var response = await SendAsync(
            Payloads.InAppEvent(messageId, type, buttonId, resolved.DistinctId),
            cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }

    /// <summary>Records that the message was shown.</summary>
    public Task<NexusAck> ImpressionAsync(
        string messageId, NexusTelemetryContext? context = null, CancellationToken cancellationToken = default)
        => TrackEventAsync(messageId, "impression", null, context, cancellationToken);

    /// <summary>Records that one of the message's buttons was tapped.</summary>
    public Task<NexusAck> ClickAsync(
        string messageId,
        string buttonId,
        NexusTelemetryContext? context = null,
        CancellationToken cancellationToken = default)
        => TrackEventAsync(messageId, "click", buttonId, context, cancellationToken);

    /// <summary>Records that the message was dismissed.</summary>
    public Task<NexusAck> DismissAsync(
        string messageId, NexusTelemetryContext? context = null, CancellationToken cancellationToken = default)
        => TrackEventAsync(messageId, "dismiss", null, context, cancellationToken);
}
