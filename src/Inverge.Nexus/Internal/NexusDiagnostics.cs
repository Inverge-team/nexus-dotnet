using System;
using System.Globalization;
using Inverge.Nexus.Diagnostics;

namespace Inverge.Nexus.Internal;

/// <summary>Routes the SDK's own messages to the configured sink.</summary>
/// <remarks>
/// Reporting must never be able to break the application, so a sink that throws
/// is swallowed here rather than propagating out of a telemetry call.
/// </remarks>
internal sealed class NexusDiagnostics
{
    private readonly NexusDiagnosticSink? _sink;
    private readonly bool _debug;

    public NexusDiagnostics(NexusDiagnosticSink? sink, bool debug)
    {
        _sink = sink;
        _debug = debug;
    }

    public void Debug(string message)
    {
        if (_debug)
        {
            Write(NexusDiagnosticLevel.Debug, message, null);
        }
    }

    public void Debug(string format, params object?[] args)
    {
        if (_debug)
        {
            Write(NexusDiagnosticLevel.Debug, Format(format, args), null);
        }
    }

    public void Information(string message) => Write(NexusDiagnosticLevel.Information, message, null);

    public void Warning(string message, Exception? exception = null)
        => Write(NexusDiagnosticLevel.Warning, message, exception);

    public void Warning(Exception? exception, string format, params object?[] args)
        => Write(NexusDiagnosticLevel.Warning, Format(format, args), exception);

    public void Error(string message, Exception? exception = null)
        => Write(NexusDiagnosticLevel.Error, message, exception);

    private static string Format(string format, object?[] args)
    {
        try
        {
            return string.Format(CultureInfo.InvariantCulture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }

    private void Write(NexusDiagnosticLevel level, string message, Exception? exception)
    {
        var sink = _sink;
        if (sink is null)
        {
            return;
        }

        try
        {
            sink(level, message, exception);
        }
        catch (Exception)
        {
            // A broken diagnostics sink is not a reason to fail the caller's
            // request. There is nowhere left to report this, so it stops here.
        }
    }
}
