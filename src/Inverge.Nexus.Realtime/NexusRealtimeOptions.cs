using System;
using System.Collections.Generic;

namespace Inverge.Nexus.Realtime;

/// <summary>How the realtime connection behaves.</summary>
public sealed class NexusRealtimeOptions
{
    /// <summary>
    /// The Socket.IO origin. Defaults to the client's <c>RealtimeUrl</c>, or its API
    /// origin.
    /// </summary>
    public string? Url { get; set; }

    /// <summary>
    /// The identity to connect as, carried in the handshake.
    /// </summary>
    /// <remarks>
    /// Optional, and only used for the user-journey correlation of realtime traffic.
    /// Room membership does not depend on it.
    /// </remarks>
    public string? DistinctId { get; set; }

    /// <summary>The session key to correlate this connection with.</summary>
    public string? SessionKey { get; set; }

    /// <summary>The device key to correlate this connection with.</summary>
    public string? DeviceKey { get; set; }

    /// <summary>Reconnect automatically after a dropped connection.</summary>
    public bool Reconnection { get; set; } = true;

    /// <summary>How many reconnect attempts before giving up.</summary>
    public int ReconnectionAttempts { get; set; } = 10;

    /// <summary>Longest delay between reconnect attempts.</summary>
    public TimeSpan ReconnectionDelayMax { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long to wait for the handshake.</summary>
    public TimeSpan ConnectionTimeout { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>How long to wait for a server acknowledgement of join, leave or emit.</summary>
    public TimeSpan AckTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Connect over WebSocket immediately rather than upgrading from long-polling.
    /// </summary>
    /// <remarks>
    /// On by default: a server-side listener has no reason to start on polling, and
    /// skipping the upgrade removes a round trip and a class of
    /// proxy-breaks-the-upgrade failure.
    /// </remarks>
    public bool PreferWebSocket { get; set; } = true;

    /// <summary>
    /// Stop reconnecting when the server refuses the handshake with
    /// <c>billing_suspended</c>.
    /// </summary>
    /// <remarks>
    /// On by default, and worth leaving on. A suspension is resolved by paying an
    /// invoice, not by retrying: a client that keeps reconnecting hammers the server
    /// every few seconds for as long as the suspension lasts, and buries the one
    /// error message that would have explained it.
    /// </remarks>
    public bool StopOnBillingSuspended { get; set; } = true;

    /// <summary>Extra handshake headers.</summary>
    public IDictionary<string, string> ExtraHeaders { get; }
        = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
