using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Internal;
using Inverge.Nexus.Models;

namespace Inverge.Nexus.Resources;

/// <summary>Feature flags, evaluated server-side for one person.</summary>
/// <remarks>
/// <para>
/// Every helper here performs one evaluation call, and one call resolves
/// <em>every</em> flag in the environment. When a request needs several flags,
/// call <see cref="EvaluateAsync"/> once and ask the result — three
/// <see cref="IsEnabledAsync"/> calls are three round trips for data you already
/// had after the first.
/// </para>
/// <para>
/// The convenience helpers degrade instead of throwing. A flag lookup is a
/// decision point, not a data fetch: letting a Nexus hiccup turn into a 500 on a
/// page that would otherwise render is the wrong trade. <see cref="EvaluateAsync"/>
/// still throws, so you can handle failure explicitly where it matters.
/// </para>
/// </remarks>
public sealed class FlagsResource : NexusResource
{
    internal FlagsResource(NexusClient client) : base(client)
    {
    }

    /// <summary>Evaluates every active flag for a person.</summary>
    /// <param name="distinctId">
    /// Who to evaluate for. Falls back to the ambient context's distinct id.
    /// </param>
    /// <param name="properties">Targeting attributes — plan, country, role.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="ArgumentException">No distinct id was available.</exception>
    public async Task<FlagEvaluation> EvaluateAsync(
        string? distinctId = null,
        object? properties = null,
        CancellationToken cancellationToken = default)
    {
        var person = distinctId ?? NexusContext.Current.DistinctId;
        if (string.IsNullOrWhiteSpace(person))
        {
            throw new ArgumentException(
                "Flag evaluation needs a distinct id. Pass one, or bind it with NexusContext.Scope(...).",
                nameof(distinctId));
        }

        var response = await SendAsync(
            Payloads.FlagsEvaluate(person!, properties), cancellationToken).ConfigureAwait(false);

        return FlagEvaluation.From(response);
    }

    /// <summary>Whether a flag is on, or <paramref name="fallback"/> if it cannot be evaluated.</summary>
    public async Task<bool> IsEnabledAsync(
        string key,
        string? distinctId = null,
        object? properties = null,
        bool fallback = false,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(key, nameof(key));

        var evaluation = await SafeEvaluateAsync(distinctId, properties, cancellationToken)
            .ConfigureAwait(false);

        // An empty evaluation means the environment hit its flag limit, not that
        // every flag is off — falling back is the honest answer.
        return evaluation is null || evaluation.IsEmpty ? fallback : evaluation.IsEnabled(key);
    }

    /// <summary>The resolved variant of a multivariate flag, or <paramref name="fallback"/>.</summary>
    public async Task<string?> VariantAsync(
        string key,
        string? distinctId = null,
        object? properties = null,
        string? fallback = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(key, nameof(key));

        var evaluation = await SafeEvaluateAsync(distinctId, properties, cancellationToken)
            .ConfigureAwait(false);

        return evaluation is null ? fallback : evaluation.Variant(key) ?? fallback;
    }

    /// <summary>A flag's attached JSON payload, or <paramref name="fallback"/>.</summary>
    public async Task<JsonNode?> PayloadAsync(
        string key,
        string? distinctId = null,
        object? properties = null,
        JsonNode? fallback = null,
        CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(key, nameof(key));

        var evaluation = await SafeEvaluateAsync(distinctId, properties, cancellationToken)
            .ConfigureAwait(false);

        return evaluation is null ? fallback : evaluation.Payload(key) ?? fallback;
    }

    private async Task<FlagEvaluation?> SafeEvaluateAsync(
        string? distinctId, object? properties, CancellationToken cancellationToken)
    {
        try
        {
            return await EvaluateAsync(distinctId, properties, cancellationToken).ConfigureAwait(false);
        }
        catch (NexusException exception)
        {
            Client.Report("Flag evaluation failed; using the caller's fallback.", exception);
            return null;
        }
        catch (ArgumentException exception)
        {
            // No distinct id available. Worth saying out loud — it means the
            // targeting silently is not happening — but not worth a 500.
            Client.Report("Flag evaluation has no distinct id; using the caller's fallback.", exception);
            return null;
        }
    }
}
