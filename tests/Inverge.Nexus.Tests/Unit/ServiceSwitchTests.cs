using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Inverge.Nexus.Tests.Support;
using Xunit;

namespace Inverge.Nexus.Tests.Unit;

/// <summary>
/// Per-service switches: every service on by default; NEXUS_&lt;SERVICE&gt;_ENABLED /
/// NexusOptions.Services / Enabled switch them off; off wins; a switched-off
/// service makes no request, never throws, and returns its neutral result.
/// </summary>
public class ServiceSwitchTests
{
    private static Func<string, string?> Env(Dictionary<string, string> vars)
        => name => vars.TryGetValue(name, out var value) ? value : null;

    [Fact]
    public void Every_service_is_on_by_default()
    {
        var options = TestClient.Options();
        options.ApplyEnvironment(Env(new Dictionary<string, string>()));

        foreach (var service in NexusServices.EnvironmentVariables.Keys)
        {
            Assert.True(options.IsServiceEnabled(service), service);
        }
    }

    [Fact]
    public void One_environment_variable_switches_one_service_off()
    {
        var options = TestClient.Options();
        options.ApplyEnvironment(Env(new Dictionary<string, string>
        {
            ["NEXUS_LIVE_ACTIVITIES_ENABLED"] = "false",
            ["NEXUS_PUSH_ENABLED"] = "off",
            ["NEXUS_EVENTS_ENABLED"] = "true",
        }));

        Assert.False(options.IsServiceEnabled(NexusServices.LiveActivities));
        Assert.False(options.IsServiceEnabled(NexusServices.Push));
        Assert.True(options.IsServiceEnabled(NexusServices.Events));
        Assert.True(options.IsServiceEnabled(NexusServices.Realtime));
        Assert.Equal("NEXUS_PUSH_ENABLED=false", options.ServiceOffReason(NexusServices.Push));
    }

    [Fact]
    public void The_master_switch_turns_everything_off()
    {
        var options = TestClient.Options();
        options.ApplyEnvironment(Env(new Dictionary<string, string> { ["NEXUS_ENABLED"] = "0" }));

        Assert.False(options.IsServiceEnabled(NexusServices.Push));
        Assert.False(options.IsServiceEnabled(NexusServices.Voice));
    }

    [Fact]
    public void Off_wins_code_cannot_re_enable_what_the_environment_switched_off()
    {
        var options = TestClient.Options();
        options.ApplyEnvironment(Env(new Dictionary<string, string> { ["NEXUS_PUSH_ENABLED"] = "false" }));
        options.Services[NexusServices.Push] = true; // configure() runs after the environment

        Assert.False(options.IsServiceEnabled(NexusServices.Push));
    }

    [Fact]
    public void Code_switches_accept_the_other_sdks_names_and_survive_clone()
    {
        var options = TestClient.Options(o => o.Services["liveActivities"] = false);
        var copy = options.Clone();

        Assert.False(copy.IsServiceEnabled(NexusServices.LiveActivities));
        Assert.True(copy.IsServiceEnabled(NexusServices.Push));
    }

    [Fact]
    public void An_unknown_service_name_fails_validation()
    {
        var options = TestClient.Options(o => o.Services["puhs"] = false);
        Assert.Throws<NexusConfigurationException>(() => options.Validate());
    }

    [Fact]
    public void Paths_map_to_services()
    {
        Assert.Equal(NexusServices.LiveActivities, NexusServices.ForPath("/partner/live-activities/o1/update"));
        Assert.Equal(NexusServices.Realtime, NexusServices.ForPath("/partner/rooms/orders%3A1/emit"));
        Assert.Equal(NexusServices.RemoteConfig, NexusServices.ForPath("/partner/remote-config/fetch"));
        Assert.Null(NexusServices.ForPath("/partner/nothing"));
    }

    [Fact]
    public async Task A_switched_off_service_makes_no_request_and_returns_neutral_results()
    {
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(500, """{"message":"would throw if called"}"""),
            o =>
            {
                o.Services[NexusServices.LiveActivities] = false;
                o.Services[NexusServices.Push] = false;
                o.Services[NexusServices.Flags] = false;
                o.Services[NexusServices.RemoteConfig] = false;
                o.Services[NexusServices.Events] = false;
            });
        await using var _ = client;
        Assert.False(client.Options.Silent); // gating must not rely on silent mode

        var started = await client.LiveActivities.Template("order_tracking", "o1").Step("pending").ToUser("u").StartAsync();
        Assert.Equal(0, started.Total);
        await client.LiveActivities.Activity("o1").Step("arrived").UpdateAsync();
        var push = await client.Push.Notification().Title("Hi").ToUser("u").SendAsync();
        Assert.NotNull(push);
        Assert.True(await client.Flags.IsEnabledAsync("new_checkout", "u", fallback: true)); // → your fallback
        Assert.Equal("fallback", await client.RemoteConfig.GetStringAsync("banner", "fallback"));
        client.Events.Track("x");

        Assert.Equal(0, transport.Count);
        Assert.False(client.IsServiceEnabled(NexusServices.Push));
    }

    [Fact]
    public async Task Other_services_still_work()
    {
        var (client, transport) = TestClient.Create(
            _ => RecordingTransport.Json(200, """{"written":1}"""),
            o =>
            {
                o.Services[NexusServices.Push] = false;
                o.Batch = true; // deterministic: FlushAsync awaits the buffered send
            });
        await using var _ = client;

        client.Events.Track("order_placed");
        await client.FlushAsync();

        Assert.Equal(1, transport.Count);
        Assert.Equal("/partner/events", transport.Last.Path);
    }
}
