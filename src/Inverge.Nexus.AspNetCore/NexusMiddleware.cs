using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Inverge.Nexus.AspNetCore;

/// <summary>
/// Binds each request's identity, route and device into the ambient telemetry
/// context, and reports unhandled exceptions.
/// </summary>
/// <remarks>
/// With this in the pipeline, nothing downstream has to pass identity to Nexus:
/// a handler can call <c>nexus.Track("checkout_started")</c> and the event already
/// carries the signed-in user, the session, the route and the request id.
/// </remarks>
public sealed class NexusMiddleware
{
    private const int MaxUserAgentLength = 200;

    private readonly RequestDelegate _next;
    private readonly INexusClient _client;
    private readonly NexusAspNetCoreOptions _options;

    /// <summary>Creates the middleware.</summary>
    public NexusMiddleware(
        RequestDelegate next, INexusClient client, IOptions<NexusAspNetCoreOptions> options)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = options?.Value ?? new NexusAspNetCoreOptions();
    }

    /// <summary>Runs the middleware.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        if (IsIgnored(context.Request.Path))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        using (NexusContext.Scope(BuildContext(context)))
        {
            var started = Stopwatch.GetTimestamp();

            try
            {
                await _next(context).ConfigureAwait(false);
            }
            catch (Exception exception) when (_options.CaptureUnhandledExceptions)
            {
                await CaptureAsync(context, exception).ConfigureAwait(false);

                // `throw;` rather than `throw exception;` — rethrowing the variable
                // would reset the stack trace and lose where it actually failed.
                throw;
            }
            finally
            {
                if (_options.TrackRequests)
                {
                    TrackRequest(context, started);
                }
            }
        }
    }

    private static string Describe(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        return endpoint?.DisplayName ?? context.Request.Path.ToString();
    }

    private bool IsIgnored(PathString path)
    {
        if (!path.HasValue)
        {
            return false;
        }

        foreach (var prefix in _options.IgnoredPathPrefixes)
        {
            if (!string.IsNullOrEmpty(prefix)
                && path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private NexusTelemetryContext BuildContext(HttpContext context)
    {
        var request = context.Request;

        var properties = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["requestId"] = context.TraceIdentifier,
            ["method"] = request.Method,
        };

        var endpoint = context.GetEndpoint()?.DisplayName;
        if (endpoint is not null)
        {
            properties["route"] = endpoint;
        }

        var extra = _options.ResolveProperties?.Invoke(context);
        if (extra is not null)
        {
            foreach (var pair in extra)
            {
                properties[pair.Key] = pair.Value;
            }
        }

        return new NexusTelemetryContext
        {
            DistinctId = ResolveDistinctId(context),
            SessionKey = ResolveSessionKey(context),
            DeviceKey = Header(context, _options.DeviceKeyHeader),
            Country = Header(context, _options.CountryHeader),
            Browser = UserAgent(context),
            OsType = "web",
            Url = Url(request),
            Properties = properties,
        };
    }

    private string? ResolveDistinctId(HttpContext context)
    {
        if (_options.ResolveDistinctId is not null)
        {
            return _options.ResolveDistinctId(context);
        }

        var user = context.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        return user.FindFirst(ClaimTypes.NameIdentifier)?.Value
               ?? user.FindFirst("sub")?.Value
               ?? user.Identity.Name;
    }

    private string? ResolveSessionKey(HttpContext context)
        => _options.ResolveSessionKey is not null
            ? _options.ResolveSessionKey(context)
            : Header(context, _options.SessionKeyHeader);

    private string? Url(HttpRequest request)
    {
        var path = request.Path.HasValue ? request.Path.Value : "/";
        return _options.IncludeQueryString && request.QueryString.HasValue
            ? path + request.QueryString.Value
            : path;
    }

    private static string? Header(HttpContext context, string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var value = context.Request.Headers[name].ToString();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string? UserAgent(HttpContext context)
    {
        var agent = Header(context, "User-Agent");
        if (agent is null)
        {
            return null;
        }

        // A hostile or merely eccentric client can send a very long User-Agent, and
        // the field is stored per session.
        return agent.Length <= MaxUserAgentLength ? agent : agent.Substring(0, MaxUserAgentLength);
    }

    private async Task CaptureAsync(HttpContext context, Exception exception)
    {
        try
        {
            await _client.Errors.CaptureExceptionAsync(
                exception,
                handled: false,
                errorContext: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["method"] = context.Request.Method,
                    ["path"] = context.Request.Path.ToString(),
                    ["endpoint"] = Describe(context),
                    ["requestId"] = context.TraceIdentifier,
                },
                cancellationToken: default).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Reporting the failure must not replace it. Whatever went wrong in the
            // request is the exception worth surfacing, and it is about to be
            // rethrown.
        }
    }

    private void TrackRequest(HttpContext context, long started)
    {
        var elapsed = Stopwatch.GetElapsedTime(started);

        _client.Track(
            _options.RequestEventName,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["method"] = context.Request.Method,
                ["path"] = context.Request.Path.ToString(),
                ["status"] = context.Response.StatusCode,
                ["durationMs"] = Math.Round(elapsed.TotalMilliseconds, 2)
                    .ToString(CultureInfo.InvariantCulture),
            });
    }
}
