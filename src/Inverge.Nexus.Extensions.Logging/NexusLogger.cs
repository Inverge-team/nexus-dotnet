using System;
using System.Collections.Generic;
using System.Globalization;
using Inverge.Nexus.Diagnostics;
using Inverge.Nexus.Models;
using Microsoft.Extensions.Logging;

namespace Inverge.Nexus.Extensions.Logging;

/// <summary>Forwards <c>ILogger</c> records to the Nexus logs product.</summary>
internal sealed class NexusLogger : ILogger
{
    private readonly string _category;
    private readonly NexusClientAccessor _accessor;
    private readonly NexusLoggingOptions _options;
    private readonly Func<IExternalScopeProvider?> _scopes;

    public NexusLogger(
        string category,
        NexusClientAccessor accessor,
        NexusLoggingOptions options,
        Func<IExternalScopeProvider?> scopes)
    {
        _category = category;
        _accessor = accessor;
        _options = options;
        _scopes = scopes;
    }

    public IDisposable BeginScope<TState>(TState state)
        where TState : notnull
        => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel)
    {
        if (logLevel == LogLevel.None || logLevel < _options.MinimumLevel)
        {
            return false;
        }

        // Never forward the SDK's own diagnostics: delivering a line about a failed
        // delivery produces another line about a failed delivery.
        if (_category.StartsWith("Inverge.Nexus", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var prefix in _options.ExcludedCategoryPrefixes)
        {
            if (!string.IsNullOrEmpty(prefix) && _category.StartsWith(prefix, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel) || formatter is null)
        {
            return;
        }

        // Anything written while the SDK is delivering is the delivery's own noise —
        // HttpClient logs every request at Information. Forwarding one would queue
        // another log line, whose delivery logs again, without end.
        if (NexusDelivery.IsDelivering)
        {
            return;
        }

        var client = _accessor.TryGet();
        if (client is null)
        {
            return;
        }

        try
        {
            var message = formatter(state, exception);
            var context = BuildContext(eventId, state, exception);

            client.Logs.Log(Translate(logLevel), message, _category, context);

            if (_options.CaptureExceptions && exception is not null)
            {
                // Fire and forget: a logging call must not become an await point, and
                // a failure to report must not fail the thing being reported.
                _ = client.Errors.CaptureExceptionAsync(
                    exception,
                    handled: true,
                    level: logLevel >= LogLevel.Critical ? "fatal" : "error",
                    errorContext: context);
            }
        }
        catch (Exception)
        {
            // A logging bridge that throws breaks whatever was being logged. There
            // is nowhere useful left to report this, so it stops here.
        }
    }

    internal static NexusLogLevel Translate(LogLevel level) => level switch
    {
        LogLevel.Trace => NexusLogLevel.Trace,
        LogLevel.Debug => NexusLogLevel.Debug,
        LogLevel.Information => NexusLogLevel.Information,
        LogLevel.Warning => NexusLogLevel.Warning,
        LogLevel.Error => NexusLogLevel.Error,
        LogLevel.Critical => NexusLogLevel.Fatal,
        _ => NexusLogLevel.Information,
    };

    private Dictionary<string, object?> BuildContext<TState>(
        EventId eventId, TState state, Exception? exception)
    {
        var context = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["logger"] = _category,
        };

        if (_options.IncludeEventId && (eventId.Id != 0 || eventId.Name is not null))
        {
            context["eventId"] = eventId.Name ?? eventId.Id.ToString(CultureInfo.InvariantCulture);
        }

        if (exception is not null)
        {
            context["exceptionType"] = exception.GetType().FullName;
        }

        // Structured message arguments: the whole point of a templated log line is
        // that the values are queryable rather than baked into a string.
        if (state is IReadOnlyList<KeyValuePair<string, object?>> values)
        {
            foreach (var pair in values)
            {
                if (!string.Equals(pair.Key, "{OriginalFormat}", StringComparison.Ordinal))
                {
                    context[pair.Key] = pair.Value;
                }
            }
        }

        if (_options.IncludeScopes)
        {
            AppendScopes(context);
        }

        return context;
    }

    private void AppendScopes(Dictionary<string, object?> context)
    {
        var provider = _scopes();
        if (provider is null)
        {
            return;
        }

        var index = 0;
        provider.ForEachScope(
            (scope, target) =>
            {
                switch (scope)
                {
                    case null:
                        return;

                    case IEnumerable<KeyValuePair<string, object?>> pairs:
                        foreach (var pair in pairs)
                        {
                            if (!string.Equals(pair.Key, "{OriginalFormat}", StringComparison.Ordinal))
                            {
                                // An inner scope's key wins over an outer one's,
                                // which is the nesting a reader expects.
                                target[pair.Key] = pair.Value;
                            }
                        }

                        return;

                    default:
                        target["scope" + index.ToString(CultureInfo.InvariantCulture)] = scope.ToString();
                        index++;
                        return;
                }
            },
            context);
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new NullScope();

        public void Dispose()
        {
        }
    }
}
