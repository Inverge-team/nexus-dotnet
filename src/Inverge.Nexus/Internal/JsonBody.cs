using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Inverge.Nexus.Internal;

/// <summary>
/// Builds a request body, dropping fields whose value is <c>null</c>.
/// </summary>
/// <remarks>
/// The API validates request bodies strictly and rejects explicit nulls, so an
/// unset field must be absent rather than present-and-null. Every <c>Set</c>
/// overload is a no-op for a null value, which lets a payload be written as one
/// flat chain without a conditional per field.
/// </remarks>
internal sealed class JsonBody
{
    private readonly JsonObject _object = new JsonObject();

    public static JsonBody Create() => new JsonBody();

    public JsonBody SetNode(string name, JsonNode? value)
    {
        if (value is not null)
        {
            _object[name] = value;
        }

        return this;
    }

    public JsonBody Set(string name, string? value)
        => value is null ? this : SetNode(name, JsonValue.Create(value));

    public JsonBody Set(string name, bool? value)
        => value is null ? this : SetNode(name, JsonValue.Create(value.Value));

    public JsonBody Set(string name, int? value)
        => value is null ? this : SetNode(name, JsonValue.Create(value.Value));

    public JsonBody Set(string name, long? value)
        => value is null ? this : SetNode(name, JsonValue.Create(value.Value));

    public JsonBody Set(string name, double? value)
        => value is null ? this : SetNode(name, JsonValue.Create(value.Value));

    /// <summary>Sets any caller value, converted by <see cref="JsonHelpers.ToNode"/>.</summary>
    public JsonBody SetAny(string name, object? value)
        => SetNode(name, JsonHelpers.ToNode(value));

    /// <summary>
    /// Sets a field the API requires to be a JSON object, writing <c>{}</c> when
    /// the value is absent rather than omitting it or sending <c>[]</c>.
    /// </summary>
    public JsonBody SetObjectAlways(string name, object? value)
    {
        _object[name] = JsonHelpers.ToObjectNode(value);
        return this;
    }

    /// <summary>Sets a string array, omitting it when the sequence is null or empty.</summary>
    public JsonBody SetStrings(string name, IEnumerable<string>? values)
    {
        if (values is null)
        {
            return this;
        }

        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add((JsonNode?)JsonValue.Create(value));
        }

        return array.Count == 0 ? this : SetNode(name, array);
    }

    /// <summary>Sets an array of already-built objects.</summary>
    public JsonBody SetItems(string name, IEnumerable<JsonObject> items)
    {
        var array = new JsonArray();
        foreach (var item in items)
        {
            array.Add((JsonNode?)item.DeepClone());
        }

        _object[name] = array;
        return this;
    }

    public JsonObject Build() => _object;
}
