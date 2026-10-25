using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Telescope;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Inverge.Nexus.AspNetCore;

/// <summary>
/// Adds the request's details to its Telescope journey: method, URL, headers,
/// query, body, response status/headers/body, the matched route pattern, the
/// user, and any unhandled exception. (The journey itself — and every query,
/// HttpClient call and log inside it — is recorded by <see cref="NexusTelescope"/>.)
/// </summary>
public sealed class NexusTelescopeMiddleware
{
    private readonly RequestDelegate _next;
    private readonly NexusTelescope _telescope;

    /// <summary>Creates the middleware.</summary>
    public NexusTelescopeMiddleware(RequestDelegate next, NexusTelescope telescope)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _telescope = telescope ?? throw new ArgumentNullException(nameof(telescope));
    }

    /// <summary>Runs the middleware.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        if (!_telescope.IsRecording)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var request = context.Request;
        if (_telescope.IgnoresPath(request.Path.Value ?? "/"))
        {
            _telescope.Discard();
            await _next(context).ConfigureAwait(false);
            return;
        }

        _telescope.Enrich(new Dictionary<string, object?>
        {
            ["method"] = request.Method,
            ["uri"] = $"{request.Scheme}://{request.Host}{request.PathBase}{request.Path}{request.QueryString}",
            ["path"] = request.Path.Value,
            ["ip"] = request.Headers["X-Forwarded-For"].FirstOrDefault()?.Split(',')[0].Trim() ?? context.Connection.RemoteIpAddress?.ToString(),
            ["headers"] = request.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => (object?)h.Value.ToString()),
            ["query"] = request.Query.Count == 0 ? null : request.Query.ToDictionary(q => q.Key, q => (object?)q.Value.ToString()),
            ["payload"] = await ReadBodyAsync(request).ConfigureAwait(false),
        });

        var capture = _telescope.Options.CaptureBodies ? new TeeStream(context.Response.Body, _telescope.BodyLimitBytes) : null;
        if (capture is not null)
        {
            context.Response.Body = capture;
        }

        var threw = false;
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            threw = true;
            _telescope.RecordException(exception, handled: false);
            throw;
        }
        finally
        {
            if (capture is not null)
            {
                context.Response.Body = capture.Inner;
            }

            // An escaping exception becomes a 500 only after we return; record it as one.
            Finish(context, capture, threw && !context.Response.HasStarted ? 500 : context.Response.StatusCode);
        }
    }

    private void Finish(HttpContext context, TeeStream? capture, int status)
    {
        var response = context.Response;
        var endpoint = context.GetEndpoint();
        var user = context.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            var id = user.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? user.FindFirst("sub")?.Value ?? user.Identity.Name;
            if (id is not null)
            {
                _telescope.SetUser(id, user.FindFirst(ClaimTypes.Email)?.Value ?? user.FindFirst("email")?.Value, user.Identity.Name);
            }
        }

        _telescope.Enrich(new Dictionary<string, object?>
        {
            ["responseStatus"] = status,
            ["responseHeaders"] = response.Headers.ToDictionary(h => h.Key.ToLowerInvariant(), h => (object?)h.Value.ToString()),
            ["route"] = (endpoint as RouteEndpoint)?.RoutePattern.RawText,
            ["controller"] = endpoint?.DisplayName,
            ["response"] = capture is null ? null : Body(response.ContentType, capture.Captured, capture.Overflow, _telescope.Options.BodySizeLimitKb),
        });
        _telescope.SetStatus(status.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<JsonNode?> ReadBodyAsync(HttpRequest request)
    {
        if (!_telescope.Options.CaptureBodies || request.ContentLength is null or 0 || request.ContentLength > _telescope.BodyLimitBytes)
        {
            return null;
        }

        var type = request.ContentType ?? "";
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync().ConfigureAwait(false);
            var obj = new JsonObject();
            foreach (var f in form)
            {
                obj[f.Key] = f.Value.ToString();
            }

            foreach (var file in form.Files)
            {
                obj[file.Name] = new JsonObject { ["name"] = file.FileName, ["size"] = file.Length, ["mime"] = file.ContentType };
            }

            return obj;
        }

        request.EnableBuffering();
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer).ConfigureAwait(false);
        request.Body.Position = 0;
        return Body(type, buffer.ToArray(), false, _telescope.Options.BodySizeLimitKb);
    }

    private static JsonNode? Body(string? contentType, byte[] bytes, bool overflow, int limitKb)
    {
        if (overflow)
        {
            return $"Body over {limitKb} KB — not recorded";
        }

        if (bytes.Length == 0)
        {
            return null;
        }

        var type = contentType ?? "";
        var text = Encoding.UTF8.GetString(bytes);
        if (type.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                return JsonNode.Parse(text);
            }
            catch (System.Text.Json.JsonException)
            {
                return text;
            }
        }

        if (type.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            return $"HTML response ({bytes.Length} bytes)";
        }

        return type.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || type.Length == 0 ? text : $"{type} ({bytes.Length} bytes)";
    }

    /// <summary>Copies what the app writes into a bounded buffer, passing everything through unchanged.</summary>
    private sealed class TeeStream : Stream
    {
        private readonly MemoryStream _copy = new MemoryStream();
        private readonly int _limit;

        public TeeStream(Stream inner, int limit)
        {
            Inner = inner;
            _limit = limit;
        }

        public Stream Inner { get; }

        public bool Overflow { get; private set; }

        public byte[] Captured => _copy.ToArray();

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => Inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => Inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            Keep(buffer.AsSpan(offset, count));
            Inner.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Keep(buffer.Span);
            await Inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        private void Keep(ReadOnlySpan<byte> data)
        {
            if (Overflow)
            {
                return;
            }

            if (_copy.Length + data.Length > _limit)
            {
                Overflow = true;
                _copy.SetLength(0);
                return;
            }

            _copy.Write(data);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _copy.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>Wires Nexus Telescope into the ASP.NET Core pipeline.</summary>
public static class NexusTelescopeApplicationBuilderExtensions
{
    /// <summary>
    /// Adds request details to each Telescope journey. Put it early in the
    /// pipeline (before routing/auth so it wraps them). Requires
    /// <c>services.AddNexusTelescope()</c>.
    /// </summary>
    public static IApplicationBuilder UseNexusTelescope(this IApplicationBuilder app)
    {
        if (app is null)
        {
            throw new ArgumentNullException(nameof(app));
        }

        return app.UseMiddleware<NexusTelescopeMiddleware>();
    }
}
