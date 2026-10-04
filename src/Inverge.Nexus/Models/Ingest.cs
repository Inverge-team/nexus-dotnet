using System;
using System.Collections.Generic;

namespace Inverge.Nexus.Models;

/// <summary>One analytics event, for a batch send.</summary>
public sealed class NexusEvent
{
    /// <summary>Creates an event.</summary>
    /// <param name="name">The event name, as it appears in the console.</param>
    /// <param name="properties">
    /// The event's properties — a dictionary, an anonymous object, or a
    /// <c>JsonNode</c>.
    /// </param>
    /// <param name="timestamp">
    /// When it happened. Omit it and the server stamps arrival time, which is
    /// wrong for an event you are replaying from your own records.
    /// </param>
    public NexusEvent(string name, object? properties = null, DateTimeOffset? timestamp = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("An event name is required.", nameof(name));
        }

        Name = name;
        Properties = properties;
        Timestamp = timestamp;
    }

    /// <summary>The event name.</summary>
    public string Name { get; }

    /// <summary>The event's properties.</summary>
    public object? Properties { get; }

    /// <summary>When it happened.</summary>
    public DateTimeOffset? Timestamp { get; }
}

/// <summary>Severity of a Nexus log line.</summary>
public enum NexusLogLevel
{
    /// <summary>Finest-grained detail.</summary>
    Trace = 0,

    /// <summary>Developer detail.</summary>
    Debug = 1,

    /// <summary>Normal operation.</summary>
    Information = 2,

    /// <summary>Something unexpected that did not break anything.</summary>
    Warning = 3,

    /// <summary>An operation failed.</summary>
    Error = 4,

    /// <summary>The process cannot continue.</summary>
    Fatal = 5,
}

/// <summary>One log line, for a batch send.</summary>
public sealed class NexusLogEntry
{
    /// <summary>Creates a log entry.</summary>
    public NexusLogEntry(
        NexusLogLevel level,
        string message,
        string? source = null,
        object? context = null,
        DateTimeOffset? timestamp = null)
    {
        Level = level;
        Message = message ?? string.Empty;
        Source = source;
        Context = context;
        Timestamp = timestamp;
    }

    /// <summary>The severity.</summary>
    public NexusLogLevel Level { get; }

    /// <summary>The message. Over-long messages are truncated server-side, not rejected.</summary>
    public string Message { get; }

    /// <summary>Where it came from — a subsystem, a logger name.</summary>
    public string? Source { get; }

    /// <summary>Structured context attached to the line.</summary>
    public object? Context { get; }

    /// <summary>When it was written.</summary>
    public DateTimeOffset? Timestamp { get; }

    /// <summary>The wire spelling of a level.</summary>
    internal static string Wire(NexusLogLevel level) => level switch
    {
        NexusLogLevel.Trace => "trace",
        NexusLogLevel.Debug => "debug",
        NexusLogLevel.Information => "info",
        NexusLogLevel.Warning => "warn",
        NexusLogLevel.Error => "error",
        NexusLogLevel.Fatal => "fatal",
        _ => "info",
    };
}

/// <summary>One stack frame, as the console renders it.</summary>
public sealed class NexusStackFrame
{
    /// <summary>Creates a frame.</summary>
    public NexusStackFrame(string? function, string? filename = null, int? lineNumber = null, string? contextLine = null)
    {
        Function = function;
        Filename = filename;
        LineNumber = lineNumber;
        ContextLine = contextLine;
    }

    /// <summary>The method, qualified by its declaring type.</summary>
    public string? Function { get; }

    /// <summary>The source file, when symbols were available.</summary>
    public string? Filename { get; }

    /// <summary>The line number, when symbols were available.</summary>
    public int? LineNumber { get; }

    /// <summary>The source line itself, when known.</summary>
    public string? ContextLine { get; }
}

/// <summary>One replay event, as produced by a recorder.</summary>
public sealed class NexusReplayEvent
{
    /// <summary>Creates a replay event from an already-shaped payload.</summary>
    /// <param name="payload">
    /// The recorder's own event object. Nexus stores it verbatim and the player
    /// interprets it, so its shape is the recorder's contract, not the SDK's.
    /// </param>
    public NexusReplayEvent(object payload)
        => Payload = payload ?? throw new ArgumentNullException(nameof(payload));

    /// <summary>The event payload.</summary>
    public object Payload { get; }

    /// <summary>Wraps a sequence of raw payloads.</summary>
    public static IEnumerable<NexusReplayEvent> FromPayloads(IEnumerable<object> payloads)
    {
        foreach (var payload in payloads)
        {
            yield return new NexusReplayEvent(payload);
        }
    }
}
