using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Inverge.Nexus.Tests.Support;
using Xunit;

namespace Inverge.Nexus.Tests.Unit;

[Collection("AmbientContext")]
public class BatchingTests : IDisposable
{
    public void Dispose() => NexusContext.Clear();

    private static void Buffered(NexusOptions options)
    {
        options.Batch = true;
        options.MaxBatch = 3;
        options.FlushInterval = TimeSpan.FromMinutes(5);
    }

    [Fact]
    public async Task Tracking_does_not_send_until_flushed()
    {
        NexusContext.Clear();
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(202, """{"written":1}"""), Buffered);
        await using var _ = client;

        client.Track("a");
        Assert.Equal(0, transport.Count);
        Assert.Equal(1, client.Pending);

        await client.FlushAsync();

        Assert.Equal(1, transport.Count);
        Assert.Equal(0, client.Pending);
    }

    [Fact]
    public async Task Items_sharing_an_identity_go_in_one_request()
    {
        NexusContext.Clear();
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(202, """{"written":2}"""), Buffered);
        await using var _ = client;

        using (NexusContext.Scope(distinctId: "user_1"))
        {
            client.Track("a");
            client.Track("b");
        }

        await client.FlushAsync();

        var request = transport.Single();
        Assert.Equal("user_1", request.Field("distinctId"));
        Assert.Equal(2, (request.Object["events"] as JsonArray)!.Count);
    }

    [Fact]
    public async Task Items_with_different_identities_are_split_per_request()
    {
        // The ingest endpoints take one identity per request, so mixing two people
        // into one batch would attribute half the events to the wrong person.
        NexusContext.Clear();
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(202, """{"written":1}"""), Buffered);
        await using var _ = client;

        using (NexusContext.Scope(distinctId: "user_1"))
        {
            client.Track("a");
        }

        using (NexusContext.Scope(distinctId: "user_2"))
        {
            client.Track("b");
        }

        await client.FlushAsync();

        var identities = transport.All().Select(r => r.Field("distinctId")).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "user_1", "user_2" }, identities);
    }

    [Fact]
    public async Task A_group_larger_than_max_batch_is_chunked()
    {
        NexusContext.Clear();
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(202, """{"written":3}"""), Buffered);
        await using var _ = client;

        for (var i = 0; i < 7; i++)
        {
            client.Track("event_" + i);
        }

        await client.FlushAsync();

        var sizes = transport.All()
            .Select(r => (r.Object["events"] as JsonArray)!.Count)
            .OrderByDescending(x => x)
            .ToArray();

        Assert.Equal(7, sizes.Sum());
        Assert.All(sizes, size => Assert.True(size <= 3, "A chunk exceeded MaxBatch."));
    }

    [Fact]
    public async Task A_full_buffer_drops_and_counts_rather_than_growing()
    {
        NexusContext.Clear();
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(202, """{"written":1}"""),
            options =>
            {
                options.Batch = true;
                options.MaxBatch = 2;
                options.MaxQueue = 2;
                options.FlushInterval = TimeSpan.FromMinutes(5);
            });
        await using var _ = client;

        for (var i = 0; i < 50; i++)
        {
            client.Logs.Information("line " + i);
        }

        Assert.True(client.Dropped > 0, "A full buffer should drop and count, not grow.");
        Assert.True(client.Pending <= 2, "Pending exceeded MaxQueue.");
    }

    [Fact]
    public async Task A_delivery_failure_never_reaches_the_caller()
    {
        NexusContext.Clear();
        var (client, _) = TestClient.Create(__ => RecordingTransport.Json(500, "{}"), Buffered);
        await using var ___ = client;

        client.Track("a");

        // Flushing a batch whose delivery fails must not throw: Track() is void, and
        // telemetry failures are not the caller's problem.
        await client.FlushAsync();
        Assert.Equal(0, client.Pending);
    }

    [Fact]
    public async Task Disposing_flushes_what_is_buffered()
    {
        NexusContext.Clear();
        var transport = new RecordingTransport(_ => RecordingTransport.Json(202, """{"written":1}"""));
        var client = TestClient.Create(transport, Buffered);

        client.Track("a");
        client.Logs.Warning("b");

        await client.DisposeAsync();

        Assert.Equal(2, transport.Count);
    }

    [Fact]
    public async Task Requests_after_dispose_are_dropped_rather_than_thrown()
    {
        NexusContext.Clear();
        var (client, transport) = TestClient.Create();
        await client.DisposeAsync();

        var result = await client.Sessions.IdentifyAsync("user_1");

        Assert.False(result.HasPayload);
        Assert.Equal(0, transport.Count);
    }

    [Fact]
    public async Task Batch_size_limits_are_enforced_locally()
    {
        var (client, _) = TestClient.Create();
        await using var __ = client;

        var tooMany = Enumerable.Range(0, 1001)
            .Select(i => new Models.NexusEvent("e" + i))
            .ToArray();

        await Assert.ThrowsAsync<ArgumentException>(() => client.Events.BatchAsync(tooMany));
    }
}

/// <summary>
/// The ambient context is process-wide state, so the suites that bind it must not
/// run in parallel with each other.
/// </summary>
[CollectionDefinition("AmbientContext", DisableParallelization = true)]
public class AmbientContextCollection
{
}
