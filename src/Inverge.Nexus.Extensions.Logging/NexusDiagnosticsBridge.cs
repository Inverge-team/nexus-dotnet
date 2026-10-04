using System;
using Inverge.Nexus.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Inverge.Nexus.Extensions.Logging;

/// <summary>
/// Routes the SDK's own diagnostics into <c>ILogger</c>.
/// </summary>
/// <remarks>
/// Pre-compiled <see cref="LoggerMessage"/> delegates rather than the
/// <c>LogWarning(...)</c> extension methods: these fire on every dropped batch and
/// every retry, which under an outage is the hottest path in the SDK — exactly when
/// you cannot afford the extension methods' boxing and format-string work on a
/// message that a disabled logger will throw away.
/// </remarks>
internal static class NexusDiagnosticsBridge
{
    private static readonly Action<ILogger, string, Exception?> LogDebug =
        LoggerMessage.Define<string>(LogLevel.Debug, new EventId(1, "NexusDiagnostic"), "{NexusMessage}");

    private static readonly Action<ILogger, string, Exception?> LogInformation =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(2, "NexusDiagnostic"), "{NexusMessage}");

    private static readonly Action<ILogger, string, Exception?> LogWarning =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(3, "NexusDiagnostic"), "{NexusMessage}");

    private static readonly Action<ILogger, string, Exception?> LogError =
        LoggerMessage.Define<string>(LogLevel.Error, new EventId(4, "NexusDiagnostic"), "{NexusMessage}");

    /// <summary>A sink that writes to <paramref name="logger"/>.</summary>
    public static NexusDiagnosticSink For(ILogger logger)
        => (level, message, exception) =>
        {
            switch (level)
            {
                case NexusDiagnosticLevel.Debug:
                    LogDebug(logger, message, exception);
                    break;
                case NexusDiagnosticLevel.Information:
                    LogInformation(logger, message, exception);
                    break;
                case NexusDiagnosticLevel.Error:
                    LogError(logger, message, exception);
                    break;
                default:
                    LogWarning(logger, message, exception);
                    break;
            }
        };
}
