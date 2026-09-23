using System.Reflection;
using System.Text;
using NutHub.Cli;

namespace NutHub.ServiceSetup;

/// <summary>
/// The NutHub systemd unit, with the same files install.sh installs (embedded from packaging/linux): the unit, the
/// udev rules for USB UPSes and the polkit rule that lets the service account power the machine off. The service
/// runs as the unprivileged "nuthub" account, created here if needed.
/// </summary>
internal sealed class SystemdServiceInstaller(ServiceOptions options, ISystemProbe probe)
{
    public const string UnitName = "nuthub.service";
    public const string Account = ServiceAccountFiles.Account;
    public const string UnitPath = "/etc/systemd/system/" + UnitName;
    public const string UdevRulesPath = "/etc/udev/rules.d/99-nuthub-ups.rules";
    public const string PolkitRulesPath = "/etc/polkit-1/rules.d/50-nuthub-poweroff.rules";
    public const string PolkitPklaPath = "/etc/polkit-1/localauthority/50-local.d/50-nuthub-poweroff.pkla";
    public const string CacheDirectory = "/var/cache/nuthub";

    // ProtectHome= and PrivateTmp= hide these from the service: an executable there could never start.
    private static readonly string[] HiddenLocations = ["/home/", "/root/", "/run/user/", "/tmp/", "/var/tmp/"];

    private string ConfigFile => options.ConfigFile ?? "/etc/nuthub/nuthub.json";

    private string ConfigDirectory => ParentOf(ConfigFile);

    public IReadOnlyList<SetupStep> Install()
    {
        Validate();
        var steps = new List<SetupStep>();

        if (!probe.GroupExists(Account))
        {
            steps.Add(new CommandStep($"Creating the group {Account}", "groupadd", ["--system", Account]));
        }

        if (!probe.AccountExists(Account))
        {
            string shell = probe.FileExists("/usr/sbin/nologin") ? "/usr/sbin/nologin"
                         : probe.FileExists("/sbin/nologin") ? "/sbin/nologin"
                         : "/bin/false";
            steps.Add(new CommandStep($"Creating the system account {Account}", "useradd",
                                      ["--system", "--gid", Account, "--home-dir", options.DataDirectory,
                                       "--no-create-home", "--shell", shell, "--comment", "NutHub UPS server", Account]));
        }

        string[] directories = [.. new[] { ConfigDirectory, options.DataDirectory }.Distinct(StringComparer.Ordinal)];
        steps.Add(new CommandStep("Creating the configuration and data directories", "install",
                                  ["-d", "-m", "0750", "-o", Account, "-g", Account, .. directories, CacheDirectory]));
        steps.Add(new CommandStep($"Giving them to the {Account} account", "chown",
                                  ["-R", $"{Account}:{Account}", .. directories]));

        steps.Add(new WriteFileStep($"Installing {UnitPath}", UnitPath, BuildUnit()));

        steps.Add(new WriteFileStep($"Installing {UdevRulesPath}", UdevRulesPath, Resource("99-nuthub-ups.rules")));
        steps.Add(new CommandStep("Reloading the udev rules", "udevadm", ["control", "--reload-rules"], IgnoreFailure: true));
        steps.Add(new CommandStep("Applying them to connected USB devices", "udevadm",
                                  ["trigger", "--subsystem-match=hidraw"], IgnoreFailure: true));

        bool polkit = false;
        if (probe.DirectoryExists("/etc/polkit-1/rules.d") || probe.DirectoryExists("/usr/share/polkit-1/rules.d"))
        {
            steps.Add(new WriteFileStep($"Installing {PolkitRulesPath}", PolkitRulesPath,
                                        Resource("50-nuthub-poweroff.rules")));
            polkit = true;
        }

        if (probe.DirectoryExists("/etc/polkit-1/localauthority/50-local.d"))
        {
            steps.Add(new WriteFileStep($"Installing {PolkitPklaPath}", PolkitPklaPath,
                                        Resource("50-nuthub-poweroff.pkla")));
            polkit = true;
        }

        if (!polkit)
        {
            steps.Add(new NoteStep("polkit was not found: NutHub cannot power off this machine unless it runs as root."));
        }

        steps.Add(new CommandStep("Reloading systemd", "systemctl", ["daemon-reload"]));
        steps.Add(new CommandStep("Enabling the service at boot", "systemctl", ["enable", UnitName]));
        if (options.Start)
        {
            // restart rather than start: after an upgrade the new executable must replace the running one.
            steps.Add(new CommandStep("Starting the service", "systemctl", ["restart", UnitName]));
        }
        else
        {
            steps.Add(new NoteStep($"The service will start at the next boot, or now with: systemctl start {UnitName}"));
        }

        return steps;
    }

    public IReadOnlyList<SetupStep> Uninstall() =>
    [
        new CommandStep("Stopping and disabling the service", "systemctl", ["disable", "--now", UnitName],
                        IgnoreFailure: true),
        new DeleteFileStep($"Removing {UnitPath}", UnitPath),
        new CommandStep("Reloading systemd", "systemctl", ["daemon-reload"]),
        new DeleteFileStep($"Removing {UdevRulesPath}", UdevRulesPath),
        new CommandStep("Reloading the udev rules", "udevadm", ["control", "--reload-rules"], IgnoreFailure: true),
        new DeleteFileStep($"Removing {PolkitRulesPath}", PolkitRulesPath),
        new DeleteFileStep($"Removing {PolkitPklaPath}", PolkitPklaPath),
        new NoteStep($"Kept: the configuration ({ConfigDirectory}), the data ({options.DataDirectory}) and the " +
                     $"{Account} account."),
    ];

    public IReadOnlyList<SetupStep> Start() =>
        [new CommandStep("Starting the service", "systemctl", ["start", UnitName])];

    public IReadOnlyList<SetupStep> Stop() =>
        [new CommandStep("Stopping the service", "systemctl", ["stop", UnitName])];

    public IReadOnlyList<SetupStep> Status() =>
        [new CommandStep("Service status", "systemctl", ["status", UnitName, "--no-pager"], ShowOutput: true)];

    /// <summary>The packaged unit with this installation's executable, locations and serial port group.</summary>
    internal string BuildUnit()
    {
        string? serialGroup = probe.GroupExists("dialout") ? "dialout" : probe.GroupExists("uucp") ? "uucp" : null;
        string execStart = string.Join(' ', new[] { options.Executable }.Concat(options.ExecutableArguments).Append("run"));
        string readWrite = string.Join(' ', new[] { options.DataDirectory, ConfigDirectory }.Distinct(StringComparer.Ordinal));

        var unit = new StringBuilder();
        foreach (string line in Resource("nuthub.service").Split('\n'))
        {
            string replaced = line switch
            {
                _ when line.StartsWith("ExecStart=", StringComparison.Ordinal) => "ExecStart=" + execStart,
                _ when line.StartsWith("Environment=NUTHUB_DATA_DIR=", StringComparison.Ordinal) =>
                    "Environment=NUTHUB_DATA_DIR=" + options.DataDirectory,
                _ when line.StartsWith("Environment=NUTHUB_CONFIG=", StringComparison.Ordinal) =>
                    "Environment=NUTHUB_CONFIG=" + ConfigFile,
                _ when line.StartsWith("ReadWritePaths=", StringComparison.Ordinal) => "ReadWritePaths=" + readWrite,
                _ when line.StartsWith("SupplementaryGroups=", StringComparison.Ordinal) =>
                    serialGroup is null ? "" : "SupplementaryGroups=" + serialGroup,
                _ => line,
            };
            if (replaced.Length > 0 || line.Length == 0)
            {
                unit.Append(replaced).Append('\n');
            }
        }

        return unit.ToString().TrimEnd('\n') + "\n";
    }

    private void Validate()
    {
        foreach (string path in new[] { options.Executable, options.DataDirectory, ConfigFile })
        {
            if (!path.StartsWith('/'))
            {
                throw new CommandException($"'{path}' is not an absolute path.");
            }

            if (path.Any(char.IsWhiteSpace))
            {
                throw new CommandException($"'{path}': paths with spaces are not supported in the systemd unit.");
            }
        }

        if (HiddenLocations.Any(h => options.Executable.StartsWith(h, StringComparison.Ordinal)))
        {
            throw new CommandException(
                $"The service cannot run {options.Executable}: home and temporary directories are hidden from it. " +
                "Copy nuthub to /opt/nuthub first (install.sh does this), then run /opt/nuthub/nuthub service install.");
        }

        foreach (string directory in new[] { options.DataDirectory, ConfigDirectory })
        {
            if (!ServiceAccountFiles.IsDedicated(directory))
            {
                throw new CommandException(
                    $"{directory} is handed over to the {Account} account, so it must be a directory of NutHub's own " +
                    "(its name must contain \"nuthub\", like /var/lib/nuthub).");
            }
        }
    }

    private static string ParentOf(string file)
    {
        int slash = file.LastIndexOf('/');
        return slash <= 0 ? "/" : file[..slash];
    }

    /// <summary>A file of packaging/linux, with LF line endings whatever the checkout used.</summary>
    internal static string Resource(string name)
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("NutHub.Packaging." + name)
                              ?? throw new InvalidOperationException($"The packaged file {name} is missing from the build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n", StringComparison.Ordinal);
    }
}
