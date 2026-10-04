using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Internal;

namespace Inverge.Nexus.Dispatch;

/// <summary>Sends each request immediately and returns the decoded response. The default.</summary>
internal sealed class DirectDispatcher : INexusDispatcher
{
    private readonly HttpEngine _engine;

    public DirectDispatcher(HttpEngine engine) => _engine = engine;

    public Task<JsonNode?> DispatchAsync(NexusRequest request, CancellationToken cancellationToken)
        => _engine.ExecuteAsync(request, cancellationToken);

    public Task FlushAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(TimeSpan timeout)
    {
        _engine.Dispose();
        return Task.CompletedTask;
    }
}
