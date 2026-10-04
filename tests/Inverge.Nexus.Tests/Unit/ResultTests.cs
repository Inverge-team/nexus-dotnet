using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Inverge.Nexus.Tests.Support;
using Xunit;

namespace Inverge.Nexus.Tests.Unit;

public class ResultTests
{
    [Fact]
    public async Task An_ignored_error_is_distinguishable_from_a_stored_one()
    {
        // Three different outcomes all arrive as a 2xx. Treating them alike hides an
        // ignored issue (not billed) and a usage cap (data lost) as success.
        var (client, _) = TestClient.Create(_ => RecordingTransport.Json(202, """{"ignored":true}"""));
        await using var __ = client;

        var result = await client.Errors.CaptureAsync("boom");

        Assert.True(result.Ignored);
        Assert.False(result.Stored);
        Assert.Null(result.Id);
    }

    [Fact]
    public async Task A_stored_error_carries_its_group()
    {
        var (client, _) = TestClient.Create(
            _ => RecordingTransport.Json(202, """{"id":"evt_1","groupId":"grp_1"}"""));
        await using var __ = client;

        var result = await client.Errors.CaptureAsync("boom");

        Assert.True(result.Stored);
        Assert.Equal("evt_1", result.Id);
        Assert.Equal("grp_1", result.GroupId);
    }

    [Fact]
    public async Task A_usage_cap_shows_up_as_skipped()
    {
        var (client, _) = TestClient.Create(_ => RecordingTransport.Json(200, """{"skipped":true}"""));
        await using var __ = client;

        var result = await client.Links.InstallAsync(clickId: "abc");

        Assert.True(result.Skipped);
    }

    [Fact]
    public async Task A_blocked_voice_leg_is_reported_inside_a_successful_response()
    {
        var (client, _) = TestClient.Create(_ => RecordingTransport.Json(
            200, """{"error":"call_blocked","reason":"destination_blocked","riskScore":0.91}"""));
        await using var __ = client;

        var result = await client.Voice.AddLegAsync("s_1", "callee", "pstn", address: "+99999");

        Assert.False(result.IsSuccess);
        Assert.True(result.IsBlocked);
        Assert.Equal("destination_blocked", result.Reason);
        Assert.Equal(0.91, result.RiskScore!.Value, 3);

        var thrown = Assert.Throws<NexusApiException>(() => result.EnsureSuccess());
        Assert.Equal("call_blocked", thrown.Code);
    }

    [Fact]
    public async Task A_pstn_leg_with_no_carrier_is_reported_but_still_returns_the_leg()
    {
        var (client, _) = TestClient.Create(_ => RecordingTransport.Json(
            201, """{"leg":{"id":"leg_1"},"error":"no_outbound_trunk"}"""));
        await using var __ = client;

        var result = await client.Voice.AddLegAsync("s_1", "callee", "pstn", address: "+964700");

        Assert.True(result.HasNoOutboundTrunk);
        Assert.False(result.IsSuccess);
        Assert.Equal("leg_1", result.Leg!.Id);
    }

    [Fact]
    public async Task Dtmf_carries_the_next_ivr_instruction()
    {
        var (client, _) = TestClient.Create(_ => RecordingTransport.Json(
            202, """{"ok":true,"ivr":{"action":"connect","endpointType":"app","to":"agent_7"}}"""));
        await using var __ = client;

        var result = await client.Voice.SendDigitAsync("s_1", "leg_1", "2");

        Assert.True(result.Ok);
        Assert.True(result.Ivr!.IsConnect);
        Assert.Equal("agent_7", result.Ivr.To);
        Assert.Equal("app", result.Ivr.EndpointType);
    }

    [Fact]
    public async Task An_ivr_queue_with_nobody_available_says_so()
    {
        var (client, _) = TestClient.Create(_ => RecordingTransport.Json(
            200, """{"instruction":{"action":"no_agents"}}"""));
        await using var __ = client;

        var result = await client.Voice.SendIvrInputAsync("s_1", "1");

        Assert.True(result.Instruction!.HasNoAgents);
    }

    [Fact]
    public async Task A_zero_recipient_emit_is_visible()
    {
        // Rooms are environment-scoped, so a subscriber on another environment's key
        // is simply not in the room. Recipients is how that shows up.
        var (client, _) = TestClient.Create(_ => RecordingTransport.Json(
            200, """{"ok":true,"room":"orders:42","events":["status"],"recipients":0,"related":[]}"""));
        await using var __ = client;

        var result = await client.Realtime.EmitAsync("orders:42", "status", new { state = "shipped" });

        Assert.True(result.Ok);
        Assert.Equal(0, result.Recipients);
        Assert.Equal(new[] { "status" }, result.Events);
    }

    [Fact]
    public async Task Flags_distinguish_booleans_from_variants()
    {
        var (client, _) = TestClient.Create(_ => RecordingTransport.Json(
            200,
            """{"flags":{"new_checkout":true,"old_thing":false,"paywall":"variant_b"},"payloads":{"paywall":{"price":9}}}"""));
        await using var __ = client;

        var flags = await client.Flags.EvaluateAsync("user_1");

        Assert.True(flags.IsEnabled("new_checkout"));
        Assert.False(flags.IsEnabled("old_thing"));
        Assert.False(flags.IsEnabled("absent"));

        Assert.Null(flags.Variant("new_checkout"));
        Assert.Equal("variant_b", flags.Variant("paywall"));
        Assert.True(flags.IsEnabled("paywall"));

        Assert.Equal("9", flags.Payload("paywall")!["price"]!.ToString());
    }

    [Fact]
    public async Task Flag_helpers_fall_back_instead_of_throwing()
    {
        // A flag lookup is a decision point. A Nexus outage must not turn a page
        // that would otherwise render into a 500.
        var (client, _) = TestClient.Create(_ => RecordingTransport.Json(500, "{}"));
        await using var __ = client;

        Assert.True(await client.Flags.IsEnabledAsync("x", "user_1", fallback: true));
        Assert.Equal("control", await client.Flags.VariantAsync("x", "user_1", fallback: "control"));

        // The explicit call still reports the failure, so it can be handled.
        await Assert.ThrowsAsync<NexusServerException>(() => client.Flags.EvaluateAsync("user_1"));
    }

    [Fact]
    public async Task An_empty_flag_evaluation_falls_back_rather_than_reading_as_all_off()
    {
        // The server answers {} when the environment hits its evaluation limit.
        var (client, _) = TestClient.Create(_ => RecordingTransport.Json(200, """{"flags":{},"payloads":{}}"""));
        await using var __ = client;

        Assert.True(await client.Flags.IsEnabledAsync("x", "user_1", fallback: true));
    }

    [Fact]
    public async Task Remote_config_falls_back_to_registered_defaults()
    {
        var (client, _) = TestClient.Create(_ => RecordingTransport.Json(
            200, """{"version":1,"etag":"e","parameters":{"fee":{"value":"7.5","valueType":"NUMBER","source":"iraq"}}}"""));
        await using var __ = client;

        client.RemoteConfig.SetDefaults(new Dictionary<string, object?>
        {
            ["fee"] = 1.0,
            ["support_number"] = "+964700",
            ["maintenance"] = false,
        });

        var snapshot = await client.RemoteConfig.FetchAsync();

        // A NUMBER stored as a string must still read as a number.
        Assert.Equal(7.5, snapshot.GetNumber("fee"));
        Assert.Equal("iraq", snapshot.SourceOf("fee"));

        // Absent keys come from the defaults, not from zero and false.
        Assert.Equal("+964700", snapshot.GetString("support_number"));
        Assert.False(snapshot.GetBoolean("maintenance"));
        Assert.Equal(99, snapshot.GetInt32("absent", 99));
    }

    [Fact]
    public async Task Remote_config_reads_are_cached_within_the_ttl()
    {
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(200, """{"version":1,"etag":"e","parameters":{}}"""),
            options => options.RemoteConfigCacheTtl = TimeSpan.FromMinutes(5));
        await using var __ = client;

        await client.RemoteConfig.FetchAsync();
        await client.RemoteConfig.FetchAsync();
        await client.RemoteConfig.FetchAsync();

        Assert.Equal(1, transport.Count);

        client.RemoteConfig.Invalidate();
        await client.RemoteConfig.FetchAsync();

        Assert.Equal(2, transport.Count);
    }

    [Fact]
    public async Task Publishing_drops_the_cache()
    {
        var (client, transport) = TestClient.Create(
            request => request.Path.Contains("publish", StringComparison.Ordinal)
                ? RecordingTransport.Json(200, """{"versionNumber":4,"etag":"e2","createdAt":"2026-10-04T10:00:00Z"}""")
                : RecordingTransport.Json(200, """{"version":1,"etag":"e","parameters":{}}"""),
            options => options.RemoteConfigCacheTtl = TimeSpan.FromMinutes(5));
        await using var __ = client;

        await client.RemoteConfig.FetchAsync();
        var published = await client.RemoteConfig.PublishAsync("test");
        await client.RemoteConfig.FetchAsync();

        Assert.Equal(4, published.VersionNumber);
        Assert.Equal("e2", published.ETag);
        Assert.Equal(3, transport.Count);
    }
}
