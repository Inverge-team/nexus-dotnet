using System;
using System.Collections.Generic;
using Xunit;

namespace Inverge.Nexus.Tests.Unit;

public class OptionsTests
{
    [Fact]
    public void A_missing_api_key_fails_with_a_clear_message()
    {
        var options = new NexusOptions();
        var exception = Assert.Throws<NexusConfigurationException>(options.Validate);
        Assert.Contains("NEXUS_API_KEY", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_relative_base_url_is_rejected()
    {
        var options = new NexusOptions { ApiKey = "nxs_x", BaseUrl = "nexus.test" };
        Assert.Throws<NexusConfigurationException>(options.Validate);
    }

    [Fact]
    public void A_trailing_slash_is_trimmed_from_the_origin()
    {
        var options = new NexusOptions { ApiKey = "nxs_x", BaseUrl = "https://nexus.test/" };
        Assert.Equal("https://nexus.test", options.BaseUrl);
        Assert.Equal("https://nexus.test/partner/events", options.UrlFor("/partner/events"));
    }

    [Fact]
    public void Explicit_options_layer_over_the_environment()
    {
        // The case this protects: an API key assigned in code used to make the SDK
        // ignore NEXUS_BASE_URL and send a staging deploy's traffic to production.
        var environment = new Dictionary<string, string?>
        {
            ["NEXUS_API_KEY"] = "nxs_from_env",
            ["NEXUS_BASE_URL"] = "https://local.test",
            ["NEXUS_BATCH"] = "false",
            ["NEXUS_TIMEOUT"] = "2.5",
            ["NEXUS_MAX_RETRIES"] = "7",
        };

        var options = new NexusOptions();
        options.ApplyEnvironment(name => environment.TryGetValue(name, out var value) ? value : null);
        options.ApiKey = "nxs_from_code";

        Assert.Equal("nxs_from_code", options.ApiKey);
        Assert.Equal("https://local.test", options.BaseUrl);
        Assert.False(options.Batch);
        Assert.Equal(TimeSpan.FromSeconds(2.5), options.Timeout);
        Assert.Equal(7, options.MaxRetries);
    }

    [Fact]
    public void An_unparseable_environment_value_leaves_the_default_alone()
    {
        var options = new NexusOptions { ApiKey = "nxs_x" };
        options.ApplyEnvironment(name => name == "NEXUS_MAX_RETRIES" ? "not-a-number" : null);

        Assert.Equal(2, options.MaxRetries);
    }

    [Fact]
    public void Cloning_isolates_the_running_client_from_later_mutation()
    {
        var options = new NexusOptions { ApiKey = "nxs_x", BaseUrl = "https://a.test" };
        options.DefaultHeaders["X-Tenant"] = "one";
        options.DefaultProperties["tier"] = "free";

        var clone = options.Clone();
        options.BaseUrl = "https://b.test";
        options.DefaultHeaders["X-Tenant"] = "two";
        options.DefaultProperties["tier"] = "pro";

        Assert.Equal("https://a.test", clone.BaseUrl);
        Assert.Equal("one", clone.DefaultHeaders["X-Tenant"]);
        Assert.Equal("free", clone.DefaultProperties["tier"]);
    }

    [Fact]
    public void The_realtime_origin_falls_back_to_the_api_origin()
    {
        var options = new NexusOptions { ApiKey = "nxs_x", BaseUrl = "https://a.test" };
        Assert.Equal("https://a.test", options.SocketBase);

        options.RealtimeUrl = "wss://rt.test/";
        Assert.Equal("wss://rt.test", options.SocketBase);
    }

    [Fact]
    public void The_version_is_reported_in_the_user_agent()
    {
        Assert.StartsWith("Inverge.Nexus.NET/", NexusVersion.UserAgent, StringComparison.Ordinal);
        Assert.DoesNotContain("+", NexusVersion.Current, StringComparison.Ordinal);
    }
}
