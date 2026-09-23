using NutHub.Cli;

namespace NutHub.ServiceSetup;

/// <summary>
/// "nuthub service install|uninstall|start|stop|status": the Windows service or the systemd unit. With --dry-run the
/// plan is printed instead of applied; --platform windows|linux previews the other operating system's plan.
/// </summary>
internal static class ServiceCommand
{
    private const string Usage =
        "nuthub service install|uninstall|start|stop|status [--data-dir DIR] [--config FILE] [--no-start] [--dry-run]";

    public static Task<int> ExecuteAsync(IReadOnlyList<string> args)
    {
        ParsedArguments parsed = ArgumentParser.Parse(args, "service", ["--dry-run", "--no-start"],
                                                      ["--data-dir", "--config", "--platform"]);
        parsed.ExpectPositionals(1, Usage);
        string action = parsed.Positionals[0];
        bool dryRun = parsed.Has("--dry-run");

        TargetPlatform current = OperatingSystem.IsWindows() ? TargetPlatform.Windows
                               : OperatingSystem.IsLinux() ? TargetPlatform.Linux
                               : throw new CommandException("Services are supported on Windows and Linux (systemd) only.");
        TargetPlatform platform = parsed.Get("--platform") switch
        {
            null => current,
            "windows" => TargetPlatform.Windows,
            "linux" => TargetPlatform.Linux,
            var other => throw new UsageException($"--platform must be windows or linux (not '{other}')."),
        };
        bool preview = platform != current;
        if (preview && !dryRun)
        {
            throw new UsageException("--platform can only preview another operating system, with --dry-run.");
        }

        if (action != "install" && (parsed.Has("--no-start") || parsed.Has("--data-dir") || parsed.Has("--config")))
        {
            throw new UsageException("--data-dir, --config and --no-start apply to 'service install' only.");
        }

        ServiceOptions options = BuildOptions(parsed, platform, preview);
        ISystemProbe probe = preview ? new PreviewSystemProbe() : new LiveSystemProbe();

        IReadOnlyList<SetupStep> plan;
        if (platform == TargetPlatform.Windows)
        {
            var windows = new WindowsServiceInstaller(options, probe);
            plan = action switch
            {
                "install" => windows.Install(),
                "uninstall" => windows.Uninstall(),
                "start" => RequireInstalled(probe, dryRun, windows.Start()),
                "stop" => RequireInstalled(probe, dryRun, windows.Stop()),
                "status" => RequireInstalled(probe, dryRun, windows.Status()),
                _ => throw new UsageException($"Unknown action '{action}'. Usage: {Usage}"),
            };
        }
        else
        {
            var systemd = new SystemdServiceInstaller(options, probe);
            plan = action switch
            {
                "install" => systemd.Install(),
                "uninstall" => systemd.Uninstall(),
                "start" => systemd.Start(),
                "stop" => systemd.Stop(),
                "status" => systemd.Status(),
                _ => throw new UsageException($"Unknown action '{action}'. Usage: {Usage}"),
            };
        }

        if (action != "status")
        {
            if (dryRun)
            {
                Console.WriteLine(platform == TargetPlatform.Windows
                    ? "# Dry run: these commands need an elevated prompt (Run as administrator)."
                    : "# Dry run: these commands need root (sudo).");
            }
            else if (!Environment.IsPrivilegedProcess)
            {
                throw new CommandException(platform == TargetPlatform.Windows
                    ? $"'nuthub service {action}' needs administrator rights: run it from an elevated prompt (Run as administrator)."
                    : $"'nuthub service {action}' needs root: run it with sudo.");
            }
        }

        int exitCode = new StepRunner(dryRun, platform, Console.Out).Run(plan);
        if (action == "status" && !dryRun)
        {
            return Task.FromResult(StatusExitCode(platform, exitCode, probe));
        }

        if (action == "install" && !dryRun)
        {
            Console.WriteLine(options.Start
                ? "NutHub is installed and running. The web panel listens on port 8493 (http://localhost:8493/)."
                : "NutHub is installed.");
        }

        return Task.FromResult(ExitCodes.Ok);
    }

    /// <summary>0 when running, 3 when installed but stopped (as systemctl and LSB scripts report), 1 otherwise.</summary>
    private static int StatusExitCode(TargetPlatform platform, int toolExitCode, ISystemProbe probe)
    {
        if (platform == TargetPlatform.Windows)
        {
            return probe.ServiceRunning() ? ExitCodes.Ok : 3;
        }

        return toolExitCode switch
        {
            0 => ExitCodes.Ok,
            3 => 3,
            _ => ExitCodes.Error,
        };
    }

    private static IReadOnlyList<SetupStep> RequireInstalled(ISystemProbe probe, bool dryRun, IReadOnlyList<SetupStep> steps)
    {
        if (!dryRun && !probe.ServiceExists())
        {
            throw new CommandException("The NutHub service is not installed: run 'nuthub service install' first.");
        }

        return steps;
    }

    private static ServiceOptions BuildOptions(ParsedArguments parsed, TargetPlatform platform, bool preview)
    {
        string? dataDir = parsed.Get("--data-dir");
        string? config = parsed.Get("--config");
        bool start = !parsed.Has("--no-start");

        if (preview)
        {
            // Paths of another operating system: taken as written, with the defaults of an installation there.
            return platform == TargetPlatform.Windows
                ? new ServiceOptions(@"C:\Program Files\NutHub\nuthub.exe", [], dataDir ?? @"C:\ProgramData\NutHub",
                                     config, start)
                : new ServiceOptions("/opt/nuthub/nuthub", [], dataDir ?? "/var/lib/nuthub", config, start);
        }

        string processPath = Environment.ProcessPath
                             ?? throw new CommandException("Cannot determine the path of the nuthub executable.");
        IReadOnlyList<string> prefix = [];
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Started as "dotnet nuthub.dll": the service must do the same.
            prefix = [Path.Combine(AppContext.BaseDirectory, "nuthub.dll")];
        }

        if (platform == TargetPlatform.Windows)
        {
            string defaultData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                                              "NutHub");
            return new ServiceOptions(processPath, prefix, Path.GetFullPath(dataDir ?? defaultData),
                                      config is null ? null : Path.GetFullPath(config), start);
        }

        // Linux: the service locations, whoever runs the command (a user's own defaults would be in their home).
        return new ServiceOptions(processPath, prefix, Path.GetFullPath(dataDir ?? "/var/lib/nuthub"),
                                  config is null ? null : Path.GetFullPath(config), start);
    }
}
