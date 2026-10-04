using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Transport;

namespace Inverge.Nexus.Tests.Support;

/// <summary>A transport that records what the SDK sent and replies with a script.</summary>
public sealed class RecordingTransport : INexusTransport
{
    private readonly Func<RecordedRequest, NexusTransportResponse> _respond;

    public RecordingTransport(Func<RecordedRequest, NexusTransportResponse>? respond = null)
        => _respond = respond ?? (_ => Json(200, "{}"));

    public ConcurrentQueue<RecordedRequest> Requests { get; } = new();

    public int Count => Requests.Count;

    public RecordedRequest Last
    {
        get
        {
            RecordedRequest? last = null;
            foreach (var request in Requests)
            {
                last = request;
            }

            return last ?? throw new InvalidOperationException("No request was recorded.");
        }
    }

    public RecordedRequest Single()
    {
        if (Requests.Count != 1)
        {
            throw new InvalidOperationException($"Expected exactly 1 request, recorded {Requests.Count}.");
        }

        return Last;
    }

    public IReadOnlyList<RecordedRequest> All() => new List<RecordedRequest>(Requests);

    public Task<NexusTransportResponse> SendAsync(
        string method,
        string url,
        IReadOnlyDictionary<string, string> headers,
        byte[]? body,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var recorded = new RecordedRequest(
            method,
            url,
            new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase),
            body is null ? null : Encoding.UTF8.GetString(body));

        Requests.Enqueue(recorded);
        return Task.FromResult(_respond(recorded));
    }

    public void Dispose()
    {
    }

    public static NexusTransportResponse Json(int status, string body)
        => new(status, Encoding.UTF8.GetBytes(body));

    public static NexusTransportResponse Json(
        int status, string body, IReadOnlyDictionary<string, string> headers)
        => new(status, Encoding.UTF8.GetBytes(body), headers);

    public static NexusTransportResponse Empty(int status) => new(status);
}

/// <summary>One request the SDK made.</summary>
public sealed record RecordedRequest(
    string Method, string Url, IReadOnlyDictionary<string, string> Headers, string? Body)
{
    /// <summary>The path and query, without the origin.</summary>
    public string Path => new Uri(Url).PathAndQuery;

    /// <summary>The body parsed as JSON.</summary>
    public JsonNode? Json => Body is null ? null : JsonNode.Parse(Body);

    /// <summary>The body as a JSON object.</summary>
    public JsonObject Object => Json as JsonObject
        ?? throw new InvalidOperationException("The request body is not a JSON object: " + Body);

    public string? Field(string name) => Object.TryGetPropertyValue(name, out var value)
        ? value?.ToString()
        : null;

    public bool Has(string name) => Object.ContainsKey(name);
}
