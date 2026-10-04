using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Inverge.Nexus.Resources;

/// <summary>Base for the product namespaces hanging off <see cref="NexusClient"/>.</summary>
public abstract class NexusResource
{
    internal NexusResource(NexusClient client) => Client = client;

    /// <summary>The client this resource sends through.</summary>
    internal NexusClient Client { get; }

    internal Task<JsonNode?> SendAsync(NexusRequest request, CancellationToken cancellationToken)
        => Client.SendAsync(request, cancellationToken);

    /// <summary>Client options, ambient context and per-call overrides, resolved.</summary>
    internal NexusTelemetryContext Resolve(NexusTelemetryContext? overrides)
        => Client.ResolveContext(overrides);
}
