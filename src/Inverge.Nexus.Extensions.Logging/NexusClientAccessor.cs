using System;
using System.Threading;
using Microsoft.Extensions.DependencyInjection;

namespace Inverge.Nexus.Extensions.Logging;

/// <summary>
/// Resolves <see cref="INexusClient"/> for the logger provider, without creating a
/// circular construction.
/// </summary>
/// <remarks>
/// <para>
/// The logging provider is built while the container is still wiring itself up, and
/// the client cannot be injected into it: constructing the client can emit log
/// records, which would re-enter the provider, which would resolve the client
/// again. So the client is resolved lazily on the first record, with a re-entrancy
/// guard that drops anything logged <em>during</em> that resolution.
/// </para>
/// <para>
/// Resolution failures are absorbed. A logging bridge that throws during startup
/// takes down an application that would otherwise have started fine, and the thing
/// it was trying to report would never be seen anyway.
/// </para>
/// </remarks>
internal sealed class NexusClientAccessor
{
    private static readonly AsyncLocal<bool> Resolving = new AsyncLocal<bool>();

    private readonly IServiceProvider _services;
    private INexusClient? _client;
    private int _failed;

    public NexusClientAccessor(IServiceProvider services) => _services = services;

    public INexusClient? TryGet()
    {
        var existing = Volatile.Read(ref _client);
        if (existing is not null)
        {
            return existing;
        }

        if (Volatile.Read(ref _failed) != 0 || Resolving.Value)
        {
            return null;
        }

        Resolving.Value = true;
        try
        {
            var resolved = _services.GetService<INexusClient>();
            if (resolved is null)
            {
                Interlocked.Exchange(ref _failed, 1);
                return null;
            }

            Interlocked.CompareExchange(ref _client, resolved, null);
            return Volatile.Read(ref _client);
        }
        catch (ObjectDisposedException)
        {
            // The container is shutting down; nothing left to log to.
            Interlocked.Exchange(ref _failed, 1);
            return null;
        }
        catch (InvalidOperationException)
        {
            // Resolution is not possible from here (scope or ordering). Give up
            // permanently rather than retrying on every single log record.
            Interlocked.Exchange(ref _failed, 1);
            return null;
        }
        finally
        {
            Resolving.Value = false;
        }
    }
}
