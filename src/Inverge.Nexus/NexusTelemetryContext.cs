using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Inverge.Nexus.Internal;

namespace Inverge.Nexus;

/// <summary>
/// Who and what a piece of telemetry belongs to.
/// </summary>
/// <remarks>
/// <para>
/// The same type serves two jobs: it is the ambient context bound by
/// <see cref="NexusContext"/>, and it is the per-call override every resource
/// method accepts. That is why none of the capture methods carry ten identity
/// parameters.
/// </para>
/// <para>
/// Resolution order, lowest first: client options → ambient context →
/// the <c>context</c> argument passed to the call.
/// </para>
/// </remarks>
public sealed record NexusTelemetryContext
{
    /// <summary>A context with nothing set.</summary>
    public static readonly NexusTelemetryContext Empty = new NexusTelemetryContext();

    /// <summary>The person this belongs to — your own user id.</summary>
    public string? DistinctId { get; init; }

    /// <summary>The journey session this belongs to.</summary>
    public string? SessionKey { get; init; }

    /// <summary>The device this belongs to.</summary>
    public string? DeviceKey { get; init; }

    /// <summary>The build/release that produced it.</summary>
    public string? Release { get; init; }

    /// <summary>The application version.</summary>
    public string? AppVersion { get; init; }

    /// <summary>The platform: <c>ios</c>, <c>android</c>, <c>web</c>, <c>server</c>.</summary>
    public string? OsType { get; init; }

    /// <summary>The OS version.</summary>
    public string? OsVersion { get; init; }

    /// <summary>The browser, for web traffic.</summary>
    public string? Browser { get; init; }

    /// <summary>Two-letter country code.</summary>
    public string? Country { get; init; }

    /// <summary>The URL or route in play.</summary>
    public string? Url { get; init; }

    /// <summary>
    /// Properties merged into every event captured in this scope — a request id,
    /// a tenant, whatever every event in the scope should carry.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Properties { get; init; }

    /// <summary>
    /// This context with every value set on <paramref name="other"/> applied over
    /// it. Properties are merged key by key, not replaced wholesale.
    /// </summary>
    public NexusTelemetryContext Merge(NexusTelemetryContext? other)
    {
        if (other is null)
        {
            return this;
        }

        return new NexusTelemetryContext
        {
            DistinctId = other.DistinctId ?? DistinctId,
            SessionKey = other.SessionKey ?? SessionKey,
            DeviceKey = other.DeviceKey ?? DeviceKey,
            Release = other.Release ?? Release,
            AppVersion = other.AppVersion ?? AppVersion,
            OsType = other.OsType ?? OsType,
            OsVersion = other.OsVersion ?? OsVersion,
            Browser = other.Browser ?? Browser,
            Country = other.Country ?? Country,
            Url = other.Url ?? Url,
            Properties = MergeProperties(Properties, other.Properties),
        };
    }

    /// <summary>Whether every field is unset.</summary>
    public bool IsEmpty
        => DistinctId is null
           && SessionKey is null
           && DeviceKey is null
           && Release is null
           && AppVersion is null
           && OsType is null
           && OsVersion is null
           && Browser is null
           && Country is null
           && Url is null
           && (Properties is null || Properties.Count == 0);

    /// <summary>Writes the set identity fields onto a request body, camelCased.</summary>
    internal void WriteTo(JsonBody body)
    {
        body.Set("distinctId", DistinctId)
            .Set("sessionKey", SessionKey)
            .Set("deviceKey", DeviceKey)
            .Set("release", Release)
            .Set("appVersion", AppVersion)
            .Set("osType", OsType)
            .Set("osVersion", OsVersion)
            .Set("browser", Browser)
            .Set("country", Country)
            .Set("url", Url);
    }

    /// <summary>
    /// A stable grouping key. Two items that produce the same key share one
    /// ingest request, which is how the batching endpoints want them.
    /// </summary>
    internal string BatchKey()
    {
        var body = JsonBody.Create();
        WriteTo(body);
        return body.Build().ToJsonString();
    }

    /// <summary>The identity fields as a body, for ingest requests.</summary>
    internal JsonObject ToWire()
    {
        var body = JsonBody.Create();
        WriteTo(body);
        return body.Build();
    }

    private static IReadOnlyDictionary<string, object?>? MergeProperties(
        IReadOnlyDictionary<string, object?>? first,
        IReadOnlyDictionary<string, object?>? second)
    {
        if (second is null || second.Count == 0)
        {
            return first;
        }

        if (first is null || first.Count == 0)
        {
            return second;
        }

        var merged = new Dictionary<string, object?>(first.Count + second.Count, StringComparer.Ordinal);
        foreach (var pair in first)
        {
            merged[pair.Key] = pair.Value;
        }

        foreach (var pair in second)
        {
            merged[pair.Key] = pair.Value;
        }

        return merged;
    }
}
