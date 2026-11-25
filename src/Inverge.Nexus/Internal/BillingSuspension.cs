using System;
using System.Globalization;
using System.Text.Json.Nodes;

namespace Inverge.Nexus.Internal;

/// <summary>
/// The billing-suspension circuit breaker of one client (one engine).
/// </summary>
/// <remarks>
/// When the API answers 402 <c>billing_suspended</c> (an overdue invoice), the
/// client stops calling it for the pause the API asks for
/// (<c>details.retryAfterSeconds</c>, default 5 minutes): calls made meanwhile
/// throw <see cref="NexusBillingSuspendedException"/> locally, with no request
/// — and telemetry is dropped quietly. When the pause ends ONE request goes out
/// as a probe (holding a short lease so concurrent callers keep waiting): a 402
/// starts a new pause, any other answer means access is back.
/// <para>
/// Without this a suspended app keeps calling at full rate, and one that
/// reports its own failures to Nexus Errors turns every refused call into
/// another one. Messaging is exempt: it is paid from its own prepaid wallet and
/// never suspended; its 402 <c>insufficient_balance</c> is not a suspension.
/// </para>
/// </remarks>
internal sealed class BillingSuspension
{
    internal const int DefaultSeconds = 300;
    private const int MinSeconds = 30;
    private const int MaxSeconds = 3600;
    private static readonly TimeSpan ProbeLease = TimeSpan.FromSeconds(30);

    private readonly object _gate = new object();
    private readonly Func<DateTimeOffset> _clock;
    private DateTimeOffset? _until;

    public BillingSuspension(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Whether <paramref name="path"/> is billed from a prepaid wallet.</summary>
    public static bool IsExempt(string path)
    {
        const string Prefix = "/partner/messaging";
        if (!path.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        return path.Length == Prefix.Length || path[Prefix.Length] == '/' || path[Prefix.Length] == '?';
    }

    /// <summary>Whether an API answer is the billing-suspension refusal.</summary>
    public static bool IsSuspension(int status, string? code)
        => status == 402 && string.Equals(code, "billing_suspended", StringComparison.Ordinal);

    /// <summary>Time the pause still has to run (zero when not paused). Read-only.</summary>
    public TimeSpan Remaining(string path)
    {
        if (IsExempt(path))
        {
            return TimeSpan.Zero;
        }

        lock (_gate)
        {
            if (_until is not DateTimeOffset until)
            {
                return TimeSpan.Zero;
            }

            var left = until - _clock();
            return left > TimeSpan.Zero ? left : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Before a request: zero to send it, else how long to refuse it for. When a
    /// pause has just ended, the caller becomes the probe.
    /// </summary>
    public TimeSpan Begin(string path)
    {
        if (IsExempt(path))
        {
            return TimeSpan.Zero;
        }

        lock (_gate)
        {
            if (_until is not DateTimeOffset until)
            {
                return TimeSpan.Zero;
            }

            var now = _clock();
            if (until > now)
            {
                return until - now;
            }

            _until = now + ProbeLease;
            return TimeSpan.Zero;
        }
    }

    /// <summary>
    /// After an HTTP answer: a suspension starts (or extends) the pause and
    /// returns its length; any other answer lifts it.
    /// </summary>
    public TimeSpan? Record(string path, int status, string? code, JsonNode? details)
    {
        if (IsSuspension(status, code))
        {
            var seconds = DefaultSeconds;
            if (details is JsonObject obj && obj["retryAfterSeconds"] is JsonValue value
                && value.TryGetValue(out double raw) && !double.IsNaN(raw) && !double.IsInfinity(raw))
            {
                seconds = (int)Math.Min(MaxSeconds, Math.Max(MinSeconds, raw));
            }

            var pause = TimeSpan.FromSeconds(seconds);
            lock (_gate)
            {
                _until = _clock() + pause;
            }

            return pause;
        }

        if (!IsExempt(path))
        {
            lock (_gate)
            {
                _until = null;
            }
        }

        return null;
    }

    /// <summary>Forgets the pause (after settling an invoice, to retry at once).</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _until = null;
        }
    }

    public static NexusBillingSuspendedException LocalRefusal(TimeSpan remaining, string method, string path)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
        return new NexusBillingSuspendedException(
            string.Format(
                CultureInfo.InvariantCulture,
                "API access is suspended for an overdue invoice. Nexus calls are paused for {0} more seconds; "
                + "settle the outstanding balance to restore access.",
                seconds),
            402,
            "billing_suspended",
            null,
            method,
            path,
            null,
            TimeSpan.FromSeconds(seconds),
            isLocal: true);
    }
}
