using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Internal;
using Inverge.Nexus.Models;

namespace Inverge.Nexus.Builders;

/// <summary>
/// Composes a Live Activity's content state and audience, then starts, updates or
/// ends it.
/// </summary>
/// <example>
/// <code>
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
    private readonly JsonObject _state = new JsonObject();
    private readonly JsonObject _attributes = new JsonObject();
    private readonly List<string> _distinctIds = new List<string>();

    private int? _priority;
    private DateTimeOffset? _staleAt;
    private DateTimeOffset? _dismissAt;

    internal LiveActivityBuilder(
        Resources.LiveActivitiesResource resource, string activityId, string? activityType)
    {
        _resource = resource;
        _activityId = activityId;
        _activityType = activityType;
    }

    /// <summary>Sets the activity's title.</summary>
    public LiveActivityBuilder Title(string value) => Set("title", value);

    /// <summary>Sets the activity's subtitle.</summary>
    public LiveActivityBuilder Subtitle(string value) => Set("subtitle", value);

    /// <summary>Sets the activity's body text.</summary>
    public LiveActivityBuilder Body(string value) => Set("body", value);

    /// <summary>Sets the activity's status line.</summary>
    public LiveActivityBuilder Status(string value) => Set("status", value);

    /// <summary>Sets progress. Both 0–100 and 0.0–1.0 are accepted by the renderers.</summary>
    public LiveActivityBuilder Progress(double value) => Set("progress", value);

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
    /// <exception cref="InvalidOperationException">The builder was created without an activity type.</exception>
    public Task<LiveActivityResult> StartAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_activityType))
        {
            throw new InvalidOperationException(
                "Starting an activity needs its type: Activity(type, id).");
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

    /// <summary>Pushes a new content state to every device showing the activity.</summary>
    public Task<LiveActivityResult> UpdateAsync(CancellationToken cancellationToken = default)
        => _resource.UpdateAsync(_activityId, _state, _staleAt, _priority, cancellationToken);

    /// <summary>Ends the activity, with an optional final state.</summary>
    public Task<LiveActivityResult> EndAsync(CancellationToken cancellationToken = default)
        => _resource.EndAsync(
            _activityId, _state.Count == 0 ? null : _state, _dismissAt, cancellationToken);
}
