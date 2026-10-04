using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Internal;
using Inverge.Nexus.Models;

namespace Inverge.Nexus.Resources;

/// <summary>Structured logging into the Nexus logs product.</summary>
/// <remarks>
/// For an app that already uses <c>ILogger</c>, prefer the provider in
/// <c>Inverge.Nexus.Extensions.Logging</c>: register it once and every existing
/// <c>logger.LogInformation(...)</c> lands in Nexus with no call-site changes.
/// This resource is for code that wants to write to Nexus explicitly.
/// </remarks>
public sealed class LogsResource : NexusResource
{
    /// <summary>The most log lines the ingest endpoint accepts in one request.</summary>
    public const int MaxBatchSize = 1000;

    internal LogsResource(NexusClient client) : base(client)
    {
    }

    /// <summary>Writes one log line. Returns as soon as it is buffered.</summary>
    /// <param name="level">The severity.</param>
    /// <param name="message">The message.</param>
    /// <param name="source">Where it came from — a subsystem or logger name.</param>
    /// <param name="logContext">Structured context attached to the line.</param>
    /// <param name="timestamp">When it was written. Defaults to arrival time.</param>
    /// <param name="context">Per-call identity and device overrides.</param>
    public void Log(
        NexusLogLevel level,
        string message,
        string? source = null,
        object? logContext = null,
        DateTimeOffset? timestamp = null,
        NexusTelemetryContext? context = null)
    {
        var resolved = Resolve(context);
        var item = Payloads.LogItem(
            NexusLogEntry.Wire(level),
            message ?? string.Empty,
            source,
            logContext,
            timestamp.HasValue ? JsonHelpers.Iso8601(timestamp.Value) : null);

        Client.EnqueueLog(item, resolved);
    }

    /// <summary>Writes a <c>trace</c> line.</summary>
    public void Trace(string message, string? source = null, object? logContext = null)
        => Log(NexusLogLevel.Trace, message, source, logContext);

    /// <summary>Writes a <c>debug</c> line.</summary>
    public void Debug(string message, string? source = null, object? logContext = null)
        => Log(NexusLogLevel.Debug, message, source, logContext);

    /// <summary>Writes an <c>info</c> line.</summary>
    public void Information(string message, string? source = null, object? logContext = null)
        => Log(NexusLogLevel.Information, message, source, logContext);

    /// <summary>Writes a <c>warn</c> line.</summary>
    public void Warning(string message, string? source = null, object? logContext = null)
        => Log(NexusLogLevel.Warning, message, source, logContext);

    /// <summary>Writes an <c>error</c> line.</summary>
    public void Error(string message, string? source = null, object? logContext = null)
        => Log(NexusLogLevel.Error, message, source, logContext);

    /// <summary>Writes a <c>fatal</c> line.</summary>
    public void Fatal(string message, string? source = null, object? logContext = null)
        => Log(NexusLogLevel.Fatal, message, source, logContext);

    /// <summary>Sends many log lines immediately, bypassing the buffer.</summary>
    /// <returns>How many lines the server wrote.</returns>
    public async Task<int> BatchAsync(
        IEnumerable<NexusLogEntry> logs,
        NexusTelemetryContext? context = null,
        CancellationToken cancellationToken = default)
    {
        if (logs is null)
        {
            throw new ArgumentNullException(nameof(logs));
        }

        var resolved = Resolve(context);
        var items = new List<JsonObject>();

        foreach (var entry in logs)
        {
            items.Add(Payloads.LogItem(
                NexusLogEntry.Wire(entry.Level),
                entry.Message,
                entry.Source,
                entry.Context,
                entry.Timestamp.HasValue ? JsonHelpers.Iso8601(entry.Timestamp.Value) : null));
        }

        if (items.Count == 0)
        {
            return 0;
        }

        Guard.AtMost(items.Count, MaxBatchSize, "log lines", nameof(logs));

        var response = await SendAsync(
            Payloads.LogsBatch(items, resolved.ToWire()), cancellationToken).ConfigureAwait(false);

        return response is null ? 0 : response.Int("written", items.Count);
    }

    /// <summary>Ships every buffered log line right now.</summary>
    public Task FlushAsync(CancellationToken cancellationToken = default)
        => Client.FlushLogsAsync(cancellationToken);
}
