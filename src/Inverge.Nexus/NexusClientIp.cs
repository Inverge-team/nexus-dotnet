using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;

namespace Inverge.Nexus;

/// <summary>
/// The END USER's IP for the request being handled, so Messaging can apply the
/// project's per-IP rate limit without every call passing <c>ClientIp</c>.
/// </summary>
/// <remarks>
/// <para>
/// The messaging API is server-to-server: the address Nexus sees is your
/// server's. So the SDK forwards your end user's address as <c>X-Client-IP</c> —
/// on <see cref="Resources.VerificationsResource.StartAsync"/> and on sends with
/// <c>MessageClass = "otp"</c>. Other sends never carry it by themselves: a
/// message triggered by a webhook would otherwise be counted against the webhook
/// sender's address.
/// </para>
/// <para>
/// In ASP.NET Core, <c>UseNexus()</c> binds <c>HttpContext.Connection.RemoteIpAddress</c>
/// for each request. Behind a proxy or load balancer, call
/// <c>app.UseForwardedHeaders()</c> (with your proxies configured) BEFORE it, so
/// that is the user and not the proxy.
/// </para>
/// <para>
/// The address flows with the async context, so <c>Task.Run</c> and other work
/// started from the request keep it. A queued job (Hangfire, a <c>Channel</c>
/// read by a hosted service) runs outside the request: carry
/// <see cref="Current"/> in the job and wrap the work in <see cref="Use"/>.
/// </para>
/// <para>
/// Private, loopback and link-local addresses are never forwarded: one of those
/// is your proxy or load balancer, and counting every user against it would
/// rate-limit them all together.
/// </para>
/// </remarks>
public static class NexusClientIp
{
    private static readonly AsyncLocal<Holder?> Ambient = new();

    /// <summary>
    /// Whether the SDK forwards the address automatically. Default: on, unless
    /// <c>NEXUS_FORWARD_CLIENT_IP</c> is <c>false</c>.
    /// </summary>
    public static bool Enabled { get; set; } = FromEnvironment();

    /// <summary>The end user's address for the current request or job, or null.</summary>
    public static string? Current => Ambient.Value?.Ip;

    /// <summary>
    /// Bind <paramref name="ip"/> as the end user's address until the returned
    /// scope is disposed — <c>null</c> for none (e.g. in a webhook handler):
    /// <code>using (NexusClientIp.Use(job.ClientIp)) { await nexus.Messaging.Verifications.StartAsync(…); }</code>
    /// </summary>
    public static IDisposable Use(string? ip)
    {
        var previous = Ambient.Value;
        Ambient.Value = new Holder(PublicIp(ip));
        return new Scope(previous);
    }

    /// <summary>A public IPv4/IPv6 address (IPv4-mapped IPv6 unwrapped), or null.</summary>
    public static string? PublicIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || !IPAddress.TryParse(ip!.Trim(), out var address))
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return null;
        }

        var b = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var isPrivate = b[0] == 0 || b[0] == 10 || b[0] >= 224
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127); // carrier-grade NAT
            return isPrivate ? null : address.ToString();
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var isPrivate = address.Equals(IPAddress.IPv6Any)
                || address.IsIPv6LinkLocal
                || address.IsIPv6SiteLocal
                || address.IsIPv6Multicast
                || (b[0] & 0xFE) == 0xFC; // unique-local fc00::/7
            return isPrivate ? null : address.ToString();
        }

        return null;
    }

    /// <summary>Headers to add: none when the caller passed an address (it goes in the body) or forwarding is off.</summary>
    internal static IReadOnlyDictionary<string, string> Headers(string? explicitIp)
    {
        if (explicitIp is not null || !Enabled)
        {
            return Empty;
        }

        var ip = Current;
        return ip is null ? Empty : new Dictionary<string, string> { ["X-Client-IP"] = ip };
    }

    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    private static bool FromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("NEXUS_FORWARD_CLIENT_IP")?.Trim().ToLowerInvariant();
        return value is not ("0" or "false" or "no" or "off");
    }

    private sealed class Holder
    {
        public Holder(string? ip) => Ip = ip;

        public string? Ip { get; }
    }

    private sealed class Scope : IDisposable
    {
        private readonly Holder? _previous;
        private bool _disposed;

        public Scope(Holder? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Ambient.Value = _previous;
        }
    }
}
