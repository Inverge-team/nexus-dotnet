using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Inverge.Nexus.Resources;
using Inverge.Nexus.Tests.Support;
using Xunit;

namespace Inverge.Nexus.Tests.Unit;

public class ShieldTests
{
    [Fact]
    public async Task Verify_request_reads_headers_and_binds_the_body()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(200,
            """{"valid":true,"decision":"allow","mode":"enforce","reason":null,"resource":"checkout","policy":"checkout","token":{"score":91,"level":"strong","providers":["key_attestation"],"deviceId":"d1","appId":"a1","platform":"android"}}"""));
        await using var __ = client;

        var v = await client.Shield.VerifyRequestAsync(
            new[] { new KeyValuePair<string, string?>("x-nexus-shield", "DPoP tok"), new KeyValuePair<string, string?>("DPoP", "proof") },
            "post", "https://api.example.com/checkout", Encoding.UTF8.GetBytes("""{"total":42}"""), "checkout");

        Assert.True(v.Allowed);
        Assert.Equal(91, v.Score);
        Assert.Equal("strong", v.Level);
        Assert.Equal(new[] { "key_attestation" }, v.Providers);
        var req = transport.Single();
        Assert.Equal("/partner/shield/verify", req.Path);
        Assert.Equal("tok", req.Field("token"));
        Assert.Equal("POST", req.Field("method"));
        Assert.Equal(ShieldResource.BodyHash("""{"total":42}"""), req.Field("bodyHash"));
    }

    [Fact]
    public async Task A_missing_token_never_calls_the_api()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Empty(500));
        await using var __ = client;

        var v = await client.Shield.VerifyAsync(null, null, "GET", "https://a/b");

        Assert.False(v.Allowed);
        Assert.Equal("token_missing", v.Reason);
        Assert.Equal(0, transport.Count);
    }

    [Fact]
    public void Body_hash_is_base64url_sha256()
        => Assert.Equal("47DEQpj8HBSa-_TImW-5JCeuQeRkm5NMpJWZG3hSuFU", ShieldResource.BodyHash(""));
}
