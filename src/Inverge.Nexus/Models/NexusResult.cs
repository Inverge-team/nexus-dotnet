using System.Text.Json.Nodes;

namespace Inverge.Nexus.Models;

/// <summary>
/// Base for the SDK's response types.
/// </summary>
/// <remarks>
/// Each result reads the fields the endpoint documents into typed properties and
/// keeps the whole payload in <see cref="Raw"/>. That combination is deliberate:
/// you get IntelliSense and compile-time names for the contract, and a field the
/// server adds next month is reachable immediately without waiting for an SDK
/// release.
/// </remarks>
public abstract class NexusResult
{
    /// <summary>Creates the result around a decoded payload.</summary>
    protected NexusResult(JsonNode? raw) => Raw = raw;

    /// <summary>
    /// The decoded response, exactly as the API sent it. <c>null</c> when the
    /// response had no body, or when a silent client swallowed a failure.
    /// </summary>
    public JsonNode? Raw { get; }

    /// <summary>Whether a response body arrived at all.</summary>
    public bool HasPayload => Raw is not null;

    /// <summary>The response as compact JSON — handy in a log line.</summary>
    public override string ToString() => Raw?.ToJsonString() ?? "(no payload)";
}
