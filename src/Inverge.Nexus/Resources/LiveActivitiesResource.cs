using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Builders;
using Inverge.Nexus.Internal;
using Inverge.Nexus.Models;

namespace Inverge.Nexus.Resources;

/// <summary>Live Activities — a live, updating view of something in progress.</summary>
/// <remarks>
/// <para>
/// Templated (recommended): design the activity once in the Nexus console; the
/// mobile SDKs render it on iOS and Android in each device's language, and the
/// server sends only data — <see cref="Template"/>, <see cref="StartTemplateAsync"/>,
/// <see cref="UpdateDataAsync"/>, <see cref="EndWithDataAsync"/>.
/// </para>
/// iOS renders these on the Lock Screen and in the Dynamic Island; Android as an
/// ongoing live notification. The server-side shape is always the same: start when
/// the order or ride or match begins, update as it changes, end when it is done.
/// iOS additionally needs the APNs key configured in the console — Live Activities
/// cannot go through FCM — and a Widget Extension in the app.
/// </remarks>
public sealed class LiveActivitiesResource : NexusResource
{
    internal LiveActivitiesResource(NexusClient client) : base(client)
    {
    }

    /// <summary>
    /// Starts a fluent activity. Pass the type and id to start one; pass just the id
    /// to update or end an existing one.
    /// </summary>
    public LiveActivityBuilder Activity(string activityTypeOrId, string? activityId = null)
    {
        Guard.NotBlank(activityTypeOrId, nameof(activityTypeOrId));

        return activityId is null
            ? new LiveActivityBuilder(this, activityTypeOrId, null)
            : new LiveActivityBuilder(this, activityId, activityTypeOrId);
    }

    /// <summary>
    /// Starts a fluent templated activity. <paramref name="template"/> is a template
    /// key from the Nexus console (e.g. <c>"order_tracking"</c>) or an inline design object.
    /// </summary>
    public LiveActivityBuilder Template(object template, string activityId)
    {
        if (template is null)
        {
            throw new ArgumentNullException(nameof(template));
        }

        if (template is string key)
        {
            Guard.NotBlank(key, nameof(template));
        }

        Guard.NotBlank(activityId, nameof(activityId));
        return new LiveActivityBuilder(this, activityId, null, template);
    }

    /// <summary>Starts a templated activity.</summary>
    /// <param name="template">A console template key (string) or an inline design object.</param>
    /// <param name="activityId">Your id for this instance. Update and end address it.</param>
    /// <param name="data">
    /// The template's data: <c>step</c>, <c>state</c>, <c>progress</c> (0–1),
    /// <c>etaAt</c> (unix seconds) and <c>values</c> for its placeholders.
    /// </param>
    /// <param name="distinctIds">Who should see it. Omit for a shared activity.</param>
    /// <param name="lang">Force one language for everyone. Default: each device's app language.</param>
    /// <param name="alertTitle">The iOS alert shown with the start. Default: the template's texts.</param>
    /// <param name="alertBody">The alert's body.</param>
    /// <param name="attributes">Extra fixed attributes.</param>
    /// <param name="staleDate">When the activity should be shown as stale.</param>
    /// <param name="priority">APNs priority: 5 routine, 10 immediate.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<LiveActivityResult> StartTemplateAsync(
        object template,
        string activityId,
        object? data = null,
        IEnumerable<string>? distinctIds = null,
        string? lang = null,
        string? alertTitle = null,
        string? alertBody = null,
        object? attributes = null,
        DateTimeOffset? staleDate = null,
        int? priority = null,
        CancellationToken cancellationToken = default)
    {
        if (template is null)
        {
            throw new ArgumentNullException(nameof(template));
        }

        Guard.NotBlank(activityId, nameof(activityId));

        var response = await SendAsync(
            Payloads.LiveActivityStartTemplate(
                template,
                activityId,
                data,
                distinctIds,
                lang,
                alertTitle,
                alertBody,
                attributes,
                staleDate.HasValue ? JsonHelpers.Iso8601(staleDate.Value) : null,
                priority),
            cancellationToken).ConfigureAwait(false);

        return LiveActivityResult.From(response);
    }

    /// <summary>
    /// Updates a templated activity with partial data — only what changed. The
    /// server merges it into the activity's current data; a null inside clears a field.
    /// </summary>
    public async Task<LiveActivityResult> UpdateDataAsync(
        string activityId,
        object data,
        DateTimeOffset? staleDate = null,
        int? priority = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(activityId, nameof(activityId));

        var response = await SendAsync(
            Payloads.LiveActivityUpdateData(
                activityId,
                data,
                staleDate.HasValue ? JsonHelpers.Iso8601(staleDate.Value) : null,
                priority),
            cancellationToken).ConfigureAwait(false);

        return LiveActivityResult.From(response);
    }

    /// <summary>
    /// Ends a templated activity with its final data shown before dismissal —
    /// e.g. <c>new { step = "completed" }</c> or <c>new { state = "canceled" }</c>.
    /// </summary>
    public async Task<LiveActivityResult> EndWithDataAsync(
        string activityId,
        object? data = null,
        DateTimeOffset? dismissalDate = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(activityId, nameof(activityId));

        var response = await SendAsync(
            Payloads.LiveActivityEnd(
                activityId,
                null,
                dismissalDate.HasValue ? JsonHelpers.Iso8601(dismissalDate.Value) : null,
                data),
            cancellationToken).ConfigureAwait(false);

        return LiveActivityResult.From(response);
    }

    /// <summary>Starts an activity.</summary>
    /// <param name="activityType">The ActivityKit attributes type, or the Android channel type.</param>
    /// <param name="activityId">Your id for this instance. Update and end address it.</param>
    /// <param name="contentState">The initial content state.</param>
    /// <param name="distinctIds">Who should see it. Omit for a shared activity.</param>
    /// <param name="attributes">Fixed attributes, set once at start.</param>
    /// <param name="staleDate">When the activity should be shown as stale.</param>
    /// <param name="priority">APNs priority: 5 routine, 10 immediate.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<LiveActivityResult> StartAsync(
        string activityType,
        string activityId,
        object? contentState = null,
        IEnumerable<string>? distinctIds = null,
        object? attributes = null,
        DateTimeOffset? staleDate = null,
        int? priority = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(activityType, nameof(activityType));
        Guard.NotBlank(activityId, nameof(activityId));

        var response = await SendAsync(
            Payloads.LiveActivityStart(
                activityType,
                activityId,
                contentState,
                distinctIds,
                attributes,
                staleDate.HasValue ? JsonHelpers.Iso8601(staleDate.Value) : null,
                priority),
            cancellationToken).ConfigureAwait(false);

        return LiveActivityResult.From(response);
    }

    /// <summary>Pushes a new content state to every device showing the activity.</summary>
    public async Task<LiveActivityResult> UpdateAsync(
        string activityId,
        object? contentState = null,
        DateTimeOffset? staleDate = null,
        int? priority = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(activityId, nameof(activityId));

        var response = await SendAsync(
            Payloads.LiveActivityUpdate(
                activityId,
                contentState,
                staleDate.HasValue ? JsonHelpers.Iso8601(staleDate.Value) : null,
                priority),
            cancellationToken).ConfigureAwait(false);

        return LiveActivityResult.From(response);
    }

    /// <summary>Ends the activity, with an optional final state and dismissal time.</summary>
    public async Task<LiveActivityResult> EndAsync(
        string activityId,
        object? contentState = null,
        DateTimeOffset? dismissalDate = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(activityId, nameof(activityId));

        var response = await SendAsync(
            Payloads.LiveActivityEnd(
                activityId,
                contentState,
                dismissalDate.HasValue ? JsonHelpers.Iso8601(dismissalDate.Value) : null),
            cancellationToken).ConfigureAwait(false);

        return LiveActivityResult.From(response);
    }

    /// <summary>Registers an iOS push-to-start token. Normally the device SDK's job.</summary>
    public async Task<NexusAck> RegisterPushToStartTokenAsync(
        string activityType,
        string token,
        NexusTelemetryContext? context = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(activityType, nameof(activityType));
        Guard.NotBlank(token, nameof(token));

        var resolved = Resolve(context);
        var response = await SendAsync(
            Payloads.LiveActivityPushToken(activityType, token, resolved.DistinctId, resolved.DeviceKey),
            cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }

    /// <summary>Registers a running activity instance's update token.</summary>
    public async Task<NexusAck> RegisterActivityAsync(
        string activityId,
        string activityType,
        string platform = "ios",
        string? updateToken = null,
        object? contentState = null,
        NexusTelemetryContext? context = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(activityId, nameof(activityId));
        Guard.NotBlank(activityType, nameof(activityType));

        var resolved = Resolve(context);
        var response = await SendAsync(
            Payloads.LiveActivityRegister(
                activityId, activityType, platform, updateToken, resolved.DistinctId, contentState),
            cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }

    /// <summary>Reports a delivery receipt, click or failure, for analytics.</summary>
    public async Task<NexusAck> TrackEventAsync(
        string activityId,
        string type,
        string? platform = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(activityId, nameof(activityId));
        Guard.NotBlank(type, nameof(type));

        var response = await SendAsync(
            Payloads.LiveActivityEvent(activityId, type, platform), cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }
}
