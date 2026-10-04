using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Inverge.Nexus.Transport;

/// <summary>
/// Sends one HTTP request. Implement this to put the SDK's traffic through your
/// own pipeline — a proxy, a recorded fixture, a test double.
/// </summary>
/// <remarks>
/// An implementation should return a <see cref="NexusTransportResponse"/> for any
/// status the server actually produced, including 4xx and 5xx: turning those into
/// Nexus errors is the client's job, and it needs the server's own message to do
/// it. Throw <see cref="NexusTransportException"/> only for genuine
/// network-level failures.
/// </remarks>
public interface INexusTransport : IDisposable
{
    /// <summary>Sends the request and returns whatever the server answered.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="url">The absolute URL.</param>
    /// <param name="headers">Headers to send.</param>
    /// <param name="body">The request body, or <c>null</c>.</param>
    /// <param name="timeout">Per-request timeout.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="NexusTransportException">The request never got a response.</exception>
    Task<NexusTransportResponse> SendAsync(
        string method,
        string url,
        IReadOnlyDictionary<string, string> headers,
        byte[]? body,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}
