using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Internal;
using Inverge.Nexus.Models;

namespace Inverge.Nexus.Resources;

/// <summary>Deep links and attribution — record what a link click led to.</summary>
public sealed class LinksResource : NexusResource
{
    private static readonly string[] Types = { "install", "open", "reengagement", "uninstall", "in_app" };

    internal LinksResource(NexusClient client) : base(client)
    {
    }

    /// <summary>Records an attribution event against a link click.</summary>
    /// <param name="type"><c>install</c>, <c>open</c>, <c>reengagement</c>, <c>uninstall</c> or <c>in_app</c>.</param>
    /// <param name="name">The campaign or link name.</param>
    /// <param name="clickId">
    /// The click this is attributed to. Without it, attribution falls back to a
    /// probabilistic match, which is weaker — pass it whenever the client has it.
    /// </param>
    /// <param name="deviceId">The device identifier.</param>
    /// <param name="platform">The platform the conversion happened on.</param>
    /// <param name="revenue">Revenue to attribute, for an <c>in_app</c> event.</param>
    /// <param name="properties">Anything else worth recording.</param>
    /// <param name="context">Per-call identity and device overrides.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<NexusAck> AttributeAsync(
        string type = "install",
        string? name = null,
        string? clickId = null,
        string? deviceId = null,
        string? platform = null,
        double? revenue = null,
        object? properties = null,
        NexusTelemetryContext? context = null,
        CancellationToken cancellationToken = default)
    {
        Guard.OneOf(type, Types, nameof(type));

        var resolved = Resolve(context);
        var response = await SendAsync(
            Payloads.LinksAttribute(resolved, type, name, clickId, deviceId, platform, revenue, properties),
            cancellationToken).ConfigureAwait(false);

        return NexusAck.From(response);
    }

    /// <summary>Records an install attribution.</summary>
    public Task<NexusAck> InstallAsync(
        string? clickId = null,
        string? name = null,
        string? platform = null,
        NexusTelemetryContext? context = null,
        CancellationToken cancellationToken = default)
        => AttributeAsync("install", name, clickId, null, platform, null, null, context, cancellationToken);

    /// <summary>Records an app open attributed to a link.</summary>
    public Task<NexusAck> OpenAsync(
        string? clickId = null,
        string? name = null,
        string? platform = null,
        NexusTelemetryContext? context = null,
        CancellationToken cancellationToken = default)
        => AttributeAsync("open", name, clickId, null, platform, null, null, context, cancellationToken);

    /// <summary>Records a re-engagement.</summary>
    public Task<NexusAck> ReengagementAsync(
        string? clickId = null,
        string? name = null,
        string? platform = null,
        NexusTelemetryContext? context = null,
        CancellationToken cancellationToken = default)
        => AttributeAsync("reengagement", name, clickId, null, platform, null, null, context, cancellationToken);
}
