using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.DocStore;
using Inverge.Nexus.Tests.Support;
using Xunit;

namespace Inverge.Nexus.Tests.Unit;

public class DocStoreTests
{
    private const string Sk = "nxsk_abc123abc123_secret";
    private const string Doc = """{"id":"o1","data":{"total":5},"version":1,"createdAt":"2026-10-28T00:00:00.000Z","updatedAt":"2026-10-28T00:00:00.000Z"}""";

    [Fact]
    public void Field_values_compile_to_operators()
    {
        var spec = FieldValue.ToSpec(new Dictionary<string, object?>
        {
            ["views"] = FieldValue.Increment(2),
            ["tags"] = FieldValue.ArrayUnion("a"),
            ["at"] = FieldValue.ServerTimestamp(),
            ["draft"] = FieldValue.Delete(),
            ["a.b"] = 1,
        });
        Assert.Equal("""{"$inc":{"views":2},"$addToSet":{"tags":{"$each":["a"]}},"$serverTime":["at"],"$unset":["draft"],"$set":{"a.b":1}}""", spec.ToJsonString());
    }

    [Fact]
    public async Task Query_body_and_service_key_header()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(200, "{\"items\":[" + Doc + "],\"nextCursor\":\"c1\",\"hasMore\":true,\"total\":9}"));
        await using var __ = client;

        var page = await client.DocStore(Sk).Collection("orders")
            .Where("status", "in", new[] { "open", "paid" })
            .Where(Filter.Or(Filter.Where("total", ">=", 100), Filter.Where("vip", "==", true)))
            .OrderBy("total", descending: true)
            .Limit(20)
            .WithCount()
            .GetAsync();

        var req = transport.Single();
        Assert.Equal("/partner/docstore/collections/orders/query", req.Path);
        Assert.Equal(Sk, req.Headers["x-nexus-service-key"]);
        Assert.Equal(
            """{"filter":{"$and":[{"status":{"$in":["open","paid"]}},{"$or":[{"total":{"$gte":100}},{"vip":{"$eq":true}}]}]},"sort":[{"field":"total","dir":"desc"}],"limit":20,"count":true}""",
            req.Json!.ToJsonString());
        Assert.Equal(5, page.Docs[0].Get("total")!.GetValue<int>());
        Assert.Equal(("c1", 9), (page.NextCursor, page.Total));
    }

    [Fact]
    public async Task As_user_sends_the_bearer_and_missing_reads_as_not_exists()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(404, """{"error":{"code":"not_found","message":"gone"}}"""));
        await using var __ = client;

        var snap = await client.DocStore(Sk).AsUser("user-jwt").Doc("orders/nope").GetAsync(expand: new[] { "user" });

        Assert.False(snap.Exists);
        var req = transport.Single();
        Assert.Equal("Bearer user-jwt", req.Headers["Authorization"]);
        Assert.False(req.Headers.ContainsKey("x-nexus-service-key"));
        Assert.Contains("expand=user", req.Path);
    }

    [Fact]
    public async Task A_usage_cap_is_reported_at_once_not_retried()
    {
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(429, """{"error":{"code":"usage_limit_exceeded","message":"cap","details":{"metric":"docstore.reads"}}}"""),
            o => { o.MaxRetries = 3; o.RetryBackoff = TimeSpan.FromMilliseconds(1); });
        await using var __ = client;

        var ex = await Assert.ThrowsAsync<NexusRateLimitException>(() => client.DocStore(Sk).Doc("a/b").GetAsync());
        Assert.Equal("usage_limit_exceeded", ex.Code);
        Assert.Equal(1, transport.Count);
    }

    [Fact]
    public async Task Errors_throw_even_when_silent_and_preconditions_are_typed()
    {
        var (client, _) = TestClient.Create(
            _ => RecordingTransport.Json(409, """{"error":{"code":"conflict","message":"x","details":{"reason":"precondition_failed"}}}"""),
            o => o.Silent = true);
        await using var __ = client;

        var ex = await Assert.ThrowsAsync<NexusConflictException>(() => client.DocStore(Sk).Doc("a/b").SetAsync(new Dictionary<string, object?> { ["x"] = 1 }, ifVersion: 1));
        Assert.True(ex.IsPreconditionFailed);
    }

    [Fact]
    public async Task Transaction_retries_after_a_race()
    {
        var balance = 100;
        var version = 3;
        var commits = 0;
        var (client, _) = TestClient.Create(req =>
        {
            if (req.Method == "GET")
            {
                return RecordingTransport.Json(200, "{\"id\":\"a\",\"data\":{\"balance\":" + balance + "},\"version\":" + version + ",\"createdAt\":\"x\",\"updatedAt\":\"x\"}");
            }

            commits++;
            if (commits == 1)
            {
                version++;
                balance += 5;
                return RecordingTransport.Json(409, """{"error":{"code":"conflict","message":"precondition","details":{"reason":"precondition_failed"}}}""");
            }

            var op = (JsonObject)req.Object["operations"]![0]!;
            Assert.Equal(version, op["precondition"]!["version"]!.GetValue<int>());
            balance = op["data"]!["balance"]!.GetValue<int>();
            return RecordingTransport.Json(200, """{"results":[{"index":0,"status":"ok","collection":"accounts","id":"a","version":9}]}""");
        });
        await using var __ = client;

        var db = client.DocStore(Sk);
        var account = db.Doc("accounts/a");
        var attempts = 0;
        var result = await db.RunTransactionAsync(async tx =>
        {
            attempts++;
            var snap = await tx.GetAsync(account);
            tx.Set(account, new Dictionary<string, object?> { ["balance"] = snap.Get("balance")!.GetValue<int>() - 40 });
            return "done";
        });

        Assert.Equal("done", result);
        Assert.Equal(2, attempts);
        Assert.Equal(65, balance);
    }

    [Fact]
    public async Task Switched_off_throws_without_a_request()
    {
        var (client, transport) = TestClient.Create(configure: o => o.Services[NexusServices.DocStore] = false);
        await using var __ = client;

        await Assert.ThrowsAsync<NexusServiceDisabledException>(() => client.DocStore(Sk).Doc("a/b").GetAsync());
        Assert.Equal(0, transport.Count);
    }

    [Fact]
    public async Task Change_stream_resumes_from_next()
    {
        var calls = 0;
        var (client, transport) = TestClient.Create(_ => ++calls == 1
            ? RecordingTransport.Json(200, """{"changes":[{"seq":4,"collection":"c","id":"x","op":"create","version":1}],"next":4,"hasMore":true}""")
            : RecordingTransport.Json(200, """{"changes":[{"seq":5,"collection":"c","id":"y","op":"delete","version":2}],"next":5,"hasMore":false}"""));
        await using var __ = client;

        var seen = new List<long>();
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DocStore(Sk).WatchChangesAsync(c =>
        {
            seen.Add(c.Seq);
            if (seen.Count == 2)
            {
                cts.Cancel();
            }

            return Task.CompletedTask;
        }, since: 3, pollInterval: TimeSpan.FromMilliseconds(10), cancellationToken: cts.Token));

        Assert.Equal(new long[] { 4, 5 }, seen);
        var paths = transport.All().Select(r => r.Path).ToList();
        Assert.Contains("since=3", paths[0]);
        Assert.Contains("since=4", paths[1]);
    }

    [Fact]
    public void Webhook_verification()
    {
        const string body = """{"event":"docstore.document.create","id":"o1"}""";
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes("whsec_x"));
        var sig = string.Concat(h.ComputeHash(Encoding.UTF8.GetBytes(ts + ".n1." + body)).Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
        var headers = new Dictionary<string, string> { ["X-Nexus-Signature"] = sig, ["X-Nexus-Timestamp"] = ts, ["X-Nexus-Nonce"] = "n1" };

        Assert.Equal("o1", DocStoreWebhook.Verify(body, headers, "whsec_x")["id"]!.GetValue<string>());
        Assert.Throws<NexusException>(() => DocStoreWebhook.Verify(body + " ", headers, "whsec_x"));
    }

    [Fact]
    public async Task Rejects_a_non_service_key()
    {
        var (client, _) = TestClient.Create();
        await using var __ = client;
        Assert.Throws<NexusConfigurationException>(() => client.DocStore("nxs_public"));
    }
}
