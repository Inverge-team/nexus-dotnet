using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Inverge.Nexus.Transport;

/// <summary>
/// The default transport, over <see cref="HttpClient"/>.
/// </summary>
/// <remarks>
/// <para>
/// With no client supplied, every <see cref="NexusClient"/> in the process shares
/// one pooled <see cref="HttpClient"/>. That is the correct default for a library:
/// a client per instance exhausts sockets under load, and a client that lives
/// forever without a connection lifetime never notices a DNS change. On modern
/// targets the shared handler recycles connections every five minutes for exactly
/// that reason.
/// </para>
/// <para>
/// In an ASP.NET Core or worker app, prefer handing in a client from
/// <c>IHttpClientFactory</c> — <c>services.AddNexus()</c> does this — so the
/// app's own handler pipeline, resilience policies and DNS behaviour apply. An
/// injected client is owned by the caller and is never disposed here.
/// </para>
/// </remarks>
public sealed class HttpClientTransport : INexusTransport
{
    private static readonly Lazy<HttpClient> Shared = new Lazy<HttpClient>(
        CreateSharedClient,
        LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private bool _disposed;

    /// <summary>Uses the process-wide shared <see cref="HttpClient"/>.</summary>
    public HttpClientTransport()
        : this(Shared.Value, ownsClient: false)
    {
    }

    /// <summary>Uses a caller-supplied <see cref="HttpClient"/>, which it does not dispose.</summary>
    /// <param name="client">The client to send through.</param>
    public HttpClientTransport(HttpClient client)
        : this(client ?? throw new ArgumentNullException(nameof(client)), ownsClient: false)
    {
    }

    private HttpClientTransport(HttpClient client, bool ownsClient)
    {
        _client = client;
        _ownsClient = ownsClient;
    }

    /// <inheritdoc />
    public async Task<NexusTransportResponse> SendAsync(
        string method,
        string url,
        IReadOnlyDictionary<string, string> headers,
        byte[]? body,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), url);

        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
        }

        ApplyHeaders(request, headers);

        // HttpClient.Timeout is per-client, so a per-request deadline has to come
        // from a linked token. The shared client's own timeout is disabled for
        // this reason; an injected client keeps whatever the app configured, and
        // whichever deadline is shorter wins.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout > TimeSpan.Zero)
        {
            deadline.CancelAfter(timeout);
        }

        try
        {
            using var response = await _client
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token)
                .ConfigureAwait(false);

            var payload = await ReadBodyAsync(response).ConfigureAwait(false);
            return new NexusTransportResponse((int)response.StatusCode, payload, ReadHeaders(response));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Our deadline fired, not the caller's — that is a timeout, which is
            // a transport failure the client may retry, not a cancellation.
            throw new NexusTransportException(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} {1} timed out after {2:0.###}s.",
                    method,
                    url,
                    timeout.TotalSeconds));
        }
        catch (HttpRequestException exception)
        {
            throw new NexusTransportException(
                string.Format(CultureInfo.InvariantCulture, "{0} {1} failed: {2}", method, url, exception.Message),
                exception);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsClient)
        {
            _client.Dispose();
        }
    }

    private static HttpClient CreateSharedClient()
    {
#if NET8_0_OR_GREATER
        var handler = new SocketsHttpHandler
        {
            // Without this a long-lived client pins the first resolved address
            // forever and never follows a DNS change.
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        };
#else
        var handler = new HttpClientHandler();
        if (handler.SupportsAutomaticDecompression)
        {
            handler.AutomaticDecompression =
                System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate;
        }
#endif

        return new HttpClient(handler, disposeHandler: true)
        {
            // The per-request deadline in SendAsync governs instead.
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
        };
    }

    private static void ApplyHeaders(HttpRequestMessage request, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var header in headers)
        {
            // Content-* headers belong on the content, not the request, and
            // HttpClient throws rather than moving them for you.
            if (header.Key.StartsWith("Content-", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Content is null)
                {
                    continue;
                }

                if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)
                    && MediaTypeHeaderValue.TryParse(header.Value, out var mediaType))
                {
                    request.Content.Headers.ContentType = mediaType;
                }
                else
                {
                    request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }

                continue;
            }

            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpResponseMessage response)
    {
        if (response.Content is null)
        {
            return Array.Empty<byte>();
        }

        return await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
    }

    private static Dictionary<string, string> ReadHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in response.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        if (response.Content is not null)
        {
            foreach (var header in response.Content.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }
        }

        return headers;
    }
}
