using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Inverge.Nexus.Extensions.Logging;

/// <summary>
/// Flushes buffered telemetry when the host shuts down.
/// </summary>
/// <remarks>
/// Without this, a process that exits promptly loses whatever is still in the
/// event and log buffers — which is exactly the window a deployment or a crash-loop
/// restart lands in, and exactly the telemetry you want from it. The container
/// would eventually dispose the client, but a hosted service's
/// <see cref="StopAsync"/> runs earlier in shutdown and inside the host's own
/// shutdown timeout.
/// </remarks>
internal sealed class NexusFlushService : IHostedService
{
    private static readonly Action<ILogger, int, Exception?> FlushTruncated =
        LoggerMessage.Define<int>(
            LogLevel.Warning,
            new EventId(10, "NexusFlushTruncated"),
            "The Nexus telemetry flush was cut short by the host shutdown timeout; {Pending} item(s) were not sent.");

    private static readonly Action<ILogger, Exception?> FlushFailed =
        LoggerMessage.Define(
            LogLevel.Warning,
            new EventId(11, "NexusFlushFailed"),
            "Flushing Nexus telemetry on shutdown failed.");

    private readonly INexusClient _client;
    private readonly ILogger<NexusFlushService> _logger;

    public NexusFlushService(INexusClient client, ILogger<NexusFlushService> logger)
    {
        _client = client;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _client.FlushAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            FlushTruncated(_logger, _client.Pending, null);
        }
        catch (Exception exception)
        {
            FlushFailed(_logger, exception);
        }
    }
}
