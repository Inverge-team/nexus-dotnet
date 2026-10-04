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
