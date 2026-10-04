using System;
using System.Threading;

namespace Inverge.Nexus.Diagnostics;

/// <summary>
/// Marks the window in which the SDK is itself delivering to Nexus.
/// </summary>
/// <remarks>
/// <para>
/// This exists to break a feedback loop that is otherwise very easy to create.
/// A logging bridge that forwards <c>ILogger</c> records to the Nexus logs
/// product is normally registered globally — which means it also sees
/// <c>HttpClient</c>'s own "Sending HTTP request POST ..." records. Those are
/// produced <em>by</em> the delivery, so forwarding one queues another log line,
/// whose delivery logs again, forever.
/// </para>
/// <para>
/// Rather than muting <c>System.Net.Http</c> wholesale — which would also hide
/// the application's genuine HTTP logs — the client marks its delivery window
/// here, and any bridge drops records written inside it. The flag is an
/// <see cref="AsyncLocal{T}"/>, so it is correct for concurrent requests and
/// across <c>await</c>.
/// </para>
/// </remarks>
public static class NexusDelivery
{
    private static readonly AsyncLocal<bool> Flag = new AsyncLocal<bool>();

    /// <summary>Whether the current execution flow is inside a Nexus delivery.</summary>
    public static bool IsDelivering => Flag.Value;

    /// <summary>
    /// Marks the current flow as delivering until the returned scope is disposed.
    /// </summary>
    public static IDisposable Enter()
    {
        if (Flag.Value)
        {
            // Already inside a delivery (a retry, or a nested call) — keep the
            // outer scope's lifetime rather than clearing the flag early.
            return NullScope.Instance;
        }

        Flag.Value = true;
        return new DeliveryScope();
    }

    private sealed class DeliveryScope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Flag.Value = false;
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new NullScope();

        public void Dispose()
        {
        }
    }
}
