using System;
using System.Threading.Tasks;
using Inverge.Nexus.Models;
using Inverge.Nexus.Tests.Support;
using Xunit;

namespace Inverge.Nexus.Tests.Unit;

/// <summary>
/// After one 402 billing_suspended the client stops calling the API until the
/// pause ends — a suspended app must not keep hammering it (or, by reporting its
/// own failures, turn each refused call into another one).
/// </summary>
public class BillingSuspensionTests
{
    private const string Suspended =
        """{"error":{"code":"billing_suspended","message":"API access is suspended: USD 54.56 is past due on invoice INV-2026-10-0001.","details":{"retryAfterSeconds":120}}}""";

    [Fact]
    public async Task One_suspension_stops_every_further_request()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(402, Suspended));
        await using var _ = client;

        var first = await Assert.ThrowsAsync<NexusBillingSuspendedException>(
            () => client.Push.SendToUserAsync("user_1", "Hi"));
        Assert.False(first.IsLocal);

        for (var i = 0; i < 50; i++)
        {
            var again = await Assert.ThrowsAsync<NexusBillingSuspendedException>(
                () => client.Push.SendToUserAsync("user_1", "Hi"));
            Assert.True(again.IsLocal);
            Assert.True(again.RetryAfter > TimeSpan.FromSeconds(100));
        }

        Assert.Equal(1, transport.Count);
    }

    [Fact]
    public async Task Telemetry_is_dropped_quietly_while_suspended()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(402, Suspended));
        await using var _ = client;

        await client.Errors.CaptureAsync("trip it");
        await client.Errors.CaptureAsync("held back");
        await client.Events.BatchAsync(new[] { new NexusEvent("signup") });

        Assert.Equal(1, transport.Count);
    }

    [Fact]
    public async Task Reset_lets_calls_through_again()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(402, Suspended));
        await using var _ = client;

        await client.Errors.CaptureAsync("trip it");
        client.ResetBillingSuspension();
        await client.Errors.CaptureAsync("after paying");

        Assert.Equal(2, transport.Count);
    }

    [Fact]
    public async Task An_empty_messaging_wallet_is_not_a_suspension()
    {
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(402, """{"error":{"code":"insufficient_balance","message":"Top up"}}"""));
        await using var _ = client;

        var error = await Assert.ThrowsAsync<NexusApiException>(
            () => client.Messaging.SendAsync(new SendMessage("sms", "+9647501234567") { Body = "x" }));
        Assert.IsNotType<NexusBillingSuspendedException>(error);

        await Assert.ThrowsAsync<NexusApiException>(
            () => client.Messaging.SendAsync(new SendMessage("sms", "+9647501234567") { Body = "y" }));
        Assert.Equal(2, transport.Count);
    }

    [Fact]
    public async Task Delivery_failures_are_never_reported_to_errors()
    {
        Assert.True(NexusDeliveryFailure.Is(new NexusServerException("down", 503, null, null, null, null, null)));
        Assert.True(NexusDeliveryFailure.Is(new InvalidOperationException("wrapped", new NexusTransportException("timeout"))));
        Assert.False(NexusDeliveryFailure.Is(new NexusValidationException("bad", 422, null, null, null, null, null)));
        Assert.False(NexusDeliveryFailure.Is(new InvalidOperationException("app bug")));

        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(202, "{}"));
        await using var _ = client;
        await client.Errors.CaptureExceptionAsync(new NexusServerException("down", 503, null, null, null, null, null));
        Assert.Equal(0, transport.Count);
    }
}
