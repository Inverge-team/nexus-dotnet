using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Inverge.Nexus.Internal;

/// <summary>
/// Conversion between caller values and <see cref="JsonNode"/>.
/// </summary>
/// <remarks>
/// Request bodies are assembled as explicit <see cref="JsonObject"/> graphs
/// rather than serialized from DTOs. That keeps the wire format in one visible
/// place (see <c>Payloads</c>), makes a body assertable byte-for-byte in a test,
/// and keeps the SDK's own code free of reflection.
/// </remarks>
internal static class JsonHelpers
{
    /// <summary>Options used for the one reflection-based path and for writing.</summary>
    internal static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    /// <summary>Converts a caller-supplied value into a JSON node.</summary>
    /// <remarks>
    /// Primitives, strings, dates, GUIDs, enums, dictionaries, sequences and
    /// <see cref="JsonNode"/> / <see cref="JsonElement"/> are handled directly.
    /// Anything else falls back to <see cref="JsonSerializer"/>, which is the
    /// SDK's only reflection path — see the remark on <see cref="SerializeUnknown"/>.
    /// </remarks>
    public static JsonNode? ToNode(object? value)
    {
        switch (value)
        {
            case null:
                return null;

            // A node already attached to another parent cannot be re-parented,
            // and a caller may legitimately pass the same node twice.
            case JsonNode node:
                return node.DeepClone();

            case JsonElement element:
                return JsonElementToNode(element);

            case string text:
                return JsonValue.Create(text);
            case bool flag:
                return JsonValue.Create(flag);
            case int i:
                return JsonValue.Create(i);
            case long l:
                return JsonValue.Create(l);
            case short s:
                return JsonValue.Create(s);
            case byte b:
                return JsonValue.Create(b);
            case uint ui:
                return JsonValue.Create(ui);
            case ulong ul:
                return JsonValue.Create(ul);
            case ushort us:
                return JsonValue.Create(us);
            case sbyte sb:
                return JsonValue.Create(sb);
            case double d:
                return JsonValue.Create(d);
            case float f:
                return JsonValue.Create(f);
            case decimal m:
                return JsonValue.Create(m);
            case char c:
                return JsonValue.Create(c.ToString());

            // The API takes timestamps as ISO-8601 strings.
            case DateTimeOffset dto:
                return JsonValue.Create(Iso8601(dto));
            case DateTime dt:
                return JsonValue.Create(Iso8601(dt));
            case TimeSpan span:
                return JsonValue.Create(span.ToString("c", CultureInfo.InvariantCulture));
            case Guid guid:
                return JsonValue.Create(guid.ToString("D", CultureInfo.InvariantCulture));
            case Uri uri:
                return JsonValue.Create(uri.ToString());
            case Enum enumValue:
                return JsonValue.Create(enumValue.ToString());

            case IDictionary<string, object?> map:
                return FromPairs(map);
            case IDictionary<string, string?> stringMap:
                return FromStringPairs(stringMap);
            case IDictionary dictionary:
                return FromUntypedDictionary(dictionary);

            case IEnumerable sequence:
                var array = new JsonArray();
                foreach (var item in sequence)
                {
                    array.Add((JsonNode?)ToNode(item));
                }

                return array;

            default:
                return SerializeUnknown(value);
        }
    }

    /// <summary>
    /// Converts a value that the API requires to be a JSON object, never an
    /// array and never null.
    /// </summary>
    /// <remarks>
    /// Several endpoints validate the field's type strictly — a survey's
    /// <c>answers</c> and a Live Activity's <c>contentState</c> are rejected when
    /// they arrive as <c>[]</c> — so an absent value becomes <c>{}</c> here.
    /// </remarks>
    public static JsonObject ToObjectNode(object? value)
        => ToNode(value) as JsonObject ?? new JsonObject();

    /// <summary>
    /// Copies <paramref name="source"/>'s top-level entries onto
    /// <paramref name="target"/>, overwriting keys that collide.
    /// </summary>
    /// <remarks>
    /// Used to layer event properties: client defaults underneath, the ambient
    /// scope's properties over those, and the call's own properties on top. A
    /// source that is not an object contributes nothing rather than throwing — a
    /// malformed property bag should not fail the request it describes.
    /// </remarks>
    public static void MergeInto(JsonObject target, object? source)
    {
        if (source is null)
        {
            return;
        }

        if (ToNode(source) is not JsonObject obj)
        {
            return;
        }

        // Detach each value first: a node cannot belong to two parents.
        foreach (var pair in obj)
        {
            target[pair.Key] = pair.Value?.DeepClone();
        }
    }

    /// <summary>ISO-8601 with millisecond precision, in UTC.</summary>
    public static string Iso8601(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    /// <summary>
    /// ISO-8601 for a <see cref="DateTime"/>. An <see cref="DateTimeKind.Unspecified"/>
    /// value is read as local time, which is what a caller writing
    /// <c>DateTime.Now</c> means.
    /// </summary>
    public static string Iso8601(DateTime value)
        => Iso8601(value.Kind == DateTimeKind.Unspecified
            ? new DateTimeOffset(value, TimeZoneInfo.Local.GetUtcOffset(value))
            : new DateTimeOffset(value));

    private static JsonObject FromPairs(IDictionary<string, object?> map)
    {
        var result = new JsonObject();
        foreach (var pair in map)
        {
            result[pair.Key] = ToNode(pair.Value);
        }

        return result;
    }

    private static JsonObject FromStringPairs(IDictionary<string, string?> map)
    {
        var result = new JsonObject();
        foreach (var pair in map)
        {
            result[pair.Key] = pair.Value is null ? null : JsonValue.Create(pair.Value);
        }

        return result;
    }

    private static JsonObject FromUntypedDictionary(IDictionary dictionary)
    {
        var result = new JsonObject();
        foreach (DictionaryEntry entry in dictionary)
        {
            var key = entry.Key as string ?? Convert.ToString(entry.Key, CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(key))
            {
                result[key!] = ToNode(entry.Value);
            }
        }

        return result;
    }

    private static JsonNode? JsonElementToNode(JsonElement element)
        => element.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(element.GetRawText());

    /// <remarks>
    /// The SDK's single reflection-based conversion: it exists so a caller can
    /// write <c>Track("order", new { total = 42 })</c>, which is the natural C#
    /// spelling. Apps published trimmed or native-AOT should pass an
    /// <see cref="IDictionary{TKey,TValue}"/> or a <see cref="JsonNode"/>
    /// instead, both of which take the reflection-free path above.
    /// </remarks>
#if !NETSTANDARD2_0
    [UnconditionalSuppressMessage(
        "AOT",
        "IL3050:RequiresDynamicCode",
        Justification = "Opt-in path for caller-supplied POCOs; dictionaries and JsonNode are reflection-free and documented as the trim-safe form.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "Opt-in path for caller-supplied POCOs; dictionaries and JsonNode are reflection-free and documented as the trim-safe form.")]
#endif
    private static JsonNode? SerializeUnknown(object value)
        => JsonSerializer.SerializeToNode(value, value.GetType(), SerializerOptions);
}
