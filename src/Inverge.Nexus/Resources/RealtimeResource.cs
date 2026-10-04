using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Internal;
using Inverge.Nexus.Models;

namespace Inverge.Nexus.Resources;

/// <summary>Realtime rooms — emit from your server, and manage the room registry.</summary>
/// <remarks>
/// <para>
/// Rooms are <strong>environment-scoped</strong>. A subscriber connected with a key
/// from a different environment is not in the room you emitted to, and the emit
/// succeeds with zero recipients. That silence is the single most common realtime
/// problem, and <see cref="EmitResult.Recipients"/> is how you spot it.
/// </para>
/// <para>
/// This resource only emits. To <em>receive</em> events in .NET — a worker reacting
/// to room traffic, a bridge — use <c>Inverge.Nexus.Realtime</c>.
/// </para>
/// </remarks>
public sealed class RealtimeResource : NexusResource
{
    internal RealtimeResource(NexusClient client) : base(client)
    {
    }

    /// <summary>Emits one named event to a room.</summary>
    /// <example>
    /// <code>
    /// await nexus.Realtime.EmitAsync("orders:42", "status", new { state = "shipped" });
    /// </code>
    /// </example>
    public Task<EmitResult> EmitAsync(
        string room,
        string eventName,
        object? payload = null,
        CancellationToken cancellationToken = default)
        => EmitAsync(room, new[] { eventName }, payload, cancellationToken);

    /// <summary>Emits several named events to a room, with one payload.</summary>
    public async Task<EmitResult> EmitAsync(
        string room,
        IEnumerable<string> events,
        object? payload = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(room, nameof(room));

        var message = new RoomMessage(room, events, payload);
        var response = await SendAsync(
            Payloads.RealtimeEmit(message.Room, message.Events, message.Payload),
            cancellationToken).ConfigureAwait(false);

        return EmitResult.From(response);
    }

    /// <summary>Emits a prepared message.</summary>
    public async Task<EmitResult> EmitAsync(RoomMessage message, CancellationToken cancellationToken = default)
    {
        if (message is null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        var response = await SendAsync(
            Payloads.RealtimeEmit(message.Room, message.Events, message.Payload),
            cancellationToken).ConfigureAwait(false);

        return EmitResult.From(response);
    }

    /// <summary>Sends to many rooms, each with its own events and payload, in one request.</summary>
    public async Task<BroadcastResult> BroadcastAsync(
        IEnumerable<RoomMessage> messages, CancellationToken cancellationToken = default)
    {
        if (messages is null)
        {
            throw new ArgumentNullException(nameof(messages));
        }

        var items = new List<RoomMessage>(messages);
        if (items.Count == 0)
        {
            return BroadcastResult.From(null);
        }

        var response = await SendAsync(
            Payloads.RealtimeBroadcast(items), cancellationToken).ConfigureAwait(false);

        return BroadcastResult.From(response);
    }

    /// <summary>Sends the same event and payload to several rooms, in one request.</summary>
    public Task<BroadcastResult> EmitToRoomsAsync(
        IEnumerable<string> rooms,
        string eventName,
        object? payload = null,
        CancellationToken cancellationToken = default)
    {
        if (rooms is null)
        {
            throw new ArgumentNullException(nameof(rooms));
        }

        var messages = new List<RoomMessage>();
        foreach (var room in rooms)
        {
            messages.Add(new RoomMessage(room, eventName, payload));
        }

        return BroadcastAsync(messages, cancellationToken);
    }

    /// <summary>
    /// Registers a room up front. Rooms are also created on first join or emit, so
    /// this is for naming and typing them deliberately.
    /// </summary>
    public async Task<RoomResult> RegisterRoomAsync(
        string name, string? type = null, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(name, nameof(name));

        var response = await SendAsync(
            Payloads.RealtimeRegisterRoom(name, type), cancellationToken).ConfigureAwait(false);

        return RoomResult.From(response);
    }

    /// <summary>The environment's registered rooms.</summary>
    public async Task<IReadOnlyList<JsonNode>> ListRoomsAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(Payloads.RealtimeListRooms(), cancellationToken).ConfigureAwait(false);
        return response.Items("rooms");
    }

    /// <summary>Deletes a registered room.</summary>
    public async Task<NexusAck> DeleteRoomAsync(string roomId, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(roomId, nameof(roomId));

        var response = await SendAsync(
            Payloads.RealtimeDeleteRoom(roomId), cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }

    /// <summary>
    /// Links two rooms, so an emit to one also reaches the other.
    /// </summary>
    /// <remarks>
    /// Both arguments are room <em>ids</em>, not names — the ids from
    /// <see cref="RegisterRoomAsync"/> or <see cref="ListRoomsAsync"/>.
    /// </remarks>
    public async Task<NexusAck> LinkAsync(
        string roomId, string relatedRoomId, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(roomId, nameof(roomId));
        Guard.NotBlank(relatedRoomId, nameof(relatedRoomId));

        var response = await SendAsync(
            Payloads.RealtimeLink(roomId, relatedRoomId), cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }

    /// <summary>Removes a link between two rooms.</summary>
    public async Task<NexusAck> UnlinkAsync(
        string roomId, string relatedRoomId, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(roomId, nameof(roomId));
        Guard.NotBlank(relatedRoomId, nameof(relatedRoomId));

        var response = await SendAsync(
            Payloads.RealtimeUnlink(roomId, relatedRoomId), cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }

    /// <summary>Rooms linked to this one, by room name.</summary>
    public async Task<IReadOnlyList<string>> RelatedAsync(
        string roomName, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(roomName, nameof(roomName));

        var response = await SendAsync(
            Payloads.RealtimeRelated(roomName), cancellationToken).ConfigureAwait(false);

        return response.Strings("related");
    }

    /// <summary>The room's payload schema, if one is set.</summary>
    public async Task<RoomSchemaResult> GetSchemaAsync(
        string roomId, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(roomId, nameof(roomId));

        var response = await SendAsync(
            Payloads.RealtimeGetSchema(roomId), cancellationToken).ConfigureAwait(false);

        return RoomSchemaResult.From(response);
    }

    /// <summary>
    /// Attaches a JSON Schema that payloads to this room must satisfy.
    /// </summary>
    /// <remarks>
    /// Attaching a schema does not enforce it — call
    /// <see cref="EnableSchemaAsync"/> for that. Once enforced, an emit whose
    /// payload fails validation is rejected with a validation error rather than
    /// delivered.
    /// </remarks>
    public async Task<RoomSchemaResult> SetSchemaAsync(
        string roomId, object schema, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(roomId, nameof(roomId));
        if (schema is null)
        {
            throw new ArgumentNullException(nameof(schema));
        }

        var response = await SendAsync(
            Payloads.RealtimeSetSchema(roomId, schema), cancellationToken).ConfigureAwait(false);

        return RoomSchemaResult.From(response);
    }

    /// <summary>Turns schema validation on or off for a room.</summary>
    public async Task<RoomSchemaResult> EnableSchemaAsync(
        string roomId, bool enabled = true, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(roomId, nameof(roomId));

        var response = await SendAsync(
            Payloads.RealtimeEnableSchema(roomId, enabled), cancellationToken).ConfigureAwait(false);

        return RoomSchemaResult.From(response);
    }

    /// <summary>Removes the room's schema entirely.</summary>
    public async Task<NexusAck> ClearSchemaAsync(string roomId, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(roomId, nameof(roomId));

        var response = await SendAsync(
            Payloads.RealtimeClearSchema(roomId), cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }
}
