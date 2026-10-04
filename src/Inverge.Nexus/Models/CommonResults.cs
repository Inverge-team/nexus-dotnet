using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Inverge.Nexus.Internal;

namespace Inverge.Nexus.Models;

/// <summary>The person created or updated by an identify call.</summary>
public sealed class IdentifyResult : NexusResult
{
    private IdentifyResult(JsonNode? raw) : base(raw)
    {
        Subject = raw.Obj("subject");
        SubjectId = Subject.Str("id");
        DistinctId = Subject.Str("distinctId");
    }

    /// <summary>The stored subject record.</summary>
    public JsonObject? Subject { get; }

    /// <summary>Nexus's own id for the person.</summary>
    public string? SubjectId { get; }

    /// <summary>The distinct id you identified them by.</summary>
    public string? DistinctId { get; }

    internal static IdentifyResult From(JsonNode? raw) => new IdentifyResult(raw);
}

/// <summary>The session a track call started or refreshed.</summary>
public sealed class SessionResult : NexusResult
{
    private SessionResult(JsonNode? raw) : base(raw)
    {
        SessionId = raw.Str("sessionId") ?? raw.Obj("session").Str("id");
        SessionKey = raw.Str("sessionKey") ?? raw.Obj("session").Str("sessionKey");
        SubjectId = raw.Str("subjectId") ?? raw.Obj("subject").Str("id");
    }

    /// <summary>Nexus's id for the session.</summary>
    public string? SessionId { get; }

    /// <summary>The session key correlating this journey.</summary>
    public string? SessionKey { get; }

    /// <summary>Nexus's id for the person the session belongs to.</summary>
    public string? SubjectId { get; }

    internal static SessionResult From(JsonNode? raw) => new SessionResult(raw);
}

/// <summary>
/// What became of a captured error.
/// </summary>
/// <remarks>
/// Three outcomes all arrive as a 2xx, and they are not the same thing:
/// stored (<see cref="Id"/> set), dropped because the issue is marked ignored in
/// the console (<see cref="Ignored"/>), or dropped because the environment hit
/// its error limit (<see cref="Skipped"/>). An ignored issue is not billed.
/// </remarks>
public sealed class ErrorCaptureResult : NexusResult
{
    private ErrorCaptureResult(JsonNode? raw) : base(raw)
    {
        Id = raw.Str("id");
        GroupId = raw.Str("groupId");
        Ignored = raw.Flag("ignored");
        Skipped = raw.Flag("skipped");
    }

    /// <summary>The stored occurrence's id, when it was stored.</summary>
    public string? Id { get; }

    /// <summary>The issue group it was folded into.</summary>
    public string? GroupId { get; }

    /// <summary>Whether the issue is marked ignored, so the occurrence was dropped and not billed.</summary>
    public bool Ignored { get; }

    /// <summary>Whether the environment's error limit dropped it.</summary>
    public bool Skipped { get; }

    /// <summary>Whether the occurrence was actually stored.</summary>
    public bool Stored => Id is not null;

    internal static ErrorCaptureResult From(JsonNode? raw) => new ErrorCaptureResult(raw);
}

/// <summary>The acknowledgement of a realtime emit.</summary>
public sealed class EmitResult : NexusResult
{
    private EmitResult(JsonNode? raw) : base(raw)
    {
        Ok = raw.Flag("ok", raw is not null);
        Room = raw.Str("room");
        Events = raw.Strings("events");
        Recipients = raw.Int("recipients");
        Related = raw.Strings("related");
        Skipped = raw.Flag("skipped");
    }

    /// <summary>Whether the emit was accepted.</summary>
    public bool Ok { get; }

    /// <summary>The room it went to.</summary>
    public string? Room { get; }

    /// <summary>The event names emitted.</summary>
    public IReadOnlyList<string> Events { get; }

    /// <summary>
    /// How many connected sockets received it. Zero is the signature of an
    /// environment mismatch — rooms are environment-scoped, so a subscriber using
    /// a key from another environment is never in the room you emitted to.
    /// </summary>
    public int Recipients { get; }

    /// <summary>Linked rooms the message was also delivered to.</summary>
    public IReadOnlyList<string> Related { get; }

    /// <summary>Whether the environment's realtime limit dropped it.</summary>
    public bool Skipped { get; }

    internal static EmitResult From(JsonNode? raw) => new EmitResult(raw);
}

/// <summary>The acknowledgement of a multi-room broadcast.</summary>
public sealed class BroadcastResult : NexusResult
{
    private BroadcastResult(JsonNode? raw) : base(raw)
    {
        Ok = raw.Flag("ok", raw is not null);
        Count = raw.Int("count");
        Results = raw.Items("results");
    }

    /// <summary>Whether the broadcast was accepted.</summary>
    public bool Ok { get; }

    /// <summary>
    /// How many rooms were emitted to. A message whose room name or event list was
    /// empty is skipped server-side, so this can be lower than what you sent.
    /// </summary>
    public int Count { get; }

    /// <summary>The per-room acknowledgements.</summary>
    public IReadOnlyList<JsonNode> Results { get; }

    internal static BroadcastResult From(JsonNode? raw) => new BroadcastResult(raw);
}

/// <summary>The outcome of a transactional push send.</summary>
public sealed class PushSendResult : NexusResult
{
    private PushSendResult(JsonNode? raw) : base(raw)
    {
        Sent = raw.Int("sent");
        Failed = raw.Int("failed");
        Recipients = raw.Int("recipients");
    }

    /// <summary>How many device deliveries the providers accepted.</summary>
    public int Sent { get; }

    /// <summary>
    /// How many were rejected. A steady non-zero count usually means stale tokens
    /// or provider credentials that need attention — the send itself still
    /// reports success.
    /// </summary>
    public int Failed { get; }

    /// <summary>How many devices the audience resolved to.</summary>
    public int Recipients { get; }

    internal static PushSendResult From(JsonNode? raw) => new PushSendResult(raw);
}

/// <summary>How many devices a Live Activity reached, per platform.</summary>
public sealed class LiveActivityResult : NexusResult
{
    private LiveActivityResult(JsonNode? raw) : base(raw)
    {
        Ios = raw.Int("ios");
        Android = raw.Int("android");
    }

    /// <summary>iOS devices reached.</summary>
    public int Ios { get; }

    /// <summary>Android devices reached.</summary>
    public int Android { get; }

    /// <summary>Devices reached in total.</summary>
    public int Total => Ios + Android;

    internal static LiveActivityResult From(JsonNode? raw) => new LiveActivityResult(raw);
}

/// <summary>The stored survey response.</summary>
public sealed class SurveyResponseResult : NexusResult
{
    private SurveyResponseResult(JsonNode? raw) : base(raw)
    {
        Id = raw.Str("id");
        Completed = raw.Flag("completed");
        Skipped = raw.Flag("skipped");
    }

    /// <summary>
    /// The response id. Pass it back as <c>responseId</c> to keep appending answers
    /// to the same response instead of creating a second one.
    /// </summary>
    public string? Id { get; }

    /// <summary>Whether the response is marked complete.</summary>
    public bool Completed { get; }

    /// <summary>Whether the environment's survey limit dropped it.</summary>
    public bool Skipped { get; }

    internal static SurveyResponseResult From(JsonNode? raw) => new SurveyResponseResult(raw);
}

/// <summary>A published Remote Config version.</summary>
public sealed class RemoteConfigPublishResult : NexusResult
{
    private RemoteConfigPublishResult(JsonNode? raw) : base(raw)
    {
        VersionNumber = raw.Int("versionNumber");
        ETag = raw.Str("etag") ?? string.Empty;
        CreatedAt = raw.Timestamp("createdAt");
    }

    /// <summary>The new version number.</summary>
    public int VersionNumber { get; }

    /// <summary>The new version's ETag.</summary>
    public string ETag { get; }

    /// <summary>When it was published.</summary>
    public DateTimeOffset? CreatedAt { get; }

    internal static RemoteConfigPublishResult From(JsonNode? raw) => new RemoteConfigPublishResult(raw);
}

/// <summary>A registered realtime room.</summary>
public sealed class RoomResult : NexusResult
{
    private RoomResult(JsonNode? raw) : base(raw)
    {
        var room = raw.Obj("room") ?? raw as JsonObject;
        Id = room.Str("id");
        Name = room.Str("name");
        Type = room.Str("type");
        Room = room;
    }

    /// <summary>The room record.</summary>
    public JsonObject? Room { get; }

    /// <summary>The room's id, as used by the link and schema endpoints.</summary>
    public string? Id { get; }

    /// <summary>The room name, as joined by subscribers.</summary>
    public string? Name { get; }

    /// <summary>The room type, when one was set.</summary>
    public string? Type { get; }

    internal static RoomResult From(JsonNode? raw) => new RoomResult(raw);
}

/// <summary>A room's payload schema and whether it is enforced.</summary>
public sealed class RoomSchemaResult : NexusResult
{
    private RoomSchemaResult(JsonNode? raw) : base(raw)
    {
        Schema = raw.Prop("schema")?.DeepClone() ?? raw.Prop("payloadSchema")?.DeepClone();
        Enabled = raw.Flag("enabled") || raw.Flag("schemaEnabled");
    }

    /// <summary>The JSON Schema payloads must satisfy, when one is set.</summary>
    public JsonNode? Schema { get; }

    /// <summary>
    /// Whether validation is actually enforced. A schema can be attached and
    /// switched off, which is why these are two separate things.
    /// </summary>
    public bool Enabled { get; }

    internal static RoomSchemaResult From(JsonNode? raw) => new RoomSchemaResult(raw);
}
