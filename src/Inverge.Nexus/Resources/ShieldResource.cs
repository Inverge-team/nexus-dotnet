using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Internal;

namespace Inverge.Nexus.Resources;

/// <summary>
/// Shield — verify that a request really comes from your app on a real device.
/// </summary>
/// <remarks>
/// Your app (Nexus client SDK with Shield on) sends <c>X-Nexus-Shield</c> (a
/// device-bound token) and <c>DPoP</c> (a single-use proof for that exact method
/// + URL) with each request to your backend. Pass them here and act on
/// <see cref="ShieldVerdict.Allowed"/>. Replay protection, blocked devices and
/// the policy configured for the resource are applied by Nexus. Use a
/// server-side API key.
/// </remarks>
public sealed class ShieldResource : NexusResource
{
    internal ShieldResource(NexusClient client) : base(client)
    {
    }

    /// <summary>Verifies a token and its proof.</summary>
    /// <param name="token">The <c>X-Nexus-Shield</c> header value.</param>
    /// <param name="proof">The <c>DPoP</c> header value.</param>
    /// <param name="method">The HTTP method of the request being verified.</param>
    /// <param name="url">The full URL of the request being verified (query ignored).</param>
    /// <param name="resource">The action, matched against your Shield policies. Defaults to <c>*</c>.</param>
    /// <param name="bodyHash">base64url SHA-256 of the raw body — see <see cref="BodyHash(byte[])"/>.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<ShieldVerdict> VerifyAsync(
        string? token,
        string? proof,
        string method,
        string url,
        string? resource = null,
        string? bodyHash = null,
        CancellationToken cancellationToken = default)
    {
        var res = resource ?? "*";
        if (string.IsNullOrEmpty(token)) return ShieldVerdict.Missing(res);
        if (token!.StartsWith("DPoP ", StringComparison.OrdinalIgnoreCase)) token = token.Substring(5);

        var response = await SendAsync(Payloads.ShieldVerify(token, proof, method, url, resource, bodyHash), cancellationToken)
            .ConfigureAwait(false);
        return ShieldVerdict.From(response, res);
    }

    /// <summary>Verifies straight from request headers (case-insensitive lookup).</summary>
    public Task<ShieldVerdict> VerifyRequestAsync(
        IEnumerable<KeyValuePair<string, string?>> headers,
        string method,
        string url,
        byte[]? body = null,
        string? resource = null,
        CancellationToken cancellationToken = default)
    {
        string? token = null, proof = null;
        foreach (var h in headers)
        {
            if (string.Equals(h.Key, "X-Nexus-Shield", StringComparison.OrdinalIgnoreCase)) token = h.Value;
            else if (string.Equals(h.Key, "DPoP", StringComparison.OrdinalIgnoreCase)) proof = h.Value;
        }

        return VerifyAsync(token, proof, method, url, resource, BodyHash(body), cancellationToken);
    }

    /// <summary>base64url SHA-256 of a raw request body (the client's <c>bh</c> claim).</summary>
    public static string? BodyHash(byte[]? body)
    {
        if (body is null) return null;
#if NETSTANDARD2_0
        using var sha = SHA256.Create();
        var digest = sha.ComputeHash(body);
#else
        var digest = SHA256.HashData(body);
#endif
        return Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>base64url SHA-256 of a UTF-8 request body.</summary>
    public static string? BodyHash(string? body) => body is null ? null : BodyHash(Encoding.UTF8.GetBytes(body));
}

/// <summary>The outcome of a Shield verification.</summary>
public sealed class ShieldVerdict
{
    private ShieldVerdict(bool valid, string decision, string mode, string? reason, string resource, string? policy, JsonNode? token)
    {
        Valid = valid;
        Decision = decision;
        Mode = mode;
        Reason = reason;
        Resource = resource;
        Policy = policy;
        Score = token is null ? null : token.Int("score");
        Level = token.Str("level");
        Providers = token is null ? Array.Empty<string>() : token.Strings("providers");
        DeviceId = token.Str("deviceId");
        AppId = token.Str("appId");
        Platform = token.Str("platform");
    }

    /// <summary>Token signature, expiry, binding and proof all checked out.</summary>
    public bool Valid { get; }

    /// <summary><c>allow</c> or <c>deny</c> — in <c>monitor</c> mode this is <c>allow</c> even when invalid.</summary>
    public string Decision { get; }

    /// <summary>True when your backend should serve the request.</summary>
    public bool Allowed => Decision == "allow";

    /// <summary><c>off</c>, <c>monitor</c>, <c>enforce</c> or <c>none</c> (no policy, or Nexus unreachable).</summary>
    public string Mode { get; }

    /// <summary>Why it is (or would be) denied — e.g. <c>proof_replayed</c>, <c>score_below_minimum</c>; <c>unavailable</c> if Nexus could not answer.</summary>
    public string? Reason { get; }

    /// <summary>The resource that was evaluated.</summary>
    public string Resource { get; }

    /// <summary>The policy that matched, if any.</summary>
    public string? Policy { get; }

    /// <summary>Risk score 0–100 at token issue.</summary>
    public int? Score { get; }

    /// <summary><c>strong</c>, <c>device</c>, <c>basic</c>, <c>weak</c> or <c>debug</c>.</summary>
    public string? Level { get; }

    /// <summary>Evidence behind the token (<c>key_attestation</c>, <c>app_attest</c>, …).</summary>
    public IReadOnlyList<string> Providers { get; }

    /// <summary>The Shield device id.</summary>
    public string? DeviceId { get; }

    /// <summary>The Shield app id.</summary>
    public string? AppId { get; }

    /// <summary><c>android</c>, <c>ios</c>, <c>web</c> or <c>other</c>.</summary>
    public string? Platform { get; }

    internal static ShieldVerdict Missing(string resource) => new ShieldVerdict(false, "deny", "none", "token_missing", resource, null, null);

    internal static ShieldVerdict From(JsonNode? body, string resource)
    {
        if (body is null) return new ShieldVerdict(false, "deny", "none", "unavailable", resource, null, null);
        return new ShieldVerdict(
            body.Flag("valid"),
            body.Str("decision") ?? "deny",
            body.Str("mode") ?? "none",
            body.Str("reason"),
            body.Str("resource") ?? resource,
            body.Str("policy"),
            body.Prop("token") is JsonObject t ? t : null);
    }
}
