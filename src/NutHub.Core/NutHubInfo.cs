using System.Reflection;

namespace NutHub.Core;

/// <summary>
/// Product-wide constants and the running version.
/// </summary>
public static class NutHubInfo
{
    public const string ProductName = "NutHub";

    /// <summary>The NUT network protocol version this server implements (NUT 2.8.x).</summary>
    public const string ProtocolVersion = "1.3";

    public const int DefaultNutPort = 3493;
    public const int DefaultHttpPort = 8493;
    public const int DefaultHttpsPort = 8494;

    public const string ProjectUrl = "https://github.com/kriseniprak/NutHub";

    /// <summary>The version from Directory.Build.props, without any "+commit" suffix.</summary>
    public static string Version { get; } = ResolveVersion();

    /// <summary>
    /// Whether this process runs as a Windows service or a systemd unit. Set once by the host at startup, from the
    /// same helpers the hosting integration uses.
    /// </summary>
    public static bool IsService { get; set; }

    /// <summary>When this process started, for uptime displays.</summary>
    public static DateTimeOffset StartedAt { get; } = DateTimeOffset.UtcNow;

    private static string ResolveVersion()
    {
        string? informational = typeof(NutHubInfo).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(informational))
        {
            return typeof(NutHubInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }

        int plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }
}
