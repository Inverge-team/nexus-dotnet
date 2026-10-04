using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Builders;
using Inverge.Nexus.Internal;
using Inverge.Nexus.Models;

namespace Inverge.Nexus.Resources;

/// <summary>Push notifications — transactional sends to people, segments or filters.</summary>
/// <remarks>
/// Campaigns, provider credentials, segments and templates live in the console.
/// This resource is for the one-off, event-driven sends your backend makes:
/// "your order shipped", "someone replied to you".
/// </remarks>
public sealed class PushResource : NexusResource
{
    private static readonly string[] Platforms = { "ios", "android", "web" };

    internal PushResource(NexusClient client) : base(client)
    {
    }

    /// <summary>Starts a fluent notification — the recommended way to send.</summary>
    public PushNotificationBuilder Notification() => new PushNotificationBuilder(SendRawAsync);

    /// <summary>Sends to specific people, across every device each has registered.</summary>
    /// <param name="distinctIds">Who to reach.</param>
    /// <param name="title">The title. Required.</param>
    /// <param name="body">The body.</param>
    /// <param name="imageUrl">A large image for the expanded notification.</param>
    /// <param name="data">Custom key/values delivered to the app.</param>
    /// <param name="options">Per-platform options.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public Task<PushSendResult> SendAsync(
        IEnumerable<string> distinctIds,
        string title,
        string? body = null,
        string? imageUrl = null,
        object? data = null,
        object? options = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(title, nameof(title));

        var builder = Notification().ToUsers(distinctIds).Title(title);

        if (body is not null)
        {
            builder.Body(body);
        }

        if (imageUrl is not null)
        {
            builder.Image(imageUrl);
        }

        if (data is not null)
        {
            builder.Data(data);
        }

        if (options is not null)
        {
            builder.Options(options);
        }

        return builder.SendAsync(cancellationToken);
    }

    /// <summary>Sends to one person.</summary>
    public Task<PushSendResult> SendToUserAsync(
        string distinctId,
        string title,
        string? body = null,
        string? imageUrl = null,
        object? data = null,
        object? options = null,
        CancellationToken cancellationToken = default)
        => SendAsync(new[] { distinctId }, title, body, imageUrl, data, options, cancellationToken);

    /// <summary>Sends to the union of saved segments.</summary>
    public Task<PushSendResult> SendToSegmentsAsync(
        IEnumerable<string> segmentIds,
        string title,
        string? body = null,
        object? data = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(title, nameof(title));

        var builder = Notification().ToSegments(segmentIds).Title(title);

        if (body is not null)
        {
            builder.Body(body);
        }

        if (data is not null)
        {
            builder.Data(data);
        }

        return builder.SendAsync(cancellationToken);
    }

    /// <summary>
    /// Sends a fully assembled payload — what the builder calls, and the escape
    /// hatch for a payload shape newer than this SDK.
    /// </summary>
    public async Task<PushSendResult> SendRawAsync(
        JsonObject payload, CancellationToken cancellationToken = default)
    {
        if (payload is null)
        {
            throw new ArgumentNullException(nameof(payload));
        }

        var response = await SendAsync(Payloads.PushSend(payload), cancellationToken).ConfigureAwait(false);
        return PushSendResult.From(response);
    }

    /// <summary>
    /// Registers or refreshes a device token.
    /// </summary>
    /// <remarks>
    /// The device SDKs normally do this themselves. Use it for server-driven and
    /// bring-your-own-token flows.
    /// </remarks>
    /// <param name="token">The provider token.</param>
    /// <param name="platform"><c>ios</c>, <c>android</c> or <c>web</c>.</param>
    /// <param name="provider">
    /// The delivery provider. Defaults to <c>webpush</c> for web and <c>fcm</c>
    /// otherwise.
    /// </param>
    /// <param name="lang">The device's language, for localised sends.</param>
    /// <param name="tags">Tags the console's segments can target.</param>
    /// <param name="context">Per-call identity and device overrides.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<NexusAck> RegisterTokenAsync(
        string token,
        string platform,
        string? provider = null,
        string? lang = null,
        IReadOnlyDictionary<string, string>? tags = null,
        NexusTelemetryContext? context = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(token, nameof(token));
        Guard.OneOf(platform, Platforms, nameof(platform));

        var resolved = Resolve(context);
        var effectiveProvider = provider
            ?? (string.Equals(platform, "web", StringComparison.Ordinal) ? "webpush" : "fcm");

        var response = await SendAsync(
            Payloads.PushRegister(resolved, token, platform, effectiveProvider, lang, tags),
            cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }

    /// <summary>Stops delivering to a token — on logout or uninstall.</summary>
    public async Task<NexusAck> UnregisterAsync(string token, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(token, nameof(token));

        var response = await SendAsync(
            Payloads.PushUnregister(token), cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }

    /// <summary>
    /// Records that a notification was opened, which is what makes the console's
    /// open rates real.
    /// </summary>
    /// <param name="campaignId">The campaign the notification belonged to.</param>
    /// <param name="token">The device token that received it.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<NexusAck> OpenedAsync(
        string campaignId, string token, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(campaignId, nameof(campaignId));
        Guard.NotBlank(token, nameof(token));

        var response = await SendAsync(
            Payloads.PushOpened(campaignId, token), cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }
}
