using System;
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
    private string? _imageUrl;
    private bool _all;

    internal PushNotificationBuilder(Func<JsonObject, CancellationToken, Task<PushSendResult>> sender)
        => _sender = sender;

    /// <summary>The notification title. Required.</summary>
    public PushNotificationBuilder Title(string title)
    {
        _title = title;
        return this;
    }

    /// <summary>The notification body.</summary>
    public PushNotificationBuilder Body(string body)
    {
        _body = body;
        return this;
    }

    /// <summary>A large image to show in the expanded notification.</summary>
    public PushNotificationBuilder Image(string url)
    {
        _imageUrl = url;
        return this;
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
        var body = JsonBody.Create()
            .Set("title", _title)
            .Set("body", _body)
            .Set("imageUrl", _imageUrl);

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

        return body.Build();
    }

    /// <summary>Sends the notification.</summary>
    /// <exception cref="InvalidOperationException">No title, or no audience.</exception>
    public Task<PushSendResult> SendAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_title))
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
