using System;
using System.Threading.Tasks;
using Inverge.Nexus.Models;
using Inverge.Nexus.Tests.Support;
using Xunit;

namespace Inverge.Nexus.Tests.Unit;

/// <summary>
/// Retries are decided per endpoint, not per verb. Repeating an ingest call is
/// harmless; repeating a push send or a voice leg is not.
/// </summary>
public class RetryTests
{
    private static void Fast(NexusOptions options)
    {
        options.MaxRetries = 2;
        options.RetryBackoff = TimeSpan.FromMilliseconds(1);
    }

    [Fact]
    public async Task A_retryable_call_is_retried_on_a_server_error()
    {
        var attempts = 0;
        var (client, transport) = TestClient.Create(
            _ =>
            {
                attempts++;
                return attempts < 3
                    ? RecordingTransport.Json(503, "{}")
                    : RecordingTransport.Json(202, """{"written":1}""");
            },
            Fast);
        await using var _ = client;

        var written = await client.Events.BatchAsync(new[] { new NexusEvent("a") });

        Assert.Equal(1, written);
        Assert.Equal(3, transport.Count);
    }

    [Fact]
    public async Task A_non_retryable_call_is_attempted_once()
    {
        // A push send that times out and is retried delivers the notification twice.
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(503, "{}"), Fast);
        await using var _ = client;

        await Assert.ThrowsAsync<NexusServerException>(
            () => client.Push.SendToUserAsync("user_1", "Hi"));

        Assert.Equal(1, transport.Count);
    }

    [Fact]
    public async Task A_client_error_is_not_retried_even_when_the_call_is_retryable()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(400, "{}"), Fast);
        await using var _ = client;

        await Assert.ThrowsAsync<NexusValidationException>(
            () => client.Events.BatchAsync(new[] { new NexusEvent("a") }));

        Assert.Equal(1, transport.Count);
    }

    [Fact]
    public async Task A_billing_suspension_is_not_retried()
    {
        // Retrying cannot clear a suspension; it only spends the caller's latency
        // budget before reporting the real problem.
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(402, """{"error":{"code":"billing_suspended","message":"suspended"}}"""),
            Fast);
        await using var _ = client;

        await Assert.ThrowsAsync<NexusBillingSuspendedException>(
            () => client.Events.BatchAsync(new[] { new NexusEvent("a") }));

        Assert.Equal(1, transport.Count);
    }

    [Fact]
    public async Task Retries_stop_at_the_configured_limit()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(500, "{}"), Fast);
        await using var _ = client;

        await Assert.ThrowsAsync<NexusServerException>(
            () => client.Events.BatchAsync(new[] { new NexusEvent("a") }));

        Assert.Equal(3, transport.Count);
    }
}
