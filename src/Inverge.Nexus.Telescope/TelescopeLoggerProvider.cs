using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Inverge.Nexus.Telescope;

/// <summary>
/// Records log records into the journey they were written in (and only while a
/// journey is open). A record carrying an exception becomes an exception entry.
/// </summary>
[ProviderAlias("NexusTelescope")]
public sealed class TelescopeLoggerProvider : ILoggerProvider
{
    private readonly NexusTelescope _telescope;

    /// <summary>Creates the provider.</summary>
    public TelescopeLoggerProvider(NexusTelescope telescope) => _telescope = telescope ?? throw new ArgumentNullException(nameof(telescope));

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => new TelescopeLogger(_telescope, categoryName);

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private sealed class TelescopeLogger : ILogger
    {
        private readonly NexusTelescope _telescope;
        private readonly string _category;
        private readonly bool _own;
        private readonly bool _framework;

        public TelescopeLogger(NexusTelescope telescope, string category)
        {
            _telescope = telescope;
            _category = category;
            // The SDK's own diagnostics, and HttpClient's per-request lines (already an http entry).
            _own = category == "Inverge.Nexus" || category.StartsWith("Inverge.Nexus.Telescope", StringComparison.Ordinal) || category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal);
            // Framework chatter ("Executing endpoint…") is noise below Warning.
            _framework = category.StartsWith("Microsoft.", StringComparison.Ordinal) || category.StartsWith("System.", StringComparison.Ordinal);
        }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
            => !_own && logLevel != LogLevel.None && logLevel >= _telescope.Options.MinimumLogLevel
               && (!_framework || logLevel >= LogLevel.Warning) && Activity.Current is not null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || formatter is null)
            {
                return;
            }

            if (exception is not null)
            {
                // An unhandled exception is logged by the framework; record it once, as an exception.
                _telescope.RecordException(exception, handled: logLevel < LogLevel.Error);
                return;
            }

            var context = new Dictionary<string, object?> { ["category"] = _category };
            if (eventId.Id != 0)
            {
                context["eventId"] = eventId.Name ?? eventId.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (state is IEnumerable<KeyValuePair<string, object?>> fields)
            {
                foreach (var f in fields)
                {
                    if (f.Key != "{OriginalFormat}")
                    {
                        context[f.Key] = f.Value;
                    }
                }
            }

            var level = logLevel switch
            {
                LogLevel.Trace => "trace",
                LogLevel.Debug => "debug",
                LogLevel.Information => "info",
                LogLevel.Warning => "warning",
                LogLevel.Error => "error",
                _ => "critical",
            };
            _telescope.Log(level, formatter(state, exception), context);
        }
    }
}
