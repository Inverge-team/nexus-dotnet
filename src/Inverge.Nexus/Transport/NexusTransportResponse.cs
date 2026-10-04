using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;

namespace Inverge.Nexus.Transport;

/// <summary>A transport-level HTTP response, before any Nexus semantics.</summary>
public sealed class NexusTransportResponse
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders
        = new Dictionary<string, string>(0, StringComparer.OrdinalIgnoreCase);

    private string? _text;

    /// <summary>Creates a response.</summary>
    /// <param name="status">The HTTP status code.</param>
    /// <param name="body">The raw body bytes.</param>
    /// <param name="headers">Response headers, keyed lower-case.</param>
    public NexusTransportResponse(
        int status,
        byte[]? body = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        Status = status;
        Body = body ?? Array.Empty<byte>();
        Headers = headers ?? NoHeaders;
    }

    /// <summary>The HTTP status code.</summary>
    public int Status { get; }

    /// <summary>The raw body bytes.</summary>
    public byte[] Body { get; }

    /// <summary>Response headers. Keys are compared case-insensitively.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; }

    /// <summary>Whether the status is 2xx.</summary>
    public bool IsSuccess => Status >= 200 && Status < 300;

    /// <summary>The body decoded as UTF-8.</summary>
    public string Text => _text ??= Body.Length == 0 ? string.Empty : Encoding.UTF8.GetString(Body);

    /// <summary>
    /// The body parsed as JSON, or <c>null</c> when it is empty or not JSON.
    /// </summary>
    /// <remarks>
    /// A non-JSON body is a normal outcome — a proxy's HTML error page, a 204 —
    /// so this returns null rather than throwing. The caller still has
    /// <see cref="Text"/> for the error it reports.
    /// </remarks>
    public JsonNode? ReadJson()
    {
        var text = Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
