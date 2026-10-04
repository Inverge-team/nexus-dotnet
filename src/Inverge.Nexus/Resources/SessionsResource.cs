using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Internal;
using Inverge.Nexus.Models;

namespace Inverge.Nexus.Resources;

/// <summary>
/// The sessions spine: who someone is, and which journey they are on.
/// </summary>
/// <remarks>
/// Events, logs, errors, surveys and replay all correlate into a session, which
/// is what turns a pile of telemetry into one person's story. On a backend this
/// is usually the first Nexus call a request makes.
/// </remarks>
public sealed class SessionsResource : NexusResource
{
    internal SessionsResource(NexusClient client) : base(client)
    {
    }

    /// <summary>Creates or updates the person behind a distinct id.</summary>
    /// <param name="distinctId">Your own id for the person.</param>
    /// <param name="email">Their email.</param>
    /// <param name="name">Their display name.</param>
    /// <param name="phone">Their phone number.</param>
    /// <param name="traits">Any other attributes — plan, role, company.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <example>
    /// <code>
    /// await nexus.Sessions.IdentifyAsync("user_123", email: "a@b.com",
    ///     traits: new { plan = "pro" });
    /// </code>
    /// </example>
    public async Task<IdentifyResult> IdentifyAsync(
        string distinctId,
        string? email = null,
        string? name = null,
        string? phone = null,
        object? traits = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(distinctId, nameof(distinctId));

        var response = await SendAsync(
            Payloads.SessionsIdentify(distinctId, email, name, phone, traits),
            cancellationToken).ConfigureAwait(false);

        return IdentifyResult.From(response);
    }

    /// <summary>
    /// Starts or refreshes the person's active session.
    /// </summary>
    /// <remarks>
    /// Identity and device fields you leave out come from the ambient
    /// <see cref="NexusContext"/> and then from the client options, so a web
    /// request that already bound its context can call this with no arguments.
    /// </remarks>
    /// <param name="email">Their email, if you are also updating it.</param>
    /// <param name="name">Their display name, if you are also updating it.</param>
    /// <param name="traits">Attributes to update alongside the session.</param>
    /// <param name="entryUrl">
    /// Where the session began. Defaults to the URL bound in the current scope.
    /// </param>
    /// <param name="context">Per-call identity and device overrides.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<SessionResult> TrackAsync(
        string? email = null,
        string? name = null,
        object? traits = null,
        string? entryUrl = null,
        NexusTelemetryContext? context = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(context);
        var response = await SendAsync(
            Payloads.SessionsTrack(resolved, email, name, traits, entryUrl),
            cancellationToken).ConfigureAwait(false);

        return SessionResult.From(response);
    }
}
