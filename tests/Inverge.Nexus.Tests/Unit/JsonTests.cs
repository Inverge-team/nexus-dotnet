using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Inverge.Nexus.Tests.Support;
using Xunit;

namespace Inverge.Nexus.Tests.Unit;

public class JsonTests
{
    [Fact]
    public async Task Dictionaries_anonymous_objects_and_json_nodes_all_work_as_properties()
    {
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(202, """{"written":1}"""));
        await using var _ = client;

        await client.Events.BatchAsync(new[]
        {
            new Models.NexusEvent("dictionary", new Dictionary<string, object?> { ["a"] = 1 }),
            new Models.NexusEvent("anonymous", new { b = true }),
            new Models.NexusEvent("node", new JsonObject { ["c"] = "x" }),
        });

        var events = (transport.Single().Object["events"] as JsonArray)!;
        Assert.Equal(1, events[0]!["properties"]!["a"]!.GetValue<int>());
        Assert.True(events[1]!["properties"]!["b"]!.GetValue<bool>());
        Assert.Equal("x", events[2]!["properties"]!["c"]!.ToString());
    }

    [Fact]
    public async Task Timestamps_are_sent_as_utc_iso_8601()
    {
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(202, """{"written":1}"""));
        await using var _ = client;

        var when = new DateTimeOffset(2026, 10, 4, 9, 30, 15, 250, TimeSpan.FromHours(3));
        await client.Events.BatchAsync(new[] { new Models.NexusEvent("a", null, when) });

        var timestamp = (transport.Single().Object["events"] as JsonArray)![0]!["timestamp"]!.ToString();
        Assert.Equal("2026-10-04T06:30:15.250Z", timestamp);
    }

    [Fact]
    public async Task The_same_node_can_be_passed_twice()
    {
        // A JsonNode cannot belong to two parents, so reusing one would throw unless
        // the SDK clones on the way in.
        var shared = new JsonObject { ["shared"] = true };
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(202, """{"written":2}"""));
        await using var _ = client;

        await client.Events.BatchAsync(new[]
        {
            new Models.NexusEvent("first", shared),
            new Models.NexusEvent("second", shared),
        });

        var events = (transport.Single().Object["events"] as JsonArray)!;
        Assert.True(events[0]!["properties"]!["shared"]!.GetValue<bool>());
        Assert.True(events[1]!["properties"]!["shared"]!.GetValue<bool>());
    }

    [Fact]
    public async Task A_json_element_payload_round_trips()
    {
        using var document = JsonDocument.Parse("""{"nested":{"ok":true}}""");
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(200, """{"ok":true}"""));
        await using var _ = client;

        await client.Realtime.EmitAsync("room", "event", document.RootElement);

        Assert.True(transport.Single().Object["payload"]!["nested"]!["ok"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Collections_become_json_arrays()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(200, """{"ok":true}"""));
        await using var _ = client;

        await client.Realtime.EmitAsync("room", "event", new[] { 1, 2, 3 });

        Assert.Equal(3, (transport.Single().Object["payload"] as JsonArray)!.Count);
    }

    [Fact]
    public async Task Enums_guids_and_dates_inside_a_payload_become_strings()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(200, """{"ok":true}"""));
        await using var _ = client;

        var id = Guid.Parse("11111111-2222-3333-4444-555555555555");
        await client.Realtime.EmitAsync("room", "event", new Dictionary<string, object?>
        {
            ["level"] = Models.NexusLogLevel.Warning,
            ["id"] = id,
            ["when"] = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero),
        });

        var payload = transport.Single().Object["payload"]!;
        Assert.Equal("Warning", payload["level"]!.ToString());
        Assert.Equal("11111111-2222-3333-4444-555555555555", payload["id"]!.ToString());
        Assert.Equal("2026-01-02T03:04:05.000Z", payload["when"]!.ToString());
    }
}
