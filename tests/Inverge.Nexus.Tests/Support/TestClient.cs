using System;
using Inverge.Nexus.Transport;

namespace Inverge.Nexus.Tests.Support;

/// <summary>Builds clients wired to a recording transport.</summary>
public static class TestClient
{
    public const string ApiKey = "nxs_abcdef123456_secret";
    public const string BaseUrl = "https://nexus.test";

    public static NexusOptions Options(Action<NexusOptions>? configure = null)
    {
        var options = new NexusOptions
        {
            ApiKey = ApiKey,
            BaseUrl = BaseUrl,
            // Deterministic by default: tests assert on what a call sent, not on
            // whether a background loop got round to sending it.
            Batch = false,
            MaxRetries = 0,
            Timeout = TimeSpan.FromSeconds(5),
        };

        configure?.Invoke(options);
        return options;
    }

    public static NexusClient Create(
        RecordingTransport transport, Action<NexusOptions>? configure = null)
        => new(Options(configure), (INexusTransport)transport);

    public static (NexusClient Client, RecordingTransport Transport) Create(
        Func<RecordedRequest, NexusTransportResponse>? respond = null,
        Action<NexusOptions>? configure = null)
    {
        var transport = new RecordingTransport(respond);
        return (Create(transport, configure), transport);
    }
}
