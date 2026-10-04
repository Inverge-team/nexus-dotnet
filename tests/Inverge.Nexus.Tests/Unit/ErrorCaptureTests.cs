using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Inverge.Nexus.Resources;
using Inverge.Nexus.Tests.Support;
using Xunit;

namespace Inverge.Nexus.Tests.Unit;

public class ErrorCaptureTests
{
    [Fact]
    public async Task An_exception_is_reported_with_its_type_message_and_stack()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(202, """{"id":"e1","groupId":"g1"}"""));
        await using var _ = client;

        try
        {
            Thrower();
        }
        catch (Exception exception)
        {
            await client.Errors.CaptureExceptionAsync(exception, errorContext: new { order = 42 });
        }

        var request = transport.Single();
        Assert.Equal("/partner/errors", request.Path);
        Assert.Equal("InvalidOperationException", request.Field("type"));
        Assert.Equal("deliberate", request.Field("message"));
        Assert.Equal("error", request.Field("level"));
        Assert.True(request.Object["handled"]!.GetValue<bool>());
        Assert.Equal("42", request.Object["context"]!["order"]!.ToString());

        var frames = (request.Object["stack"] as JsonArray)!;
        Assert.NotEmpty(frames);
        // Throw site first: the first line in the console should be where it broke.
        Assert.Contains("Thrower", frames[0]!["function"]!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_inner_exception_is_appended_with_a_marker()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(202, """{"id":"e1"}"""));
        await using var _ = client;

        try
        {
            try
            {
                Thrower();
            }
            catch (Exception inner)
            {
                throw new ApplicationException("outer", inner);
            }
        }
        catch (Exception exception)
        {
            await client.Errors.CaptureExceptionAsync(exception);
        }

        var frames = (transport.Single().Object["stack"] as JsonArray)!
            .Select(f => f!["function"]!.ToString())
            .ToArray();

        Assert.Contains(frames, f => f.StartsWith("caused by InvalidOperationException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unhandled_is_recorded_when_asked()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(202, """{"id":"e1"}"""));
        await using var _ = client;

        await client.Errors.CaptureExceptionAsync(new Exception("x"), handled: false);

        Assert.False(transport.Single().Object["handled"]!.GetValue<bool>());
    }

    [Fact]
    public void A_fingerprint_is_stable_for_the_same_throw_site()
    {
        Exception First()
        {
            try
            {
                Thrower();
                throw new Exception("unreachable");
            }
            catch (Exception exception)
            {
                return exception;
            }
        }

        var a = ErrorsResource.Fingerprint(First());
        var b = ErrorsResource.Fingerprint(First());

        Assert.Equal(a, b);
        Assert.StartsWith("InvalidOperationException@", a, StringComparison.Ordinal);
    }

    [Fact]
    public void Stack_frames_of_an_unthrown_exception_are_empty_rather_than_null()
    {
        Assert.Empty(ErrorsResource.StackFrames(new Exception("never thrown")));
    }

    [Fact]
    public async Task An_explicit_fingerprint_is_sent()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(202, """{"id":"e1"}"""));
        await using var _ = client;

        await client.Errors.CaptureAsync(
            "Gateway timeout for order 918273",
            type: "GatewayTimeout",
            fingerprint: "gateway-timeout");

        // Without a fingerprint, an order id in the message creates one issue per
        // order instead of one issue.
        Assert.Equal("gateway-timeout", transport.Single().Field("fingerprint"));
    }

    private static void Thrower() => throw new InvalidOperationException("deliberate");
}
