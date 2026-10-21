using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Internal;
using Inverge.Nexus.Models;

namespace Inverge.Nexus.Builders;

/// <summary>
/// Composes a Live Activity's data (or content state) and audience, then starts,
/// updates or ends it.
/// </summary>
/// <remarks>
/// Template-only setters (<see cref="Step"/>, <see cref="EtaAt(DateTimeOffset)"/>,
/// <see cref="Value"/>, <see cref="Canceled"/>, …) send template <c>data</c>, which
/// the server merges on update. On their own, <see cref="Progress"/> and the texts
/// keep sending a plain <c>contentState</c> for your own widget.
/// </remarks>
/// <example>
/// <code>
/// await nexus.LiveActivities.Template("order_tracking", "order_42")
///     .Step("pending").EtaAt(DateTimeOffset.UtcNow.AddMinutes(25))
///     .Value("driver", "Ahmed").ToUser("user_1")
///     .StartAsync();
///
/// await nexus.LiveActivities.Activity("order_42").Step("arrived").UpdateAsync();
/// await nexus.LiveActivities.Activity("order_42").Step("completed").EndAsync();
///
/// // Plain (your own iOS widget):
/// await nexus.LiveActivities.Activity("DeliveryAttributes", "order_42")
///     .Title("Order #42").Status("Preparing").Progress(20)
///     .ToUser("user_1").Priority(10)
///     .StartAsync();
///
/// await nexus.LiveActivities.Activity("order_42").Status("Delivered").EndAsync();
/// </code>
/// </example>
public sealed class LiveActivityBuilder
{
    private readonly Resources.LiveActivitiesResource _resource;
    private readonly string _activityId;
    private readonly string? _activityType;
    private readonly object? _template;
    private readonly JsonObject _state = new JsonObject();
    private readonly JsonObject _data = new JsonObject();
    private string? _lang;
    private string? _alertTitle;
    private string? _alertBody;
    private readonly JsonObject _attributes = new JsonObject();
    private readonly List<string> _distinctIds = new List<string>();

    private int? _priority;
    private DateTimeOffset? _staleAt;
    private DateTimeOffset? _dismissAt;

    internal LiveActivityBuilder(
        Resources.LiveActivitiesResource resource, string activityId, string? activityType, object? template = null)
    {
        _resource = resource;
        _activityId = activityId;
        _activityType = activityType;
        _template = template;
    }

    /// <summary>A template, or a template-only field (step / state / etaAt / values).</summary>
    private bool Templated
        => _template is not null
           || _data.ContainsKey("step") || _data.ContainsKey("state")
           || _data.ContainsKey("etaAt") || _data.ContainsKey("values");

    // ── template data ───────────────────────────────────────────────────────

    /// <summary>The current step — a step id or label of the template.</summary>
    public LiveActivityBuilder Step(string step)
    {
        Guard.NotBlank(step, nameof(step));
        _data["step"] = step;
        return this;
    }

    /// <summary>The arrival time — the device shows a live countdown with no further pushes.</summary>
    public LiveActivityBuilder EtaAt(DateTimeOffset at)
    {
        _data["etaAt"] = at.ToUnixTimeSeconds();
        return this;
    }

    /// <summary>The arrival time as unix seconds (milliseconds are detected and converted).</summary>
    public LiveActivityBuilder EtaAt(long unixTime)
    {
        _data["etaAt"] = unixTime > 100_000_000_000L ? (long)Math.Round(unixTime / 1000d) : unixTime;
        return this;
    }

    /// <summary>A fixed ETA text instead of a countdown, e.g. <c>"8 min"</c>.</summary>
    public LiveActivityBuilder Eta(string text) => Value("eta", text);

    /// <summary>One value for the template's <c>{{placeholder}}</c>.</summary>
    public LiveActivityBuilder Value(string key, object? value)
    {
        Guard.NotBlank(key, nameof(key));
        if (_data["values"] is not JsonObject values)
        {
            values = new JsonObject();
            _data["values"] = values;
        }

        values[key] = JsonHelpers.ToNode(value);
        return this;
    }

    /// <summary>Several placeholder values at once (an anonymous object or a dictionary).</summary>
    public LiveActivityBuilder Values(object values)
    {
        if (_data["values"] is not JsonObject target)
        {
            target = new JsonObject();
            _data["values"] = target;
        }

        JsonHelpers.MergeInto(target, values);
        return this;
    }

    /// <summary>Shows the canceled state.</summary>
    public LiveActivityBuilder Canceled()
    {
        _data["state"] = "canceled";
        return this;
    }

    /// <summary>Shows the finished state.</summary>
    public LiveActivityBuilder Done()
    {
        _data["state"] = "done";
        return this;
    }

    /// <summary>Merges raw template data (step, state, progress, etaAt, values).</summary>
    public LiveActivityBuilder Data(object data)
    {
        JsonHelpers.MergeInto(_data, data);
        return this;
    }

    /// <summary>Forces one language for every recipient. Start only; default: each device's own.</summary>
    public LiveActivityBuilder Lang(string lang)
    {
        Guard.NotBlank(lang, nameof(lang));
        _lang = lang;
        return this;
    }

    /// <summary>The iOS alert shown with the start. Start only.</summary>
    public LiveActivityBuilder Alert(string title, string? body = null)
    {
        Guard.NotBlank(title, nameof(title));
        _alertTitle = title;
        _alertBody = body;
        return this;
    }

    // ── content state (templated: these override the template's texts) ─────

    /// <summary>Sets the activity's title.</summary>
    public LiveActivityBuilder Title(string value) => Text("title", value);

    /// <summary>Sets the activity's subtitle.</summary>
    public LiveActivityBuilder Subtitle(string value) => Text("subtitle", value);

    /// <summary>Sets the activity's body text.</summary>
    public LiveActivityBuilder Body(string value) => Text("body", value);

    private LiveActivityBuilder Text(string key, string value)
    {
        _data[key] = value;
        return Set(key, value);
    }

    /// <summary>Sets the activity's status line.</summary>
    public LiveActivityBuilder Status(string value) => Set("status", value);

    /// <summary>Sets progress, 0.0–1.0 (or 0–100). Templated: toward the next step or along the bar.</summary>
    public LiveActivityBuilder Progress(double value)
    {
        _data["progress"] = value;
        return Set("progress", value);
    }

    /// <summary>Sets any content-state field.</summary>
    public LiveActivityBuilder Set(string key, object? value)
    {
        Guard.NotBlank(key, nameof(key));
        _state[key] = JsonHelpers.ToNode(value);
        return this;
    }

    /// <summary>Merges several content-state fields at once.</summary>
    public LiveActivityBuilder State(object state)
    {
        JsonHelpers.MergeInto(_state, state);
        return this;
    }

    /// <summary>Targets specific people. Start only.</summary>
    public LiveActivityBuilder ToUsers(IEnumerable<string> distinctIds)
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

    /// <summary>Targets one person. Start only.</summary>
    public LiveActivityBuilder ToUser(string distinctId) => ToUsers(new[] { distinctId });

    /// <summary>
    /// Makes this a shared activity that many people watch — a match, a launch, an
    /// incident — rather than one person's order.
    /// </summary>
    public LiveActivityBuilder Shared()
    {
        _distinctIds.Clear();
        return this;
    }

    /// <summary>
    /// Sets fixed attributes, read once when the activity starts.
    /// </summary>
    /// <remarks>
    /// On iOS these are ActivityKit's static attributes: they cannot be changed by
    /// a later update, so anything that will change belongs in the content state.
    /// </remarks>
    public LiveActivityBuilder Attributes(object attributes)
    {
        JsonHelpers.MergeInto(_attributes, attributes);
        return this;
    }

    /// <summary>
    /// Sets the APNs priority: 5 is routine and unmetered, 10 is immediate and
    /// metered against your Live Activity budget.
    /// </summary>
    public LiveActivityBuilder Priority(int priority)
    {
        _priority = priority;
        return this;
    }

    /// <summary>Marks the activity stale after this time, so the UI can dim it.</summary>
    public LiveActivityBuilder StaleAt(DateTimeOffset when)
    {
        _staleAt = when;
        return this;
    }

    /// <summary>When iOS should remove the ended activity. End only.</summary>
    public LiveActivityBuilder DismissAt(DateTimeOffset when)
    {
        _dismissAt = when;
        return this;
    }

    /// <summary>Starts the activity.</summary>
    /// <exception cref="InvalidOperationException">The builder has neither a template nor an activity type.</exception>
    public Task<LiveActivityResult> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_template is not null)
        {
            return _resource.StartTemplateAsync(
                _template,
                _activityId,
                _data,
                _distinctIds.Count == 0 ? null : _distinctIds,
                _lang,
                _alertTitle,
                _alertBody,
                _attributes.Count == 0 ? null : _attributes,
                _staleAt,
                _priority,
                cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(_activityType))
        {
            throw new InvalidOperationException(
                "Starting an activity needs a template — Template(key, id) — or its type — Activity(type, id).");
        }

        return _resource.StartAsync(
            _activityType!,
            _activityId,
            _state,
            _distinctIds.Count == 0 ? null : _distinctIds,
            _attributes.Count == 0 ? null : _attributes,
            _staleAt,
            _priority,
            cancellationToken);
    }

    /// <summary>
    /// Pushes the change to every device showing the activity — templated: partial
    /// data, merged server-side; plain: the new content state.
    /// </summary>
    public Task<LiveActivityResult> UpdateAsync(CancellationToken cancellationToken = default)
        => Templated
            ? _resource.UpdateDataAsync(_activityId, _data, _staleAt, _priority, cancellationToken)
            : _resource.UpdateAsync(_activityId, _state, _staleAt, _priority, cancellationToken);

    /// <summary>Ends the activity, with an optional final state.</summary>
    public Task<LiveActivityResult> EndAsync(CancellationToken cancellationToken = default)
        => Templated
            ? _resource.EndWithDataAsync(_activityId, _data.Count == 0 ? null : _data, _dismissAt, cancellationToken)
            : _resource.EndAsync(_activityId, _state.Count == 0 ? null : _state, _dismissAt, cancellationToken);
}
