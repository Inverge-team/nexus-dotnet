using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Inverge.Nexus.Internal;

/// <summary>
/// Tolerant readers for response payloads.
/// </summary>
/// <remarks>
/// Responses are read field by field rather than deserialized into DTOs. That
/// is deliberate: a field the server adds tomorrow is available through
/// <c>Raw</c> without an SDK upgrade, and a field that changes shape degrades to
/// a default instead of throwing somewhere far from the call site. Indexing a
/// <see cref="JsonNode"/> that is not an object throws, so every read here goes
/// through a type check first.
/// </remarks>
internal static class JsonRead
{
    public static JsonNode? Prop(this JsonNode? node, string name)
        => node is JsonObject obj && obj.TryGetPropertyValue(name, out var value) ? value : null;

    public static JsonObject? Obj(this JsonNode? node, string name)
        => node.Prop(name) as JsonObject;

    public static JsonArray? Arr(this JsonNode? node, string name)
        => node.Prop(name) as JsonArray;

    public static string? Str(this JsonNode? node, string name)
        => AsString(node.Prop(name));

    public static bool Flag(this JsonNode? node, string name, bool fallback = false)
        => AsBool(node.Prop(name), fallback);

    public static int Int(this JsonNode? node, string name, int fallback = 0)
    {
        var value = AsDouble(node.Prop(name));
        return value.HasValue ? (int)value.Value : fallback;
    }

    public static long Long(this JsonNode? node, string name, long fallback = 0)
    {
        var value = AsDouble(node.Prop(name));
        return value.HasValue ? (long)value.Value : fallback;
    }

    public static double Number(this JsonNode? node, string name, double fallback = 0)
        => AsDouble(node.Prop(name)) ?? fallback;

    public static DateTimeOffset? Timestamp(this JsonNode? node, string name)
    {
        var text = node.Str(name);
        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    /// <summary>The items of an array property, objects and scalars alike.</summary>
    public static IReadOnlyList<JsonNode> Items(this JsonNode? node, string name)
    {
        var array = node.Arr(name);
        if (array is null)
        {
            return System.Array.Empty<JsonNode>();
        }

        var items = new List<JsonNode>(array.Count);
        foreach (var item in array)
        {
            if (item is not null)
            {
                items.Add(item);
            }
        }

        return items;
    }

    /// <summary>A string array property, skipping non-string entries.</summary>
    public static IReadOnlyList<string> Strings(this JsonNode? node, string name)
    {
        var array = node.Arr(name);
        if (array is null)
        {
            return System.Array.Empty<string>();
        }

        var values = new List<string>(array.Count);
        foreach (var item in array)
        {
            var text = AsString(item);
            if (text is not null)
            {
                values.Add(text);
            }
        }

        return values;
    }

    /// <summary>An object property flattened to a dictionary of raw nodes.</summary>
    public static IReadOnlyDictionary<string, JsonNode?> Map(this JsonNode? node, string name)
    {
        var obj = node.Obj(name);
        if (obj is null)
        {
            return new Dictionary<string, JsonNode?>(0);
        }

        var map = new Dictionary<string, JsonNode?>(obj.Count, System.StringComparer.Ordinal);
        foreach (var pair in obj)
        {
            map[pair.Key] = pair.Value?.DeepClone();
        }

        return map;
    }

    public static string? AsString(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out string? text))
        {
            return text;
        }

        // A server that answers with a number or a bool where a string is
        // expected should not blank the field out.
        if (value.TryGetValue(out double number))
        {
            return number.ToString(CultureInfo.InvariantCulture);
        }

        return value.TryGetValue(out bool flag) ? (flag ? "true" : "false") : null;
    }

    public static bool AsBool(JsonNode? node, bool fallback = false)
    {
        if (node is not JsonValue value)
        {
            return fallback;
        }

        if (value.TryGetValue(out bool flag))
        {
            return flag;
        }

        if (value.TryGetValue(out double number))
        {
            return number != 0;
        }

        if (!value.TryGetValue(out string? text) || text is null)
        {
            return fallback;
        }

        return text.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "on" or "y" or "t" => true,
            "0" or "false" or "no" or "off" or "n" or "f" => false,
            _ => fallback,
        };
    }

    public static double? AsDouble(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue(out double number))
        {
            return number;
        }

        if (value.TryGetValue(out bool flag))
        {
            return flag ? 1 : 0;
        }

        if (value.TryGetValue(out string? text)
            && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
