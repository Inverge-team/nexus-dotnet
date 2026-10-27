using System;
using System.Collections.Generic;

namespace Inverge.Nexus;

/// <summary>
/// The Nexus products that can be switched off without removing the code that
/// uses them. <b>Every service is on by default.</b>
/// </summary>
/// <remarks>
/// <para>
/// From the environment (read by <see cref="NexusOptions.FromEnvironment"/>):
/// <c>NEXUS_LIVE_ACTIVITIES_ENABLED=false</c> switches only Live Activities off;
/// <c>NEXUS_ENABLED=false</c> switches everything off. <c>false</c>, <c>0</c>,
/// <c>off</c> and <c>no</c> mean off; anything else, or unset, means on.
/// </para>
/// <para>
/// In code: <c>options.Services[NexusServices.Push] = false</c> or
/// <c>options.Enabled = false</c>. Off wins — a service is on only if neither
/// the code nor the environment turns it off, so operations can always switch one
/// off with an environment variable.
/// </para>
/// <para>
/// A switched-off service makes no request and never throws: calls return their
/// neutral result (flags → your fallback, Remote Config → your default, counts
/// → zero), and events / logs are not even buffered.
/// </para>
/// </remarks>
public static class NexusServices
{
    /// <summary>Realtime rooms and emits.</summary>
    public const string Realtime = "realtime";

    /// <summary>Product analytics events.</summary>
    public const string Events = "events";

    /// <summary>Error capture.</summary>
    public const string Errors = "errors";

    /// <summary>Logs.</summary>
    public const string Logs = "logs";

    /// <summary>Sessions.</summary>
    public const string Sessions = "sessions";

    /// <summary>Feature flags.</summary>
    public const string Flags = "flags";

    /// <summary>Remote Config.</summary>
    public const string RemoteConfig = "remote_config";

    /// <summary>Deep links.</summary>
    public const string Links = "links";

    /// <summary>Surveys.</summary>
    public const string Surveys = "surveys";

    /// <summary>Push notifications.</summary>
    public const string Push = "push";

    /// <summary>In-app messages.</summary>
    public const string InApp = "in_app";

    /// <summary>Live Activities.</summary>
    public const string LiveActivities = "live_activities";

    /// <summary>Session replay.</summary>
    public const string Replay = "replay";

    /// <summary>Voice.</summary>
    public const string Voice = "voice";

    /// <summary>Telescope — request-journey tracing.</summary>
    public const string Telescope = "telescope";

    /// <summary>Shield — app &amp; device integrity verification.</summary>
    public const string Shield = "shield";

    /// <summary>The master switch: <c>NEXUS_ENABLED</c>.</summary>
    public const string AllEnvironmentVariable = "NEXUS_ENABLED";

    /// <summary>Service → the environment variable that switches it.</summary>
    public static IReadOnlyDictionary<string, string> EnvironmentVariables { get; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [Realtime] = "NEXUS_REALTIME_ENABLED",
            [Events] = "NEXUS_EVENTS_ENABLED",
            [Errors] = "NEXUS_ERRORS_ENABLED",
            [Logs] = "NEXUS_LOGS_ENABLED",
            [Sessions] = "NEXUS_SESSIONS_ENABLED",
            [Flags] = "NEXUS_FLAGS_ENABLED",
            [RemoteConfig] = "NEXUS_REMOTE_CONFIG_ENABLED",
            [Links] = "NEXUS_LINKS_ENABLED",
            [Surveys] = "NEXUS_SURVEYS_ENABLED",
            [Push] = "NEXUS_PUSH_ENABLED",
            [InApp] = "NEXUS_IN_APP_ENABLED",
            [LiveActivities] = "NEXUS_LIVE_ACTIVITIES_ENABLED",
            [Replay] = "NEXUS_REPLAY_ENABLED",
            [Voice] = "NEXUS_VOICE_ENABLED",
            [Telescope] = "NEXUS_TELESCOPE_ENABLED",
            [Shield] = "NEXUS_SHIELD_ENABLED",
        };

    // The other Nexus SDKs' spellings, so one shared config can drive any of them.
    private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["remoteConfig"] = RemoteConfig,
        ["inApp"] = InApp,
        ["liveActivities"] = LiveActivities,
    };

    private static readonly string[] OffValues = { "false", "0", "off", "no" };

    // `/partner/<segment>` → service.
    private static readonly Dictionary<string, string> Paths = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["rooms"] = Realtime,
        ["events"] = Events,
        ["errors"] = Errors,
        ["logs"] = Logs,
        ["sessions"] = Sessions,
        ["flags"] = Flags,
        ["remote-config"] = RemoteConfig,
        ["links"] = Links,
        ["surveys"] = Surveys,
        ["push"] = Push,
        ["inapp"] = InApp,
        ["live-activities"] = LiveActivities,
        ["replay"] = Replay,
        ["voice"] = Voice,
        ["telescope"] = Telescope,
        ["shield"] = Shield,
    };

    /// <summary>The canonical service name (accepts <c>liveActivities</c> etc.), or null if unknown.</summary>
    public static string? Normalize(string? service)
    {
        if (string.IsNullOrWhiteSpace(service))
        {
            return null;
        }

        var name = service!.Trim();
        if (Aliases.TryGetValue(name, out var alias))
        {
            return alias;
        }

        foreach (var known in EnvironmentVariables.Keys)
        {
            if (string.Equals(known, name, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        return null;
    }

    /// <summary>The service a <c>/partner/...</c> path belongs to, or null for unswitched paths.</summary>
    public static string? ForPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var p = path!.TrimStart('/');
        if (p.StartsWith("partner/", StringComparison.Ordinal))
        {
            p = p.Substring("partner/".Length);
        }

        var slash = p.IndexOf('/');
        var segment = slash < 0 ? p : p.Substring(0, slash);
        return Paths.TryGetValue(segment, out var service) ? service : null;
    }

    /// <summary><c>false</c> / <c>0</c> / <c>off</c> / <c>no</c> = off; anything else, or unset, = on.</summary>
    public static bool IsOn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var v = value!.Trim();
        foreach (var off in OffValues)
        {
            if (string.Equals(v, off, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
