using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Internal;
using Inverge.Nexus.Models;

namespace Inverge.Nexus.Resources;

/// <summary>In-product surveys — headless: Nexus targets and stores, you render.</summary>
public sealed class SurveysResource : NexusResource
{
    internal SurveysResource(NexusClient client) : base(client)
    {
    }

    /// <summary>
    /// The surveys this person is currently eligible for, with targeting, sampling
    /// and frequency capping already applied.
    /// </summary>
    public async Task<IReadOnlyList<JsonNode>> ActiveAsync(
        object? properties = null,
        string? deviceType = null,
        NexusTelemetryContext? context = null,
        CancellationToken cancellationToken = default)
    {
        var resolved = Resolve(context);
        var response = await SendAsync(
            Payloads.SurveysActive(resolved, properties, deviceType), cancellationToken).ConfigureAwait(false);

        return response.Items("surveys");
    }

    /// <summary>Submits a response, partial or complete.</summary>
    /// <param name="surveyId">The survey being answered.</param>
    /// <param name="answers">Answers keyed by question id.</param>
    /// <param name="completed">Mark the response complete.</param>
    /// <param name="dismissed">Record that the person dismissed the survey.</param>
    /// <param name="responseId">
    /// An existing response to append to. Pass back the id from a previous call, or
    /// each partial submission creates a separate response.
    /// </param>
    /// <param name="iterationKey">Which recurrence of a repeating survey this is.</param>
    /// <param name="context">Per-call identity and device overrides.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<SurveyResponseResult> RespondAsync(
        string surveyId,
        object? answers = null,
        bool? completed = null,
        bool? dismissed = null,
        string? responseId = null,
        string? iterationKey = null,
        NexusTelemetryContext? context = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(surveyId, nameof(surveyId));

        var resolved = Resolve(context);
        var response = await SendAsync(
            Payloads.SurveysRespond(
                resolved, surveyId, answers, completed, dismissed, responseId, iterationKey),
            cancellationToken).ConfigureAwait(false);

        return SurveyResponseResult.From(response);
    }

    /// <summary>Submits a completed response in one call.</summary>
    public Task<SurveyResponseResult> CompleteAsync(
        string surveyId,
        object answers,
        string? responseId = null,
        string? iterationKey = null,
        NexusTelemetryContext? context = null,
        CancellationToken cancellationToken = default)
        => RespondAsync(surveyId, answers, true, null, responseId, iterationKey, context, cancellationToken);

    /// <summary>
    /// Records that the person dismissed the survey.
    /// </summary>
    /// <remarks>
    /// Worth sending: a dismissal feeds frequency capping, so skipping it means the
    /// same survey keeps being offered to someone who has already said no.
    /// </remarks>
    public Task<SurveyResponseResult> DismissAsync(
        string surveyId,
        string? responseId = null,
        string? iterationKey = null,
        NexusTelemetryContext? context = null,
        CancellationToken cancellationToken = default)
        => RespondAsync(surveyId, null, null, true, responseId, iterationKey, context, cancellationToken);
}
