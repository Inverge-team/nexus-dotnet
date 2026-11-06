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
    public void When_and_Unless_branch_without_leaving_the_chain()
    {
        var (client, _) = TestClient.Create();
        var ids = new List<string>();
        string? missing = null;

        var body = client.Push.Notification()
            .Title("Hi")
            .When("https://cdn.test/a.png", (p, url) => p.Image(url))
            .When(missing, (p, v) => p.Body("never"))
            .When(ids, (p, v) => p.ToUsers(v), p => p.ToSegment("All Clients"))
            .When(ids.Count == 0, p => p.IosBadge(1))
            .Unless(true, p => p.Body("never"))
            .Body((string?)null)               // an absent optional value is a no-op
            .Build();

        Assert.Equal("https://cdn.test/a.png", body["imageUrl"]!.ToString());
        Assert.False(body.ContainsKey("body"));
        Assert.False(body.ContainsKey("distinctIds"));
        Assert.Equal("All Clients", body["segmentIds"]![0]!.ToString());
        Assert.Equal(1, body["options"]!["iosBadge"]!.GetValue<int>());
    }

    [Fact]
    public async Task Title_and_Body_take_one_text_per_language()
    {
        var (client, _) = TestClient.Create();

        var body = client.Push.Notification()
            .Title(new Dictionary<string, string> { ["en"] = "Your order shipped", ["ar"] = "تم شحن طلبك", ["ckb"] = "" })
            .Body(new Dictionary<string, string> { ["en"] = "Track it in the app", ["ar"] = "", ["ckb"] = "بەدوایدا بچۆ" })
            .ToUser("u1")
            .Build();

        Assert.Equal("Your order shipped", body["title"]!.ToString());
        Assert.Equal("Track it in the app", body["body"]!.ToString());
        Assert.Equal("en", body["defaultLanguage"]!.ToString());
        var l = body["localizations"]!;
        Assert.Equal("تم شحن طلبك", l["ar"]!["title"]!.ToString());
        Assert.Equal("Track it in the app", l["ar"]!["body"]!.ToString());   // empty → default's
        Assert.Equal("Your order shipped", l["ckb"]!["title"]!.ToString());  // empty → default's
        Assert.Equal("بەدوایدا بچۆ", l["ckb"]!["body"]!.ToString());

        var d = client.Push.Notification()
            .Title(new Dictionary<string, string> { ["ar"] = "مرحبا", ["ckb"] = "سڵاو" })
            .DefaultLanguage("ckb").ToUser("u").Build();
        Assert.Equal("سڵاو", d["title"]!.ToString());
        Assert.Equal("ckb", d["defaultLanguage"]!.ToString());

        var plain = client.Push.Notification().Title("Hi").ToUser("u").Build();
        Assert.False(plain.ContainsKey("localizations"));
        Assert.False(plain.ContainsKey("defaultLanguage"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => client.Push.Notification()
            .Title(new Dictionary<string, string> { ["en"] = "" }).ToUser("u").SendAsync());
    }

    [Fact]
    public async Task SendAt_schedules_with_an_absolute_instant_and_can_be_canceled()
    {
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(200, """{"id":"c1","status":"scheduled","scheduledAt":"2026-11-07T09:00:00.000Z","sentAt":null,"recipients":0,"sent":0,"failed":0}"""));
        await using var _ = client;

        var result = await client.Push.Notification()
            .Title("Hi")
            .ToUser("user_1")
            .Name("Morning")
            .ScheduleAsync(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.FromHours(3)));

        Assert.True(result.IsScheduled);
        Assert.Equal("c1", result.ScheduledId);
        var body = transport.Last.Object;
        Assert.Equal("2026-11-07T09:00:00Z", body["sendAt"]!.ToString());
        Assert.Equal("Morning", body["name"]!.ToString());

        var scheduled = await client.Push.ScheduledAsync("c/1");
        Assert.Equal("GET", transport.Last.Method);
        Assert.Equal("/partner/push/scheduled/c%2F1", transport.Last.Path);
        Assert.Equal("scheduled", scheduled.Status);

        await client.Push.CancelScheduledAsync("c1");
        Assert.Equal("POST", transport.Last.Method);
        Assert.Equal("/partner/push/scheduled/c1/cancel", transport.Last.Path);

        Assert.False(client.Push.Notification().Title("t").ToUser("u").SendAt(null).Build().ContainsKey("sendAt"));
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
    public async Task A_templated_activity_starts_by_key_and_sends_only_data()
    {
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(200, """{"ios":1,"android":2,"iosTargets":1,"iosWarning":"x"}"""));
        await using var _ = client;

        var started = await client.LiveActivities.Template("order_tracking", "order_42")
            .Step("pending")
            .EtaAt(new DateTimeOffset(2026, 10, 21, 12, 0, 0, TimeSpan.Zero))
            .Value("driver", "Ahmed")
            .Values(new Dictionary<string, object> { ["liters"] = 500 })
            .ToUser("user_1")
            .Lang("ckb")
            .Alert("Order received", "500 L")
            .StartAsync();

        Assert.Equal(1, started.IosTargets);
        Assert.Equal("x", started.IosWarning);
        var body = transport.Last.Object;
        Assert.Equal("/partner/live-activities/start", transport.Last.Path);
        Assert.Equal("order_tracking", body["template"]!.ToString());
        Assert.False(body.ContainsKey("activityType")); // the server targets the SDK widget's type
        Assert.False(body.ContainsKey("contentState"));
        Assert.Equal("pending", body["data"]!["step"]!.ToString());
        Assert.Equal(1792584000L, body["data"]!["etaAt"]!.GetValue<long>());
        Assert.Equal("Ahmed", body["data"]!["values"]!["driver"]!.ToString());
        Assert.Equal(500, body["data"]!["values"]!["liters"]!.GetValue<int>());
        Assert.Equal("ckb", body["lang"]!.ToString());
        Assert.Equal("Order received", body["alert"]!["title"]!.ToString());
        Assert.Equal("500 L", body["alert"]!["body"]!.ToString());
    }

    [Fact]
    public async Task Template_updates_are_partial_data_and_ends_carry_the_final_state()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(200, """{"ios":1,"android":0}"""));
        await using var _ = client;

        await client.LiveActivities.Activity("order_42").Step("arrived").Progress(0.5).UpdateAsync();
        Assert.Equal("/partner/live-activities/order_42/update", transport.Last.Path);
        var update = transport.Last.Object;
        Assert.Equal("arrived", update["data"]!["step"]!.ToString());
        Assert.Equal(0.5, update["data"]!["progress"]!.GetValue<double>());
        Assert.False(update.ContainsKey("contentState"));

        await client.LiveActivities.Activity("order_42").Step("completed").EndAsync();
        Assert.Equal("/partner/live-activities/order_42/end", transport.Last.Path);
        Assert.Equal("completed", transport.Last.Object["data"]!["step"]!.ToString());

        await client.LiveActivities.Activity("order_42").Canceled().EndAsync();
        Assert.Equal("canceled", transport.Last.Object["data"]!["state"]!.ToString());
    }

    [Fact]
    public async Task Progress_alone_keeps_the_plain_content_state()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(200, """{"ios":0,"android":0}"""));
        await using var _ = client;

        await client.LiveActivities.Activity("order_42").Status("On the way").Progress(70).UpdateAsync();
        var body = transport.Last.Object;
        Assert.Equal("On the way", body["contentState"]!["status"]!.ToString());
        Assert.False(body.ContainsKey("data"));
    }

    [Fact]
    public async Task Template_plain_methods_and_inline_designs()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(200, """{"ios":0,"android":1}"""));
        await using var _ = client;

        await client.LiveActivities.StartTemplateAsync("order_tracking", "o1");
        Assert.IsType<JsonObject>(transport.Last.Object["data"]); // {} — never omitted or []

        var design = new JsonObject { ["layout"] = "steps", ["steps"] = new JsonArray(new JsonObject { ["id"] = "a" }, new JsonObject { ["id"] = "b" }) };
        await client.LiveActivities.StartTemplateAsync(design, "o2", new { step = "a" });
        Assert.Equal(2, (transport.Last.Object["template"]!["steps"] as JsonArray)!.Count);

        await client.LiveActivities.UpdateDataAsync("o2", new JsonObject { ["step"] = "b", ["etaAt"] = null });
        Assert.True(transport.Last.Object["data"]!.AsObject().ContainsKey("etaAt")); // null clears it server-side

        await client.LiveActivities.EndWithDataAsync("o2", new { state = "done" });
        Assert.Equal("done", transport.Last.Object["data"]!["state"]!.ToString());
    }

    [Fact]
    public async Task Eta_in_milliseconds_is_converted()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(200, """{"ios":0,"android":0}"""));
        await using var _ = client;

        await client.LiveActivities.Template("t", "o").EtaAt(1792584000123L).StartAsync();
        Assert.Equal(1792584000L, transport.Last.Object["data"]!["etaAt"]!.GetValue<long>());
    }

    [Fact]
    public async Task Starting_needs_a_template_or_a_type()
    {
        var (client, _) = TestClient.Create(_ => RecordingTransport.Json(200, """{"ios":0,"android":0}"""));
        await using var __ = client;

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.LiveActivities.Activity("o").Step("a").StartAsync());
        Assert.Contains("template", ex.Message);
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
