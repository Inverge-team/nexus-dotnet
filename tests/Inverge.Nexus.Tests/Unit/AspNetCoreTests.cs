using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Inverge.Nexus.AspNetCore;
using Inverge.Nexus.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace Inverge.Nexus.Tests.Unit;

[Collection("AmbientContext")]
public class AspNetCoreTests : IDisposable
{
    public void Dispose() => NexusContext.Clear();

    [Fact]
    public async Task The_request_identity_and_route_are_bound_for_the_handler()
    {
        NexusContext.Clear();
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(202, """{"written":1}"""));
        await using var _ = client;

        NexusTelemetryContext? seen = null;
        var middleware = Middleware(client, _ =>
        {
            seen = NexusContext.Current;
            return Task.CompletedTask;
        });

        var context = Request("/checkout", distinctId: "user_1");
        context.Request.Headers["X-Nexus-Session"] = "sess_42";
        context.Request.Headers["X-Nexus-Device"] = "dev_7";
        context.Request.Headers["CF-IPCountry"] = "IQ";
        context.Request.Headers["User-Agent"] = "TestAgent/1.0";

        await middleware.InvokeAsync(context);

        Assert.NotNull(seen);
        Assert.Equal("user_1", seen!.DistinctId);
        Assert.Equal("sess_42", seen.SessionKey);
        Assert.Equal("dev_7", seen.DeviceKey);
        Assert.Equal("IQ", seen.Country);
        Assert.Equal("TestAgent/1.0", seen.Browser);
        Assert.Equal("/checkout", seen.Url);
        Assert.Equal("web", seen.OsType);
        Assert.Equal("GET", seen.Properties!["method"]);
    }

    [Fact]
    public async Task The_scope_is_torn_down_after_the_request()
    {
        NexusContext.Clear();
        var (client, _) = TestClient.Create();
        await using var __ = client;

        var middleware = Middleware(client, _ => Task.CompletedTask);
        await middleware.InvokeAsync(Request("/a", distinctId: "user_1"));

        Assert.Null(NexusContext.Current.DistinctId);
    }

    [Fact]
    public async Task The_query_string_is_excluded_by_default()
    {
        // Query strings routinely carry tokens and invite codes; telemetry is the
        // last place those should be copied to.
        NexusContext.Clear();
        var (client, _) = TestClient.Create();
        await using var __ = client;

        string? url = null;
        var middleware = Middleware(client, _ =>
        {
            url = NexusContext.Current.Url;
            return Task.CompletedTask;
        });

        var context = Request("/reset");
        context.Request.QueryString = new QueryString("?token=secret");
        await middleware.InvokeAsync(context);

        Assert.Equal("/reset", url);
    }

    [Fact]
    public async Task The_query_string_is_included_when_asked_for()
    {
        NexusContext.Clear();
        var (client, _) = TestClient.Create();
        await using var __ = client;

        string? url = null;
        var middleware = Middleware(
            client,
            _ =>
            {
                url = NexusContext.Current.Url;
                return Task.CompletedTask;
            },
            options => options.IncludeQueryString = true);

        var context = Request("/search");
        context.Request.QueryString = new QueryString("?q=shoes");
        await middleware.InvokeAsync(context);

        Assert.Equal("/search?q=shoes", url);
    }

    [Fact]
    public async Task An_unhandled_exception_is_reported_and_then_rethrown()
    {
        NexusContext.Clear();
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(202, """{"id":"e1"}"""));
        await using var _ = client;

        var middleware = Middleware(client, _ => throw new InvalidOperationException("handler blew up"));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => middleware.InvokeAsync(Request("/boom")));

        Assert.Equal("handler blew up", thrown.Message);
        // The stack still points at the handler, not at the middleware.
        Assert.Contains("AspNetCoreTests", thrown.StackTrace!, StringComparison.Ordinal);

        var error = transport.All().Single(r => r.Path == "/partner/errors");
        Assert.Equal("InvalidOperationException", error.Field("type"));
        Assert.False(error.Object["handled"]!.GetValue<bool>());
        Assert.Equal("/boom", error.Object["context"]!["path"]!.ToString());
    }

    [Fact]
    public async Task Health_endpoints_are_ignored()
    {
        NexusContext.Clear();
        var (client, _) = TestClient.Create();
        await using var __ = client;

        var bound = true;
        var middleware = Middleware(client, _ =>
        {
            bound = NexusContext.Current.Url is not null;
            return Task.CompletedTask;
        });

        await middleware.InvokeAsync(Request("/healthz"));

        Assert.False(bound);
    }

    [Fact]
    public async Task Request_events_are_off_unless_asked_for()
    {
        // Events are metered; billing one per request, health checks included, is
        // rarely what anyone means by product analytics.
        NexusContext.Clear();
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(202, """{"written":1}"""));
        await using var _ = client;

        await Middleware(client, _ => Task.CompletedTask).InvokeAsync(Request("/a"));
        await client.FlushAsync();
        Assert.Equal(0, transport.Count);

        await Middleware(client, _ => Task.CompletedTask, o => o.TrackRequests = true)
            .InvokeAsync(Request("/b"));
        await client.FlushAsync();

        var events = transport.Single().Object["events"]!.AsArray();
        Assert.Equal("http_request", events[0]!["name"]!.ToString());
        Assert.Equal("200", events[0]!["properties"]!["status"]!.ToString());
    }

    [Fact]
    public async Task A_custom_identity_resolver_wins()
    {
        NexusContext.Clear();
        var (client, _) = TestClient.Create();
        await using var __ = client;

        string? seen = null;
        var middleware = Middleware(
            client,
            _ =>
            {
                seen = NexusContext.Current.DistinctId;
                return Task.CompletedTask;
            },
            options => options.ResolveDistinctId = http => http.Request.Headers["X-Tenant-User"].ToString());

        var context = Request("/a", distinctId: "claim_user");
        context.Request.Headers["X-Tenant-User"] = "header_user";
        await middleware.InvokeAsync(context);

        Assert.Equal("header_user", seen);
    }

    private static NexusMiddleware Middleware(
        INexusClient client,
        RequestDelegate next,
        Action<NexusAspNetCoreOptions>? configure = null)
    {
        var options = new NexusAspNetCoreOptions();
        configure?.Invoke(options);
        return new NexusMiddleware(next, client, Options.Create(options));
    }

    private static DefaultHttpContext Request(string path, string? distinctId = null)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "GET";
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();

        if (distinctId is not null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, distinctId) }, "test"));
        }

        return context;
    }
}
