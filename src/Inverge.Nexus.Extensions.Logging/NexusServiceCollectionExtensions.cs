using System;
using System.Net.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Inverge.Nexus.Extensions.Logging;

/// <summary>Registers the Nexus client in a dependency-injection container.</summary>
public static class NexusServiceCollectionExtensions
{
    /// <summary>The configuration section bound by default.</summary>
    public const string DefaultConfigurationSection = "Nexus";

    /// <summary>The named <see cref="HttpClient"/> the SDK sends through.</summary>
    public const string HttpClientName = "Inverge.Nexus";

    /// <summary>
    /// Registers <see cref="INexusClient"/> as a singleton.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Options are read from the <c>Nexus</c> configuration section first, then the
    /// <c>NEXUS_*</c> environment, then <paramref name="configure"/>. Code wins over
    /// the environment, and the environment wins over appsettings — so a container
    /// can repoint or re-key a deploy without a rebuild.
    /// </para>
    /// <para>
    /// The client sends through a named <c>IHttpClientFactory</c> client, so the
    /// application's own handlers, resilience policies and connection lifetime apply.
    /// A hosted service flushes buffered telemetry on shutdown.
    /// </para>
    /// </remarks>
    /// <param name="services">The container.</param>
    /// <param name="configure">Options applied last, over configuration and environment.</param>
    /// <param name="configurationSection">
    /// The configuration section to bind. Pass <c>null</c> to bind nothing.
    /// </param>
    public static IServiceCollection AddNexus(
        this IServiceCollection services,
        Action<NexusOptions>? configure = null,
        string? configurationSection = DefaultConfigurationSection)
    {
        if (services is null)
        {
            throw new ArgumentNullException(nameof(services));
        }

        services.AddOptions();
        services.AddHttpClient(HttpClientName);

        services.AddSingleton<IConfigureOptions<NexusOptions>>(provider =>
            new ConfigureNexusOptions(
                provider.GetService<IConfiguration>(), configurationSection, configure));

        services.TryAddSingleton<INexusClient>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<NexusOptions>>().Value;

            // Route the SDK's own diagnostics into ILogger. The provider in this
            // package refuses to forward the "Inverge.Nexus" category back to Nexus,
            // so this cannot feed itself.
            var logger = provider.GetService<ILoggerFactory>()?.CreateLogger("Inverge.Nexus");
            if (logger is not null && options.DiagnosticSink is null)
            {
                options.DiagnosticSink = NexusDiagnosticsBridge.For(logger);
            }

            var httpClient = provider
                .GetRequiredService<IHttpClientFactory>()
                .CreateClient(HttpClientName);

            return new NexusClient(options, httpClient);
        });

        services.AddSingleton<IHostedService, NexusFlushService>();

        return services;
    }

    /// <summary>
    /// Registers the client and forwards <c>ILogger</c> records to Nexus in one call.
    /// </summary>
    public static IServiceCollection AddNexus(
        this IServiceCollection services,
        Action<NexusOptions>? configure,
        Action<NexusLoggingOptions>? configureLogging,
        string? configurationSection = DefaultConfigurationSection)
    {
        services.AddNexus(configure, configurationSection);
        services.AddLogging(logging => logging.AddNexus(configureLogging));
        return services;
    }

    /// <summary>
    /// Forwards <c>ILogger</c> records to the Nexus logs product, and records
    /// carrying an exception to error monitoring.
    /// </summary>
    /// <remarks>
    /// Call <see cref="AddNexus(IServiceCollection, Action{NexusOptions}, string)"/>
    /// as well — this adds the bridge, not the client it writes through.
    /// </remarks>
    public static ILoggingBuilder AddNexus(
        this ILoggingBuilder builder, Action<NexusLoggingOptions>? configure = null)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder));
        }

        builder.Services.AddOptions();
        builder.Services.TryAddEnumerable(
            ServiceDescriptor.Singleton<ILoggerProvider, NexusLoggerProvider>());

        if (configure is not null)
        {
            builder.Services.Configure(configure);
        }

        return builder;
    }

    private sealed class ConfigureNexusOptions : IConfigureOptions<NexusOptions>
    {
        private readonly IConfiguration? _configuration;
        private readonly string? _section;
        private readonly Action<NexusOptions>? _configure;

        public ConfigureNexusOptions(
            IConfiguration? configuration, string? section, Action<NexusOptions>? configure)
        {
            _configuration = configuration;
            _section = section;
            _configure = configure;
        }

        public void Configure(NexusOptions options)
        {
            // Lowest precedence: appsettings and friends.
            if (_configuration is not null && !string.IsNullOrEmpty(_section))
            {
                NexusOptionsBinder.Bind(_configuration.GetSection(_section!), options);
            }

            // Then the NEXUS_* environment, which is how a deployed container is
            // repointed without touching its image.
            options.ApplyEnvironment();

            // Highest precedence: what the application said in code.
            _configure?.Invoke(options);
        }
    }
}
