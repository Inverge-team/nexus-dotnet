using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Internal;

namespace Inverge.Nexus.Resources;

/// <summary>
/// Push Authentication — approve sign-ins and transactions on the user's
/// enrolled phone: match the number, tap, a code from the phone, or silently.
/// </summary>
/// <remarks>
/// <code>
/// var enrollment = await nexus.PushAuth.CreateEnrollmentAsync("user_123");   // → token for the app
/// var c = await nexus.PushAuth.CreateChallengeAsync(new CreatePushAuthChallenge {
///     Identity = "user_123", Message = "Sign in to Acme", Method = "number_match",
///     Context = { ["device"] = "Chrome on macOS" } });
/// Show(c!.RequesterView?["number"]);
/// var done = await nexus.PushAuth.WaitForChallengeAsync(c.Id);
/// if (done!.Status == "approved") await nexus.PushAuth.VerifyReceiptAsync(done.Receipt!);
/// </code>
/// The client's API key must be GRANTED Push Authentication server access
/// (console → API keys → the key → Push Authentication → Server access; off by default) —
/// a server-only key, never one shipped in an app; without it calls fail with
/// <c>server_access_required</c>. Runs directly and always throws on failure
/// (<see cref="NexusApiException"/> with the server's code, e.g. <c>no_factors</c>,
/// <c>identity_locked</c>, <c>too_many_pending</c>); switched off
/// (<c>NEXUS_PUSH_AUTH_ENABLED=false</c>) every call returns <see langword="null"/>.
/// </remarks>
public sealed class PushAuthResource : NexusResource
{
    private const string Base = "/partner/push-auth";

    internal PushAuthResource(NexusClient client) : base(client)
    {
    }

    /// <summary>A single-use enrollment token (10 min) the app passes to the SDK's <c>enroll(token)</c>.</summary>
    public async Task<PushAuthEnrollment?> CreateEnrollmentAsync(string identity, string? friendlyName = null, int? ttlSeconds = null, CancellationToken cancellationToken = default)
    {
        Guard.NotBlank(identity, nameof(identity));
        var body = new JsonObject { ["identity"] = identity };
        if (friendlyName != null) body["friendlyName"] = friendlyName;
        if (ttlSeconds != null) body["ttlSeconds"] = ttlSeconds;
        return await CallAsync("POST", "/enrollments", body, cancellationToken).ConfigureAwait(false) is JsonObject o ? new PushAuthEnrollment(o) : null;
    }

    /// <summary>An identity's devices (or every device, newest first).</summary>
    public async Task<IReadOnlyList<PushAuthFactor>> ListFactorsAsync(string? identity = null, string? status = null, CancellationToken cancellationToken = default)
    {
        var q = new List<string>();
        if (identity != null) q.Add("identity=" + Uri.EscapeDataString(identity));
        if (status != null) q.Add("status=" + Uri.EscapeDataString(status));
        var data = await CallAsync("GET", "/factors" + (q.Count > 0 ? "?" + string.Join("&", q) : string.Empty), null, cancellationToken).ConfigureAwait(false);
        var list = new List<PushAuthFactor>();
        if (data is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonObject o) list.Add(new PushAuthFactor(o));
            }
        }

        return list;
    }

    /// <summary>Remove a device for good.</summary>
    public async Task<PushAuthFactor?> RevokeFactorAsync(string factorId, CancellationToken cancellationToken = default)
        => await CallAsync("DELETE", "/factors/" + Seg(factorId), null, cancellationToken).ConfigureAwait(false) is JsonObject o ? new PushAuthFactor(o) : null;

    /// <summary>Lock a device (it can't approve until unlocked).</summary>
    public async Task<PushAuthFactor?> LockFactorAsync(string factorId, CancellationToken cancellationToken = default)
        => await CallAsync("POST", "/factors/" + Seg(factorId) + "/lock", new JsonObject(), cancellationToken).ConfigureAwait(false) is JsonObject o ? new PushAuthFactor(o) : null;

    /// <summary>Unlock a locked device.</summary>
    public async Task<PushAuthFactor?> UnlockFactorAsync(string factorId, CancellationToken cancellationToken = default)
        => await CallAsync("POST", "/factors/" + Seg(factorId) + "/unlock", new JsonObject(), cancellationToken).ConfigureAwait(false) is JsonObject o ? new PushAuthFactor(o) : null;

    /// <summary>Lift an identity's push-bombing lock.</summary>
    public Task<JsonNode?> UnlockIdentityAsync(string identity, CancellationToken cancellationToken = default)
        => CallAsync("POST", "/identities/" + Seg(identity) + "/unlock", new JsonObject(), cancellationToken);

    /// <summary>Start an approval. <see cref="PushAuthChallenge.RequesterView"/> holds what your screen shows.</summary>
    public async Task<PushAuthChallenge?> CreateChallengeAsync(CreatePushAuthChallenge challenge, CancellationToken cancellationToken = default)
    {
        if (challenge == null) throw new ArgumentNullException(nameof(challenge));
        Guard.NotBlank(challenge.Identity, nameof(challenge.Identity));
        Guard.NotBlank(challenge.Message, nameof(challenge.Message));
        var details = new JsonObject { ["message"] = challenge.Message };
        if (challenge.Fields.Count > 0)
        {
            var fields = new JsonArray();
            foreach (var f in challenge.Fields) fields.Add((JsonNode)new JsonObject { ["label"] = f.Key, ["value"] = f.Value });
            details["fields"] = fields;
        }

        if (challenge.Context.Count > 0)
        {
            var ctx = new JsonObject();
            foreach (var kv in challenge.Context) ctx[kv.Key] = kv.Value;
            details["context"] = ctx;
        }

        var body = new JsonObject { ["identity"] = challenge.Identity, ["details"] = details };
        if (challenge.Method != null) body["method"] = challenge.Method;
        if (challenge.Methods != null)
        {
            var m = new JsonArray();
            foreach (var x in challenge.Methods) m.Add((JsonNode?)JsonValue.Create(x));
            body["methods"] = m;
        }

        if (challenge.MethodOptions != null) body["methodOptions"] = challenge.MethodOptions.DeepClone();
        if (challenge.FactorId != null) body["factorId"] = challenge.FactorId;
        if (challenge.HiddenDetails != null) body["hiddenDetails"] = challenge.HiddenDetails.DeepClone();
        if (challenge.ExpiresInSeconds != null) body["expiresInSeconds"] = challenge.ExpiresInSeconds;
        if (challenge.FallbackTo != null) body["fallback"] = new JsonObject { ["to"] = challenge.FallbackTo };
        if (challenge.Ref != null) body["ref"] = challenge.Ref;
        if (challenge.IdempotencyKey != null) body["idempotencyKey"] = challenge.IdempotencyKey;
        return await CallAsync("POST", "/challenges", body, cancellationToken).ConfigureAwait(false) is JsonObject o ? new PushAuthChallenge(o) : null;
    }

    /// <summary>Current state; <paramref name="wait"/> (≤ 30 s) returns as soon as anything changes.</summary>
    public async Task<PushAuthChallenge?> GetChallengeAsync(string challengeId, TimeSpan? wait = null, CancellationToken cancellationToken = default)
    {
        var w = (int)Math.Max(0, Math.Min(30, Math.Round(wait?.TotalSeconds ?? 0)));
        var request = Request("GET", "/challenges/" + Seg(challengeId) + (w > 0 ? "?wait=" + w.ToString(CultureInfo.InvariantCulture) : string.Empty), null, retryable: true);
        if (w > 0) request.Timeout = TimeSpan.FromSeconds(w + 10);
        return await Client.ExecuteMessagingAsync(request, cancellationToken).ConfigureAwait(false) is JsonObject o ? new PushAuthChallenge(o) : null;
    }

    /// <summary>Long-poll until decided — or until <paramref name="untilStage"/> (e.g. <c>awaiting_code</c>).</summary>
    public async Task<PushAuthChallenge?> WaitForChallengeAsync(string challengeId, TimeSpan? timeout = null, string? untilStage = null, CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromHours(1));
        var c = await GetChallengeAsync(challengeId, null, cancellationToken).ConfigureAwait(false);
        while (c != null && c.Status == "pending" && (untilStage == null || c.Stage != untilStage))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var left = deadline - DateTimeOffset.UtcNow;
            if (left <= TimeSpan.Zero) break;
            c = await GetChallengeAsync(challengeId, TimeSpan.FromSeconds(Math.Max(1, Math.Min(25, left.TotalSeconds))), cancellationToken).ConfigureAwait(false);
        }

        return c;
    }

    /// <summary>The code the user typed (code display, or the SMS/Telegram fallback).</summary>
    public async Task<PushAuthChallenge?> CompleteAsync(string challengeId, string code, CancellationToken cancellationToken = default)
        => await CallAsync("POST", "/challenges/" + Seg(challengeId) + "/complete", new JsonObject { ["code"] = code }, cancellationToken).ConfigureAwait(false) is JsonObject o ? new PushAuthChallenge(o) : null;

    /// <summary>Cancel a pending challenge (the devices remove the prompt; not billed).</summary>
    public async Task<PushAuthChallenge?> CancelAsync(string challengeId, CancellationToken cancellationToken = default)
        => await CallAsync("POST", "/challenges/" + Seg(challengeId) + "/cancel", new JsonObject(), cancellationToken).ConfigureAwait(false) is JsonObject o ? new PushAuthChallenge(o) : null;

    /// <summary>Verify an approval receipt; <paramref name="consume"/> (default) makes it single-use.</summary>
    public async Task<PushAuthReceiptCheck?> VerifyReceiptAsync(string token, bool consume = true, CancellationToken cancellationToken = default)
        => await CallAsync("POST", "/tokens/verify", new JsonObject { ["token"] = token, ["consume"] = consume }, cancellationToken).ConfigureAwait(false) is JsonObject o ? new PushAuthReceiptCheck(o) : null;

    /// <summary>
    /// Verify a webhook: hex HMAC-SHA256 over <c>timestamp.nonce.body</c>. Pass the RAW
    /// body and the <c>x-nexus-signature</c> / <c>x-nexus-timestamp</c> / <c>x-nexus-nonce</c>
    /// headers. Dedupe on the body's <c>id</c> — deliveries are at-least-once.
    /// </summary>
    public static bool VerifyWebhook(string body, string? signature, string? timestamp, string? nonce, string secret, int toleranceSeconds = 300)
    {
        if (signature == null || nonce == null || !long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ts)) return false;
        if (Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - ts) > toleranceSeconds) return false;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(ts.ToString(CultureInfo.InvariantCulture) + "." + nonce + "." + body));
        var expected = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) expected.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        var a = Encoding.ASCII.GetBytes(expected.ToString());
        var given = Encoding.ASCII.GetBytes(signature);
        if (a.Length != given.Length) return false;
        var diff = 0;
        for (var i = 0; i < a.Length; i++) diff |= a[i] ^ given[i];
        return diff == 0;
    }

    private static NexusRequest Request(string method, string path, JsonObject? body, bool retryable = false)
        => new NexusRequest(method, Base + path, body, null, retryable);

    private Task<JsonNode?> CallAsync(string method, string path, JsonObject? body, CancellationToken cancellationToken)
        => Client.ExecuteMessagingAsync(Request(method, path, body, retryable: method == "GET"), cancellationToken);

    private static string Seg(string value) => Uri.EscapeDataString(value);
}

/// <summary>What to approve and how.</summary>
public sealed class CreatePushAuthChallenge
{
    /// <summary>Your user id.</summary>
    public string Identity { get; set; } = string.Empty;

    /// <summary>What the user approves — shown on the phone and signed over by it.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Labelled lines (label → value), e.g. Amount → 25,000 IQD.</summary>
    public IList<KeyValuePair<string, string>> Fields { get; } = new List<KeyValuePair<string, string>>();

    /// <summary>Where the request comes from: <c>ip</c>, <c>location</c>, <c>device</c>, <c>app</c>.</summary>
    public IDictionary<string, string> Context { get; } = new Dictionary<string, string>();

    /// <summary><c>tap</c> · <c>number_match</c> · <c>code_display</c> · <c>silent</c> (default: the policy's).</summary>
    public string? Method { get; set; }

    /// <summary>An escalation chain, e.g. <c>["silent", "number_match"]</c>.</summary>
    public IReadOnlyList<string>? Methods { get; set; }

    /// <summary>Per-method options, e.g. <c>{"number_match": {"entry": "type"}}</c>.</summary>
    public JsonObject? MethodOptions { get; set; }

    /// <summary>Only this device (default: every active device of the identity).</summary>
    public string? FactorId { get; set; }

    /// <summary>Kept with the challenge, never shown.</summary>
    public JsonObject? HiddenDetails { get; set; }

    /// <summary>30–3600 (default: the policy's).</summary>
    public int? ExpiresInSeconds { get; set; }

    /// <summary>A phone number for the one-time-code fallback.</summary>
    public string? FallbackTo { get; set; }

    /// <summary>Your correlation value, echoed in webhooks and the receipt.</summary>
    public string? Ref { get; set; }

    /// <summary>Retry-safe creation.</summary>
    public string? IdempotencyKey { get; set; }
}

/// <summary>A Push Authentication challenge as your server sees it.</summary>
public sealed class PushAuthChallenge
{
    internal PushAuthChallenge(JsonObject o)
    {
        Raw = o;
        Id = o.Str("id") ?? string.Empty;
        Identity = o.Str("identity") ?? string.Empty;
        Status = o.Str("status") ?? string.Empty;
        Stage = o.Str("stage");
        Method = o.Str("method") ?? string.Empty;
        RequesterView = o.Obj("requesterView");
        Reason = o.Str("reason");
        FactorId = o.Str("factorId");
        Receipt = o.Str("receipt");
        Ref = o.Str("ref");
        ExpiresAt = o.Timestamp("expiresAt");
    }

    /// <summary>The challenge id (<c>pac_…</c>).</summary>
    public string Id { get; }
    /// <summary>Your user id.</summary>
    public string Identity { get; }
    /// <summary><c>pending</c> · <c>approved</c> · <c>denied</c> · <c>expired</c> · <c>cancelled</c>.</summary>
    public string Status { get; }
    /// <summary><c>awaiting_code</c> · <c>fallback</c> · null.</summary>
    public string? Stage { get; }
    /// <summary>The current method.</summary>
    public string Method { get; }
    /// <summary>What your sign-in screen shows (e.g. <c>number</c>).</summary>
    public JsonObject? RequesterView { get; }
    /// <summary>Why it ended (<c>user_denied</c>, <c>number_mismatch</c>, <c>fraud_reported</c>, …).</summary>
    public string? Reason { get; }
    /// <summary>The device that answered.</summary>
    public string? FactorId { get; }
    /// <summary>The approval receipt (ES256 JWT) when approved.</summary>
    public string? Receipt { get; }
    /// <summary>Your correlation value.</summary>
    public string? Ref { get; }
    /// <summary>When it expires.</summary>
    public DateTimeOffset? ExpiresAt { get; }
    /// <summary>The full response.</summary>
    public JsonObject Raw { get; }
}

/// <summary>An enrolled device.</summary>
public sealed class PushAuthFactor
{
    internal PushAuthFactor(JsonObject o)
    {
        Raw = o;
        Id = o.Str("id") ?? string.Empty;
        Identity = o.Str("identity") ?? string.Empty;
        Status = o.Str("status") ?? string.Empty;
        KeyStrength = o.Str("keyStrength") ?? "unknown";
        Attested = o.Flag("attested");
    }

    /// <summary>The factor id (<c>paf_…</c>).</summary>
    public string Id { get; }
    /// <summary>Your user id.</summary>
    public string Identity { get; }
    /// <summary><c>active</c> · <c>locked</c> · <c>revoked</c>.</summary>
    public string Status { get; }
    /// <summary><c>strongbox</c> · <c>tee</c> · <c>secure_enclave</c> · <c>software</c> · <c>unknown</c>.</summary>
    public string KeyStrength { get; }
    /// <summary>The keys were proven by hardware attestation.</summary>
    public bool Attested { get; }
    /// <summary>The full response.</summary>
    public JsonObject Raw { get; }
}

/// <summary>A single-use enrollment ticket.</summary>
public sealed class PushAuthEnrollment
{
    internal PushAuthEnrollment(JsonObject o)
    {
        Id = o.Str("id") ?? string.Empty;
        Token = o.Str("token") ?? string.Empty;
        ExpiresAt = o.Timestamp("expiresAt");
    }

    /// <summary>The enrollment id.</summary>
    public string Id { get; }
    /// <summary>Hand this to the app.</summary>
    public string Token { get; }
    /// <summary>When the token stops working.</summary>
    public DateTimeOffset? ExpiresAt { get; }
}

/// <summary>The result of verifying an approval receipt.</summary>
public sealed class PushAuthReceiptCheck
{
    internal PushAuthReceiptCheck(JsonObject o)
    {
        Valid = o.Flag("valid");
        Reason = o.Str("reason");
        Claims = o.Obj("claims");
    }

    /// <summary>Signature, environment, expiry, revocation (and single use) all passed.</summary>
    public bool Valid { get; }
    /// <summary>Why not (<c>token_replayed</c>, <c>token_expired</c>, …).</summary>
    public string? Reason { get; }
    /// <summary><c>sub</c> (identity), <c>fid</c>, <c>mth</c>, <c>amr</c>, <c>dh</c>, <c>ks</c>, …</summary>
    public JsonObject? Claims { get; }
}
