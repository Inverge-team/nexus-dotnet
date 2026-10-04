using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Diagnostics;
using Inverge.Nexus.Transport;

namespace Inverge.Nexus.Internal;

/// <summary>
/// Turns a <see cref="NexusRequest"/> into bytes on a socket: auth headers, JSON
/// encoding, retries, and mapping a failure onto the right exception.
/// </summary>
internal sealed class HttpEngine : IDisposable
{
    /// <summary>
    /// Statuses worth repeating for a request already marked safe to repeat.
    /// </summary>
    /// <remarks>
    /// 402 (billing suspended) and every other 4xx are deliberately absent:
    /// repeating them cannot change the answer, and retrying a suspension just
    /// burns the caller's latency budget before it reports the real problem.
    /// </remarks>
    private static readonly HashSet<int> RetryStatuses = new HashSet<int> { 408, 425, 429, 500, 502, 503, 504 };

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    private readonly NexusOptions _options;
    private readonly INexusTransport _transport;
    private readonly bool _ownsTransport;
    private readonly NexusDiagnostics _diagnostics;
    private bool _disposed;

    public HttpEngine(NexusOptions options, INexusTransport? transport, NexusDiagnostics diagnostics)
    {
        _options = options;
        _diagnostics = diagnostics;
        _ownsTransport = transport is null;
        _transport = transport ?? new HttpClientTransport();
    }

    public NexusOptions Options => _options;

    /// <summary>Executes a request and returns the decoded body.</summary>
    /// <exception cref="NexusApiException">The server answered with a failure.</exception>
    /// <exception cref="NexusTransportException">The request never got a response.</exception>
    public async Task<JsonNode?> ExecuteAsync(NexusRequest request, CancellationToken cancellationToken)
    {
        // Anything logged between here and the end of the call is the SDK's own
        // traffic. A logging bridge drops those records, which is what keeps a
        // globally-registered Nexus log sink from feeding on itself.
        using (NexusDelivery.Enter())
        {
            return await ExecuteCoreAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsTransport)
        {
            // Only a transport this engine created is torn down. The shared
            // HttpClient behind the default transport stays up for the process.
            _transport.Dispose();
        }
    }

    private async Task<JsonNode?> ExecuteCoreAsync(NexusRequest request, CancellationToken cancellationToken)
    {
        var url = _options.UrlFor(request.Path);
        var body = Encode(request.Body);
        var headers = BuildHeaders(request, body is not null);
        var attempts = request.Retryable ? 1 + Math.Max(0, _options.MaxRetries) : 1;

        NexusException? lastFailure = null;

        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0)
            {
                _diagnostics.Debug(
                    "Retry {0}/{1} for {2} {3}.", attempt, attempts - 1, request.Method, request.Path);
            }

            NexusTransportResponse response;
            try
            {
                response = await _transport
                    .SendAsync(request.Method, url, headers, body, _options.Timeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (NexusTransportException exception)
            {
                lastFailure = exception;
                if (attempt + 1 >= attempts)
                {
                    throw;
                }

                await DelayAsync(Backoff(attempt, null), cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccess && RetryStatuses.Contains(response.Status) && attempt + 1 < attempts)
            {
                await DelayAsync(Backoff(attempt, response), cancellationToken).ConfigureAwait(false);
                continue;
            }

            return Parse(request, response);
        }

        throw lastFailure ?? new NexusTransportException(
            string.Format(CultureInfo.InvariantCulture, "{0} {1} failed.", request.Method, url));
    }

    private static byte[]? Encode(JsonObject? body)
        => body is null ? null : Encoding.UTF8.GetBytes(body.ToJsonString(JsonHelpers.SerializerOptions));

    private static Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        => delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);

    private Dictionary<string, string> BuildHeaders(NexusRequest request, bool hasBody)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["x-api-key"] = _options.ApiKey,
            ["Accept"] = "application/json",
            ["User-Agent"] = _options.ResolvedUserAgent,
        };

        foreach (var header in _options.DefaultHeaders)
        {
            headers[header.Key] = header.Value;
        }

        if (request.Headers is not null)
        {
            foreach (var header in request.Headers)
            {
                headers[header.Key] = header.Value;
            }
        }

        if (hasBody)
        {
            headers["Content-Type"] = "application/json";
        }

        return headers;
    }

    private TimeSpan Backoff(int attempt, NexusTransportResponse? response)
    {
        // The server's own Retry-After beats our guess.
        var retryAfter = NexusApiExceptionFactory.ReadRetryAfter(response?.Headers);
        if (retryAfter.HasValue)
        {
            return retryAfter.Value > MaxBackoff ? MaxBackoff : retryAfter.Value;
        }

        var backoff = _options.RetryBackoff;
        var exponential = TimeSpan.FromTicks(backoff.Ticks * (1L << Math.Min(attempt, 10)));
        var delay = exponential + Jitter.Next(backoff);
        return delay > MaxBackoff ? MaxBackoff : delay;
    }

    private static JsonNode? Parse(NexusRequest request, NexusTransportResponse response)
    {
        // The Remote Config fetch endpoint normally answers 200 with
        // {"notModified": true}, but a conditional request can be short-circuited
        // to a bare 304 by a proxy in front of the API. Normalise the two so the
        // caller only has to understand one of them.
        if (response.Status == 304)
        {
            return new JsonObject { ["notModified"] = JsonValue.Create(true) };
        }

        if (response.IsSuccess)
        {
            return response.ReadJson();
        }

        throw NexusApiExceptionFactory.Create(
            response.Status,
            response.ReadJson(),
            request.Method,
            request.Path,
            response.Text,
            response.Headers);
    }
}
