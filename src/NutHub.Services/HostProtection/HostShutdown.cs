using NutHub.Services.Processes;

namespace NutHub.Services.HostProtection;

/// <summary>A program to run to shut the machine down, with its arguments (started without a shell).</summary>
internal sealed record ShutdownInvocation(string FileName, IReadOnlyList<string> Arguments)
{
    public override string ToString() => CommandLine.Format(FileName, Arguments);
}

/// <summary>The command that shuts down the machine NutHub runs on.</summary>
public static class HostShutdown
{
    /// <summary>
    /// When this environment variable is "1" the shutdown command and the UPS power-off are never executed (only
    /// logged), whatever the configuration says: a safety switch for test machines and containers.
    /// </summary>
    public const string DisableEnvironmentVariable = "NUTHUB_DISABLE_SHUTDOWN";

    private const string WindowsComment = "NutHub: UPS power critical";

    /// <summary>
    /// The operating system's default shutdown command, as shown in the web panel when no command is configured
    /// (e.g. <c>shutdown.exe /s /f /t 0 /d 6:12 /c "NutHub: UPS power critical"</c> or <c>systemctl poweroff</c>).
    /// The program is shown by name; it is run from the system directory.
    /// </summary>
    public static string DefaultCommand
    {
        get
        {
            ShutdownInvocation first = DefaultInvocations()[0];
            return CommandLine.Format(Path.GetFileName(first.FileName), first.Arguments);
        }
    }

    /// <summary>
    /// The default command and its fallbacks, tried in order until one succeeds.
    /// </summary>
    /// <remarks>
    /// Windows: <c>/s</c> shut down, <c>/f</c> close applications without asking, <c>/t 0</c> now, <c>/d 6:12</c>
    /// the predefined unplanned reason "Power Failure: Environment" (recorded in the event log; a <c>u:</c> prefix
    /// would make it a user-defined code that is not registered on the system), <c>/c</c> the comment. The fallback
    /// drops the reason and comment in case a policy rejects them. Linux: <c>systemctl poweroff</c>, or the classic
    /// <c>shutdown -h now</c> on systems without systemd.
    /// </remarks>
    internal static IReadOnlyList<ShutdownInvocation> DefaultInvocations()
    {
        if (OperatingSystem.IsWindows())
        {
            string system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string exe = string.IsNullOrEmpty(system) ? "shutdown.exe" : Path.Combine(system, "shutdown.exe");
            return
            [
                new ShutdownInvocation(exe, ["/s", "/f", "/t", "0", "/d", "6:12", "/c", WindowsComment]),
                new ShutdownInvocation(exe, ["/s", "/f", "/t", "0"]),
            ];
        }

        if (OperatingSystem.IsMacOS())
        {
            return [new ShutdownInvocation(FindExecutable("shutdown", "/sbin", "/usr/sbin") ?? "shutdown", ["-h", "now"])];
        }

        var list = new List<ShutdownInvocation>();
        if (FindExecutable("systemctl", "/usr/bin", "/bin", "/usr/sbin", "/sbin") is { } systemctl)
        {
            list.Add(new ShutdownInvocation(systemctl, ["poweroff"]));
        }

        list.Add(new ShutdownInvocation(FindExecutable("shutdown", "/usr/sbin", "/sbin", "/usr/bin", "/bin") ?? "shutdown",
                                        ["-h", "now"]));
        return list;
    }

    /// <summary>The configured command, or the operating system default when none is set.</summary>
    internal static IReadOnlyList<ShutdownInvocation> Resolve(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return DefaultInvocations();
        }

        List<string> parts = CommandLine.Split(configured);
        if (parts.Count == 0)
        {
            return DefaultInvocations();
        }

        // If the configured program fails, the system default still gets a chance: the machine must go down.
        var custom = new ShutdownInvocation(parts[0], parts.Skip(1).ToList());
        return [custom, .. DefaultInvocations()];
    }

    private static string? FindExecutable(string name, params string[] directories)
    {
        foreach (string directory in directories)
        {
            string candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
