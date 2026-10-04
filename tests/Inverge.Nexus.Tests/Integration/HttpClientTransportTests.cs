using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Transport;
using Xunit;

namespace Inverge.Nexus.Tests.Integration;

/// <summary>
/// Drives the real <see cref="HttpClientTransport"/> against a loopback HTTP server.
/// Every other suite substitutes the transport, so without these the one component
/// that actually touches a socket would ship unexercised.
/// </summary>
public sealed class HttpClientTransportTests : IAsyncLifetime
{
    private HttpListener _listener = null!;
    private string _origin = null!;
    private CancellationTokenSource _cts = null!;
    private Task _loop = null!;

    private Func<HttpListenerContext, Task> _handle = context =>
    {
        context.Response.StatusCode = 200;
        return WriteAsync(context, """{"ok":true}""");
    };

    public Task InitializeAsync()
    {
        // Port 0 would be ideal, but HttpListener needs a concrete prefix; retry a
        // few random high ports so a parallel run cannot collide.
        var random = new Random();
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var port = random.Next(20000, 60000);
            var listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");

            try
            {
                listener.Start();
                _listener = listener;
                _origin = $"http://127.0.0.1:{port}";
                break;
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }

        Assert.NotNull(_listener);

        _cts = new CancellationTokenSource();
        _loop = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }

                try
                {
                    await _handle(context);
                }
                catch (Exception)
                {
                    // The listener must survive a handler fault.
                }
                finally
                {
                    try
                    {
                        context.Response.Close();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        });

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _cts.Cancel();
        _listener.Close();
        _cts.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task A_real_request_carries_the_key_body_and_user_agent()
    {
        string? apiKey = null;
        string? userAgent = null;
        string? contentType = null;
        string? body = null;
        string? method = null;
        string? path = null;

        _handle = async context =>
        {
            apiKey = context.Request.Headers["x-api-key"];
            userAgent = context.Request.Headers["User-Agent"];
            contentType = context.Request.ContentType;
            method = context.Request.HttpMethod;
            path = context.Request.Url?.AbsolutePath;

            using var reader = new System.IO.StreamReader(context.Request.InputStream, Encoding.UTF8);
            body = await reader.ReadToEndAsync();

            context.Response.StatusCode = 200;
            await WriteAsync(context, """{"subject":{"id":"sub_1","distinctId":"user_1"}}""");
        };

        await using var nexus = new NexusClient(new NexusOptions
        {
            ApiKey = "nxs_real_key",
            BaseUrl = _origin,
            Batch = false,
            MaxRetries = 0,
        });

        var result = await nexus.Sessions.IdentifyAsync("user_1", email: "a@b.com");

        Assert.Equal("POST", method);
        Assert.Equal("/partner/sessions/identify", path);
        Assert.Equal("nxs_real_key", apiKey);
        Assert.StartsWith("Inverge.Nexus.NET/", userAgent!, StringComparison.Ordinal);
        Assert.Equal("application/json", contentType);
        Assert.Contains("\"distinctId\":\"user_1\"", body!, StringComparison.Ordinal);

        Assert.Equal("sub_1", result.SubjectId);
        Assert.Equal("user_1", result.DistinctId);
    }

    [Fact]
    public async Task A_real_error_response_becomes_the_right_exception()
    {
        _handle = async context =>
        {
            context.Response.StatusCode = 403;
            await WriteAsync(
                context, """{"error":{"code":"forbidden","message":"Key cannot write here"}}""");
        };

        await using var nexus = new NexusClient(new NexusOptions
        {
            ApiKey = "nxs_real_key",
            BaseUrl = _origin,
            Batch = false,
            MaxRetries = 0,
        });

        var exception = await Assert.ThrowsAsync<NexusPermissionDeniedException>(
            () => nexus.Sessions.IdentifyAsync("user_1"));

        Assert.Equal("forbidden", exception.Code);
        Assert.Equal("Key cannot write here", exception.Message);
    }

    [Fact]
    public async Task A_per_request_timeout_becomes_a_transport_exception()
    {
        _handle = async context =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            context.Response.StatusCode = 200;
            await WriteAsync(context, "{}");
        };

        await using var nexus = new NexusClient(new NexusOptions
        {
            ApiKey = "nxs_real_key",
            BaseUrl = _origin,
            Batch = false,
            MaxRetries = 0,
            Timeout = TimeSpan.FromMilliseconds(250),
        });

        var exception = await Assert.ThrowsAsync<NexusTransportException>(
            () => nexus.Sessions.IdentifyAsync("user_1"));

        Assert.Contains("timed out", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_a_timeout()
    {
        _handle = async context =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            context.Response.StatusCode = 200;
            await WriteAsync(context, "{}");
        };

        await using var nexus = new NexusClient(new NexusOptions
        {
            ApiKey = "nxs_real_key",
            BaseUrl = _origin,
            Batch = false,
            MaxRetries = 0,
            Timeout = TimeSpan.FromSeconds(30),
        });

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        // A cancelled caller is not a failed request: it must surface as
        // OperationCanceledException, not as a Nexus transport failure.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => nexus.Sessions.IdentifyAsync("user_1", cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task A_retryable_call_recovers_from_a_real_503()
    {
        var attempts = 0;
        _handle = async context =>
        {
            attempts++;
            if (attempts < 3)
            {
                context.Response.StatusCode = 503;
                await WriteAsync(context, """{"error":{"code":"unavailable","message":"try later"}}""");
                return;
            }

            context.Response.StatusCode = 202;
            await WriteAsync(context, """{"written":1}""");
        };

        await using var nexus = new NexusClient(new NexusOptions
        {
            ApiKey = "nxs_real_key",
            BaseUrl = _origin,
            Batch = false,
            MaxRetries = 3,
            RetryBackoff = TimeSpan.FromMilliseconds(5),
        });

        var written = await nexus.Events.BatchAsync(new[] { new Models.NexusEvent("a") });

        Assert.Equal(1, written);
        Assert.Equal(3, attempts);
    }

    [Fact]
    public async Task A_retry_after_header_is_honoured_on_a_real_429()
    {
        var attempts = 0;
        _handle = async context =>
        {
            attempts++;
            if (attempts == 1)
            {
                context.Response.StatusCode = 429;
                context.Response.Headers["Retry-After"] = "0";
                await WriteAsync(context, """{"error":{"code":"rate_limited","message":"slow down"}}""");
                return;
            }

            context.Response.StatusCode = 202;
            await WriteAsync(context, """{"written":1}""");
        };

        await using var nexus = new NexusClient(new NexusOptions
        {
            ApiKey = "nxs_real_key",
            BaseUrl = _origin,
            Batch = false,
            MaxRetries = 2,
            RetryBackoff = TimeSpan.FromMilliseconds(5),
        });

        Assert.Equal(1, await nexus.Events.BatchAsync(new[] { new Models.NexusEvent("a") }));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task A_closed_port_becomes_a_transport_exception()
    {
        // The listener is up on _origin; pick a port nothing is listening on.
        await using var nexus = new NexusClient(new NexusOptions
        {
            ApiKey = "nxs_real_key",
            BaseUrl = "http://127.0.0.1:1",
            Batch = false,
            MaxRetries = 0,
            Timeout = TimeSpan.FromSeconds(5),
        });

        await Assert.ThrowsAsync<NexusTransportException>(
            () => nexus.Sessions.IdentifyAsync("user_1"));
    }

    [Fact]
    public async Task Default_headers_reach_the_wire()
    {
        string? tenant = null;
        _handle = async context =>
        {
            tenant = context.Request.Headers["X-Tenant"];
            context.Response.StatusCode = 200;
            await WriteAsync(context, "{}");
        };

        var options = new NexusOptions
        {
            ApiKey = "nxs_real_key",
            BaseUrl = _origin,
            Batch = false,
            MaxRetries = 0,
        };
        options.DefaultHeaders["X-Tenant"] = "acme";

        await using var nexus = new NexusClient(options);
        await nexus.Sessions.IdentifyAsync("user_1");

        Assert.Equal("acme", tenant);
    }

    [Fact]
    public async Task An_empty_body_response_is_not_an_error()
    {
        // DELETE /partner/remote-config/parameters/:key answers 204.
        _handle = context =>
        {
            context.Response.StatusCode = 204;
            return Task.CompletedTask;
        };

        await using var nexus = new NexusClient(new NexusOptions
        {
            ApiKey = "nxs_real_key",
            BaseUrl = _origin,
            Batch = false,
            MaxRetries = 0,
        });

        await nexus.RemoteConfig.DeleteParameterAsync("fee");
    }

    private static async Task WriteAsync(HttpListenerContext context, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes.AsMemory());
    }
}
