using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Inverge.Nexus.Tests.Support;
using Xunit;

namespace Inverge.Nexus.Tests.Unit;

public class ErrorMappingTests
{
    private const string Envelope =
        """{"error":{"code":"validation_failed","message":"distinctId must be a string","details":{"field":"distinctId"}}}""";

    [Theory]
    [InlineData(400, typeof(NexusValidationException))]
    [InlineData(401, typeof(NexusAuthenticationException))]
    [InlineData(403, typeof(NexusPermissionDeniedException))]
    [InlineData(404, typeof(NexusNotFoundException))]
    [InlineData(422, typeof(NexusValidationException))]
    [InlineData(429, typeof(NexusRateLimitException))]
    [InlineData(500, typeof(NexusServerException))]
    [InlineData(503, typeof(NexusServerException))]
    [InlineData(418, typeof(NexusApiException))]
    public async Task Status_maps_to_the_right_exception(int status, Type expected)
    {
        var (client, _) = TestClient.Create(_ => RecordingTransport.Json(status, Envelope));
        await using var __ = client;

        var exception = await Assert.ThrowsAnyAsync<NexusApiException>(
            () => client.Sessions.IdentifyAsync("user_1"));

        Assert.IsType(expected, exception);
        Assert.Equal(status, exception.Status);
    }

    [Fact]
    public async Task Error_envelope_fields_are_surfaced()
    {
        var (client, _) = TestClient.Create(_ => RecordingTransport.Json(400, Envelope));
        await using var __ = client;

        var exception = await Assert.ThrowsAsync<NexusValidationException>(
            () => client.Sessions.IdentifyAsync("user_1"));

        Assert.Equal("validation_failed", exception.Code);
        Assert.Equal("distinctId must be a string", exception.Message);
        Assert.Equal("distinctId", exception.Details!["field"]!.ToString());
        Assert.Equal("POST", exception.Method);
        Assert.Equal("/partner/sessions/identify", exception.Path);
    }

    [Fact]
    public async Task Billing_suspension_gets_its_own_exception_type()
    {
        // The data plane answers 402 while an organisation has unpaid invoices past
        // the grace period. It must be distinguishable from a bad key, because the
        // fix is a payment rather than a credential.
        var (client, _) = TestClient.Create(_ => RecordingTransport.Json(
            402, """{"error":{"code":"billing_suspended","message":"Data plane suspended"}}"""));
        await using var __ = client;

        var exception = await Assert.ThrowsAsync<NexusBillingSuspendedException>(
            () => client.Sessions.IdentifyAsync("user_1"));

        Assert.Equal(402, exception.Status);
        Assert.Equal("billing_suspended", exception.Code);
    }

    [Fact]
    public async Task Rate_limit_reads_retry_after_seconds()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["retry-after"] = "12" };
        var (client, _) = TestClient.Create(_ => RecordingTransport.Json(429, "{}", headers));
        await using var __ = client;

        var exception = await Assert.ThrowsAsync<NexusRateLimitException>(
            () => client.Sessions.IdentifyAsync("user_1"));

        Assert.Equal(TimeSpan.FromSeconds(12), exception.RetryAfter);
    }

    [Fact]
    public async Task A_body_that_is_not_json_still_produces_a_usable_error()
    {
        // A proxy or load balancer in front of the API can answer with HTML. The
        // SDK must report the status rather than fail while parsing the failure.
        var (client, _) = TestClient.Create(_ => RecordingTransport.Json(502, "<html>Bad Gateway</html>"));
        await using var __ = client;

        var exception = await Assert.ThrowsAsync<NexusServerException>(
            () => client.Sessions.IdentifyAsync("user_1"));

        Assert.Equal(502, exception.Status);
        Assert.Contains("502", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Bad Gateway", exception.ResponseBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Silent_mode_swallows_failures()
    {
        var (client, _) = TestClient.Create(
            _ => RecordingTransport.Json(500, "{}"), options => options.Silent = true);
        await using var __ = client;

        var result = await client.Sessions.IdentifyAsync("user_1");

        Assert.False(result.HasPayload);
        Assert.Null(result.SubjectId);
    }

    [Fact]
    public async Task Transport_failures_become_transport_exceptions()
    {
        var transport = new ThrowingTransport();
        await using var client = new NexusClient(TestClient.Options(), transport);

        var exception = await Assert.ThrowsAsync<NexusTransportException>(
            () => client.Sessions.IdentifyAsync("user_1"));

        Assert.Contains("refused", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ThrowingTransport : Inverge.Nexus.Transport.INexusTransport
    {
        public Task<Inverge.Nexus.Transport.NexusTransportResponse> SendAsync(
            string method,
            string url,
            IReadOnlyDictionary<string, string> headers,
            byte[]? body,
            TimeSpan timeout,
            System.Threading.CancellationToken cancellationToken)
            => throw new NexusTransportException("connection refused");

        public void Dispose()
        {
        }
    }
}
