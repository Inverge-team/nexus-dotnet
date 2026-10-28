using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Internal;

namespace Inverge.Nexus.DocStore;

/// <summary>
/// Nexus DocStore — the document database. Get one from <see cref="NexusClient.DocStore"/>.
/// </summary>
/// <remarks>
/// With a service key (<c>NEXUS_DOCSTORE_SERVICE_KEY</c>) security rules are
/// bypassed — server use only; <see cref="AsUser"/> acts as an end user (their
/// JWT) so rules apply. Unlike telemetry, every DocStore call throws on failure
/// (also with <c>Silent</c>) and always runs synchronously with the caller,
/// whatever dispatcher the client uses: a read that quietly returns nothing
/// looks exactly like missing data.
/// </remarks>
public sealed class DocStoreClient
{
    /// <summary>The environment variable read for the service key.</summary>
    public const string ServiceKeyEnvironmentVariable = "NEXUS_DOCSTORE_SERVICE_KEY";

    private readonly NexusClient _client;
    private readonly string? _serviceKey;
    private readonly string? _token;

    internal DocStoreClient(NexusClient client, string? serviceKey, string? token)
    {
        if (serviceKey is not null && !serviceKey.StartsWith("nxsk_", StringComparison.Ordinal))
        {
            throw new NexusConfigurationException("A DocStore service key starts with 'nxsk_'.");
        }

        _client = client;
        _serviceKey = serviceKey;
        _token = token;
    }

    /// <summary>The same database as one end user (their JWT): security rules apply.</summary>
    public DocStoreClient AsUser(string token) => new(_client, _serviceKey, token);

    /// <summary>As an anonymous app user — rules see <c>auth == null</c>.</summary>
    public DocStoreClient Anonymous() => new(_client, null, null);

    /// <summary>A collection.</summary>
    public CollectionRef Collection(string name) => new(this, name, new JsonObject());

    /// <summary><c>Doc("posts/p1")</c>.</summary>
    public DocumentRef Doc(string path)
    {
        var i = path.IndexOf('/');
        if (i <= 0 || i == path.Length - 1)
        {
            throw new ArgumentException("'" + path + "' is not a <collection>/<id> path.", nameof(path));
        }

        return new DocumentRef(this, path.Substring(0, i), path.Substring(i + 1));
    }

    /// <summary>Up to 500 writes committed atomically.</summary>
    public WriteBatch Batch() => new(this);

    /// <summary>
    /// Runs <paramref name="fn"/> optimistically: reads record versions, writes
    /// carry them, and a concurrent change makes the commit fail and the
    /// function run again (up to <paramref name="maxAttempts"/>).
    /// </summary>
    public async Task<T> RunTransactionAsync<T>(Func<Transaction, Task<T>> fn, int maxAttempts = 5, CancellationToken cancellationToken = default)
    {
        var random = new Random();
        for (var attempt = 1; ; attempt++)
        {
            var tx = new Transaction(this);
            var result = await fn(tx).ConfigureAwait(false);
            try
            {
                await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
                return result;
            }
            catch (NexusConflictException e) when (e.IsPreconditionFailed && attempt < maxAttempts)
            {
                var ms = Math.Min(1000, 25 * Math.Pow(2, attempt)) * (0.5 + random.NextDouble());
                await Task.Delay(TimeSpan.FromMilliseconds(ms), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>One page of the change stream (service key).</summary>
    public async Task<(IReadOnlyList<DocStoreChange> Changes, long Next, bool HasMore)> ChangesAsync(
        long since = 0, IEnumerable<string>? collections = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        var r = await SendAsync("GET", "/changes" + Qs(("since", since.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("collections", Join(collections)), ("limit", limit?.ToString(System.Globalization.CultureInfo.InvariantCulture))), null, true, cancellationToken).ConfigureAwait(false);
        var list = r.Items("changes").Select(c => new DocStoreChange(
            c.Long("seq"), c.Str("collection") ?? "", c.Str("id") ?? "", c.Str("op") ?? "", c.Int("version"),
            c["data"] as JsonObject, c.Str("actor"), c.Str("txId"), c.Timestamp("at"))).ToList();
        return (list, r.Long("next", since), r.Flag("hasMore"));
    }

    /// <summary>
    /// Calls <paramref name="handler"/> for every committed change after
    /// <paramref name="since"/>, in commit order, until cancelled. Persist each
    /// change's <see cref="DocStoreChange.Seq"/> and pass it back to resume.
    /// </summary>
    public async Task WatchChangesAsync(
        Func<DocStoreChange, Task> handler, long since = 0, IEnumerable<string>? collections = null,
        TimeSpan? pollInterval = null, CancellationToken cancellationToken = default)
    {
        var cursor = since;
        var cols = collections?.ToList();
        while (!cancellationToken.IsCancellationRequested)
        {
            var (changes, next, more) = await ChangesAsync(cursor, cols, 500, cancellationToken).ConfigureAwait(false);
            foreach (var change in changes)
            {
                await handler(change).ConfigureAwait(false);
            }

            cursor = next;
            if (!more)
            {
                await Task.Delay(pollInterval ?? TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Collections as code (service key): create or update <paramref name="name"/>
    /// to match <paramref name="config"/> (rules, schema, relations, uniqueConstraints, searchFields …).
    /// </summary>
    public async Task<JsonObject?> DeployCollectionAsync(string name, object config, CancellationToken cancellationToken = default)
        => await SendAsync("PUT", "/collections/" + Uri.EscapeDataString(name), JsonHelpers.ToObjectNode(config), true, cancellationToken).ConfigureAwait(false) as JsonObject;

    internal async Task<JsonNode?> SendAsync(string method, string path, JsonObject? body, bool retryable, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (_token is not null)
        {
            headers["Authorization"] = "Bearer " + _token;
        }
        else if (_serviceKey is not null)
        {
            headers["x-nexus-service-key"] = _serviceKey;
        }

        return await _client.ExecuteDirectAsync(new NexusRequest(method, "/partner/docstore" + path, body, headers, retryable), NexusServices.DocStore, cancellationToken).ConfigureAwait(false);
    }

    internal static string Qs(params (string Key, string? Value)[] pairs)
    {
        var parts = pairs.Where(p => p.Value is not null).Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value!)).ToList();
        return parts.Count == 0 ? string.Empty : "?" + string.Join("&", parts);
    }

    internal static string? Join(IEnumerable<string>? values)
    {
        var list = values?.ToList();
        return list is null || list.Count == 0 ? null : string.Join(",", list);
    }

    internal static string Iso(DateTimeOffset when) => DocStoreIso.Of(when);
}

/// <summary>An immutable query — every builder returns a new one.</summary>
public class Query
{
    internal Query(DocStoreClient db, string collection, JsonObject state)
    {
        Db = db;
        CollectionName = collection;
        State = state;
    }

    internal DocStoreClient Db { get; }

    /// <summary>The collection this query reads.</summary>
    public string CollectionName { get; }

    internal JsonObject State { get; }

    private Query With(string key, JsonNode? value)
    {
        var next = (JsonObject)State.DeepClone();
        next[key] = value;
        return new Query(Db, CollectionName, next);
    }

    /// <summary><c>Where("total", "&gt;=", 100)</c>.</summary>
    public Query Where(string field, string op, object? value) => Where(Filter.Where(field, op, value));

    /// <summary>Adds a composed filter (<see cref="Filter.Or"/>, <see cref="Filter.Related"/>, raw JSON).</summary>
    public Query Where(JsonObject filter)
    {
        var cur = State["filter"];
        return With("filter", cur is null ? filter.DeepClone() : new JsonObject { ["$and"] = new JsonArray(cur.DeepClone(), filter.DeepClone()) });
    }

    /// <summary>Sorts by a field (<c>$createdAt</c>, <c>$updatedAt</c>, <c>$id</c> work too).</summary>
    public Query OrderBy(string field, bool descending = false)
    {
        var sort = State["sort"] is JsonArray a ? (JsonArray)a.DeepClone() : new JsonArray();
        sort.Add((JsonNode?)new JsonObject { ["field"] = field == "id" ? "$id" : field, ["dir"] = descending ? "desc" : "asc" });
        return With("sort", sort);
    }

    /// <summary>At most <paramref name="n"/> documents per page.</summary>
    public Query Limit(int n) => With("limit", n);

    /// <summary>Keyset pagination: the <see cref="QueryResult.NextCursor"/> of the previous page.</summary>
    public Query StartAfter(string cursor) => With("cursor", cursor);

    /// <summary>Only these fields (dot paths).</summary>
    public Query Select(params string[] fields) => With("select", new JsonArray(fields.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()));

    /// <summary>Inline related documents.</summary>
    public Query Expand(params string[] relations)
    {
        var a = State["expand"] is JsonArray cur ? (JsonArray)cur.DeepClone() : new JsonArray();
        foreach (var r in relations)
        {
            a.Add((JsonNode?)JsonValue.Create(r));
        }

        return With("expand", a);
    }

    /// <summary>Full-text search over the collection's search fields.</summary>
    public Query Search(string text) => With("search", text);

    /// <summary>Time travel: the collection as it was at <paramref name="when"/>.</summary>
    public Query AsOf(DateTimeOffset when) => With("asOf", DocStoreClient.Iso(when));

    /// <summary>Also return the total number of matches.</summary>
    public Query WithCount() => With("count", true);

    /// <summary>Runs the query.</summary>
    public async Task<QueryResult> GetAsync(CancellationToken cancellationToken = default)
    {
        var r = await Db.SendAsync("POST", "/collections/" + Uri.EscapeDataString(CollectionName) + "/query", (JsonObject)State.DeepClone(), true, cancellationToken).ConfigureAwait(false);
        var docs = r.Items("items").Select(d => new DocumentSnapshot(CollectionName, d.Str("id") ?? "", (JsonObject)d.DeepClone())).ToList();
        int? total = r?["total"] is JsonNode t ? t.GetValue<int>() : null;
        return new QueryResult(docs, r.Str("nextCursor"), r.Flag("hasMore"), total);
    }

    /// <summary>Number of matching documents.</summary>
    public async Task<int> CountAsync(CancellationToken cancellationToken = default)
    {
        var groups = await AggregateAsync(new Dictionary<string, Aggregation> { ["n"] = Aggregation.Count() }, null, cancellationToken).ConfigureAwait(false);
        return groups.Count == 0 ? 0 : (int)(groups[0].Values.TryGetValue("n", out var n) ? n ?? 0 : 0);
    }

    /// <summary>Aggregations, optionally grouped (up to 5 fields).</summary>
    public async Task<IReadOnlyList<(IReadOnlyDictionary<string, JsonNode?> Key, IReadOnlyDictionary<string, double?> Values)>> AggregateAsync(
        IReadOnlyDictionary<string, Aggregation> aggregations, IEnumerable<string>? groupBy = null, CancellationToken cancellationToken = default)
    {
        var aggs = new JsonObject();
        foreach (var pair in aggregations)
        {
            aggs[pair.Key] = pair.Value.ToJson();
        }

        var body = JsonBody.Create()
            .SetNode("filter", State["filter"]?.DeepClone())
            .SetNode("search", State["search"]?.DeepClone())
            .SetNode("asOf", State["asOf"]?.DeepClone())
            .SetNode("aggregations", aggs)
            .SetNode("groupBy", groupBy is null ? null : new JsonArray(groupBy.Select(g => (JsonNode?)JsonValue.Create(g)).ToArray()))
            .Build();
        var r = await Db.SendAsync("POST", "/collections/" + Uri.EscapeDataString(CollectionName) + "/aggregate", body, true, cancellationToken).ConfigureAwait(false);
        return r.Items("groups").Select(g =>
        {
            IReadOnlyDictionary<string, JsonNode?> key = g.Map("key");
            var values = new Dictionary<string, double?>(StringComparer.Ordinal);
            foreach (var v in g.Map("values"))
            {
                values[v.Key] = JsonRead.AsDouble(v.Value);
            }

            return (key, (IReadOnlyDictionary<string, double?>)values);
        }).ToList();
    }
}

/// <summary>A collection: a query over all its documents plus document access.</summary>
public sealed class CollectionRef : Query
{
    internal CollectionRef(DocStoreClient db, string name, JsonObject state) : base(db, name, state)
    {
    }

    /// <summary>A document by id.</summary>
    public DocumentRef Doc(string id) => new(Db, CollectionName, id);

    /// <summary>Creates a document with a generated, time-sortable id (or <paramref name="id"/>).</summary>
    public async Task<DocumentSnapshot> AddAsync(object data, string? id = null, CancellationToken cancellationToken = default)
    {
        var body = JsonBody.Create().Set("id", id).SetNode("data", JsonHelpers.ToObjectNode(data)).Build();
        var r = await Db.SendAsync("POST", "/collections/" + Uri.EscapeDataString(CollectionName) + "/docs", body, false, cancellationToken).ConfigureAwait(false);
        return new DocumentSnapshot(CollectionName, r.Str("id") ?? id ?? "", r?["doc"]?.DeepClone() as JsonObject);
    }

    /// <summary>Point-in-time restore of the whole collection (service key) — written as new versions, so it is undoable.</summary>
    public async Task<JsonObject?> RestoreAsync(DateTimeOffset asOf, bool dryRun = false, CancellationToken cancellationToken = default)
        => await Db.SendAsync("POST", "/collections/" + Uri.EscapeDataString(CollectionName) + "/restore", new JsonObject { ["asOf"] = DocStoreClient.Iso(asOf), ["dryRun"] = dryRun }, false, cancellationToken).ConfigureAwait(false) as JsonObject;
}

/// <summary>One document.</summary>
public sealed class DocumentRef
{
    private readonly DocStoreClient _db;

    internal DocumentRef(DocStoreClient db, string collection, string id)
    {
        _db = db;
        Collection = collection;
        Id = id;
    }

    /// <summary>The collection.</summary>
    public string Collection { get; }

    /// <summary>The document id.</summary>
    public string Id { get; }

    /// <summary><c>collection/id</c>.</summary>
    public string Path => Collection + "/" + Id;

    private string Url => "/collections/" + Uri.EscapeDataString(Collection) + "/docs/" + Uri.EscapeDataString(Id);

    private DocumentSnapshot Snap(JsonNode? raw) => new(Collection, Id, raw?.DeepClone() as JsonObject);

    /// <summary>Reads the document; a missing one returns a snapshot with <c>Exists == false</c>.</summary>
    public async Task<DocumentSnapshot> GetAsync(IEnumerable<string>? expand = null, DateTimeOffset? asOf = null, CancellationToken cancellationToken = default)
    {
        try
        {
            var qs = DocStoreClient.Qs(("expand", DocStoreClient.Join(expand)), ("asOf", asOf is null ? null : DocStoreClient.Iso(asOf.Value)));
            return Snap(await _db.SendAsync("GET", Url + qs, null, true, cancellationToken).ConfigureAwait(false));
        }
        catch (NexusNotFoundException)
        {
            return Snap(null);
        }
    }

    /// <summary>
    /// Creates or replaces (<paramref name="merge"/> deep-merges).
    /// <paramref name="ifVersion"/> refuses to overwrite a change you haven't seen (0 = must not exist).
    /// </summary>
    public async Task<DocumentSnapshot> SetAsync(object data, bool merge = false, int? ifVersion = null, CancellationToken cancellationToken = default)
    {
        var body = JsonBody.Create()
            .SetNode("data", JsonHelpers.ToObjectNode(data))
            .Set("merge", merge ? true : (bool?)null)
            .SetNode("precondition", ifVersion is null ? null : new JsonObject { ["version"] = ifVersion.Value })
            .Build();
        var r = await _db.SendAsync("PUT", Url, body, false, cancellationToken).ConfigureAwait(false);
        return Snap(r?["doc"]);
    }

    /// <summary>Partial update with field values and dot paths.</summary>
    public async Task<DocumentSnapshot> UpdateAsync(IReadOnlyDictionary<string, object?> update, int? ifVersion = null, bool upsert = false, CancellationToken cancellationToken = default)
    {
        var body = JsonBody.Create()
            .SetNode("update", FieldValue.ToSpec(update))
            .Set("upsert", upsert ? true : (bool?)null)
            .SetNode("precondition", ifVersion is null ? null : new JsonObject { ["version"] = ifVersion.Value })
            .Build();
        var r = await _db.SendAsync("PATCH", Url, body, false, cancellationToken).ConfigureAwait(false);
        return Snap(r?["doc"]);
    }

    /// <summary>Deletes the document (idempotent).</summary>
    public async Task DeleteAsync(int? ifVersion = null, CancellationToken cancellationToken = default)
        => await _db.SendAsync("DELETE", Url + DocStoreClient.Qs(("ifVersion", ifVersion?.ToString(System.Globalization.CultureInfo.InvariantCulture))), null, false, cancellationToken).ConfigureAwait(false);

    /// <summary>Every version, newest first (raw API objects with <c>version</c>, <c>op</c>, <c>at</c>, <c>data</c>, <c>changedFields</c>).</summary>
    public async Task<IReadOnlyList<JsonObject>> HistoryAsync(int? limit = null, CancellationToken cancellationToken = default)
    {
        var r = await _db.SendAsync("GET", Url + "/history" + DocStoreClient.Qs(("limit", limit?.ToString(System.Globalization.CultureInfo.InvariantCulture))), null, true, cancellationToken).ConfigureAwait(false);
        return r.Items("versions").Select(v => (JsonObject)v.DeepClone()).ToList();
    }

    /// <summary>Back to an earlier version (written as a NEW version — undoable).</summary>
    public async Task<DocumentSnapshot> RestoreAsync(int version, CancellationToken cancellationToken = default)
    {
        var r = await _db.SendAsync("POST", Url + "/restore", new JsonObject { ["version"] = version }, false, cancellationToken).ConfigureAwait(false);
        return Snap(r?["doc"]);
    }
}

/// <summary>Up to 500 writes committed atomically: all apply, or none do.</summary>
public class WriteBatch
{
    internal WriteBatch(DocStoreClient db) => Db = db;

    internal DocStoreClient Db { get; }

    internal List<JsonObject> Ops { get; } = new();

    /// <summary>Creates (fails if it exists).</summary>
    public WriteBatch Create(DocumentRef doc, object data)
    {
        Ops.Add(new JsonObject { ["type"] = "create", ["collection"] = doc.Collection, ["id"] = doc.Id, ["data"] = JsonHelpers.ToObjectNode(data) });
        return this;
    }

    /// <summary>Creates or replaces.</summary>
    public WriteBatch Set(DocumentRef doc, object data, bool merge = false, int? ifVersion = null)
    {
        var op = new JsonObject { ["type"] = "set", ["collection"] = doc.Collection, ["id"] = doc.Id, ["data"] = JsonHelpers.ToObjectNode(data) };
        if (merge)
        {
            op["merge"] = true;
        }

        if (ifVersion is not null)
        {
            op["precondition"] = new JsonObject { ["version"] = ifVersion.Value };
        }

        Ops.Add(op);
        return this;
    }

    /// <summary>Partial update.</summary>
    public WriteBatch Update(DocumentRef doc, IReadOnlyDictionary<string, object?> update, int? ifVersion = null)
    {
        var op = new JsonObject { ["type"] = "update", ["collection"] = doc.Collection, ["id"] = doc.Id, ["update"] = FieldValue.ToSpec(update) };
        if (ifVersion is not null)
        {
            op["precondition"] = new JsonObject { ["version"] = ifVersion.Value };
        }

        Ops.Add(op);
        return this;
    }

    /// <summary>Deletes.</summary>
    public WriteBatch Delete(DocumentRef doc, int? ifVersion = null)
    {
        var op = new JsonObject { ["type"] = "delete", ["collection"] = doc.Collection, ["id"] = doc.Id };
        if (ifVersion is not null)
        {
            op["precondition"] = new JsonObject { ["version"] = ifVersion.Value };
        }

        Ops.Add(op);
        return this;
    }

    internal virtual IEnumerable<JsonObject> FinalOps() => Ops;

    /// <summary>Commits; returns the per-operation results.</summary>
    public async Task<IReadOnlyList<JsonObject>> CommitAsync(CancellationToken cancellationToken = default)
    {
        if (Ops.Count == 0)
        {
            return Array.Empty<JsonObject>();
        }

        var ops = new JsonArray(FinalOps().Select(o => (JsonNode?)o.DeepClone()).ToArray());
        var r = await Db.SendAsync("POST", "/commit", new JsonObject { ["operations"] = ops }, false, cancellationToken).ConfigureAwait(false);
        return r.Items("results").Select(x => (JsonObject)x.DeepClone()).ToList();
    }
}

/// <summary>Read-modify-write; use through <see cref="DocStoreClient.RunTransactionAsync{T}"/>.</summary>
public sealed class Transaction : WriteBatch
{
    private readonly Dictionary<string, int> _seen = new(StringComparer.Ordinal);

    internal Transaction(DocStoreClient db) : base(db)
    {
    }

    /// <summary>Reads a document and records its version for the commit.</summary>
    public async Task<DocumentSnapshot> GetAsync(DocumentRef doc, CancellationToken cancellationToken = default)
    {
        var snap = await doc.GetAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        _seen[doc.Path] = snap.Version;
        return snap;
    }

    internal override IEnumerable<JsonObject> FinalOps()
    {
        foreach (var op in Ops)
        {
            var key = op.Str("collection") + "/" + op.Str("id");
            if (!op.ContainsKey("precondition") && _seen.TryGetValue(key, out var v))
            {
                op["precondition"] = new JsonObject { ["version"] = v };
            }

            if (op.Str("type") == "create" && !op.ContainsKey("precondition"))
            {
                op["precondition"] = new JsonObject { ["exists"] = false };
            }

            yield return op;
        }
    }
}

/// <summary>Verifies DocStore trigger webhooks.</summary>
/// <remarks>
/// Signature = HMAC-SHA256 (hex) of <c>"{timestamp}.{nonce}.{rawBody}"</c> with
/// the trigger's <c>whsec_…</c> secret, sent as <c>x-nexus-signature</c> with
/// <c>x-nexus-timestamp</c> and <c>x-nexus-nonce</c>. Deliveries are
/// at-least-once: dedupe on the payload's <c>deliveryId</c> (or <c>seq</c>).
/// </remarks>
public static class DocStoreWebhook
{
    /// <summary>Verifies and returns the decoded payload; throws <see cref="NexusException"/> on failure.</summary>
    public static JsonObject Verify(
        string rawBody, IEnumerable<KeyValuePair<string, string>> headers, string secret,
        TimeSpan? tolerance = null, Func<string, bool>? seenNonce = null, DateTimeOffset? now = null)
    {
        var h = headers.ToDictionary(kv => kv.Key.ToLowerInvariant(), kv => kv.Value, StringComparer.Ordinal);
        if (!h.TryGetValue("x-nexus-signature", out var sig) || !h.TryGetValue("x-nexus-timestamp", out var ts) || !h.TryGetValue("x-nexus-nonce", out var nonce))
        {
            throw new NexusException("DocStore webhook: missing x-nexus-signature / x-nexus-timestamp / x-nexus-nonce.");
        }

        if (!long.TryParse(ts, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            || Math.Abs((now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() - seconds) > (tolerance ?? TimeSpan.FromMinutes(5)).TotalSeconds)
        {
            throw new NexusException("DocStore webhook: timestamp outside the tolerance.");
        }

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(ts + "." + nonce + "." + rawBody));
        var expected = string.Concat(hash.Select(b => b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)));
        if (!FixedTimeEquals(expected, sig))
        {
            throw new NexusException("DocStore webhook: signature mismatch.");
        }

        if (seenNonce is not null && seenNonce(nonce))
        {
            throw new NexusException("DocStore webhook: nonce already used (replay).");
        }

        return JsonNode.Parse(rawBody) as JsonObject ?? new JsonObject();
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        var diff = 0;
        for (var i = 0; i < a.Length; i++)
        {
            diff |= a[i] ^ b[i];
        }

        return diff == 0;
    }
}
