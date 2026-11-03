using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Inverge.Nexus;

/// <summary>
/// One call to the Nexus partner API.
/// </summary>
/// <remarks>
/// Resources build these; the client executes them. Pass one to
/// <see cref="INexusClient.SendAsync"/> to reach an endpoint the typed surface
/// does not cover yet.
/// </remarks>
public sealed class NexusRequest
{
    /// <summary>Creates a request.</summary>
    /// <param name="method">HTTP method, e.g. <c>POST</c>.</param>
    /// <param name="path">Path rooted at the API origin, e.g. <c>/partner/events</c>.</param>
    /// <param name="body">JSON body, or <c>null</c> for a bodiless request.</param>
    /// <param name="headers">Extra headers for this request only.</param>
    /// <param name="retryable">
    /// Whether the request is safe to repeat. Telemetry ingest and reads are;
    /// anything a user would notice happening twice — a push send, a voice leg,
    /// a realtime emit — is deliberately not, so a timeout never doubles it.
    /// </param>
    public NexusRequest(
        string method,
        string path,
        JsonObject? body = null,
        IReadOnlyDictionary<string, string>? headers = null,
        bool retryable = false)
    {
        if (string.IsNullOrWhiteSpace(method))
        {
            throw new ArgumentException("An HTTP method is required.", nameof(method));
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A request path is required.", nameof(path));
        }

        Method = method.ToUpperInvariant();
        Path = path;
        Body = body;
        Headers = headers;
        Retryable = retryable;
    }

    /// <summary>
    /// A request whose body is raw bytes (a file upload) rather than JSON,
    /// sent with <paramref name="contentType"/>.
    /// </summary>
    public NexusRequest(
        string method,
        string path,
        byte[] rawBody,
        string contentType,
        IReadOnlyDictionary<string, string>? headers = null,
        bool retryable = false)
        : this(method, path, (JsonObject?)null, headers, retryable)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            throw new ArgumentException("A content type is required for a raw body.", nameof(contentType));
        }

        RawBody = rawBody ?? throw new ArgumentNullException(nameof(rawBody));
        ContentType = contentType;
    }

    /// <summary>The HTTP method, upper-cased.</summary>
    public string Method { get; }

    /// <summary>The path, rooted at the configured API origin.</summary>
    public string Path { get; }

    /// <summary>The JSON body, or <c>null</c>.</summary>
    public JsonObject? Body { get; }

    /// <summary>Headers for this request only, merged over the configured defaults.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; }

    /// <summary>Whether a failed attempt may be repeated.</summary>
    public bool Retryable { get; }

    /// <summary>The raw body of an upload; <see langword="null"/> for JSON requests.</summary>
    public byte[]? RawBody { get; }

    /// <summary>The media type of <see cref="RawBody"/>.</summary>
    public string? ContentType { get; }

    /// <summary>The body as compact JSON — useful in assertions and logs.</summary>
    public string BodyJson() => Body?.ToJsonString() ?? string.Empty;

    /// <inheritdoc />
    public override string ToString() => Method + " " + Path;
}
