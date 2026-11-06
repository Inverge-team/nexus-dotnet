using System;
using System.Collections;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Internal;
using Inverge.Nexus.Models;

namespace Inverge.Nexus.Builders;

/// <summary>
/// Composes a transactional push notification, then sends it.
/// </summary>
/// <remarks>
/// Targeting is a union: users, segments and an ad-hoc filter combine.
/// <see cref="ToEveryone"/> overrides all of them.
/// </remarks>
/// <example>
/// <code>
/// await nexus.Push.Notification()
///     .Title("Your order shipped")
///     .Body("Track it in the app")
///     .Data(new { screen = "/orders/42" })
///     .Button("track", "Track", url: "https://example.com/track")
///     .IosBadge(1)
///     .ToSegments(new[] { "vip", "active_7d" })
///     .SendAsync();
///
/// // Branch without leaving the chain, and deliver later:
/// await nexus.Push.Notification()
///     .Title(title)
///     .Image(thumbnail)                      // null leaves it unset
///     .When(ids.Count == 0, p => p.ToSegment("All Clients"), p => p.ToUsers(ids))
///     .When(eta, (p, at) => p.SendAt(at.AddHours(-1)))
///     .SendAsync();                          // result.IsScheduled, result.ScheduledId
/// </code>
/// </example>
public sealed class PushNotificationBuilder
{
    private readonly Func<JsonObject, CancellationToken, Task<PushSendResult>> _sender;
    private readonly JsonObject _data = new JsonObject();
    private readonly JsonObject _options = new JsonObject();
    private readonly List<string> _distinctIds = new List<string>();
    private readonly List<string> _segmentIds = new List<string>();
    private readonly JsonObject _filter = new JsonObject();

    private string? _title;
    private string? _body;
    private Dictionary<string, string> _titles = new Dictionary<string, string>();
    private Dictionary<string, string> _bodies = new Dictionary<string, string>();
    private string? _defaultLanguage;
    private string? _imageUrl;
    private bool _all;
    private DateTimeOffset? _sendAt;
    private string? _name;

    internal PushNotificationBuilder(Func<JsonObject, CancellationToken, Task<PushSendResult>> sender)
        => _sender = sender;

    /// <summary>The notification title. Required.</summary>
    public PushNotificationBuilder Title(string title)
    {
        _title = title;
        _titles = new Dictionary<string, string>();
        return this;
    }

    /// <summary>
    /// One title per language, keyed by language code. Each device gets its
    /// language's; an empty or missing translation falls back to the default
    /// language (see <see cref="DefaultLanguage"/>).
    /// </summary>
    /// <example><c>.Title(new Dictionary&lt;string, string&gt; { ["en"] = "Your order shipped", ["ar"] = "تم شحن طلبك" })</c></example>
    public PushNotificationBuilder Title(IReadOnlyDictionary<string, string> titles)
    {
        _titles = Translations(titles ?? throw new ArgumentNullException(nameof(titles)));
        _title = null;
        return this;
    }

    /// <summary>The notification body. Null or empty leaves it unset.</summary>
    public PushNotificationBuilder Body(string? body)
    {
        _body = string.IsNullOrEmpty(body) ? null : body;
        _bodies = new Dictionary<string, string>();
        return this;
    }

    /// <summary>One body per language, like <see cref="Title(IReadOnlyDictionary{string, string})"/>.</summary>
    public PushNotificationBuilder Body(IReadOnlyDictionary<string, string> bodies)
    {
        _bodies = Translations(bodies ?? throw new ArgumentNullException(nameof(bodies)));
        _body = null;
        return this;
    }

    /// <summary>
    /// The language a device gets when its own has no translation. Defaults to
    /// <c>en</c> when there is one, else the first language given.
    /// </summary>
    public PushNotificationBuilder DefaultLanguage(string? language)
    {
        _defaultLanguage = string.IsNullOrWhiteSpace(language) ? null : language!.Trim();
        return this;
    }

    private static Dictionary<string, string> Translations(IReadOnlyDictionary<string, string> map)
    {
        var result = new Dictionary<string, string>();
        foreach (var pair in map)
        {
            var lang = pair.Key?.Trim();
            if (!string.IsNullOrEmpty(lang) && !string.IsNullOrWhiteSpace(pair.Value))
            {
                result[lang!] = pair.Value;
            }
        }

        return result;
    }

    /// <summary>
    /// Base text (the default language's, when given per language) plus one
    /// complete title/body per language — a language missing one takes the default's.
    /// </summary>
    private (string? Title, string? Body, JsonObject? Localizations, string? DefaultLanguage) Content()
    {
        var langs = new List<string>(_titles.Keys);
        foreach (var lang in _bodies.Keys)
        {
            if (!langs.Contains(lang))
            {
                langs.Add(lang);
            }
        }

        if (langs.Count == 0)
        {
            return (_title, _body, null, null);
        }

        var def = _defaultLanguage ?? (langs.Contains("en") ? "en" : langs[0]);
        var title = _title ?? (_titles.TryGetValue(def, out var dt) ? dt : FirstOrNull(_titles));
        var body = _body ?? (_bodies.TryGetValue(def, out var db) ? db : null);

        var localizations = new JsonObject();
        foreach (var lang in langs)
        {
            var t = _titles.TryGetValue(lang, out var lt) ? lt : title;
            if (t is null)
            {
                continue;
            }

            var b = _bodies.TryGetValue(lang, out var lb) ? lb : body;
            localizations[lang] = JsonBody.Create().Set("title", t).Set("body", b).Build();
        }

        return (title, body, localizations.Count > 0 ? localizations : null, def);
    }

    private static string? FirstOrNull(Dictionary<string, string> map)
    {
        foreach (var value in map.Values)
        {
            return value;
        }

        return null;
    }

    /// <summary>A large image to show in the expanded notification. Null or empty leaves it unset.</summary>
    public PushNotificationBuilder Image(string? url)
    {
        _imageUrl = string.IsNullOrEmpty(url) ? null : url;
        return this;
    }

    /// <summary>
    /// Applies <paramref name="then"/> only when <paramref name="condition"/> is
    /// true — Laravel's <c>when()</c> — otherwise <paramref name="otherwise"/>, if given.
    /// </summary>
    public PushNotificationBuilder When(
        bool condition,
        Action<PushNotificationBuilder> then,
        Action<PushNotificationBuilder>? otherwise = null)
    {
        if (then is null)
        {
            throw new ArgumentNullException(nameof(then));
        }

        if (condition)
        {
            then(this);
        }
        else
        {
            otherwise?.Invoke(this);
        }

        return this;
    }

    /// <summary>
    /// Applies <paramref name="then"/> with <paramref name="value"/> when the value
    /// is "truthy" — not null, <c>false</c>, zero, an empty string or an empty
    /// collection — otherwise <paramref name="otherwise"/>, if given.
    /// </summary>
    public PushNotificationBuilder When<T>(
        T? value,
        Action<PushNotificationBuilder, T> then,
        Action<PushNotificationBuilder>? otherwise = null)
    {
        if (then is null)
        {
            throw new ArgumentNullException(nameof(then));
        }

        if (IsTruthy(value))
        {
            then(this, value!);
        }
        else
        {
            otherwise?.Invoke(this);
        }

        return this;
    }

    /// <summary>The inverse of <see cref="When(bool, Action{PushNotificationBuilder}, Action{PushNotificationBuilder})"/>.</summary>
    public PushNotificationBuilder Unless(
        bool condition,
        Action<PushNotificationBuilder> then,
        Action<PushNotificationBuilder>? otherwise = null)
        => When(!condition, then, otherwise);

    /// <summary>
    /// Delivers at this time instead of now (up to a year ahead; a past time goes
    /// out within a minute). <see cref="SendAsync"/> then returns a result with
    /// <see cref="PushSendResult.IsScheduled"/> set. Null clears it, so
    /// <c>.SendAt(maybe)</c> sends now when there is no time.
    /// </summary>
    public PushNotificationBuilder SendAt(DateTimeOffset? at)
    {
        _sendAt = at;
        return this;
    }

    /// <summary>A label shown for a scheduled send in the console.</summary>
    public PushNotificationBuilder Name(string? label)
    {
        _name = string.IsNullOrEmpty(label) ? null : label;
        return this;
    }

    private static bool IsTruthy<T>(T? value)
    {
        switch (value)
        {
            case null:
                return false;
            case bool b:
                return b;
            case string s:
                return s.Length > 0;
            case int i:
                return i != 0;
            case long l:
                return l != 0;
            case double d:
                return d != 0;
            case decimal m:
                return m != 0;
            case ICollection c:
                return c.Count > 0;
            case IEnumerable e:
                return e.GetEnumerator().MoveNext();
            default:
                return true;
        }
    }

    /// <summary>
    /// Custom key/values delivered to the app rather than displayed. Merged with
    /// anything set earlier.
    /// </summary>
    public PushNotificationBuilder Data(object data)
    {
        JsonHelpers.MergeInto(_data, data);
        return this;
    }

    /// <summary>Adds an action button.</summary>
    /// <param name="id">Your id for the button, reported back on tap.</param>
    /// <param name="text">The button label.</param>
    /// <param name="url">A URL to open.</param>
    /// <param name="event">An analytics event to fire instead of opening a URL.</param>
    /// <param name="icon">An icon name.</param>
    /// <param name="style"><c>primary</c>, <c>secondary</c> or <c>text</c>.</param>
    public PushNotificationBuilder Button(
        string id,
        string text,
        string? url = null,
        string? @event = null,
        string? icon = null,
        string? style = null)
    {
        Guard.NotBlank(id, nameof(id));
        Guard.NotBlank(text, nameof(text));

        if (_options["buttons"] is not JsonArray buttons)
        {
            buttons = new JsonArray();
            _options["buttons"] = buttons;
        }

        buttons.Add((JsonNode?)JsonBody.Create()
            .Set("id", id)
            .Set("text", text)
            .Set("url", url)
            .Set("event", @event)
            .Set("icon", icon)
            .Set("style", style)
            .Build());

        return this;
    }

    /// <summary>Sets the iOS app badge count.</summary>
    public PushNotificationBuilder IosBadge(int count) => Option("iosBadge", count);

    /// <summary>Sets the iOS relevance score, which orders notifications in a summary.</summary>
    public PushNotificationBuilder IosRelevanceScore(double score) => Option("iosRelevanceScore", score);

    /// <summary>
    /// Sets the iOS interruption level: <c>passive</c>, <c>active</c>,
    /// <c>time-sensitive</c> or <c>critical</c>.
    /// </summary>
    /// <remarks>
    /// <c>time-sensitive</c> and <c>critical</c> break through Focus modes and need
    /// an Apple entitlement; without it the notification is delivered at
    /// <c>active</c> instead.
    /// </remarks>
    public PushNotificationBuilder IosInterruptionLevel(string level) => Option("iosInterruptionLevel", level);

    /// <summary>Sets the iOS subtitle line.</summary>
    public PushNotificationBuilder IosSubtitle(string subtitle) => Option("iosSubtitle", subtitle);

    /// <summary>Sets Android lock-screen visibility: <c>public</c>, <c>private</c> or <c>secret</c>.</summary>
    public PushNotificationBuilder AndroidVisibility(string visibility)
        => Option("androidVisibility", visibility);

    /// <summary>Sets the Android large icon.</summary>
    public PushNotificationBuilder AndroidLargeIcon(string url) => Option("androidLargeIcon", url);

    /// <summary>Sets the Android big-picture image.</summary>
    public PushNotificationBuilder AndroidBigPicture(string url) => Option("androidBigPicture", url);

    /// <summary>Sets the Android accent colour.</summary>
    public PushNotificationBuilder AndroidAccentColor(string color) => Option("androidAccentColor", color);

    /// <summary>
    /// Sets any option field directly — the escape hatch for a server option newer
    /// than this SDK.
    /// </summary>
    public PushNotificationBuilder Option(string key, object? value)
    {
        Guard.NotBlank(key, nameof(key));
        _options[key] = JsonHelpers.ToNode(value);
        return this;
    }

    /// <summary>Merges several options at once.</summary>
    public PushNotificationBuilder Options(object options)
    {
        JsonHelpers.MergeInto(_options, options);
        return this;
    }

    /// <summary>Targets specific people, across every device each has registered.</summary>
    public PushNotificationBuilder ToUsers(IEnumerable<string> distinctIds)
    {
        if (distinctIds is null)
        {
            throw new ArgumentNullException(nameof(distinctIds));
        }

        foreach (var id in distinctIds)
        {
            if (!string.IsNullOrWhiteSpace(id) && !_distinctIds.Contains(id))
            {
                _distinctIds.Add(id);
            }
        }

        return this;
    }

    /// <summary>Targets one person.</summary>
    public PushNotificationBuilder ToUser(string distinctId) => ToUsers(new[] { distinctId });

    /// <summary>Targets the union of saved segments.</summary>
    public PushNotificationBuilder ToSegments(IEnumerable<string> segmentIds)
    {
        if (segmentIds is null)
        {
            throw new ArgumentNullException(nameof(segmentIds));
        }

        foreach (var id in segmentIds)
        {
            if (!string.IsNullOrWhiteSpace(id) && !_segmentIds.Contains(id))
            {
                _segmentIds.Add(id);
            }
        }

        return this;
    }

    /// <summary>Targets one saved segment.</summary>
    public PushNotificationBuilder ToSegment(string segmentId) => ToSegments(new[] { segmentId });

    /// <summary>
    /// Targets devices matching an ad-hoc filter: <c>platform</c>, <c>lang</c>,
    /// <c>osType</c>, <c>appVersion</c>, <c>lastSeenDays</c>, <c>tags</c>.
    /// </summary>
    public PushNotificationBuilder Where(object filter)
    {
        JsonHelpers.MergeInto(_filter, filter);
        return this;
    }

    /// <summary>
    /// Targets every subscribed device, overriding the other targets.
    /// </summary>
    /// <remarks>
    /// This reaches your entire install base in one call. It is deliberately
    /// spelled out rather than implied by sending with no target.
    /// </remarks>
    public PushNotificationBuilder ToEveryone()
    {
        _all = true;
        return this;
    }

    /// <summary>The assembled payload, without sending. Useful in tests.</summary>
    public JsonObject Build()
    {
        var content = Content();
        var body = JsonBody.Create()
            .Set("title", content.Title)
            .Set("body", content.Body)
            .Set("defaultLanguage", content.DefaultLanguage)
            .Set("imageUrl", _imageUrl);

        if (content.Localizations is not null)
        {
            body.SetNode("localizations", content.Localizations);
        }

        if (_data.Count > 0)
        {
            body.SetNode("data", _data.DeepClone());
        }

        if (_options.Count > 0)
        {
            body.SetNode("options", _options.DeepClone());
        }

        body.SetStrings("distinctIds", _distinctIds)
            .SetStrings("segmentIds", _segmentIds);

        if (_filter.Count > 0)
        {
            body.SetNode("filter", _filter.DeepClone());
        }

        if (_all)
        {
            body.Set("all", true);
        }

        if (_sendAt is { } at)
        {
            body.Set("sendAt", Payloads.SendAtValue(at)).Set("name", _name);
        }

        return body.Build();
    }

    /// <summary>Schedules the notification — shorthand for <c>.SendAt(at).SendAsync()</c>.</summary>
    public Task<PushSendResult> ScheduleAsync(DateTimeOffset at, CancellationToken cancellationToken = default)
        => SendAt(at).SendAsync(cancellationToken);

    /// <summary>Sends the notification — or schedules it, with <see cref="SendAt"/>.</summary>
    /// <exception cref="InvalidOperationException">No title, or no audience.</exception>
    public Task<PushSendResult> SendAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Content().Title))
        {
            throw new InvalidOperationException("A push notification requires Title().");
        }

        if (!_all && _distinctIds.Count == 0 && _segmentIds.Count == 0 && _filter.Count == 0)
        {
            throw new InvalidOperationException(
                "A push notification requires an audience: ToUsers(), ToSegments(), Where() or ToEveryone().");
        }

        return _sender(Build(), cancellationToken);
    }
}
