using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Tests.Support;
using Xunit;

namespace Inverge.Nexus.Tests.Unit;

/// <summary>
/// Exercises the background machinery — the flush loop and the fire-and-forget
/// dispatcher — rather than only the explicit-flush path the other suites use.
/// </summary>
[Collection("AmbientContext")]
public class ConcurrencyTests : IDisposable
{
    public void Dispose() => NexusContext.Clear();

    [Fact]
    public async Task The_background_loop_ships_a_partial_batch_when_the_interval_elapses()
    {
        NexusContext.Clear();
        var delivered = new SemaphoreSlim(0);
        var transport = new RecordingTransport(_ =>
        {
            delivered.Release();
            return RecordingTransport.Json(202, """{"written":1}""");
        });

        await using var client = TestClient.Create(transport, options =>
        {
            options.Batch = true;
            options.MaxBatch = 100;                                    // never reached
            options.FlushInterval = TimeSpan.FromMilliseconds(100);    // so the timer must fire
        });

        client.Track("a");

        Assert.True(
            await delivered.WaitAsync(TimeSpan.FromSeconds(5)),
            "The interval-driven flush never delivered the buffered event.");

        Assert.Equal(1, transport.Count);
    }

    [Fact]
    public async Task The_background_loop_ships_immediately_once_max_batch_is_reached()
    {
        NexusContext.Clear();
        var delivered = new SemaphoreSlim(0);
        var transport = new RecordingTransport(_ =>
        {
            delivered.Release();
            return RecordingTransport.Json(202, """{"written":3}""");
        });

        await using var client = TestClient.Create(transport, options =>
        {
            options.Batch = true;
            options.MaxBatch = 3;
            options.FlushInterval = TimeSpan.FromMinutes(5);   // so only the size trigger can fire
        });

        client.Track("a");
        client.Track("b");
        client.Track("c");

        Assert.True(
            await delivered.WaitAsync(TimeSpan.FromSeconds(5)),
            "Reaching MaxBatch did not trigger a send.");
    }

    [Fact]
    public async Task Concurrent_writers_lose_nothing()
    {
        NexusContext.Clear();
        var transport = new RecordingTransport(_ => RecordingTransport.Json(202, """{"written":1}"""));

        await using var client = TestClient.Create(transport, options =>
        {
            options.Batch = true;
            options.MaxBatch = 10;
            options.MaxQueue = 10_000;
            options.FlushInterval = TimeSpan.FromMilliseconds(50);
        });

        await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (var i = 0; i < 125; i++)
            {
                client.Track("event", new { worker, i });
            }
        })));

        await client.FlushAsync(TimeSpan.FromSeconds(10));

        var sent = transport.All().Sum(r => r.Object["events"]!.AsArray().Count);
        Assert.Equal(1000, sent + (int)client.Dropped);
        Assert.Equal(0, client.Dropped);
    }

    [Fact]
    public async Task A_background_client_returns_immediately_and_still_delivers()
    {
        NexusContext.Clear();
        var delivered = new SemaphoreSlim(0);
        var transport = new RecordingTransport(_ =>
        {
            delivered.Release();
            return RecordingTransport.Json(200, """{"sent":1,"failed":0,"recipients":1}""");
        });

        await using var client = TestClient.Create(transport);
        var background = client.Background();

        // Fire-and-forget: nothing is returned, so the result is empty by design.
        var result = await background.Push.SendToUserAsync("user_1", "Welcome");
        Assert.False(result.HasPayload);

        Assert.True(
            await delivered.WaitAsync(TimeSpan.FromSeconds(5)),
            "The background dispatcher never delivered the request.");

        await background.DisposeAsync();
    }

    [Fact]
    public async Task A_background_client_swallows_delivery_failures()
    {
        NexusContext.Clear();
        var attempted = new SemaphoreSlim(0);
        var transport = new RecordingTransport(_ =>
        {
            attempted.Release();
            return RecordingTransport.Json(500, "{}");
        });

        await using var client = TestClient.Create(transport);
        var background = client.Background();

        // A fire-and-forget call cannot surface a failure to its caller; it has
        // already returned by the time delivery is attempted.
        await background.Errors.CaptureAsync("boom");

        Assert.True(await attempted.WaitAsync(TimeSpan.FromSeconds(5)));
        await background.DisposeAsync();
    }

    [Fact]
    public async Task Disposing_twice_is_harmless()
    {
        NexusContext.Clear();
        var (client, _) = TestClient.Create();

        await client.DisposeAsync();
        await client.DisposeAsync();
        client.Dispose();

        Assert.True(client.IsDisposed);
    }
}
