using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Inverge.Nexus.Internal;

namespace Inverge.Nexus.Models;

/// <summary>
/// Every active flag resolved for one person.
/// </summary>
/// <remarks>
/// One evaluation covers every flag in the environment, so asking the result
/// several questions costs one request. Calling <c>IsEnabledAsync</c> three times
/// costs three.
/// </remarks>
/// <example>
/// <code>
/// var flags = await nexus.Flags.EvaluateAsync("user_1", new { plan = "pro" });
/// if (flags.IsEnabled("new_checkout")) { ... }
/// var copy = flags.Payload("paywall");
/// </code>
/// </example>
public sealed class FlagEvaluation : NexusResult
{
    private readonly IReadOnlyDictionary<string, JsonNode?> _flags;
    private readonly IReadOnlyDictionary<string, JsonNode?> _payloads;

    private FlagEvaluation(JsonNode? raw) : base(raw)
    {
        _flags = raw.Map("flags");
        _payloads = raw.Map("payloads");
    }

    /// <summary>
    /// Flag keys mapped to their resolved value: a boolean for an on/off flag,
    /// the variant name for a multivariate one.
    /// </summary>
    public IReadOnlyDictionary<string, JsonNode?> Flags => _flags;

    /// <summary>Flag keys mapped to their attached JSON payload.</summary>
    public IReadOnlyDictionary<string, JsonNode?> Payloads => _payloads;

    /// <summary>Whether any flag came back. An empty result usually means a usage cap.</summary>
    public bool IsEmpty => _flags.Count == 0;

    /// <summary>
    /// Whether a flag is on — true for an enabled boolean flag, and for any
    /// resolved variant of a multivariate one.
    /// </summary>
    public bool IsEnabled(string key)
    {
        if (!_flags.TryGetValue(key, out var value))
        {
            return false;
        }

        var text = JsonRead.AsString(value);
        if (text is not null && !string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
                             && !string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
        {
            // A variant name: the flag resolved to something, so it is on.
            return text.Length > 0;
        }

        return JsonRead.AsBool(value);
    }

    /// <summary>The resolved variant of a multivariate flag, or <c>null</c>.</summary>
    public string? Variant(string key)
    {
        if (!_flags.TryGetValue(key, out var value))
        {
            return null;
        }

        var text = JsonRead.AsString(value);
        if (text is null)
        {
            return null;
        }

        // A boolean flag has no variant, however it was encoded on the wire.
        return string.Equals(text, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(text, "false", StringComparison.OrdinalIgnoreCase)
            ? null
            : text;
    }

    /// <summary>The flag's attached JSON payload, or <c>null</c>.</summary>
    public JsonNode? Payload(string key)
        => _payloads.TryGetValue(key, out var value) ? value : null;

    /// <summary>Whether the evaluation mentions a flag at all.</summary>
    public bool Contains(string key) => _flags.ContainsKey(key);

    internal static FlagEvaluation From(JsonNode? raw) => new FlagEvaluation(raw);
}
