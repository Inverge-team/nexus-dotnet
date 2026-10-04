using System;
using System.Linq;
using System.Threading.Tasks;
using Inverge.Nexus.Extensions.Logging;
using Inverge.Nexus.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Inverge.Nexus.Tests.Unit;

public class LoggingBridgeTests
{
    [Fact]
    public async Task Log_records_become_nexus_log_lines()
    {
        var transport = new RecordingTransport(_ => RecordingTransport.Json(202, """{"written":1}"""));
        await using var client = TestClient.Create(transport);

        var provider = Provider(client);
        var logger = provider.CreateLogger("MyApp.Checkout");

        logger.LogInformation("Charging {OrderId} for {Total}", 42, 9.99);
        await client.FlushAsync();

        var request = transport.Single();
        Assert.Equal("/partner/logs", request.Path);

        var line = request.Object["logs"]![0]!;
        Assert.Equal("info", line["level"]!.ToString());
        Assert.Equal("Charging 42 for 9.99", line["message"]!.ToString());
        Assert.Equal("MyApp.Checkout", line["source"]!.ToString());

        // The template's arguments stay queryable rather than being baked into the
        // message string — the whole point of structured logging.
        Assert.Equal("42", line["context"]!["OrderId"]!.ToString());
        Assert.Equal("MyApp.Checkout", line["context"]!["logger"]!.ToString());
    }

    [Fact]
    public async Task A_record_carrying_an_exception_also_becomes_an_issue()
    {
        var transport = new RecordingTransport(_ => RecordingTransport.Json(202, """{"written":1}"""));
        await using var client = TestClient.Create(transport);

        var logger = Provider(client).CreateLogger("MyApp");

        try
        {
            throw new InvalidOperationException("kaboom");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Charge failed");
        }

        await client.FlushAsync();
        // The error report is fire-and-forget from inside a void Log call.
        await WaitForAsync(() => transport.All().Any(r => r.Path == "/partner/errors"));

        var error = transport.All().First(r => r.Path == "/partner/errors");
        Assert.Equal("InvalidOperationException", error.Field("type"));
        Assert.Equal("kaboom", error.Field("message"));
        Assert.Equal("error", error.Field("level"));
    }

    [Fact]
    public async Task The_sdks_own_category_is_never_forwarded()
    {
        // Forwarding a Nexus log line about a failed delivery would queue another
        // Nexus log line, forever.
        var transport = new RecordingTransport(_ => RecordingTransport.Json(202, """{"written":1}"""));
        await using var client = TestClient.Create(transport);

        var provider = Provider(client);
        provider.CreateLogger("Inverge.Nexus").LogWarning("delivery failed");
        provider.CreateLogger("Inverge.Nexus.Internal.BatchQueue").LogWarning("buffer full");

        await client.FlushAsync();

        Assert.Equal(0, transport.Count);
    }

    [Fact]
    public async Task Records_written_during_a_delivery_are_dropped()
    {
        // HttpClient logs every request at Information. Without this guard, a
        // globally registered bridge feeds the SDK its own traffic without end.
        var transport = new RecordingTransport(_ => RecordingTransport.Json(202, """{"written":1}"""));
        await using var client = TestClient.Create(transport);

        var logger = Provider(client).CreateLogger("System.Net.Http.HttpClient");

        using (Inverge.Nexus.Diagnostics.NexusDelivery.Enter())
        {
            logger.LogInformation("Sending HTTP request POST https://nexus.test/partner/logs");
        }

        await client.FlushAsync();
        Assert.Equal(0, transport.Count);

        // Outside the delivery window the same record is forwarded normally.
        logger.LogInformation("Sending HTTP request POST https://example.test/");
        await client.FlushAsync();
        Assert.Equal(1, transport.Count);
    }

    [Fact]
    public async Task Records_below_the_minimum_level_are_not_forwarded()
    {
        var transport = new RecordingTransport(_ => RecordingTransport.Json(202, """{"written":1}"""));
        await using var client = TestClient.Create(transport);

        var logger = Provider(client, options => options.MinimumLevel = LogLevel.Warning)
            .CreateLogger("MyApp");

        logger.LogInformation("chatty");
        logger.LogWarning("important");
        await client.FlushAsync();

        var lines = transport.Single().Object["logs"]!.AsArray();
        Assert.Single(lines);
        Assert.Equal("warn", lines[0]!["level"]!.ToString());
    }

    [Fact]
    public async Task Excluded_category_prefixes_are_not_forwarded()
    {
        var transport = new RecordingTransport(_ => RecordingTransport.Json(202, """{"written":1}"""));
        await using var client = TestClient.Create(transport);

        var logger = Provider(client, o => o.ExcludedCategoryPrefixes = new[] { "Microsoft." })
            .CreateLogger("Microsoft.AspNetCore.Hosting");

        logger.LogInformation("noise");
        await client.FlushAsync();

        Assert.Equal(0, transport.Count);
    }

    [Fact]
    public void A_missing_client_does_not_break_logging()
    {
        // The provider is built before the container can supply a client. It must
        // drop records rather than throw during startup.
        var services = new ServiceCollection().BuildServiceProvider();
        using var provider = new NexusLoggerProvider(services, Options.Create(new NexusLoggingOptions()));

        provider.CreateLogger("MyApp").LogError("no client yet");
    }

    private static NexusLoggerProvider Provider(
        INexusClient client, Action<NexusLoggingOptions>? configure = null)
    {
        var options = new NexusLoggingOptions { MinimumLevel = LogLevel.Trace };
        configure?.Invoke(options);

        var services = new ServiceCollection()
            .AddSingleton(client)
            .BuildServiceProvider();

        return new NexusLoggerProvider(services, Options.Create(options));
    }

    private static async Task WaitForAsync(Func<bool> condition, int timeoutMs = 2000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Fail("The condition was not met within the timeout.");
    }
}
