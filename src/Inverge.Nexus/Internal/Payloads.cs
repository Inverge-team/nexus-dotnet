using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Inverge.Nexus.Internal;

/// <summary>
/// Every partner-API path and wire field name, in one place.
/// </summary>
/// <remarks>
/// <para>
/// This class is pure: it builds a <see cref="NexusRequest"/> and never performs
/// I/O. Resources call into it, so the HTTP contract lives here rather than
/// scattered across fifteen resource classes — when an endpoint changes, it
/// changes once.
/// </para>
/// <para>
/// The <c>retryable</c> flag is set per endpoint, not per verb. Ingest and reads
/// are safe to repeat. A push send, a voice leg, a realtime emit and a survey
/// response are not: repeating one after a timeout would double something a
/// person can see.
/// </para>
/// </remarks>
internal static class Payloads
{
    /// <summary>Context fields every ingest endpoint accepts.</summary>
    private static readonly string[] IngestContextFields =
    {
        "release", "distinctId", "sessionKey", "deviceKey", "osType", "osVersion", "browser", "appVersion",
    };

    private static string Seg(string value) => Uri.EscapeDataString(value ?? string.Empty);

    /// <summary>Copies only the fields an ingest endpoint reads from a context body.</summary>
    private static JsonBody IngestContext(JsonObject context)
    {
        var body = JsonBody.Create();
        foreach (var field in IngestContextFields)
        {
            body.SetNode(field, context.Prop(field)?.DeepClone());
        }

        return body;
    }

    // ── Realtime ───────────────────────────────────────────────────────────

    public static NexusRequest RealtimeEmit(string room, IEnumerable<string> events, object? payload)
    {
        var body = JsonBody.Create().SetStrings("events", events).Build();

        // The payload is forwarded verbatim to subscribers and may legitimately
        // be null, a scalar, or an array — so it is written unconditionally,
        // unlike the null-dropping fields everywhere else.
        body["payload"] = JsonHelpers.ToNode(payload);

        return new NexusRequest("POST", "/partner/rooms/" + Seg(room) + "/emit", body);
    }

    public static NexusRequest RealtimeBroadcast(IEnumerable<RoomMessage> messages)
    {
        var rooms = new JsonArray();
        foreach (var message in messages)
        {
            rooms.Add((JsonNode?)message.ToJson());
        }

        var body = new JsonObject { ["rooms"] = rooms };
        return new NexusRequest("POST", "/partner/rooms/emit", body);
    }

    public static NexusRequest RealtimeRegisterRoom(string name, string? type)
        => new NexusRequest(
            "POST",
            "/partner/rooms",
            JsonBody.Create().Set("name", name).Set("type", type).Build());

    public static NexusRequest RealtimeListRooms()
        => new NexusRequest("GET", "/partner/rooms", retryable: true);

    public static NexusRequest RealtimeDeleteRoom(string roomId)
        => new NexusRequest("DELETE", "/partner/rooms/" + Seg(roomId));

    public static NexusRequest RealtimeLink(string roomId, string relatedId)
        => new NexusRequest("POST", "/partner/rooms/" + Seg(roomId) + "/link/" + Seg(relatedId));

    public static NexusRequest RealtimeUnlink(string roomId, string relatedId)
        => new NexusRequest("POST", "/partner/rooms/" + Seg(roomId) + "/unlink/" + Seg(relatedId));

    public static NexusRequest RealtimeGetSchema(string roomId)
        => new NexusRequest("GET", "/partner/rooms/" + Seg(roomId) + "/schema", retryable: true);

    public static NexusRequest RealtimeSetSchema(string roomId, object schema)
        => new NexusRequest(
            "POST",
            "/partner/rooms/" + Seg(roomId) + "/schema",
            JsonBody.Create().SetObjectAlways("schema", schema).Build());

    public static NexusRequest RealtimeEnableSchema(string roomId, bool enabled)
        => new NexusRequest(
            "POST",
            "/partner/rooms/" + Seg(roomId) + "/schema/enable",
            JsonBody.Create().Set("enabled", enabled).Build());

    public static NexusRequest RealtimeClearSchema(string roomId)
        => new NexusRequest("DELETE", "/partner/rooms/" + Seg(roomId) + "/schema");

    public static NexusRequest RealtimeRelated(string name)
        => new NexusRequest("GET", "/partner/rooms/" + Seg(name) + "/related", retryable: true);

    // ── Sessions ───────────────────────────────────────────────────────────

    public static NexusRequest SessionsIdentify(
        string distinctId, string? email, string? name, string? phone, object? traits)
        => new NexusRequest(
            "POST",
            "/partner/sessions/identify",
            JsonBody.Create()
                .Set("distinctId", distinctId)
                .Set("email", email)
                .Set("name", name)
                .Set("phone", phone)
                .SetAny("traits", traits)
                .Build(),
            retryable: true);

    public static NexusRequest SessionsTrack(
        NexusTelemetryContext context, string? email, string? name, object? traits, string? entryUrl)
    {
        var body = JsonBody.Create()
            .Set("distinctId", context.DistinctId)
            .Set("deviceKey", context.DeviceKey)
            .Set("sessionKey", context.SessionKey)
            .Set("browser", context.Browser)
            .Set("appVersion", context.AppVersion)
            .Set("country", context.Country)
            .Set("osType", context.OsType)
            .Set("osVersion", context.OsVersion)
            .Set("email", email)
            .Set("name", name)
            .SetAny("traits", traits)
            // A session's entry URL defaults to whatever URL the scope was bound
            // with, which is the right answer for a web request.
            .Set("entryUrl", entryUrl ?? context.Url);

        return new NexusRequest("POST", "/partner/sessions/track", body.Build(), retryable: true);
    }

    // ── Events ─────────────────────────────────────────────────────────────

    public static NexusRequest EventsBatch(IReadOnlyList<JsonObject> events, JsonObject context)
        => new NexusRequest(
            "POST",
            "/partner/events",
            IngestContext(context).SetItems("events", events).Build(),
            retryable: true);

    public static JsonObject EventItem(string name, object? properties, string? timestamp)
        => JsonBody.Create()
            .Set("name", name)
            .SetAny("properties", properties)
            .Set("timestamp", timestamp)
            .Build();

    // ── Logs ───────────────────────────────────────────────────────────────

    public static NexusRequest LogsBatch(IReadOnlyList<JsonObject> logs, JsonObject context)
        => new NexusRequest(
            "POST",
            "/partner/logs",
            IngestContext(context).SetItems("logs", logs).Build(),
            retryable: true);

    public static JsonObject LogItem(
        string level, string message, string? source, object? context, string? timestamp)
        => JsonBody.Create()
            .Set("level", level)
            .Set("message", message)
            .Set("source", source)
            .SetAny("context", context)
            .Set("timestamp", timestamp)
            .Build();

    // ── Errors ─────────────────────────────────────────────────────────────

    public static NexusRequest ErrorsCapture(
        NexusTelemetryContext context,
        string message,
        string? type,
        string? level,
        bool? handled,
        string? fingerprint,
        JsonNode? stack,
        object? errorContext)
    {
        var body = JsonBody.Create();
        context.WriteTo(body);

        body.Set("message", message)
            .Set("type", type)
            .Set("level", level)
            .Set("handled", handled)
            .Set("fingerprint", fingerprint)
            .SetNode("stack", stack)
            .SetAny("context", errorContext);

        return new NexusRequest("POST", "/partner/errors", body.Build(), retryable: true);
    }

    // ── Feature flags ──────────────────────────────────────────────────────

    public static NexusRequest FlagsEvaluate(string distinctId, object? properties)
        => new NexusRequest(
            "POST",
            "/partner/flags/evaluate",
            JsonBody.Create().Set("distinctId", distinctId).SetAny("properties", properties).Build(),
            retryable: true);

    // ── Remote Config ──────────────────────────────────────────────────────

    public static NexusRequest RemoteConfigFetch(JsonObject context, string? etag)
    {
        var headers = string.IsNullOrEmpty(etag)
            ? null
            : new Dictionary<string, string>(1, StringComparer.OrdinalIgnoreCase) { ["If-None-Match"] = etag! };

        return new NexusRequest("POST", "/partner/remote-config/fetch", context, headers, retryable: true);
    }

    public static NexusRequest RemoteConfigSetParameter(
        string key, string valueType, object? defaultValue, object? conditionalValues, string? description)
    {
        var body = JsonBody.Create()
            .Set("key", key)
            .Set("valueType", valueType)
            .SetAny("conditionalValues", conditionalValues)
            .Set("description", description);

        // A parameter's default may legitimately be `false`, `0` or an empty
        // string; only an absent value is omitted, which ToNode already yields
        // as null for null input.
        var node = JsonHelpers.ToNode(defaultValue);
        if (node is not null)
        {
            body.SetNode("defaultValue", node);
        }

        return new NexusRequest("PUT", "/partner/remote-config/parameters", body.Build());
    }

    public static NexusRequest RemoteConfigDeleteParameter(string key)
        => new NexusRequest("DELETE", "/partner/remote-config/parameters/" + Seg(key));

    public static NexusRequest RemoteConfigPublish(string? description)
        => new NexusRequest(
            "POST",
            "/partner/remote-config/publish",
            JsonBody.Create().Set("description", description).Build());

    // ── Deep links ─────────────────────────────────────────────────────────

    public static NexusRequest LinksAttribute(
        NexusTelemetryContext context,
        string type,
        string? name,
        string? clickId,
        string? deviceId,
        string? platform,
        double? revenue,
        object? properties)
    {
        var body = JsonBody.Create()
            .Set("distinctId", context.DistinctId)
            .Set("sessionKey", context.SessionKey)
            .Set("osType", context.OsType)
            .Set("country", context.Country)
            .Set("type", type)
            .Set("name", name)
            .Set("clickId", clickId)
            .Set("deviceId", deviceId)
            .Set("platform", platform)
            .Set("revenue", revenue)
            .SetAny("properties", properties);

        return new NexusRequest("POST", "/partner/links/attribute", body.Build(), retryable: true);
    }

    // ── Surveys ────────────────────────────────────────────────────────────

    public static NexusRequest SurveysActive(
        NexusTelemetryContext context, object? properties, string? deviceType)
        => new NexusRequest(
            "POST",
            "/partner/surveys/active",
            JsonBody.Create()
                .Set("distinctId", context.DistinctId)
                .Set("deviceKey", context.DeviceKey)
                .Set("osType", context.OsType)
                .SetAny("properties", properties)
                .Set("deviceType", deviceType)
                .Build(),
            retryable: true);

    public static NexusRequest SurveysRespond(
        NexusTelemetryContext context,
        string surveyId,
        object? answers,
        bool? completed,
        bool? dismissed,
        string? responseId,
        string? iterationKey)
    {
        var body = JsonBody.Create();
        context.WriteTo(body);

        body.Set("surveyId", surveyId)
            .Set("completed", completed)
            .Set("dismissed", dismissed)
            .Set("responseId", responseId)
            .Set("iterationKey", iterationKey)
            // Validated server-side as an object: an absent answer set must be
            // `{}`, never `[]` and never omitted.
            .SetObjectAlways("answers", answers);

        return new NexusRequest("POST", "/partner/surveys/responses", body.Build());
    }

    // ── Push ───────────────────────────────────────────────────────────────

    public static NexusRequest PushSend(JsonObject payload)
        => new NexusRequest("POST", "/partner/push/send", payload);

    public static NexusRequest PushRegister(
        NexusTelemetryContext context,
        string token,
        string platform,
        string? provider,
        string? lang,
        object? tags)
        => new NexusRequest(
            "POST",
            "/partner/push/register",
            JsonBody.Create()
                .Set("token", token)
                .Set("platform", platform)
                .Set("provider", provider)
                .Set("distinctId", context.DistinctId)
                .Set("deviceKey", context.DeviceKey)
                .Set("appVersion", context.AppVersion)
                .Set("osType", context.OsType)
                .Set("osVersion", context.OsVersion)
                .Set("lang", lang)
                .SetAny("tags", tags)
                .Build());

    public static NexusRequest PushUnregister(string token)
        => new NexusRequest(
            "POST",
            "/partner/push/unregister",
            JsonBody.Create().Set("token", token).Build());

    /// <remarks>
    /// The endpoint takes the campaign id and the device token — both required.
    /// It is not keyed by a per-notification id.
    /// </remarks>
    public static NexusRequest PushOpened(string campaignId, string token)
        => new NexusRequest(
            "POST",
            "/partner/push/opened",
            JsonBody.Create().Set("campaignId", campaignId).Set("token", token).Build(),
            retryable: true);

    // ── In-app messages ────────────────────────────────────────────────────

    public static NexusRequest InAppActive(string? distinctId, string? deviceKey)
        => new NexusRequest(
            "POST",
            "/partner/inapp/active",
            JsonBody.Create().Set("distinctId", distinctId).Set("deviceKey", deviceKey).Build(),
            retryable: true);

    public static NexusRequest InAppEvent(string messageId, string type, string? buttonId, string? distinctId)
        => new NexusRequest(
            "POST",
            "/partner/inapp/event",
            JsonBody.Create()
                .Set("messageId", messageId)
                .Set("type", type)
                .Set("buttonId", buttonId)
                .Set("distinctId", distinctId)
                .Build());

    // ── Live Activities ────────────────────────────────────────────────────

    public static NexusRequest LiveActivityStart(
        string activityType,
        string activityId,
        object? contentState,
        IEnumerable<string>? distinctIds,
        object? attributes,
        string? staleDate,
        int? priority)
        => new NexusRequest(
            "POST",
            "/partner/live-activities/start",
            JsonBody.Create()
                .Set("activityType", activityType)
                .Set("activityId", activityId)
                .SetObjectAlways("contentState", contentState)
                .SetStrings("distinctIds", distinctIds)
                .SetAny("attributes", attributes)
                .Set("staleDate", staleDate)
                .Set("priority", priority)
                .Build());

    public static NexusRequest LiveActivityUpdate(
        string activityId, object? contentState, string? staleDate, int? priority)
        => new NexusRequest(
            "POST",
            "/partner/live-activities/" + Seg(activityId) + "/update",
            JsonBody.Create()
                .SetObjectAlways("contentState", contentState)
                .Set("staleDate", staleDate)
                .Set("priority", priority)
                .Build());

    public static NexusRequest LiveActivityEnd(string activityId, object? contentState, string? dismissalDate)
        => new NexusRequest(
            "POST",
            "/partner/live-activities/" + Seg(activityId) + "/end",
            JsonBody.Create()
                .SetAny("contentState", contentState)
                .Set("dismissalDate", dismissalDate)
                .Build());

    public static NexusRequest LiveActivityPushToken(
        string activityType, string token, string? distinctId, string? deviceKey)
        => new NexusRequest(
            "POST",
            "/partner/live-activities/push-token",
            JsonBody.Create()
                .Set("activityType", activityType)
                .Set("token", token)
                .Set("distinctId", distinctId)
                .Set("deviceKey", deviceKey)
                .Build());

    public static NexusRequest LiveActivityRegister(
        string activityId,
        string activityType,
        string platform,
        string? updateToken,
        string? distinctId,
        object? contentState)
        => new NexusRequest(
            "POST",
            "/partner/live-activities/register",
            JsonBody.Create()
                .Set("activityId", activityId)
                .Set("activityType", activityType)
                .Set("platform", platform)
                .Set("updateToken", updateToken)
                .Set("distinctId", distinctId)
                .SetAny("contentState", contentState)
                .Build());

    public static NexusRequest LiveActivityEvent(string activityId, string type, string? platform)
        => new NexusRequest(
            "POST",
            "/partner/live-activities/event",
            JsonBody.Create()
                .Set("activityId", activityId)
                .Set("type", type)
                .Set("platform", platform)
                .Build());

    // ── Session replay ─────────────────────────────────────────────────────

    public static NexusRequest ReplayIngest(
        NexusTelemetryContext context,
        string recordingId,
        IReadOnlyList<JsonObject> events,
        string? href,
        int? width,
        int? height)
    {
        var body = JsonBody.Create()
            .Set("recordingId", recordingId)
            .Set("distinctId", context.DistinctId)
            .Set("sessionKey", context.SessionKey)
            .Set("deviceKey", context.DeviceKey)
            .Set("osType", context.OsType)
            .Set("osVersion", context.OsVersion)
            .Set("browser", context.Browser)
            .Set("appVersion", context.AppVersion)
            .Set("href", href ?? context.Url)
            .Set("width", width)
            .Set("height", height)
            .SetItems("events", events);

        return new NexusRequest("POST", "/partner/replay", body.Build(), retryable: true);
    }

    // ── Voice ──────────────────────────────────────────────────────────────

    public static NexusRequest VoiceCreateCall(
        string? direction,
        string? applicationId,
        string? proxyIdentityId,
        string? recordingMode,
        object? metadata)
        => new NexusRequest(
            "POST",
            "/partner/voice/calls",
            JsonBody.Create()
                .Set("direction", direction)
                .Set("applicationId", applicationId)
                .Set("proxyIdentityId", proxyIdentityId)
                .Set("recordingMode", recordingMode)
                .SetAny("metadata", metadata)
                .Build());

    public static NexusRequest VoiceAddLeg(
        string sessionId,
        string role,
        string endpointType,
        string? direction,
        string? address,
        string? callerId,
        string? identityId)
        => new NexusRequest(
            "POST",
            "/partner/voice/legs",
            JsonBody.Create()
                .Set("sessionId", sessionId)
                .Set("role", role)
                .Set("endpointType", endpointType)
                .Set("direction", direction)
                .Set("address", address)
                .Set("callerId", callerId)
                .Set("identityId", identityId)
                .Build());

    public static NexusRequest VoiceLegAction(string action, string legId, string? reason = null)
        => new NexusRequest(
            "POST",
            "/partner/voice/legs/" + action,
            JsonBody.Create().Set("legId", legId).Set("reason", reason).Build());

    public static NexusRequest VoiceDtmf(string sessionId, string legId, string digit, int? durationMs)
        => new NexusRequest(
            "POST",
            "/partner/voice/dtmf",
            JsonBody.Create()
                .Set("sessionId", sessionId)
                .Set("legId", legId)
                .Set("digit", digit)
                .Set("durationMs", durationMs)
                .Build());

    public static NexusRequest VoiceIvrStart(string flowId, string? applicationId)
        => new NexusRequest(
            "POST",
            "/partner/voice/ivr/start",
            JsonBody.Create().Set("flowId", flowId).Set("applicationId", applicationId).Build());

    public static NexusRequest VoiceIvrAdvance(string sessionId)
        => new NexusRequest(
            "POST",
            "/partner/voice/ivr/advance",
            JsonBody.Create().Set("sessionId", sessionId).Build());

    public static NexusRequest VoiceIvrInput(string sessionId, string digit)
        => new NexusRequest(
            "POST",
            "/partner/voice/ivr/input",
            JsonBody.Create().Set("sessionId", sessionId).Set("digit", digit).Build());

    public static NexusRequest VoiceRegisterDevice(
        string identityId, string deviceId, string platform, string? voipToken, string? fcmToken)
        => new NexusRequest(
            "POST",
            "/partner/voice/devices",
            JsonBody.Create()
                .Set("identityId", identityId)
                .Set("deviceId", deviceId)
                .Set("platform", platform)
                .Set("voipToken", voipToken)
                .Set("fcmToken", fcmToken)
                .Build());

    public static NexusRequest VoicePresence(string identityId, string deviceId, string status)
        => new NexusRequest(
            "POST",
            "/partner/voice/presence",
            JsonBody.Create()
                .Set("identityId", identityId)
                .Set("deviceId", deviceId)
                .Set("status", status)
                .Build());

    public static NexusRequest VoiceQuality(
        string sessionId,
        string? legId,
        int? rttMs,
        int? jitterMs,
        double? packetLoss,
        int? bitrateKbps,
        double? mos,
        string? codec,
        string? candidateType)
        => new NexusRequest(
            "POST",
            "/partner/voice/quality",
            JsonBody.Create()
                .Set("sessionId", sessionId)
                .Set("legId", legId)
                .Set("rttMs", rttMs)
                .Set("jitterMs", jitterMs)
                .Set("packetLoss", packetLoss)
                .Set("bitrateKbps", bitrateKbps)
                .Set("mos", mos)
                .Set("codec", codec)
                .Set("candidateType", candidateType)
                .Build(),
            retryable: true);

    public static NexusRequest VoiceTransferBlind(
        string sessionId,
        string legId,
        string targetType,
        string? targetAddress,
        string? targetIdentityId,
        string? callerId)
        => new NexusRequest(
            "POST",
            "/partner/voice/transfer/blind",
            JsonBody.Create()
                .Set("sessionId", sessionId)
                .Set("legId", legId)
                .Set("targetType", targetType)
                .Set("targetAddress", targetAddress)
                .Set("targetIdentityId", targetIdentityId)
                .Set("callerId", callerId)
                .Build());

    public static NexusRequest VoiceTransferWarmStart(
        string sessionId,
        string otherPartyLegId,
        string targetType,
        string? targetAddress,
        string? targetIdentityId,
        string? callerId)
        => new NexusRequest(
            "POST",
            "/partner/voice/transfer/warm/start",
            JsonBody.Create()
                .Set("sessionId", sessionId)
                .Set("otherPartyLegId", otherPartyLegId)
                .Set("targetType", targetType)
                .Set("targetAddress", targetAddress)
                .Set("targetIdentityId", targetIdentityId)
                .Set("callerId", callerId)
                .Build());

    public static NexusRequest VoiceTransferWarmFinish(string action, string otherPartyLegId, string legId)
        => new NexusRequest(
            "POST",
            "/partner/voice/transfer/warm/" + action,
            JsonBody.Create().Set("otherPartyLegId", otherPartyLegId).Set("legId", legId).Build());

    public static NexusRequest VoiceConferenceMute(string legId, bool muted)
        => new NexusRequest(
            "POST",
            "/partner/voice/conference/mute",
            JsonBody.Create().Set("legId", legId).Set("muted", muted).Build());

    public static NexusRequest VoiceConferenceRemove(string sessionId, string legId)
        => new NexusRequest(
            "POST",
            "/partner/voice/conference/remove",
            JsonBody.Create().Set("sessionId", sessionId).Set("legId", legId).Build());
}
