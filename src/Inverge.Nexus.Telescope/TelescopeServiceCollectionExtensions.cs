using System;
using System.Threading;
using System.Threading.Tasks;
using Inverge.Nexus.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Inverge.Nexus.Telescope;

/// <summary>Registers Nexus Telescope.</summary>
public static class TelescopeServiceCollectionExtensions
{
    /// <summary>
    /// Adds Nexus Telescope: a singleton <see cref="NexusTelescope"/> that starts
    /// with the host and flushes on shutdown, and an ILogger provider that records
    /// log lines into journeys. Calls <c>AddNexus()</c> if it has not been.
    /// For ASP.NET Core also add <c>app.UseNexusTelescope()</c>.
    /// </summary>
    public static IServiceCollection AddNexusTelescope(this IServiceCollection services, Action<TelescopeOptions>? configure = null, Action<NexusOptions>? configureNexus = null)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        services.AddNexus(configureNexus);
        var options = new TelescopeOptions();
        configure?.Invoke(options);
        services.TryAddSingleton(provider => new NexusTelescope(provider.GetRequiredService<INexusClient>(), options));
        services.AddSingleton<IHostedService, TelescopeHostedService>();
        services.AddSingleton<ILoggerProvider, TelescopeLoggerProvider>();
        return services;
    }
}

/// <summary>Starts Telescope with the host and flushes it on shutdown.</summary>
internal sealed class TelescopeHostedService : IHostedService
{
    private readonly NexusTelescope _telescope;

    public TelescopeHostedService(NexusTelescope telescope) => _telescope = telescope;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _telescope.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => _telescope.FlushAsync(cancellationToken);
}
