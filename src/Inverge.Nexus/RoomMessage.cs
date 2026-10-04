using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Inverge.Nexus.Internal;

namespace Inverge.Nexus;

/// <summary>
/// One realtime message: the room, the event name or names, and the payload.
/// </summary>
/// <example>
/// <code>
/// await nexus.Realtime.BroadcastAsync(new[]
/// {
///     new RoomMessage("orders:1", "location", new { lat = 1 }),
///     new RoomMessage("orders:2", new[] { "location", "eta" }, new { lat = 2 }),
/// });
/// </code>
/// </example>
public sealed class RoomMessage
{
    /// <summary>Creates a message carrying one event name.</summary>
    public RoomMessage(string room, string @event, object? payload = null)
        : this(room, new[] { @event }, payload)
    {
    }

    /// <summary>Creates a message carrying several event names.</summary>
    public RoomMessage(string room, IEnumerable<string> events, object? payload = null)
    {
        if (string.IsNullOrWhiteSpace(room))
        {
            throw new ArgumentException("A room name is required.", nameof(room));
        }

        if (events is null)
        {
            throw new ArgumentNullException(nameof(events));
        }

        var names = new List<string>();
        foreach (var name in events)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        if (names.Count == 0)
        {
            throw new ArgumentException("At least one event name is required.", nameof(events));
        }

        Room = room;
        Events = names;
        Payload = payload;
    }

    /// <summary>The room name, as joined by subscribers.</summary>
    public string Room { get; }

    /// <summary>The event names to emit.</summary>
    public IReadOnlyList<string> Events { get; }

    /// <summary>The payload delivered to subscribers.</summary>
    public object? Payload { get; }

    /// <summary>The wire form used by the batch-emit endpoint.</summary>
    internal JsonObject ToJson()
    {
        var events = new JsonArray();
        foreach (var name in Events)
        {
            events.Add((JsonNode?)JsonValue.Create(name));
        }

        return new JsonObject
        {
            ["name"] = JsonValue.Create(Room),
            ["events"] = events,
            ["payload"] = JsonHelpers.ToNode(Payload),
        };
    }
}
