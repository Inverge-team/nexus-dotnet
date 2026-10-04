using System;
using System.Globalization;
using System.Text.Json.Nodes;
using Inverge.Nexus.Internal;

namespace Inverge.Nexus.Models;

/// <summary>One resolved Remote Config parameter, with conversions.</summary>
/// <remarks>
/// The conversions are forgiving on purpose: configuration is edited by people,
/// and a parameter typed as a string in the console but read as a number here
/// should still work rather than throwing in production.
/// </remarks>
public sealed class RemoteConfigValue
{
    internal RemoteConfigValue(string key, JsonNode? value, string? valueType = null, string? source = null)
    {
        Key = key;
        Value = value;
        ValueType = valueType;
        Source = source;
    }

    /// <summary>The parameter key.</summary>
    public string Key { get; }

    /// <summary>The resolved value, as JSON.</summary>
    public JsonNode? Value { get; }

    /// <summary>The declared type: <c>STRING</c>, <c>NUMBER</c>, <c>BOOLEAN</c> or <c>JSON</c>.</summary>
    public string? ValueType { get; }

    /// <summary>
    /// Which condition supplied the value, or <c>null</c> when it came from the
    /// parameter's default. Useful when a rollout is not behaving as expected.
    /// </summary>
    public string? Source { get; }

    /// <summary>Whether a value was resolved at all.</summary>
    public bool HasValue => Value is not null;

    /// <summary>The value as a string.</summary>
    public string AsString(string fallback = "")
    {
        if (Value is null)
        {
            return fallback;
        }

        if (Value is JsonObject || Value is JsonArray)
        {
            return Value.ToJsonString();
        }

        return JsonRead.AsString(Value) ?? fallback;
    }

    /// <summary>The value as a boolean, reading <c>"true"</c>, <c>"1"</c>, <c>"yes"</c> and friends.</summary>
    public bool AsBoolean(bool fallback = false) => JsonRead.AsBool(Value, fallback);

    /// <summary>The value as a double.</summary>
    public double AsNumber(double fallback = 0) => JsonRead.AsDouble(Value) ?? fallback;

    /// <summary>The value as an int.</summary>
    public int AsInt32(int fallback = 0)
    {
        var number = JsonRead.AsDouble(Value);
        return number.HasValue ? (int)number.Value : fallback;
    }

    /// <summary>The value as a long.</summary>
    public long AsInt64(long fallback = 0)
    {
        var number = JsonRead.AsDouble(Value);
        return number.HasValue ? (long)number.Value : fallback;
    }

    /// <summary>
    /// The value as JSON. A value stored as a JSON <em>string</em> is parsed, so a
    /// parameter typed STRING but holding <c>{"a":1}</c> still comes back usable.
    /// </summary>
    public JsonNode? AsJson(JsonNode? fallback = null)
    {
        switch (Value)
        {
            case null:
                return fallback;
            case JsonObject:
            case JsonArray:
                return Value;
        }

        var text = JsonRead.AsString(Value);
        if (string.IsNullOrWhiteSpace(text))
        {
            return fallback;
        }

        try
        {
            return JsonNode.Parse(text!) ?? fallback;
        }
        catch (System.Text.Json.JsonException)
        {
            return fallback;
        }
    }

    /// <inheritdoc />
    public override string ToString()
        => string.Format(CultureInfo.InvariantCulture, "{0}={1}", Key, AsString("(unset)"));
}
