using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using Inverge.Nexus.Internal;

namespace Inverge.Nexus.DocStore;

/// <summary>
/// Server-side atomic field updates for <see cref="DocumentRef.UpdateAsync"/> —
/// applied under a row lock, so concurrent increments never lose.
/// </summary>
/// <example><code>
/// await doc.UpdateAsync(new Dictionary&lt;string, object?&gt;
/// {
///     ["views"] = FieldValue.Increment(1),
///     ["tags"] = FieldValue.ArrayUnion("new"),
///     ["editedAt"] = FieldValue.ServerTimestamp(),
/// });
/// </code></example>
public sealed class FieldValue
{
    private FieldValue(string kind, JsonNode? value = null, int? slice = null)
    {
        Kind = kind;
        Value = value;
        Slice = slice;
    }

    internal string Kind { get; }
    internal JsonNode? Value { get; }
    internal int? Slice { get; }

    /// <summary>Adds <paramref name="n"/> atomically.</summary>
    public static FieldValue Increment(double n) => new("inc", JsonValue.Create(n));
    /// <summary>Multiplies atomically.</summary>
    public static FieldValue Multiply(double n) => new("mul", JsonValue.Create(n));
    /// <summary>Keeps the smaller of the stored value and <paramref name="value"/>.</summary>
    public static FieldValue Minimum(object value) => new("min", JsonHelpers.ToNode(value));
    /// <summary>Keeps the larger of the stored value and <paramref name="value"/>.</summary>
    public static FieldValue Maximum(object value) => new("max", JsonHelpers.ToNode(value));
    /// <summary>Adds values to an array if absent.</summary>
    public static FieldValue ArrayUnion(params object?[] values) => new("union", Array(values));
    /// <summary>Removes every occurrence of these values from an array.</summary>
    public static FieldValue ArrayRemove(params object?[] values) => new("remove", Array(values));
    /// <summary>Appends (duplicates allowed); <paramref name="keepLast"/> caps the array — a rolling log.</summary>
    public static FieldValue ArrayPush(IEnumerable<object?> values, int? keepLast = null)
        => new("push", Array(values), keepLast is null ? null : -keepLast);
    /// <summary>The server's clock at commit (ISO-8601).</summary>
    public static FieldValue ServerTimestamp() => new("serverTime");
    /// <summary>Removes the field.</summary>
    public static FieldValue Delete() => new("delete");
    /// <summary>Deep-merges an object into the field (null values delete keys).</summary>
    public static FieldValue Merge(object value) => new("merge", JsonHelpers.ToObjectNode(value));

    private static JsonArray Array(IEnumerable<object?> values)
    {
        var a = new JsonArray();
        foreach (var v in values)
        {
            a.Add((JsonNode?)JsonHelpers.ToNode(v));
        }

        return a;
    }

    /// <summary>
    /// Field values and plain values → the API's update operators. A map that
    /// already uses <c>$</c> operators passes through unchanged.
    /// </summary>
    public static JsonObject ToSpec(IReadOnlyDictionary<string, object?> update)
    {
        if (update is null)
        {
            throw new ArgumentNullException(nameof(update));
        }

        var allOps = update.Count > 0;
        foreach (var key in update.Keys)
        {
            allOps &= key.StartsWith("$", StringComparison.Ordinal);
        }

        var spec = new JsonObject();
        if (allOps)
        {
            foreach (var pair in update)
            {
                spec[pair.Key] = JsonHelpers.ToNode(pair.Value);
            }

            return spec;
        }

        JsonObject Map(string op)
        {
            if (spec[op] is not JsonObject o)
            {
                o = new JsonObject();
                spec[op] = o;
            }

            return o;
        }

        JsonArray List(string op)
        {
            if (spec[op] is not JsonArray a)
            {
                a = new JsonArray();
                spec[op] = a;
            }

            return a;
        }

        foreach (var pair in update)
        {
            if (pair.Value is not FieldValue fv)
            {
                Map("$set")[pair.Key] = JsonHelpers.ToNode(pair.Value);
                continue;
            }

            switch (fv.Kind)
            {
                case "inc": Map("$inc")[pair.Key] = fv.Value!.DeepClone(); break;
                case "mul": Map("$mul")[pair.Key] = fv.Value!.DeepClone(); break;
                case "min": Map("$min")[pair.Key] = fv.Value?.DeepClone(); break;
                case "max": Map("$max")[pair.Key] = fv.Value?.DeepClone(); break;
                case "union": Map("$addToSet")[pair.Key] = new JsonObject { ["$each"] = fv.Value!.DeepClone() }; break;
                case "remove": Map("$pull")[pair.Key] = new JsonObject { ["$in"] = fv.Value!.DeepClone() }; break;
                case "push":
                    var push = new JsonObject { ["$each"] = fv.Value!.DeepClone() };
                    if (fv.Slice is int s)
                    {
                        push["$slice"] = s;
                    }

                    Map("$push")[pair.Key] = push;
                    break;
                case "serverTime": List("$serverTime").Add((JsonNode?)JsonValue.Create(pair.Key)); break;
                case "delete": List("$unset").Add((JsonNode?)JsonValue.Create(pair.Key)); break;
                case "merge": Map("$merge")[pair.Key] = fv.Value!.DeepClone(); break;
            }
        }

        return spec;
    }
}

/// <summary>Composable query conditions — the JSON filter the API takes.</summary>
public static class Filter
{
    private static readonly Dictionary<string, string> Ops = new(StringComparer.Ordinal)
    {
        ["=="] = "$eq", ["!="] = "$ne", ["<"] = "$lt", ["<="] = "$lte", [">"] = "$gt", [">="] = "$gte",
        ["in"] = "$in", ["not-in"] = "$nin", ["between"] = "$between",
        ["array-contains"] = "$contains", ["array-contains-any"] = "$containsAny", ["array-contains-all"] = "$containsAll",
        ["starts-with"] = "$startsWith", ["ends-with"] = "$endsWith", ["matches"] = "$regex", ["exists"] = "$exists",
        ["size"] = "$size", ["type"] = "$type",
    };

    /// <summary>One condition: <c>Filter.Where("total", "&gt;=", 100)</c>.</summary>
    public static JsonObject Where(string field, string op, object? value)
    {
        if (!Ops.TryGetValue(op, out var wire))
        {
            throw new ArgumentException("Unknown DocStore operator '" + op + "'.", nameof(op));
        }

        var v = value switch
        {
            DateTimeOffset d => JsonValue.Create(JsonHelpers.Iso8601(d)),
            DateTime d => JsonValue.Create(JsonHelpers.Iso8601(d)),
            _ => JsonHelpers.ToNode(value),
        };
        return new JsonObject { [field == "id" ? "$id" : field] = new JsonObject { [wire] = v } };
    }

    /// <summary>Any of the filters.</summary>
    public static JsonObject Or(params JsonObject[] filters) => Combine("$or", filters);
    /// <summary>All of the filters.</summary>
    public static JsonObject And(params JsonObject[] filters) => Combine("$and", filters);
    /// <summary>Negation.</summary>
    public static JsonObject Not(JsonObject filter) => new() { ["$not"] = filter.DeepClone() };
    /// <summary>Matches through a relation: <c>Filter.Related("author", Filter.Where("verified", "==", true))</c>.</summary>
    public static JsonObject Related(string relation, JsonObject filter)
        => new() { [relation] = new JsonObject { ["$rel"] = filter.DeepClone() } };

    private static JsonObject Combine(string op, JsonObject[] filters)
    {
        var a = new JsonArray();
        foreach (var f in filters)
        {
            a.Add((JsonNode?)f.DeepClone());
        }

        return new JsonObject { [op] = a };
    }
}

/// <summary>An aggregation for <see cref="Query.AggregateAsync"/>.</summary>
public sealed class Aggregation
{
    private Aggregation(string op, string? field)
    {
        Op = op;
        Field = field;
    }

    internal string Op { get; }
    internal string? Field { get; }

    /// <summary>Number of matching documents.</summary>
    public static Aggregation Count() => new("count", null);
    /// <summary>Number of distinct values.</summary>
    public static Aggregation CountDistinct(string field) => new("countDistinct", field);
    /// <summary>Sum of a numeric field.</summary>
    public static Aggregation Sum(string field) => new("sum", field);
    /// <summary>Average of a numeric field.</summary>
    public static Aggregation Average(string field) => new("avg", field);
    /// <summary>Smallest numeric value.</summary>
    public static Aggregation Min(string field) => new("min", field);
    /// <summary>Largest numeric value.</summary>
    public static Aggregation Max(string field) => new("max", field);

    internal JsonObject ToJson()
    {
        var o = new JsonObject { ["op"] = Op };
        if (Field is not null)
        {
            o["field"] = Field;
        }

        return o;
    }
}

/// <summary>One document as read (or returned by a write).</summary>
public sealed class DocumentSnapshot
{
    internal DocumentSnapshot(string collection, string id, JsonObject? raw)
    {
        Collection = collection;
        Id = id;
        Raw = raw;
    }

    /// <summary>The collection.</summary>
    public string Collection { get; }

    /// <summary>The document id.</summary>
    public string Id { get; }

    /// <summary>The full API representation (id, data, version, timestamps, expand), or null.</summary>
    public JsonObject? Raw { get; }

    /// <summary>False for a document that does not exist.</summary>
    public bool Exists => Raw is not null;

    /// <summary>The document data, or null when it does not exist.</summary>
    public JsonObject? Data => Raw?["data"] as JsonObject;

    /// <summary>A field by dot path (<c>"address.city"</c>), or null.</summary>
    public JsonNode? Get(string path)
    {
        JsonNode? cur = Data;
        foreach (var part in path.Split('.'))
        {
            cur = cur is JsonObject o && o.TryGetPropertyValue(part, out var next) ? next : null;
            if (cur is null)
            {
                return null;
            }
        }

        return cur;
    }

    /// <summary>The document version (0 when it does not exist).</summary>
    public int Version => Raw.Int("version");

    /// <summary>When the document was created.</summary>
    public DateTimeOffset? CreatedAt => Raw.Timestamp("createdAt");

    /// <summary>When it last changed.</summary>
    public DateTimeOffset? UpdatedAt => Raw.Timestamp("updatedAt");

    /// <summary>Related documents requested with <c>Expand</c>.</summary>
    public JsonObject? Expand => Raw?["expand"] as JsonObject;
}

/// <summary>A page of query results.</summary>
public sealed class QueryResult
{
    internal QueryResult(IReadOnlyList<DocumentSnapshot> docs, string? nextCursor, bool hasMore, int? total)
    {
        Docs = docs;
        NextCursor = nextCursor;
        HasMore = hasMore;
        Total = total;
    }

    /// <summary>The documents on this page.</summary>
    public IReadOnlyList<DocumentSnapshot> Docs { get; }

    /// <summary>Pass to <see cref="Query.StartAfter"/> for the next page.</summary>
    public string? NextCursor { get; }

    /// <summary>Whether more pages follow.</summary>
    public bool HasMore { get; }

    /// <summary>Set when the query used <see cref="Query.WithCount"/>.</summary>
    public int? Total { get; }
}

/// <summary>One committed write from the change stream. <see cref="Seq"/> is the resume point.</summary>
public sealed record DocStoreChange(long Seq, string Collection, string Id, string Op, int Version, JsonObject? Data, string? Actor, string? TxId, DateTimeOffset? At);

internal static class DocStoreIso
{
    public static string Of(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
