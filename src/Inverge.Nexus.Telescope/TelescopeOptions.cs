using System;
using System.Collections.Generic;

namespace Inverge.Nexus.Telescope;

/// <summary>What Nexus Telescope records. Most of it can also be changed from the console without a deploy.</summary>
public sealed class TelescopeOptions
{
    /// <summary>Master switch (also <c>NEXUS_TELESCOPE_ENABLED=false</c>, or pausing it in the console).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The service name shown in traces. Default: <c>NEXUS_SERVICE_NAME</c>, else the entry assembly name.</summary>
    public string? Service { get; set; }

    /// <summary>Release / version shown with each journey.</summary>
    public string? Release { get; set; }

    /// <summary>Fraction of journeys kept, 0..1. <c>null</c> = the console's sample rate. Failed, slow and monitored journeys are always kept.</summary>
    public double? SampleRate { get; set; }

    /// <summary>Request paths never recorded (globs, <c>*</c> = anything).</summary>
    public IList<string> IgnorePaths { get; } = new List<string> { "/health*", "/healthz", "/favicon.ico", "/swagger*", "/_framework/*" };

    /// <summary>Entry types never recorded (<c>log</c>, <c>cache</c>, …).</summary>
    public IList<string> IgnoreTypes { get; } = new List<string>();

    /// <summary>Extra keys redacted from headers and payloads (passwords, tokens and cookies always are).</summary>
    public IList<string> RedactKeys { get; } = new List<string>();

    /// <summary>ActivitySource names never turned into entries.</summary>
    public IList<string> IgnoreActivitySources { get; } = new List<string>();

    /// <summary>Record request/response bodies (JSON is parsed). Default <c>true</c>.</summary>
    public bool CaptureBodies { get; set; } = true;

    /// <summary>Largest body recorded, in KB. Default 64.</summary>
    public int BodySizeLimitKb { get; set; } = 64;

    /// <summary>Entries kept per journey; the rest are counted. Default 2000.</summary>
    public int MaxEntries { get; set; } = 2000;

    /// <summary>Log records at or above this level become entries. Default Information.</summary>
    public Microsoft.Extensions.Logging.LogLevel MinimumLogLevel { get; set; } = Microsoft.Extensions.Logging.LogLevel.Information;

    /// <summary>How often finished journeys are shipped. Default 2 s.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(2);
}
