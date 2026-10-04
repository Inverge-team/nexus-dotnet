using System;
using System.Threading;

namespace Inverge.Nexus.Internal;

/// <summary>Thread-safe random jitter for retry backoff.</summary>
/// <remarks>
/// Jitter is what stops a fleet of workers that all failed on the same backend
/// blip from retrying in lockstep and re-creating the blip. A plain shared
/// <see cref="Random"/> is not thread-safe and silently degenerates to returning
/// zero under contention, so each thread gets its own, seeded distinctly.
/// </remarks>
internal static class Jitter
{
    private static int _seed = Environment.TickCount;

    private static readonly ThreadLocal<Random> Local = new ThreadLocal<Random>(
        () => new Random(Interlocked.Increment(ref _seed)));

    /// <summary>A value in <c>[0, span)</c>.</summary>
    public static TimeSpan Next(TimeSpan span)
        => span <= TimeSpan.Zero
            ? TimeSpan.Zero
            : TimeSpan.FromTicks((long)(Local.Value!.NextDouble() * span.Ticks));
}
