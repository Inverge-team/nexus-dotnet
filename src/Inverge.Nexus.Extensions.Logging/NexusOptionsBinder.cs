using System;
using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Inverge.Nexus.Extensions.Logging;

/// <summary>
/// Binds a configuration section onto <see cref="NexusOptions"/>, key by key.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written rather than delegating to <c>ConfigurationBinder.Bind</c>, for two
/// reasons. The binder needs reflection over the options type, which neither trims
/// nor native-AOT-compiles cleanly; and it would read the <see cref="TimeSpan"/>
/// properties in <c>"00:00:10"</c> format, whereas every other way of configuring
/// this SDK expresses a timeout as a number of seconds. Binding by hand keeps
/// <c>"Timeout": 10</c> meaning ten seconds, the same as <c>NEXUS_TIMEOUT=10</c>.
/// </para>
/// <para>
/// A key that is absent leaves the existing value alone, which is what makes the
/// layering in <c>AddNexus</c> work.
/// </para>
/// </remarks>
internal static class NexusOptionsBinder
{
    public static void Bind(IConfiguration section, NexusOptions options)
    {
        String(section, "ApiKey", value => options.ApiKey = value);
        String(section, "BaseUrl", value => options.BaseUrl = value);
        String(section, "RealtimeUrl", value => options.RealtimeUrl = value);
        String(section, "AppVersion", value => options.AppVersion = value);
        String(section, "Release", value => options.Release = value);
        String(section, "OsType", value => options.OsType = value);
        String(section, "OsVersion", value => options.OsVersion = value);
        String(section, "UserAgent", value => options.UserAgent = value);

        Seconds(section, "Timeout", value => options.Timeout = value);
        Seconds(section, "RetryBackoff", value => options.RetryBackoff = value);
        Seconds(section, "FlushInterval", value => options.FlushInterval = value);
        Seconds(section, "RemoteConfigCacheTtl", value => options.RemoteConfigCacheTtl = value);

        Integer(section, "MaxRetries", value => options.MaxRetries = value);
        Integer(section, "MaxBatch", value => options.MaxBatch = value);
        Integer(section, "MaxQueue", value => options.MaxQueue = value);

        Boolean(section, "Batch", value => options.Batch = value);
        Boolean(section, "Silent", value => options.Silent = value);
        Boolean(section, "Debug", value => options.Debug = value);

        foreach (var header in section.GetSection("DefaultHeaders").GetChildren())
        {
            if (header.Value is not null)
            {
                options.DefaultHeaders[header.Key] = header.Value;
            }
        }

        foreach (var property in section.GetSection("DefaultProperties").GetChildren())
        {
            if (property.Value is not null)
            {
                options.DefaultProperties[property.Key] = property.Value;
            }
        }
    }

    private static void String(IConfiguration section, string key, Action<string> apply)
    {
        var value = section[key];
        if (!string.IsNullOrWhiteSpace(value))
        {
            apply(value!);
        }
    }

    private static void Integer(IConfiguration section, string key, Action<int> apply)
    {
        var value = section[key];
        if (!string.IsNullOrWhiteSpace(value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            apply(parsed);
        }
    }

    private static void Seconds(IConfiguration section, string key, Action<TimeSpan> apply)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            && seconds > 0)
        {
            apply(TimeSpan.FromSeconds(seconds));
            return;
        }

        // Still accept the "00:00:10" spelling, so a config written against the
        // default binder's conventions does not silently do nothing.
        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out var span) && span > TimeSpan.Zero)
        {
            apply(span);
        }
    }

    private static void Boolean(IConfiguration section, string key, Action<bool> apply)
    {
        var value = section[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        switch (value!.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "yes":
            case "on":
                apply(true);
                break;
            case "0":
            case "false":
            case "no":
            case "off":
                apply(false);
                break;
        }
    }
}
