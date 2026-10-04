using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Inverge.Nexus.Models;
using Inverge.Nexus.Tests.Support;
using Xunit;

namespace Inverge.Nexus.Tests.Unit;

/// <summary>
/// Locks down the wire contract: paths, field names, and the shapes the API
/// validates strictly. These are the assertions that catch an accidental rename.
/// </summary>
public class PayloadTests
{
    [Fact]
    public async Task Identify_posts_camel_cased_fields()
    {
        var (client, transport) = TestClient.Create();
        await using var _ = client;

        await client.Sessions.IdentifyAsync(
            "user_1", email: "a@b.com", name: "A", phone: "+1", traits: new Dictionary<string, object?> { ["plan"] = "pro" });

        var request = transport.Single();
        Assert.Equal("POST", request.Method);
        Assert.Equal("/partner/sessions/identify", request.Path);
        Assert.Equal("user_1", request.Field("distinctId"));
        Assert.Equal("a@b.com", request.Field("email"));
        Assert.Equal("pro", request.Object["traits"]!["plan"]!.ToString());
    }

    [Fact]
    public async Task Unset_fields_are_absent_rather_than_null()
    {
        // The API validates strictly and rejects explicit nulls, so an omitted
        // argument has to disappear from the body entirely.
        var (client, transport) = TestClient.Create();
        await using var _ = client;

        await client.Sessions.IdentifyAsync("user_1");

        var request = transport.Single();
        Assert.True(request.Has("distinctId"));
        Assert.False(request.Has("email"));
        Assert.False(request.Has("name"));
        Assert.False(request.Has("phone"));
        Assert.False(request.Has("traits"));
    }

    [Fact]
    public async Task Survey_answers_serialize_as_an_object_even_when_absent()
    {
        // Dismissing a survey sends no answers, and the server rejects `[]` for
        // this field — it must be `{}`.
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(202, """{"id":"r1","completed":false}"""));
        await using var _ = client;

        await client.Surveys.DismissAsync("survey_1");

        var answers = transport.Single().Object["answers"];
        Assert.IsType<JsonObject>(answers);
        Assert.Equal("{}", answers!.ToJsonString());
    }

    [Fact]
    public async Task Live_activity_content_state_serializes_as_an_object_even_when_absent()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(200, """{"ios":1,"android":0}"""));
        await using var _ = client;

        await client.LiveActivities.StartAsync("DeliveryAttributes", "order_42");

        Assert.IsType<JsonObject>(transport.Single().Object["contentState"]);
    }

    [Fact]
    public async Task Realtime_emit_always_sends_a_payload_key()
    {
        // Subscribers receive whatever `payload` holds, including null. Dropping the
        // key for a null payload would change what listeners see.
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(200, """{"ok":true,"recipients":0}"""));
        await using var _ = client;

        await client.Realtime.EmitAsync("orders:42", "ping");

        var request = transport.Single();
        Assert.Equal("/partner/rooms/orders%3A42/emit", request.Path);
        Assert.True(request.Has("payload"));
        Assert.Null(request.Object["payload"]);
    }

    [Fact]
    public async Task Room_names_are_url_encoded_in_the_path()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(200, """{"ok":true}"""));
        await using var _ = client;

        await client.Realtime.EmitAsync("tenant/42:orders#a b", "ping");

        Assert.Equal("/partner/rooms/tenant%2F42%3Aorders%23a%20b/emit", transport.Single().Path);
    }

    [Fact]
    public async Task Push_opened_sends_the_campaign_id_and_token()
    {
        // The endpoint is keyed by campaign and device token; both are required.
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(200, """{"ok":true}"""));
        await using var _ = client;

        await client.Push.OpenedAsync("campaign_7", "device-token");

        var request = transport.Single();
        Assert.Equal("/partner/push/opened", request.Path);
        Assert.Equal("campaign_7", request.Field("campaignId"));
        Assert.Equal("device-token", request.Field("token"));
    }

    [Fact]
    public async Task Events_batch_groups_the_context_outside_the_items()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(202, """{"written":2}"""));
        await using var _ = client;

        var written = await client.Events.BatchAsync(
            new[] { new NexusEvent("a"), new NexusEvent("b") },
            new NexusTelemetryContext { DistinctId = "user_1", AppVersion = "1.2.3" });

        Assert.Equal(2, written);

        var request = transport.Single();
        Assert.Equal("/partner/events", request.Path);
        Assert.Equal("user_1", request.Field("distinctId"));
        Assert.Equal("1.2.3", request.Field("appVersion"));
        Assert.Equal(2, (request.Object["events"] as JsonArray)!.Count);
    }

    [Fact]
    public async Task Remote_config_fetch_sends_the_etag_as_a_conditional_header()
    {
        var responses = 0;
        var (client, transport) = TestClient.Create(_ =>
        {
            responses++;
            return responses == 1
                ? RecordingTransport.Json(200, """{"version":3,"etag":"abc","parameters":{"fee":{"value":5}}}""")
                : RecordingTransport.Json(200, """{"version":3,"etag":"abc","notModified":true,"parameters":{}}""");
        });
        await using var _ = client;

        var first = await client.RemoteConfig.FetchAsync();
        Assert.Equal(5, first.GetInt32("fee"));
        Assert.False(transport.Last.Headers.ContainsKey("If-None-Match"));

        var second = await client.RemoteConfig.FetchAsync(fresh: true);
        Assert.Equal("abc", transport.Last.Headers["If-None-Match"]);

        // A "not modified" response carries no parameters; the snapshot has to keep
        // serving the values it already resolved rather than falling back to zero.
        Assert.True(second.NotModified);
        Assert.Equal(5, second.GetInt32("fee"));
    }

    [Fact]
    public async Task Voice_leg_sends_every_leg_field()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(
            201, """{"leg":{"id":"leg_1","sessionId":"s_1"},"join":{"room":"r","region":"eu","access":{"token":"t"},"turn":{"urls":[]}}}"""));
        await using var _ = client;

        var result = await client.Voice.AddLegAsync(
            "s_1", "callee", "pstn", direction: "outbound", address: "+964700", callerId: "+964701", identityId: "agent_1");

        var request = transport.Single();
        Assert.Equal("/partner/voice/legs", request.Path);
        Assert.Equal("s_1", request.Field("sessionId"));
        Assert.Equal("callee", request.Field("role"));
        Assert.Equal("pstn", request.Field("endpointType"));
        Assert.Equal("outbound", request.Field("direction"));
        Assert.Equal("+964700", request.Field("address"));
        Assert.Equal("+964701", request.Field("callerId"));
        Assert.Equal("agent_1", request.Field("identityId"));

        Assert.True(result.IsSuccess);
        Assert.Equal("leg_1", result.Leg!.Id);
        Assert.Equal("t", result.Join!.Access!.Token);
    }

    [Theory]
    [InlineData("ring")]
    [InlineData("answer")]
    [InlineData("hold")]
    [InlineData("resume")]
    public async Task Leg_actions_post_to_their_own_path(string action)
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(200, """{"ok":true}"""));
        await using var _ = client;

        var task = action switch
        {
            "ring" => client.Voice.RingAsync("leg_1"),
            "answer" => client.Voice.AnswerAsync("leg_1"),
            "hold" => client.Voice.HoldAsync("leg_1"),
            _ => client.Voice.ResumeAsync("leg_1"),
        };

        await task;
        Assert.Equal("/partner/voice/legs/" + action, transport.Single().Path);
    }

    [Fact]
    public async Task Hangup_sends_its_reason()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(200, """{"ok":true}"""));
        await using var _ = client;

        await client.Voice.HangupAsync("leg_1", "declined");

        Assert.Equal("declined", transport.Single().Field("reason"));
    }

    [Fact]
    public async Task Api_key_and_user_agent_go_on_every_request()
    {
        var (client, transport) = TestClient.Create();
        await using var _ = client;

        await client.Sessions.IdentifyAsync("user_1");

        var headers = transport.Single().Headers;
        Assert.Equal(TestClient.ApiKey, headers["x-api-key"]);
        Assert.StartsWith("Inverge.Nexus.NET/", headers["User-Agent"]);
        Assert.Equal("application/json", headers["Content-Type"]);
    }
}
