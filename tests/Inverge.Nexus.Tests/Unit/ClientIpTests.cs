using System.IO;
using System.Net;
using System.Threading.Tasks;
using Inverge.Nexus.AspNetCore;
using Inverge.Nexus.Models;
using Inverge.Nexus.Tests.Support;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace Inverge.Nexus.Tests.Unit;

/// <summary>The end user's IP reaching Messaging's per-IP rate limit.</summary>
[Collection("AmbientContext")]
public class ClientIpTests
{
    private const string User = "203.0.113.42";

    [Fact]
    public async Task A_verification_forwards_the_bound_address()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(201, "{}"));
        await using var _ = client;

        using (NexusClientIp.Use(User))
        {
            await client.Messaging.Verifications.StartAsync(new StartVerification("+9647501234567"));
        }

        var req = transport.Single();
        Assert.Equal(User, req.Headers["X-Client-IP"]);
        Assert.Null(req.Field("clientIp"));
    }

    [Fact]
    public async Task An_explicit_address_goes_in_the_body_and_wins()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(201, "{}"));
        await using var _ = client;

        using (NexusClientIp.Use(User))
        {
            await client.Messaging.Verifications.StartAsync(new StartVerification("+9647501234567") { ClientIp = "198.51.100.7" });
        }

        var req = transport.Single();
        Assert.Equal("198.51.100.7", req.Field("clientIp"));
        Assert.False(req.Headers.ContainsKey("X-Client-IP"));
    }

    [Fact]
    public async Task Only_otp_sends_pick_the_address_up_by_themselves()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(202, "{}"));
        await using var _ = client;

        using (NexusClientIp.Use(User))
        {
            await client.Messaging.SendAsync(new SendMessage("sms", "+9647501234567") { Body = "Your order shipped" });
            await client.Messaging.SendAsync(new SendMessage("sms", "+9647501234567") { Body = "Code 1234", MessageClass = "otp" });
        }

        Assert.False(transport.All()[0].Headers.ContainsKey("X-Client-IP"));
        Assert.Equal(User, transport.All()[1].Headers["X-Client-IP"]);
    }

    [Theory]
    [InlineData("10.1.2.3")]
    [InlineData("172.20.0.1")]
    [InlineData("192.168.0.4")]
    [InlineData("127.0.0.1")]
    [InlineData("100.64.0.1")]
    [InlineData("::1")]
    [InlineData("fd12::1")]
    [InlineData("fe80::1")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("nope")]
    public void Proxy_and_private_addresses_are_never_forwarded(string ip)
        => Assert.Null(NexusClientIp.PublicIp(ip));

    [Fact]
    public void Mapped_and_public_ipv6_addresses_are_kept()
    {
        Assert.Equal(User, NexusClientIp.PublicIp("::ffff:" + User));
        Assert.Equal("2a00:1450:4001:81b::200e", NexusClientIp.PublicIp("2a00:1450:4001:81b::200e"));
    }

    [Fact]
    public async Task The_middleware_binds_the_connection_address_and_lets_go_after()
    {
        var (client, _) = TestClient.Create();
        await using var __ = client;
        string? seen = null;
        var middleware = new NexusMiddleware(
            _ =>
            {
                seen = NexusClientIp.Current;
                return Task.CompletedTask;
            },
            client,
            Options.Create(new NexusAspNetCoreOptions { TrackRequests = false }));
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/otp";
        context.Request.Headers["X-Forwarded-For"] = "9.9.9.9"; // anyone can send this
        context.Connection.RemoteIpAddress = IPAddress.Parse(User);
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        Assert.Equal(User, seen);
        Assert.Null(NexusClientIp.Current);
    }

    [Fact]
    public async Task Work_started_from_the_request_keeps_it_and_a_queued_job_rebinds_it()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(201, "{}"));
        await using var _ = client;

        string? carried;
        using (NexusClientIp.Use(User))
        {
            carried = await Task.Run(() => NexusClientIp.Current); // flows with the async context
        }

        Assert.Null(NexusClientIp.Current);
        using (NexusClientIp.Use(carried)) // the worker, given the job's address
        {
            await client.Messaging.Verifications.StartAsync(new StartVerification("+9647501234567"));
        }

        Assert.Equal(User, transport.Single().Headers["X-Client-IP"]);
    }

    [Fact]
    public async Task Forwarding_can_be_switched_off()
    {
        var (client, transport) = TestClient.Create(_ => RecordingTransport.Json(201, "{}"));
        await using var _ = client;
        NexusClientIp.Enabled = false;
        try
        {
            using (NexusClientIp.Use(User))
            {
                await client.Messaging.Verifications.StartAsync(new StartVerification("+9647501234567"));
            }
        }
        finally
        {
            NexusClientIp.Enabled = true;
        }

        Assert.False(transport.Single().Headers.ContainsKey("X-Client-IP"));
    }
}
