using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Internal;
using Inverge.Nexus.Models;

namespace Inverge.Nexus.Resources;

/// <summary>Remote Config — server-evaluated, publishable configuration.</summary>
/// <remarks>
/// <para>
/// This is how you change behaviour without a deploy: a fee, a phone number, a
/// copy string, a kill switch. Conditions (platform, version, country, percentile,
/// custom attributes) are evaluated server-side, so the values that arrive are
/// final.
/// </para>
/// <para>
/// Reads are cached in-process for <see cref="NexusOptions.RemoteConfigCacheTtl"/>
/// and revalidated with the previous ETag, so a hot path can read freely. Writes
/// edit a <em>draft</em>: nothing changes for anyone until
/// <see cref="PublishAsync"/>.
/// </para>
/// </remarks>
public sealed class RemoteConfigResource : NexusResource
{
    private static readonly string[] ValueTypes = { "STRING", "NUMBER", "BOOLEAN", "JSON" };

    private readonly Dictionary<string, object?> _defaults = new Dictionary<string, object?>(StringComparer.Ordinal);
    private readonly Dictionary<string, CacheEntry> _cache = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
    private readonly object _gate = new object();

    internal RemoteConfigResource(NexusClient client) : base(client)
    {
    }

    /// <summary>
    /// Registers in-app fallbacks, used when the template does not define a key or
    /// the fetch fails.
    /// </summary>
    /// <remarks>
    /// Set these at startup. They are what keeps a config outage from changing
    /// behaviour: without a default, a missing key silently reads as zero, false or
    /// empty.
    /// </remarks>
    public void SetDefaults(IReadOnlyDictionary<string, object?> defaults)
    {
        if (defaults is null)
        {
            throw new ArgumentNullException(nameof(defaults));
        }

        lock (_gate)
        {
            foreach (var pair in defaults)
            {
                _defaults[pair.Key] = pair.Value;
            }
        }
    }

    /// <summary>The in-app defaults currently registered.</summary>
    public IReadOnlyDictionary<string, object?> Defaults
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, object?>(_defaults, StringComparer.Ordinal);
            }
        }
    }

    /// <summary>Resolves the active template for a context.</summary>
    /// <param name="context">
    /// The evaluation context: <c>appVersion</c>, <c>platform</c>, <c>country</c>,
    /// <c>language</c>, <c>userProperties</c>, and so on. <c>platform</c> defaults
    /// to <c>server</c> — pass the real one when resolving on behalf of a device,
    /// or platform conditions will not match.
    /// </param>
    /// <param name="fresh">Bypass the in-process cache.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<RemoteConfigSnapshot> FetchAsync(
        IReadOnlyDictionary<string, object?>? context = null,
        bool fresh = false,
        CancellationToken cancellationToken = default)
    {
        var resolved = ResolveFetchContext(context);
        var key = resolved.ToJsonString();

        CacheEntry? entry;
        lock (_gate)
        {
            _cache.TryGetValue(key, out entry);
        }

        if (!fresh && entry is not null && DateTime.UtcNow - entry.At < Client.Options.RemoteConfigCacheTtl)
        {
            return entry.Snapshot;
        }

        var previous = entry?.Snapshot;
        var etag = string.IsNullOrEmpty(previous?.ETag) ? null : previous!.ETag;

        var response = await SendAsync(
            Payloads.RemoteConfigFetch(resolved, etag), cancellationToken).ConfigureAwait(false);

        // A silent client swallows a failure as a null response. Serving the last
        // good snapshot beats serving nothing: config is what the app runs on.
        if (response is null && previous is not null)
        {
            return previous;
        }

        var snapshot = RemoteConfigSnapshot.From(response, Defaults, previous);

        lock (_gate)
        {
            _cache[key] = new CacheEntry(snapshot, DateTime.UtcNow);
        }

        return snapshot;
    }

    /// <summary>One parameter's resolved value, as a string.</summary>
    public async Task<string> GetStringAsync(
        string key,
        string fallback = "",
        IReadOnlyDictionary<string, object?>? context = null,
        CancellationToken cancellationToken = default)
        => (await FetchAsync(context, false, cancellationToken).ConfigureAwait(false)).GetString(key, fallback);

    /// <summary>One parameter's resolved value, as a boolean.</summary>
    public async Task<bool> GetBooleanAsync(
        string key,
        bool fallback = false,
        IReadOnlyDictionary<string, object?>? context = null,
        CancellationToken cancellationToken = default)
        => (await FetchAsync(context, false, cancellationToken).ConfigureAwait(false)).GetBoolean(key, fallback);

    /// <summary>One parameter's resolved value, as a number.</summary>
    public async Task<double> GetNumberAsync(
        string key,
        double fallback = 0,
        IReadOnlyDictionary<string, object?>? context = null,
        CancellationToken cancellationToken = default)
        => (await FetchAsync(context, false, cancellationToken).ConfigureAwait(false)).GetNumber(key, fallback);

    /// <summary>One parameter's resolved value, as an integer.</summary>
    public async Task<int> GetInt32Async(
        string key,
        int fallback = 0,
        IReadOnlyDictionary<string, object?>? context = null,
        CancellationToken cancellationToken = default)
        => (await FetchAsync(context, false, cancellationToken).ConfigureAwait(false)).GetInt32(key, fallback);

    /// <summary>One parameter's resolved value, as JSON.</summary>
    public async Task<JsonNode?> GetJsonAsync(
        string key,
        JsonNode? fallback = null,
        IReadOnlyDictionary<string, object?>? context = null,
        CancellationToken cancellationToken = default)
        => (await FetchAsync(context, false, cancellationToken).ConfigureAwait(false)).GetJson(key, fallback);

    /// <summary>Drops the in-process cache, so the next read refetches.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cache.Clear();
        }
    }

    /// <summary>Creates or updates a parameter in the draft template.</summary>
    /// <remarks>Nothing changes for clients until <see cref="PublishAsync"/>.</remarks>
    /// <param name="key">The parameter key.</param>
    /// <param name="value">The default value. Its CLR type decides the value type unless you say otherwise.</param>
    /// <param name="valueType"><c>STRING</c>, <c>NUMBER</c>, <c>BOOLEAN</c> or <c>JSON</c>.</param>
    /// <param name="conditionalValues">Per-condition overrides, keyed by condition name.</param>
    /// <param name="description">What the parameter is for.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<NexusAck> SetParameterAsync(
        string key,
        object? value = null,
        string? valueType = null,
        IReadOnlyDictionary<string, object?>? conditionalValues = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(key, nameof(key));

        var type = (valueType ?? InferValueType(value)).ToUpperInvariant();
        Guard.OneOf(type, ValueTypes, nameof(valueType));

        var response = await SendAsync(
            Payloads.RemoteConfigSetParameter(key, type, value, conditionalValues, description),
            cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }

    /// <summary>Removes a parameter from the draft template.</summary>
    public async Task DeleteParameterAsync(string key, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(key, nameof(key));
        await SendAsync(Payloads.RemoteConfigDeleteParameter(key), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Makes the draft live, and drops the local cache.</summary>
    public async Task<RemoteConfigPublishResult> PublishAsync(
        string? description = null, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            Payloads.RemoteConfigPublish(description), cancellationToken).ConfigureAwait(false);

        Invalidate();
        return RemoteConfigPublishResult.From(response);
    }

    /// <summary>Sets one parameter and publishes it in a single step.</summary>
    public async Task<RemoteConfigPublishResult> SetAsync(
        string key,
        object? value,
        string? valueType = null,
        string? description = null,
        CancellationToken cancellationToken = default)
    {
        await SetParameterAsync(key, value, valueType, null, description, cancellationToken)
            .ConfigureAwait(false);

        return await PublishAsync(description, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Maps a CLR value onto a Remote Config value type.</summary>
    public static string InferValueType(object? value) => value switch
    {
        bool => "BOOLEAN",
        sbyte or byte or short or ushort or int or uint or long or ulong or float or double or decimal => "NUMBER",
        JsonObject or JsonArray => "JSON",
        System.Collections.IEnumerable and not string => "JSON",
        _ => "STRING",
    };

    private JsonObject ResolveFetchContext(IReadOnlyDictionary<string, object?>? context)
    {
        var body = new JsonObject();

        if (context is not null)
        {
            foreach (var pair in context)
            {
                body[pair.Key] = JsonHelpers.ToNode(pair.Value);
            }
        }

        // Conditions are matched on these, so a missing one silently fails to
        // match rather than erring. Fill from the ambient context and options.
        var ambient = Resolve(null);
        SetIfAbsent(body, "platform", ambient.OsType ?? "server");
        SetIfAbsent(body, "appVersion", ambient.AppVersion);
        SetIfAbsent(body, "osVersion", ambient.OsVersion);
        SetIfAbsent(body, "country", ambient.Country);

        return body;
    }

    private static void SetIfAbsent(JsonObject body, string name, string? value)
    {
        if (value is not null && !body.ContainsKey(name))
        {
            body[name] = JsonValue.Create(value);
        }
    }

    private sealed class CacheEntry
    {
        public CacheEntry(RemoteConfigSnapshot snapshot, DateTime at)
        {
            Snapshot = snapshot;
            At = at;
        }

        public RemoteConfigSnapshot Snapshot { get; }

        public DateTime At { get; }
    }
}
