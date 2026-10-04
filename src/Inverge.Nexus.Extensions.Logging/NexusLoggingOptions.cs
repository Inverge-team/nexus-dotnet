using Microsoft.Extensions.Logging;

namespace Inverge.Nexus.Extensions.Logging;

/// <summary>Controls what the <see cref="NexusLoggerProvider"/> forwards.</summary>
public sealed class NexusLoggingOptions
{
    /// <summary>
    /// The lowest level forwarded to Nexus.
    /// </summary>
    /// <remarks>
    /// This is a second gate on top of the logging framework's own filters, so you
    /// can keep <c>Debug</c> in the console while sending only <c>Information</c> and
    /// above over the network — logs are billed per line.
    /// </remarks>
    public LogLevel MinimumLevel { get; set; } = LogLevel.Information;

    /// <summary>
    /// Also report records that carry an exception to error monitoring, so a plain
    /// <c>logger.LogError(ex, ...)</c> produces a grouped, stack-traced issue.
    /// </summary>
    public bool CaptureExceptions { get; set; } = true;

    /// <summary>
    /// Include the active logging scopes in each line's context.
    /// </summary>
    /// <remarks>
    /// In ASP.NET Core this is what carries the request id and route into the log
    /// line, which is usually the difference between a searchable log and a wall of
    /// text.
    /// </remarks>
    public bool IncludeScopes { get; set; } = true;

    /// <summary>Include the event id, when the call site set one.</summary>
    public bool IncludeEventId { get; set; } = true;

    /// <summary>
    /// Category prefixes never forwarded.
    /// </summary>
    /// <remarks>
    /// <c>Inverge.Nexus</c> is excluded unconditionally and is not listed here: the
    /// SDK's own diagnostics must never become Nexus log lines, because delivering
    /// them would produce more of them.
    /// </remarks>
    public string[] ExcludedCategoryPrefixes { get; set; } = System.Array.Empty<string>();
}
