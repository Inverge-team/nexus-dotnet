using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Inverge.Nexus.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Inverge.Nexus.Tests.Unit;

public class DependencyInjectionTests
{
    [Fact]
    public void The_client_is_registered_as_a_singleton()
    {
        using var provider = Build(options => options.ApiKey = "nxs_x");

        var first = provider.GetRequiredService<INexusClient>();
        var second = provider.GetRequiredService<INexusClient>();

        // A client per request would leak a background buffer per request and lose
        // the batching that makes telemetry cheap.
        Assert.Same(first, second);
    }

    [Fact]
    public void Configuration_is_bound_from_the_nexus_section()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nexus:ApiKey"] = "nxs_from_config",
                ["Nexus:BaseUrl"] = "https://config.test",
                ["Nexus:Batch"] = "false",
                ["Nexus:MaxBatch"] = "25",
                ["Nexus:Timeout"] = "7",
                ["Nexus:DefaultHeaders:X-Tenant"] = "acme",
            })
            .Build();

        using var provider = Build(null, configuration);
        var options = provider.GetRequiredService<INexusClient>().Options;

        Assert.Equal("nxs_from_config", options.ApiKey);
        Assert.Equal("https://config.test", options.BaseUrl);
        Assert.False(options.Batch);
        Assert.Equal(25, options.MaxBatch);
        // A timeout written as a number means seconds, matching NEXUS_TIMEOUT.
        Assert.Equal(TimeSpan.FromSeconds(7), options.Timeout);
        Assert.Equal("acme", options.DefaultHeaders["X-Tenant"]);
    }

    [Fact]
    public void A_timespan_spelled_the_binder_way_is_also_accepted()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nexus:ApiKey"] = "nxs_x",
                ["Nexus:Timeout"] = "00:00:12",
            })
            .Build();

        using var provider = Build(null, configuration);

        Assert.Equal(TimeSpan.FromSeconds(12), provider.GetRequiredService<INexusClient>().Options.Timeout);
    }

    [Fact]
    public void Code_wins_over_configuration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Nexus:ApiKey"] = "nxs_from_config",
                ["Nexus:BaseUrl"] = "https://config.test",
            })
            .Build();

        using var provider = Build(options => options.BaseUrl = "https://code.test", configuration);
        var options = provider.GetRequiredService<INexusClient>().Options;

        Assert.Equal("https://code.test", options.BaseUrl);
        Assert.Equal("nxs_from_config", options.ApiKey);
    }

    [Fact]
    public void A_missing_key_fails_at_resolution_with_a_clear_message()
    {
        using var provider = Build(options => options.BaseUrl = "https://x.test");

        var exception = Assert.Throws<NexusConfigurationException>(
            () => provider.GetRequiredService<INexusClient>());

        Assert.Contains("NEXUS_API_KEY", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_sdks_diagnostics_are_routed_to_ILogger()
    {
        using var provider = Build(options => options.ApiKey = "nxs_x");

        Assert.NotNull(provider.GetRequiredService<INexusClient>().Options.DiagnosticSink);
    }

    [Fact]
    public async Task A_hosted_service_flushes_on_shutdown()
    {
        using var provider = Build(options =>
        {
            options.ApiKey = "nxs_x";
            options.BaseUrl = "https://nexus.test";
        });

        var hosted = provider.GetServices<IHostedService>();
        var flusher = Assert.Single(hosted);

        await flusher.StartAsync(default);
        // Nothing is buffered, so this proves the wiring rather than the delivery.
        await flusher.StopAsync(default);
    }

    [Fact]
    public void The_logging_provider_can_be_registered_alongside_the_client()
    {
        using var provider = Build(options => options.ApiKey = "nxs_x", null, logging: true);

        var providers = provider.GetServices<ILoggerProvider>();
        Assert.Contains(providers, p => p is NexusLoggerProvider);
    }

    private static ServiceProvider Build(
        Action<NexusOptions>? configure,
        IConfiguration? configuration = null,
        bool logging = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        if (configuration is not null)
        {
            services.AddSingleton(configuration);
        }

        if (logging)
        {
            services.AddNexus(configure, _ => { });
        }
        else
        {
            services.AddNexus(configure);
        }

        return services.BuildServiceProvider();
    }
}
