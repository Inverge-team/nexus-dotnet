using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Inverge.Nexus.Internal;

namespace Inverge.Nexus.Models;

/// <summary>A call session — the container every leg belongs to.</summary>
public sealed class VoiceSession
{
    internal VoiceSession(JsonObject node)
    {
        Raw = node;
        Id = node.Str("id");
        Status = node.Str("status");
        Direction = node.Str("direction");
        RecordingMode = node.Str("recordingMode");
        ApplicationId = node.Str("applicationId");
        StartedAt = node.Timestamp("startedAt") ?? node.Timestamp("createdAt");
        Metadata = node.Obj("metadata");
    }

    /// <summary>The session record as sent.</summary>
    public JsonObject Raw { get; }

    /// <summary>The session id, which every leg and control call references.</summary>
    public string? Id { get; }

    /// <summary>The session's lifecycle status.</summary>
    public string? Status { get; }

    /// <summary><c>inbound</c>, <c>outbound</c> or <c>internal</c>.</summary>
    public string? Direction { get; }

    /// <summary><c>disabled</c>, <c>mixed</c>, <c>dual_channel</c> or <c>per_track</c>.</summary>
    public string? RecordingMode { get; }

    /// <summary>The voice application this session belongs to, when one was given.</summary>
    public string? ApplicationId { get; }

    /// <summary>When the session began.</summary>
    public DateTimeOffset? StartedAt { get; }

    /// <summary>Whatever metadata you attached at creation.</summary>
    public JsonObject? Metadata { get; }
}

/// <summary>One participant's leg of a call.</summary>
public sealed class VoiceLeg
{
    internal VoiceLeg(JsonObject node)
    {
        Raw = node;
        Id = node.Str("id");
        SessionId = node.Str("sessionId");
        Role = node.Str("role");
        EndpointType = node.Str("endpointType");
        Direction = node.Str("direction");
        Status = node.Str("status");
        Address = node.Str("address");
        IdentityId = node.Str("identityId");
    }

    /// <summary>The leg record as sent.</summary>
    public JsonObject Raw { get; }

    /// <summary>
    /// The leg id. Every control call — ring, answer, hold, hangup, mute, transfer
    /// — is addressed by this.
    /// </summary>
    public string? Id { get; }

    /// <summary>The session this leg belongs to.</summary>
    public string? SessionId { get; }

    /// <summary><c>caller</c>, <c>callee</c>, <c>agent</c>, <c>supervisor</c>, and so on.</summary>
    public string? Role { get; }

    /// <summary><c>webrtc</c>, <c>sip</c>, <c>pstn</c> or <c>app</c>.</summary>
    public string? EndpointType { get; }

    /// <summary>The leg's direction.</summary>
    public string? Direction { get; }

    /// <summary>The leg's lifecycle status.</summary>
    public string? Status { get; }

    /// <summary>The dialled address, for SIP and PSTN legs.</summary>
    public string? Address { get; }

    /// <summary>The identity this leg rings, for app legs.</summary>
    public string? IdentityId { get; }
}

/// <summary>A short-lived media access token for joining the session's room.</summary>
public sealed class VoiceAccessToken
{
    internal VoiceAccessToken(JsonObject node)
    {
        Raw = node;
        Token = node.Str("token");
        Url = node.Str("url");
        ExpiresAt = node.Timestamp("expiresAt");
    }

    /// <summary>The token record as sent.</summary>
    public JsonObject Raw { get; }

    /// <summary>The signed join token. Scoped to this one leg; do not cache or reuse it.</summary>
    public string? Token { get; }

    /// <summary>The media server URL to join.</summary>
    public string? Url { get; }

    /// <summary>When the token stops working — minutes, not hours.</summary>
    public DateTimeOffset? ExpiresAt { get; }
}

/// <summary>Short-lived TURN relay credentials for the device to use.</summary>
public sealed class VoiceTurnCredential
{
    internal VoiceTurnCredential(JsonObject node)
    {
        Raw = node;
        Username = node.Str("username");
        Credential = node.Str("credential");
        TtlSeconds = node.Int("ttlSec");
        ExpiresAt = node.Timestamp("expiresAt");
        Urls = node.Strings("urls");
    }

    /// <summary>The credential record as sent.</summary>
    public JsonObject Raw { get; }

    /// <summary>The TURN username.</summary>
    public string? Username { get; }

    /// <summary>The TURN credential.</summary>
    public string? Credential { get; }

    /// <summary>How long it is valid for.</summary>
    public int TtlSeconds { get; }

    /// <summary>When it expires.</summary>
    public DateTimeOffset? ExpiresAt { get; }

    /// <summary>
    /// The relay URLs. An <em>empty</em> list means TURN is not configured on the
    /// backend — calls will still connect wherever a direct path exists and fail
    /// behind a symmetric NAT, which is a confusing way to learn about it.
    /// </summary>
    public IReadOnlyList<string> Urls { get; }
}

/// <summary>Everything a WebRTC client needs to join a call.</summary>
public sealed class VoiceJoinBundle
{
    internal VoiceJoinBundle(JsonObject node)
    {
        Raw = node;
        Room = node.Str("room");
        Region = node.Str("region");
        Access = node.Obj("access") is { } access ? new VoiceAccessToken(access) : null;
        Turn = node.Obj("turn") is { } turn ? new VoiceTurnCredential(turn) : null;
    }

    /// <summary>The bundle as sent.</summary>
    public JsonObject Raw { get; }

    /// <summary>The media room name.</summary>
    public string? Room { get; }

    /// <summary>The media region serving the call.</summary>
    public string? Region { get; }

    /// <summary>The access token to join with.</summary>
    public VoiceAccessToken? Access { get; }

    /// <summary>The TURN configuration to use.</summary>
    public VoiceTurnCredential? Turn { get; }
}

/// <summary>A created call session.</summary>
public sealed class VoiceCallResult : NexusResult
{
    private VoiceCallResult(JsonNode? raw) : base(raw)
        => Session = raw.Obj("session") is { } session ? new VoiceSession(session) : null;

    /// <summary>The new session.</summary>
    public VoiceSession? Session { get; }

    /// <summary>The new session's id.</summary>
    public string? SessionId => Session?.Id;

    internal static VoiceCallResult From(JsonNode? raw) => new VoiceCallResult(raw);
}

/// <summary>
/// The outcome of adding a leg to a call.
/// </summary>
/// <remarks>
/// <para>
/// This endpoint reports failure <em>inside a successful response</em>, which is
/// the single most surprising thing about the Voice API. A blocked call, a missing
/// outbound trunk and an unreachable media plane all arrive as 2xx with
/// <see cref="Error"/> set, so that the SDK can end the call cleanly instead of
/// treating a provisioning problem as a crash.
/// </para>
/// <para>
/// Check <see cref="IsSuccess"/>, or call <see cref="EnsureSuccess"/> to turn the
/// in-body error into an exception.
/// </para>
/// </remarks>
public sealed class VoiceLegResult : NexusResult
{
    private VoiceLegResult(JsonNode? raw) : base(raw)
    {
        Leg = raw.Obj("leg") is { } leg ? new VoiceLeg(leg) : null;
        Join = raw.Obj("join") is { } join ? new VoiceJoinBundle(join) : null;
        Dialing = raw.Obj("dialing");
        Error = raw.Str("error");
        Reason = raw.Str("reason");
        RiskScore = raw.Prop("riskScore") is null ? null : raw.Number("riskScore");
    }

    /// <summary>The created leg, absent only when the call was blocked outright.</summary>
    public VoiceLeg? Leg { get; }

    /// <summary>
    /// The join bundle for a <c>webrtc</c> leg. Hand its token and TURN config to
    /// the device SDK.
    /// </summary>
    public VoiceJoinBundle? Join { get; }

    /// <summary>For a <c>pstn</c> or <c>sip</c> leg, the room and number being dialled.</summary>
    public JsonObject? Dialing { get; }

    /// <summary>
    /// The in-body error code, when the leg did not come up:
    /// <c>call_blocked</c> (fraud policy refused it),
    /// <c>no_outbound_trunk</c> (no carrier configured for PSTN),
    /// <c>media_unavailable</c> (the media plane is unconfigured or unreachable).
    /// </summary>
    public string? Error { get; }

    /// <summary>Why a blocked call was blocked.</summary>
    public string? Reason { get; }

    /// <summary>The fraud risk score behind a <c>call_blocked</c> decision.</summary>
    public double? RiskScore { get; }

    /// <summary>Whether the leg is actually usable.</summary>
    public bool IsSuccess => Error is null && Leg is not null;

    /// <summary>Whether the fraud policy refused the destination.</summary>
    public bool IsBlocked => string.Equals(Error, "call_blocked", StringComparison.Ordinal);

    /// <summary>Whether PSTN has no outbound carrier configured.</summary>
    public bool HasNoOutboundTrunk => string.Equals(Error, "no_outbound_trunk", StringComparison.Ordinal);

    /// <summary>Whether the media plane could not provision the room.</summary>
    public bool IsMediaUnavailable => string.Equals(Error, "media_unavailable", StringComparison.Ordinal);

    /// <summary>Returns this result, or throws if the leg did not come up.</summary>
    /// <exception cref="NexusApiException">The response carried an error code.</exception>
    public VoiceLegResult EnsureSuccess()
    {
        if (IsSuccess)
        {
            return this;
        }

        var code = Error ?? "leg_not_created";
        var detail = Reason is null ? string.Empty : " (" + Reason + ")";
        throw new NexusApiException(
            "The voice leg was not established: " + code + detail + ".",
            status: 200,
            code: code,
            details: Raw?.DeepClone(),
            method: "POST",
            path: "/partner/voice/legs");
    }

    internal static VoiceLegResult From(JsonNode? raw) => new VoiceLegResult(raw);
}

/// <summary>One step of an IVR flow, as a directive to act on.</summary>
/// <remarks>
/// Transfers and queues are resolved server-side into a <c>connect</c>, so the
/// client never has to know about queues or agent availability — it either gets
/// somebody to call, or <c>no_agents</c>.
/// </remarks>
public sealed class IvrInstruction
{
    internal IvrInstruction(JsonObject node)
    {
        Raw = node;
        Action = node.Str("action");
        EndpointType = node.Str("endpointType");
        To = node.Str("to");
        Reason = node.Str("reason");
    }

    /// <summary>The instruction as sent, including action-specific fields.</summary>
    public JsonObject Raw { get; }

    /// <summary>
    /// What to do: <c>play</c>, <c>collect</c>, <c>voicemail</c>, <c>hangup</c>,
    /// <c>connect</c>, <c>no_agents</c> or <c>ended</c>.
    /// </summary>
    public string? Action { get; }

    /// <summary>For <c>connect</c>: <c>app</c>, <c>pstn</c> or <c>sip</c>.</summary>
    public string? EndpointType { get; }

    /// <summary>For <c>connect</c>: the address or identity to call.</summary>
    public string? To { get; }

    /// <summary>Why the flow ended, when it did.</summary>
    public string? Reason { get; }

    /// <summary>Whether this instruction is telling you to place a call.</summary>
    public bool IsConnect => string.Equals(Action, "connect", StringComparison.Ordinal);

    /// <summary>Whether the flow is over.</summary>
    public bool IsEnded => string.Equals(Action, "ended", StringComparison.Ordinal);

    /// <summary>Whether a queue resolved to nobody being available.</summary>
    public bool HasNoAgents => string.Equals(Action, "no_agents", StringComparison.Ordinal);
}

/// <summary>An IVR flow's current step.</summary>
public sealed class IvrResult : NexusResult
{
    private IvrResult(JsonNode? raw) : base(raw)
    {
        SessionId = raw.Str("sessionId");
        Instruction = raw.Obj("instruction") is { } instruction ? new IvrInstruction(instruction) : null;
    }

    /// <summary>
    /// The IVR session id, returned when the flow starts.
    /// </summary>
    /// <remarks>
    /// While the caller is still in the menu this is a synthetic id held in Redis —
    /// no call session exists in the database until the flow resolves to a
    /// <c>connect</c>, because only then is there a real call.
    /// </remarks>
    public string? SessionId { get; }

    /// <summary>What to do next.</summary>
    public IvrInstruction? Instruction { get; }

    internal static IvrResult From(JsonNode? raw) => new IvrResult(raw);
}

/// <summary>The result of sending a DTMF digit.</summary>
public sealed class VoiceDtmfResult : NexusResult
{
    private VoiceDtmfResult(JsonNode? raw) : base(raw)
    {
        Ok = raw.Flag("ok", raw is not null);
        Ivr = raw.Obj("ivr") is { } ivr ? new IvrInstruction(ivr) : null;
    }

    /// <summary>Whether the digit was accepted.</summary>
    public bool Ok { get; }

    /// <summary>
    /// The next IVR step, when the session is inside a flow. <c>null</c> both when
    /// there is no IVR and when the flow has just ended.
    /// </summary>
    public IvrInstruction? Ivr { get; }

    internal static VoiceDtmfResult From(JsonNode? raw) => new VoiceDtmfResult(raw);
}

/// <summary>An identity's rolled-up presence after a heartbeat.</summary>
public sealed class VoicePresenceResult : NexusResult
{
    private VoicePresenceResult(JsonNode? raw) : base(raw)
    {
        Ok = raw.Flag("ok", raw is not null);
        Status = raw.Str("status") ?? raw.Obj("status").Str("status");
    }

    /// <summary>Whether the heartbeat was accepted.</summary>
    public bool Ok { get; }

    /// <summary>
    /// The identity's status across all its devices — not just the one that
    /// heartbeat. A person on a call on their phone is <c>IN_CALL</c> even while
    /// their laptop reports <c>ONLINE</c>.
    /// </summary>
    public string? Status { get; }

    internal static VoicePresenceResult From(JsonNode? raw) => new VoicePresenceResult(raw);
}
