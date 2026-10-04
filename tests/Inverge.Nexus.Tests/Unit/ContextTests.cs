using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Inverge.Nexus.Tests.Support;
using Xunit;

namespace Inverge.Nexus.Tests.Unit;

[Collection("AmbientContext")]
public class ContextTests : IDisposable
{
    public void Dispose() => NexusContext.Clear();

    [Fact]
    public void Scope_restores_the_previous_context()
    {
        NexusContext.Clear();
        using (NexusContext.Scope(distinctId: "outer"))
        {
            Assert.Equal("outer", NexusContext.Current.DistinctId);

            using (NexusContext.Scope(distinctId: "inner", url: "/checkout"))
            {
                Assert.Equal("inner", NexusContext.Current.DistinctId);
                Assert.Equal("/checkout", NexusContext.Current.Url);
            }

            Assert.Equal("outer", NexusContext.Current.DistinctId);
            Assert.Null(NexusContext.Current.Url);
        }

        Assert.Null(NexusContext.Current.DistinctId);
    }

    [Fact]
    public void A_nested_scope_inherits_what_it_does_not_set()
    {
        NexusContext.Clear();
        using var outer = NexusContext.Scope(distinctId: "user_1", country: "IQ");
        using var inner = NexusContext.Scope(url: "/pay");

        Assert.Equal("user_1", NexusContext.Current.DistinctId);
        Assert.Equal("IQ", NexusContext.Current.Country);
        Assert.Equal("/pay", NexusContext.Current.Url);
    }

    [Fact]
    public async Task Context_flows_across_await_boundaries()
    {
        NexusContext.Clear();
        using var scope = NexusContext.Scope(distinctId: "user_1");

        await Task.Yield();
        await Task.Run(() => Assert.Equal("user_1", NexusContext.Current.DistinctId));

        Assert.Equal("user_1", NexusContext.Current.DistinctId);
    }

    [Fact]
    public async Task Concurrent_flows_do_not_see_each_others_identity()
    {
        NexusContext.Clear();

        async Task<string?> Isolated(string id)
        {
            using var scope = NexusContext.Scope(distinctId: id);
            await Task.Delay(5);
            return NexusContext.Current.DistinctId;
        }

        var results = await Task.WhenAll(Isolated("a"), Isolated("b"), Isolated("c"));

        Assert.Equal(new[] { "a", "b", "c" }, results);
        Assert.Null(NexusContext.Current.DistinctId);
    }

    [Fact]
    public void Reset_keeps_the_device_and_starts_a_new_session()
    {
        NexusContext.Clear();
        NexusContext.Set(new NexusTelemetryContext
        {
            DistinctId = "user_1",
            DeviceKey = "dev_1",
            SessionKey = "sess_old",
            AppVersion = "1.0.0",
        });

        NexusContext.Reset();

        Assert.Null(NexusContext.Current.DistinctId);
        Assert.Equal("dev_1", NexusContext.Current.DeviceKey);
        Assert.Equal("1.0.0", NexusContext.Current.AppVersion);
        Assert.NotNull(NexusContext.Current.SessionKey);
        Assert.NotEqual("sess_old", NexusContext.Current.SessionKey);
    }

    [Fact]
    public async Task Resolution_order_is_options_then_ambient_then_call()
    {
        NexusContext.Clear();
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(202, """{"written":1}"""),
            options =>
            {
                options.AppVersion = "from-options";
                options.Release = "r1";
                options.OsType = "server";
            });
        await using var _ = client;

        using (NexusContext.Scope(distinctId: "ambient", appVersion: "from-ambient"))
        {
            await client.Events.BatchAsync(
                new[] { new Models.NexusEvent("checkout") },
                new NexusTelemetryContext { AppVersion = "from-call" });
        }

        var request = transport.Single();
        Assert.Equal("from-call", request.Field("appVersion"));
        Assert.Equal("ambient", request.Field("distinctId"));
        Assert.Equal("r1", request.Field("release"));
        Assert.Equal("server", request.Field("osType"));
    }

    [Fact]
    public void Ambient_properties_layer_under_call_properties()
    {
        NexusContext.Clear();
        var merged = new NexusTelemetryContext
        {
            Properties = new Dictionary<string, object?> { ["a"] = 1, ["b"] = 1 },
        }.Merge(new NexusTelemetryContext
        {
            Properties = new Dictionary<string, object?> { ["b"] = 2, ["c"] = 3 },
        });

        Assert.Equal(1, merged.Properties!["a"]);
        Assert.Equal(2, merged.Properties["b"]);
        Assert.Equal(3, merged.Properties["c"]);
    }

    [Fact]
    public async Task Scoped_properties_reach_captured_events()
    {
        NexusContext.Clear();
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(202, """{"written":1}"""),
            options => options.DefaultProperties["tier"] = "free");
        await using var _ = client;

        using (NexusContext.Scope(properties: new Dictionary<string, object?> { ["requestId"] = "rq_1", ["tier"] = "pro" }))
        {
            await client.Events.BatchAsync(new[] { new Models.NexusEvent("checkout", new Dictionary<string, object?> { ["total"] = 42 }) });
        }

        var properties = transport.Single().Object["events"]![0]!["properties"]!;
        Assert.Equal("rq_1", properties["requestId"]!.ToString());
        Assert.Equal("42", properties["total"]!.ToString());
        // Scope wins over the client default; the call would win over both.
        Assert.Equal("pro", properties["tier"]!.ToString());
    }
}
