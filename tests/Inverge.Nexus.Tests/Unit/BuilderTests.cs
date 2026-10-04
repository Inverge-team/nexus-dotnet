using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Inverge.Nexus.Tests.Support;
using Xunit;

namespace Inverge.Nexus.Tests.Unit;

public class BuilderTests
{
    [Fact]
    public async Task The_push_builder_assembles_content_options_and_audience()
    {
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(200, """{"sent":3,"failed":1,"recipients":4}"""));
        await using var _ = client;

        var result = await client.Push.Notification()
            .Title("Your order shipped")
            .Body("Track it in the app")
            .Image("https://cdn.test/box.png")
            .Data(new Dictionary<string, object?> { ["screen"] = "/orders/42" })
            .Button("track", "Track", url: "https://cdn.test/track", style: "primary")
            .IosBadge(1)
            .IosInterruptionLevel("time-sensitive")
            .AndroidVisibility("public")
            .ToUsers(new[] { "user_1", "user_2" })
            .ToSegments(new[] { "vip" })
            .SendAsync();

        var body = transport.Single().Object;
        Assert.Equal("Your order shipped", body["title"]!.ToString());
        Assert.Equal("https://cdn.test/box.png", body["imageUrl"]!.ToString());
        Assert.Equal("/orders/42", body["data"]!["screen"]!.ToString());
        Assert.Equal(1, body["options"]!["iosBadge"]!.GetValue<int>());
        Assert.Equal("public", body["options"]!["androidVisibility"]!.ToString());
        Assert.Equal("track", body["options"]!["buttons"]![0]!["id"]!.ToString());
        Assert.Equal(2, (body["distinctIds"] as JsonArray)!.Count);
        Assert.Single((body["segmentIds"] as JsonArray)!);

        Assert.Equal(3, result.Sent);
        Assert.Equal(1, result.Failed);
        Assert.Equal(4, result.Recipients);
    }

    [Fact]
    public async Task A_push_without_a_title_is_refused_before_it_is_sent()
    {
        var (client, transport) = TestClient.Create();
        await using var _ = client;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.Push.Notification().ToUser("user_1").SendAsync());
        Assert.Equal(0, transport.Count);
    }

    [Fact]
    public async Task A_push_without_an_audience_is_refused_before_it_is_sent()
    {
        // Sending with no target must not quietly mean "everyone".
        var (client, transport) = TestClient.Create();
        await using var _ = client;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.Push.Notification().Title("Hi").SendAsync());
        Assert.Equal(0, transport.Count);
    }

    [Fact]
    public async Task Reaching_everyone_has_to_be_spelled_out()
    {
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(200, """{"sent":1000,"failed":0,"recipients":1000}"""));
        await using var _ = client;

        await client.Push.Notification().Title("Hi").ToEveryone().SendAsync();

        Assert.True(transport.Single().Object["all"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Duplicate_recipients_are_collapsed()
    {
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(200, """{"sent":1,"failed":0,"recipients":1}"""));
        await using var _ = client;

        await client.Push.Notification()
            .Title("Hi")
            .ToUser("user_1")
            .ToUser("user_1")
            .ToUsers(new[] { "user_1", "user_2" })
            .SendAsync();

        Assert.Equal(2, (transport.Single().Object["distinctIds"] as JsonArray)!.Count);
    }

    [Fact]
    public async Task The_live_activity_builder_starts_updates_and_ends()
    {
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(200, """{"ios":2,"android":1}"""));
        await using var _ = client;

        var started = await client.LiveActivities.Activity("DeliveryAttributes", "order_42")
            .Title("Order #42")
            .Status("Preparing")
            .Progress(20)
            .ToUser("user_1")
            .Priority(10)
            .StartAsync();

        Assert.Equal(3, started.Total);
        var start = transport.Last.Object;
        Assert.Equal("/partner/live-activities/start", transport.Last.Path);
        Assert.Equal("DeliveryAttributes", start["activityType"]!.ToString());
        Assert.Equal("Preparing", start["contentState"]!["status"]!.ToString());
        Assert.Equal(10, start["priority"]!.GetValue<int>());

        await client.LiveActivities.Activity("order_42").Status("On the way").Progress(70).UpdateAsync();
        Assert.Equal("/partner/live-activities/order_42/update", transport.Last.Path);

        await client.LiveActivities.Activity("order_42").Status("Delivered").EndAsync();
        Assert.Equal("/partner/live-activities/order_42/end", transport.Last.Path);
        Assert.Equal("Delivered", transport.Last.Object["contentState"]!["status"]!.ToString());
    }

    [Fact]
    public async Task Updating_without_a_type_is_allowed_but_starting_is_not()
    {
        var (client, _) = TestClient.Create(__ => RecordingTransport.Json(200, """{"ios":0,"android":0}"""));
        await using var ___ = client;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.LiveActivities.Activity("order_42").Status("x").StartAsync());
    }

    [Fact]
    public async Task Invalid_enumerations_are_rejected_at_the_call_site()
    {
        var (client, transport) = TestClient.Create();
        await using var _ = client;

        await Assert.ThrowsAsync<ArgumentException>(
            () => client.Voice.AddLegAsync("s_1", "not-a-role", "webrtc"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.Voice.AddLegAsync("s_1", "callee", "carrier-pigeon"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.Voice.PresenceAsync("id", "dev", "SLEEPY"));
        await Assert.ThrowsAsync<ArgumentException>(
            () => client.Push.RegisterTokenAsync("token", "blackberry"));

        Assert.Equal(0, transport.Count);
    }

    [Fact]
    public async Task The_recording_url_is_built_from_the_configured_origin()
    {
        var (client, _) = TestClient.Create();
        await using var __ = client;

        var url = client.Voice.RecordingDownloadUrl("tok en/+", inline: true);

        Assert.Equal(
            TestClient.BaseUrl + "/partner/voice/recordings/download?token=tok%20en%2F%2B&inline=1",
            url);
    }
}
