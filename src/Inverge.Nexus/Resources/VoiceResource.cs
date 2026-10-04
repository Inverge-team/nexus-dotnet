using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Internal;
using Inverge.Nexus.Models;

namespace Inverge.Nexus.Resources;

/// <summary>Nexus Voice (CPaaS) — the call control plane.</summary>
/// <remarks>
/// <para>
/// A call is a <strong>session</strong> with one or more <strong>legs</strong>.
/// Create the session, add a leg per participant, then drive it: ring, answer,
/// hold, hangup, DTMF, transfer, conference moderation. A <c>webrtc</c> leg comes
/// back with a join bundle for the device to connect with; <c>pstn</c> and
/// <c>sip</c> legs are dialled by the media plane.
/// </para>
/// <para>
/// The tenant is always the environment behind your API key — never passed in a
/// body — and grants, rooms and caller ID are all decided server-side.
/// </para>
/// </remarks>
public sealed class VoiceResource : NexusResource
{
    /// <summary>Valid leg roles.</summary>
    public static readonly string[] Roles =
        { "caller", "callee", "agent", "supervisor", "conference", "ivr", "announcement" };

    /// <summary>Valid endpoint types.</summary>
    public static readonly string[] EndpointTypes = { "webrtc", "sip", "pstn", "app" };

    /// <summary>Valid call directions.</summary>
    public static readonly string[] Directions = { "inbound", "outbound", "internal" };

    /// <summary>Valid recording modes.</summary>
    public static readonly string[] RecordingModes = { "disabled", "mixed", "dual_channel", "per_track" };

    /// <summary>Valid presence statuses.</summary>
    public static readonly string[] PresenceStatuses =
        { "ONLINE", "OFFLINE", "BUSY", "AWAY", "DND", "RINGING", "IN_CALL" };

    private static readonly string[] TargetTypes = { "webrtc", "sip", "pstn", "app" };
    private static readonly string[] DevicePlatforms = { "ios", "android", "other" };

    internal VoiceResource(NexusClient client) : base(client)
    {
    }

    /// <summary>Creates a call session.</summary>
    /// <param name="direction"><c>inbound</c>, <c>outbound</c> or <c>internal</c>.</param>
    /// <param name="applicationId">The voice application this call belongs to.</param>
    /// <param name="proxyIdentityId">
    /// Route through a masking identity, so neither party sees the other's real
    /// number.
    /// </param>
    /// <param name="recordingMode">
    /// <c>disabled</c>, <c>mixed</c>, <c>dual_channel</c> or <c>per_track</c>.
    /// Recording starts only once the call is answered.
    /// </param>
    /// <param name="metadata">Anything you want attached to the session.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<VoiceCallResult> CreateCallAsync(
        string? direction = null,
        string? applicationId = null,
        string? proxyIdentityId = null,
        string? recordingMode = null,
        object? metadata = null,
        CancellationToken cancellationToken = default)
    {
        if (direction is not null)
        {
            Guard.OneOf(direction, Directions, nameof(direction));
        }

        if (recordingMode is not null)
        {
            Guard.OneOf(recordingMode, RecordingModes, nameof(recordingMode));
        }

        var response = await SendAsync(
            Payloads.VoiceCreateCall(direction, applicationId, proxyIdentityId, recordingMode, metadata),
            cancellationToken).ConfigureAwait(false);

        return VoiceCallResult.From(response);
    }

    /// <summary>
    /// Adds a participant leg to a session.
    /// </summary>
    /// <remarks>
    /// Read <see cref="VoiceLegResult"/> before using this: the endpoint reports a
    /// blocked call, a missing carrier and an unreachable media plane inside a
    /// <em>successful</em> response, so a 2xx does not mean the leg is live. Check
    /// <see cref="VoiceLegResult.IsSuccess"/> or call
    /// <see cref="VoiceLegResult.EnsureSuccess"/>.
    /// </remarks>
    /// <param name="sessionId">The session to add to.</param>
    /// <param name="role">The participant's role. One of <see cref="Roles"/>.</param>
    /// <param name="endpointType">How they connect. One of <see cref="EndpointTypes"/>.</param>
    /// <param name="direction">The leg's direction. Defaults to <c>internal</c> server-side.</param>
    /// <param name="address">The number or SIP URI to dial, for <c>pstn</c> and <c>sip</c> legs.</param>
    /// <param name="callerId">The caller ID to present on an outbound PSTN/SIP leg.</param>
    /// <param name="identityId">The identity to ring, for <c>app</c> legs.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<VoiceLegResult> AddLegAsync(
        string sessionId,
        string role,
        string endpointType,
        string? direction = null,
        string? address = null,
        string? callerId = null,
        string? identityId = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(sessionId, nameof(sessionId));
        Guard.OneOf(role, Roles, nameof(role));
        Guard.OneOf(endpointType, EndpointTypes, nameof(endpointType));

        if (direction is not null)
        {
            Guard.OneOf(direction, Directions, nameof(direction));
        }

        var response = await SendAsync(
            Payloads.VoiceAddLeg(sessionId, role, endpointType, direction, address, callerId, identityId),
            cancellationToken).ConfigureAwait(false);

        return VoiceLegResult.From(response);
    }

    /// <summary>Starts ringing a leg.</summary>
    public Task<NexusAck> RingAsync(string legId, CancellationToken cancellationToken = default)
        => LegActionAsync("ring", legId, null, cancellationToken);

    /// <summary>Marks a leg answered.</summary>
    public Task<NexusAck> AnswerAsync(string legId, CancellationToken cancellationToken = default)
        => LegActionAsync("answer", legId, null, cancellationToken);

    /// <summary>Puts a leg on hold.</summary>
    public Task<NexusAck> HoldAsync(string legId, CancellationToken cancellationToken = default)
        => LegActionAsync("hold", legId, null, cancellationToken);

    /// <summary>Takes a leg off hold.</summary>
    public Task<NexusAck> ResumeAsync(string legId, CancellationToken cancellationToken = default)
        => LegActionAsync("resume", legId, null, cancellationToken);

    /// <summary>Ends a leg.</summary>
    /// <param name="legId">The leg to end.</param>
    /// <param name="reason">
    /// Why it ended. This is what distinguishes a missed call from a declined one
    /// and from a normal hangup in the console, so a meaningful value is worth it.
    /// </param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public Task<NexusAck> HangupAsync(
        string legId, string reason = "hangup", CancellationToken cancellationToken = default)
        => LegActionAsync("hangup", legId, reason, cancellationToken);

    /// <summary>
    /// Sends a DTMF digit.
    /// </summary>
    /// <remarks>
    /// When the session is inside an IVR, the result's
    /// <see cref="VoiceDtmfResult.Ivr"/> carries the next instruction — there is no
    /// need to poll the IVR separately after a keypress.
    /// </remarks>
    public async Task<VoiceDtmfResult> SendDigitAsync(
        string sessionId,
        string legId,
        string digit,
        int? durationMs = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(sessionId, nameof(sessionId));
        Guard.NotBlank(legId, nameof(legId));
        Guard.NotBlank(digit, nameof(digit));

        var response = await SendAsync(
            Payloads.VoiceDtmf(sessionId, legId, digit, durationMs), cancellationToken).ConfigureAwait(false);

        return VoiceDtmfResult.From(response);
    }

    /// <summary>
    /// Starts an in-app IVR flow.
    /// </summary>
    /// <remarks>
    /// The menu phase costs nothing: no call session exists until the flow resolves
    /// to a <c>connect</c>, because only then is there a call to bill.
    /// </remarks>
    public async Task<IvrResult> StartIvrAsync(
        string flowId, string? applicationId = null, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(flowId, nameof(flowId));

        var response = await SendAsync(
            Payloads.VoiceIvrStart(flowId, applicationId), cancellationToken).ConfigureAwait(false);

        return IvrResult.From(response);
    }

    /// <summary>Advances an IVR flow — after a prompt finished playing, say.</summary>
    public async Task<IvrResult> AdvanceIvrAsync(
        string sessionId, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(sessionId, nameof(sessionId));

        var response = await SendAsync(
            Payloads.VoiceIvrAdvance(sessionId), cancellationToken).ConfigureAwait(false);

        return IvrResult.From(response);
    }

    /// <summary>Feeds a menu keypress into an IVR flow.</summary>
    public async Task<IvrResult> SendIvrInputAsync(
        string sessionId, string digit, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(sessionId, nameof(sessionId));
        Guard.NotBlank(digit, nameof(digit));

        var response = await SendAsync(
            Payloads.VoiceIvrInput(sessionId, digit), cancellationToken).ConfigureAwait(false);

        return IvrResult.From(response);
    }

    /// <summary>Registers a device so incoming calls can ring it.</summary>
    /// <param name="identityId">
    /// The identity this device belongs to. Calls ring the identity, so this must
    /// match what the caller dials — after a login, re-register under the logged-in
    /// identity or calls will ring an identity nobody is listening on.
    /// </param>
    /// <param name="deviceId">A stable id for the device.</param>
    /// <param name="platform"><c>ios</c>, <c>android</c> or <c>other</c>.</param>
    /// <param name="voipToken">The APNs VoIP token, for iOS.</param>
    /// <param name="fcmToken">The FCM token, for Android.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<NexusAck> RegisterDeviceAsync(
        string identityId,
        string deviceId,
        string platform,
        string? voipToken = null,
        string? fcmToken = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(identityId, nameof(identityId));
        Guard.NotBlank(deviceId, nameof(deviceId));
        Guard.OneOf(platform, DevicePlatforms, nameof(platform));

        var response = await SendAsync(
            Payloads.VoiceRegisterDevice(identityId, deviceId, platform, voipToken, fcmToken),
            cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }

    /// <summary>Heartbeats a device's presence, and reads back the identity's rolled-up status.</summary>
    public async Task<VoicePresenceResult> PresenceAsync(
        string identityId,
        string deviceId,
        string status,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(identityId, nameof(identityId));
        Guard.NotBlank(deviceId, nameof(deviceId));
        Guard.OneOf(status, PresenceStatuses, nameof(status));

        var response = await SendAsync(
            Payloads.VoicePresence(identityId, deviceId, status), cancellationToken).ConfigureAwait(false);

        return VoicePresenceResult.From(response);
    }

    /// <summary>Reports per-leg media quality, which feeds the console's voice analytics.</summary>
    /// <param name="sessionId">The session being measured.</param>
    /// <param name="legId">The leg being measured.</param>
    /// <param name="rttMs">Round-trip time in milliseconds.</param>
    /// <param name="jitterMs">Jitter in milliseconds.</param>
    /// <param name="packetLoss">Packet loss as a fraction.</param>
    /// <param name="bitrateKbps">Bitrate in kbps.</param>
    /// <param name="mos">Mean opinion score.</param>
    /// <param name="codec">The negotiated codec.</param>
    /// <param name="candidateType">The ICE candidate type in use — <c>relay</c> means TURN.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<NexusAck> ReportQualityAsync(
        string sessionId,
        string? legId = null,
        int? rttMs = null,
        int? jitterMs = null,
        double? packetLoss = null,
        int? bitrateKbps = null,
        double? mos = null,
        string? codec = null,
        string? candidateType = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(sessionId, nameof(sessionId));

        var response = await SendAsync(
            Payloads.VoiceQuality(
                sessionId, legId, rttMs, jitterMs, packetLoss, bitrateKbps, mos, codec, candidateType),
            cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }

    /// <summary>Hands the call to someone else and drops the transferring leg immediately.</summary>
    public async Task<NexusAck> TransferBlindAsync(
        string sessionId,
        string legId,
        string targetType,
        string? targetAddress = null,
        string? targetIdentityId = null,
        string? callerId = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(sessionId, nameof(sessionId));
        Guard.NotBlank(legId, nameof(legId));
        Guard.OneOf(targetType, TargetTypes, nameof(targetType));

        var response = await SendAsync(
            Payloads.VoiceTransferBlind(
                sessionId, legId, targetType, targetAddress, targetIdentityId, callerId),
            cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }

    /// <summary>
    /// Starts a consult leg while the other party waits on hold.
    /// </summary>
    /// <remarks>
    /// Finish with <see cref="TransferWarmCompleteAsync"/> to connect the two, or
    /// <see cref="TransferWarmCancelAsync"/> to abandon the consult and go back to
    /// the original party. Leaving a warm transfer unfinished leaves them on hold.
    /// </remarks>
    public async Task<NexusAck> TransferWarmStartAsync(
        string sessionId,
        string otherPartyLegId,
        string targetType,
        string? targetAddress = null,
        string? targetIdentityId = null,
        string? callerId = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(sessionId, nameof(sessionId));
        Guard.NotBlank(otherPartyLegId, nameof(otherPartyLegId));
        Guard.OneOf(targetType, TargetTypes, nameof(targetType));

        var response = await SendAsync(
            Payloads.VoiceTransferWarmStart(
                sessionId, otherPartyLegId, targetType, targetAddress, targetIdentityId, callerId),
            cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }

    /// <summary>Connects the two parties and drops the agent who initiated the transfer.</summary>
    public async Task<NexusAck> TransferWarmCompleteAsync(
        string otherPartyLegId, string legId, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(otherPartyLegId, nameof(otherPartyLegId));
        Guard.NotBlank(legId, nameof(legId));

        var response = await SendAsync(
            Payloads.VoiceTransferWarmFinish("complete", otherPartyLegId, legId),
            cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }

    /// <summary>Abandons the consult and returns to the original party.</summary>
    public async Task<NexusAck> TransferWarmCancelAsync(
        string otherPartyLegId, string consultLegId, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(otherPartyLegId, nameof(otherPartyLegId));
        Guard.NotBlank(consultLegId, nameof(consultLegId));

        var response = await SendAsync(
            Payloads.VoiceTransferWarmFinish("cancel", otherPartyLegId, consultLegId),
            cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }

    /// <summary>Mutes or unmutes a conference participant.</summary>
    public async Task<NexusAck> MuteAsync(
        string legId, bool muted = true, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(legId, nameof(legId));

        var response = await SendAsync(
            Payloads.VoiceConferenceMute(legId, muted), cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }

    /// <summary>Removes a participant from a conference.</summary>
    public async Task<NexusAck> RemoveParticipantAsync(
        string sessionId, string legId, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(sessionId, nameof(sessionId));
        Guard.NotBlank(legId, nameof(legId));

        var response = await SendAsync(
            Payloads.VoiceConferenceRemove(sessionId, legId), cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }

    /// <summary>
    /// Builds the fetchable URL for a recording access token.
    /// </summary>
    /// <remarks>
    /// Recordings have no permanent URL on purpose. The console mints a short-lived
    /// token bound to one recording and its expiry; this turns that token into a
    /// link. Treat the result as a secret with a short life — do not store it, and
    /// do not put it anywhere a log will keep it.
    /// </remarks>
    /// <param name="accessToken">The minted access token.</param>
    /// <param name="inline">
    /// Serve inline for playback in a browser rather than as a download.
    /// </param>
    public string RecordingDownloadUrl(string accessToken, bool inline = false)
    {
        Guard.NotBlank(accessToken, nameof(accessToken));

        var query = "token=" + Uri.EscapeDataString(accessToken);
        if (inline)
        {
            query += "&inline=1";
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "{0}/partner/voice/recordings/download?{1}",
            Client.Options.HttpBase,
            query);
    }

    private async Task<NexusAck> LegActionAsync(
        string action, string legId, string? reason, CancellationToken cancellationToken)
    {
        Guard.NotBlank(legId, nameof(legId));

        var response = await SendAsync(
            Payloads.VoiceLegAction(action, legId, reason), cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }
}
