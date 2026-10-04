using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Inverge.Nexus.Dispatch;

/// <summary>
/// Decides what actually happens when a resource method is called.
/// </summary>
/// <remarks>
/// Implement this to hand delivery to your own infrastructure — a queue, a
/// durable outbox, a hosted channel — so Nexus traffic inherits whatever
/// reliability guarantees the rest of your system has.
/// </remarks>
public interface INexusDispatcher
{
    /// <summary>
    /// Delivers a request. A dispatcher that does not deliver inline returns
    /// <c>null</c>, so calls that need the response cannot be used with one.
    /// </summary>
    Task<JsonNode?> DispatchAsync(NexusRequest request, CancellationToken cancellationToken);

    /// <summary>Waits for in-flight deliveries. A no-op for a synchronous dispatcher.</summary>
    Task FlushAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Releases resources, flushing first.</summary>
    Task StopAsync(TimeSpan timeout);
}
