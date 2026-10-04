using System.Reflection;

namespace Inverge.Nexus;

/// <summary>The SDK's own version, as reported to the API.</summary>
public static class NexusVersion
{
    private static readonly string Resolved = ReadVersion();

    /// <summary>The package version, e.g. <c>1.0.0</c>.</summary>
    public static string Current => Resolved;

    /// <summary>The <c>User-Agent</c> the SDK sends.</summary>
    public static string UserAgent => "Inverge.Nexus.NET/" + Resolved;

    private static string ReadVersion()
    {
        var assembly = typeof(NexusVersion).GetTypeInfo().Assembly;

        // InformationalVersion carries the NuGet version, which is what a server
        // operator correlating SDK traffic actually wants to see. It can carry a
        // "+<commit sha>" suffix from source link; trim that.
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational!.IndexOf('+');
            return plus < 0 ? informational : informational.Substring(0, plus);
        }

        var version = assembly.GetName().Version;
        return version is null ? "0.0.0" : version.ToString(3);
    }
}
