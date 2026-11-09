using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Inverge.Nexus.Resources;
using Inverge.Nexus.Tests.Support;
using Xunit;

namespace Inverge.Nexus.Tests.Unit;

public class PushAuthTests
{
    private const string Pending = """{"id":"pac_1","identity":"u1","status":"pending","stage":null,"method":"number_match","requesterView":{"number":"42"}}""";

    [Fact]
    public async Task CreateChallenge_sends_the_wire_fields()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(201, Pending));
        await using var __ = client;
        var challenge = new CreatePushAuthChallenge { Identity = "u1", Message = "Sign in to Acme", Method = "number_match", Methods = null, FallbackTo = "+9647501234567" };
        challenge.Fields.Add(new KeyValuePair<string, string>("Amount", "25"));
        challenge.Context["device"] = "Chrome";

        var c = await client.PushAuth.CreateChallengeAsync(challenge);

        Assert.Equal("42", c!.RequesterView!["number"]!.GetValue<string>());
        var req = transport.Single();
        Assert.Equal(("POST", "/partner/push-auth/challenges"), (req.Method, req.Path));
        Assert.False(req.Headers.ContainsKey("X-Nexus-Secret"));
        Assert.True(JsonNode.DeepEquals(
            JsonNode.Parse("""{"identity":"u1","details":{"message":"Sign in to Acme","fields":[{"label":"Amount","value":"25"}],"context":{"device":"Chrome"}},"method":"number_match","fallback":{"to":"+9647501234567"}}"""),
            req.Json), req.Body);
    }

    [Fact]
    public async Task WaitForChallenge_long_polls_until_decided()
    {
        var replies = new Queue<string>(new[] { Pending, Pending, """{"id":"pac_1","status":"approved","receipt":"a.b.c"}""" });
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(200, replies.Dequeue()));
        await using var __ = client;

        var done = await client.PushAuth.WaitForChallengeAsync("pac_1", TimeSpan.FromMinutes(1));

        Assert.Equal("approved", done!.Status);
        Assert.Equal("a.b.c", done.Receipt);
        Assert.Matches(@"^/partner/push-auth/challenges/pac_1\?wait=\d+$", transport.All()[1].Path);
    }

    [Fact]
    public async Task Server_errors_surface_with_their_code()
    {
        var (client, _) = TestClient.Create(_ => RecordingTransport.Json(423, """{"error":{"code":"identity_locked","message":"locked"}}"""));
        await using var __ = client;
        var ex = await Assert.ThrowsAnyAsync<NexusApiException>(() => client.PushAuth.CreateChallengeAsync(new CreatePushAuthChallenge { Identity = "u", Message = "m" }));
        Assert.Equal("identity_locked", ex.Code);
    }

    [Fact]
    public void Webhook_signature_and_replay_window()
    {
        const string body = """{"id":"pawh_1"}""";
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("whsec_x"));
        var sig = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{ts}.n1.{body}"))).ToLowerInvariant();
        Assert.True(PushAuthResource.VerifyWebhook(body, sig, ts, "n1", "whsec_x"));
        Assert.False(PushAuthResource.VerifyWebhook(body + " ", sig, ts, "n1", "whsec_x"));
        var old = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1000).ToString(CultureInfo.InvariantCulture);
        Assert.False(PushAuthResource.VerifyWebhook(body, sig, old, "n1", "whsec_x"));
    }
}
