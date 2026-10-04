using System.Text.Json.Nodes;
using Inverge.Nexus.Internal;

namespace Inverge.Nexus.Models;

/// <summary>
/// The answer from an endpoint that only confirms it did the thing.
/// </summary>
/// <remarks>
/// <see cref="Skipped"/> is the one to watch. Several ingest endpoints answer
/// <c>{"skipped": true}</c> with a 2xx when the environment has hit a usage limit
/// — the call succeeded, and the data was not stored. Treating that as success
/// hides a billing problem as missing data.
/// </remarks>
public sealed class NexusAck : NexusResult
{
    private NexusAck(JsonNode? raw) : base(raw)
    {
        Ok = raw.Flag("ok", raw is not null);
        Skipped = raw.Flag("skipped");
    }

    /// <summary>Whether the server acknowledged the call.</summary>
    public bool Ok { get; }

    /// <summary>Whether the server accepted the call but stored nothing, usually a usage cap.</summary>
    public bool Skipped { get; }

    internal static NexusAck From(JsonNode? raw) => new NexusAck(raw);
}
