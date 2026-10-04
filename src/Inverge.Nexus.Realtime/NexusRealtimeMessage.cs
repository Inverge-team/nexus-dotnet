using System;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Inverge.Nexus.Realtime;

/// <summary>One message received from a realtime room.</summary>
public sealed class NexusRealtimeMessage
{
    internal NexusRealtimeMessage(string eventName, JsonNode? payload, string? rawText)
    {
        EventName = eventName;
        Payload = payload;
        RawText = rawText;
    }

    /// <summary>The event name the message was emitted under.</summary>
    public string EventName { get; }

    /// <summary>The payload, as JSON. <c>null</c> when the emit carried none.</summary>
    public JsonNode? Payload { get; }

    /// <summary>The raw frame text, for when the payload will not parse as expected.</summary>
    public string? RawText { get; }

    /// <summary>
    /// Deserializes the payload into <typeparamref name="T"/>.
    /// </summary>
    /// <remarks>
    /// Reflection-based, so a trimmed or native-AOT host should read
    /// <see cref="Payload"/> directly, or pass a <paramref name="options"/> built
    /// from a source-generated context.
    /// </remarks>
    public T? As<T>(JsonSerializerOptions? options = null)
        => Payload is null ? default : Payload.Deserialize<T>(options);

    /// <inheritdoc />
    public override string ToString()
        => EventName + " " + (Payload?.ToJsonString() ?? "(no payload)");
}

/// <summary>Why a realtime connection failed or dropped.</summary>
public sealed class NexusRealtimeErrorEventArgs : EventArgs
{
    internal NexusRealtimeErrorEventArgs(string message, Exception? exception = null)
    {
        Message = message ?? string.Empty;
        Exception = exception;
    }

    /// <summary>The server's message, or the transport's.</summary>
    public string Message { get; }

    /// <summary>The underlying exception, when there was one.</summary>
    public Exception? Exception { get; }

    /// <summary>
    /// Whether the handshake was refused because the organisation's data plane is
    /// suspended for unpaid invoices.
    /// </summary>
    /// <remarks>
    /// The server reports this distinctly from a bad key precisely so a client can
    /// tell "pay the bill" apart from "fix your credentials" and stop retrying.
    /// </remarks>
    public bool IsBillingSuspended
        => Message.IndexOf("billing_suspended", StringComparison.OrdinalIgnoreCase) >= 0;

    /// <summary>Whether the API key was rejected.</summary>
    public bool IsUnauthorized
        => Message.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0;
}
