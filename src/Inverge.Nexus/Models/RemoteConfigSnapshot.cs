using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Inverge.Nexus.Internal;

namespace Inverge.Nexus.Models;

/// <summary>
/// A resolved Remote Config template: typed getters over the values the server
/// evaluated for your context.
/// </summary>
/// <remarks>
/// Conditions — platform, version, country, percentile, custom attributes — are
/// evaluated server-side, so what arrives here is final. Nothing is decided
/// locally except the in-app defaults you registered, which fill a key the
/// template does not define.
/// </remarks>
public sealed class RemoteConfigSnapshot : NexusResult
{
    private readonly IReadOnlyDictionary<string, RemoteConfigValue> _parameters;
    private readonly IReadOnlyDictionary<string, object?> _defaults;

    private RemoteConfigSnapshot(
        JsonNode? raw,
        int version,
        string etag,
        bool notModified,
        bool throttled,
        IReadOnlyDictionary<string, RemoteConfigValue> parameters,
        IReadOnlyDictionary<string, object?> defaults)
        : base(raw)
    {
        Version = version;
        ETag = etag;
        NotModified = notModified;
        Throttled = throttled;
        _parameters = parameters;
        _defaults = defaults;
    }

    /// <summary>The published template version these values came from.</summary>
    public int Version { get; }

    /// <summary>
    /// The ETag of the resolved result. It hashes the evaluated values, not just
    /// the template, so a changed context produces a new ETag rather than a false
    /// "not modified".
    /// </summary>
    public string ETag { get; }

    /// <summary>Whether the server said nothing changed since the ETag we sent.</summary>
    public bool NotModified { get; }

    /// <summary>
    /// Whether the environment hit its fetch limit. The values here are the last
    /// good ones plus your defaults — the server deliberately served and billed
    /// nothing.
    /// </summary>
    public bool Throttled { get; }

    /// <summary>The resolved parameters, by key.</summary>
    public IReadOnlyDictionary<string, RemoteConfigValue> Parameters => _parameters;

    /// <summary>The in-app defaults in force for this snapshot.</summary>
    public IReadOnlyDictionary<string, object?> Defaults => _defaults;

    /// <summary>The parameter, falling back to the in-app default you registered.</summary>
    public RemoteConfigValue Value(string key)
    {
        if (_parameters.TryGetValue(key, out var found))
        {
            return found;
        }

        _defaults.TryGetValue(key, out var fallback);
        return new RemoteConfigValue(key, JsonHelpers.ToNode(fallback));
    }

    /// <summary>A string parameter.</summary>
    public string GetString(string key, string fallback = "") => Value(key).AsString(fallback);

    /// <summary>A boolean parameter — the shape a kill switch takes.</summary>
    public bool GetBoolean(string key, bool fallback = false) => Value(key).AsBoolean(fallback);

    /// <summary>A numeric parameter.</summary>
    public double GetNumber(string key, double fallback = 0) => Value(key).AsNumber(fallback);

    /// <summary>An integer parameter.</summary>
    public int GetInt32(string key, int fallback = 0) => Value(key).AsInt32(fallback);

    /// <summary>A JSON parameter.</summary>
    public JsonNode? GetJson(string key, JsonNode? fallback = null) => Value(key).AsJson(fallback);

    /// <summary>Which condition supplied a key's value, or <c>null</c> for the default.</summary>
    public string? SourceOf(string key)
        => _parameters.TryGetValue(key, out var found) ? found.Source : null;

    /// <summary>Whether the key is defined by the template or by your defaults.</summary>
    public bool Contains(string key) => _parameters.ContainsKey(key) || _defaults.ContainsKey(key);

    /// <summary>A flat key-to-value map, defaults merged underneath.</summary>
    public IReadOnlyDictionary<string, JsonNode?> ToDictionary()
    {
        var all = new Dictionary<string, JsonNode?>(
            _parameters.Count + _defaults.Count, StringComparer.Ordinal);

        foreach (var pair in _defaults)
        {
            all[pair.Key] = JsonHelpers.ToNode(pair.Value);
        }

        foreach (var pair in _parameters)
        {
            all[pair.Key] = pair.Value.Value;
        }

        return all;
    }

    internal static RemoteConfigSnapshot From(
        JsonNode? raw,
        IReadOnlyDictionary<string, object?> defaults,
        RemoteConfigSnapshot? previous)
    {
        var notModified = raw.Flag("notModified");
        var throttled = raw.Flag("throttled");

        // "Not modified" carries no parameters, so the previously resolved values
        // are the answer. Without this the snapshot would come back empty and
        // every getter would silently fall through to its default.
        if (notModified && previous is not null)
        {
            return new RemoteConfigSnapshot(
                raw,
                previous.Version,
                previous.ETag,
                notModified: true,
                throttled: throttled,
                previous._parameters,
                defaults);
        }

        var parameters = new Dictionary<string, RemoteConfigValue>(StringComparer.Ordinal);
        if (raw.Obj("parameters") is { } parametersNode)
        {
            foreach (var entry in parametersNode)
            {
                if (entry.Value is JsonObject details)
                {
                    parameters[entry.Key] = new RemoteConfigValue(
                        entry.Key,
                        details.Prop("value")?.DeepClone(),
                        details.Str("valueType"),
                        details.Str("source"));
                }
                else
                {
                    // Tolerate a flat {key: value} shape.
                    parameters[entry.Key] = new RemoteConfigValue(entry.Key, entry.Value?.DeepClone());
                }
            }
        }

        return new RemoteConfigSnapshot(
            raw,
            raw.Int("version"),
            raw.Str("etag") ?? string.Empty,
            notModified,
            throttled,
            parameters,
            defaults);
    }
}
