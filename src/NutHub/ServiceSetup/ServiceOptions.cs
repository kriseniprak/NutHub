using System.Runtime.Versioning;
using System.ServiceProcess;

namespace NutHub.ServiceSetup;

/// <summary>What a service setup plan needs to know about the system. Only reads, never changes anything.</summary>
internal interface ISystemProbe
{
    /// <summary>Windows: whether the NutHub service is registered.</summary>
    bool ServiceExists();

    /// <summary>Windows: whether the NutHub service is running.</summary>
    bool ServiceRunning();

    bool AccountExists(string name);

    bool GroupExists(string name);

    bool DirectoryExists(string path);

    bool FileExists(string path);
}

/// <summary>Answers from this machine.</summary>
internal sealed class LiveSystemProbe : ISystemProbe
{
    public bool ServiceExists() => OperatingSystem.IsWindows() && QueryService() is not null;

    public bool ServiceRunning() => OperatingSystem.IsWindows() && QueryService() == ServiceControllerStatus.Running;

    public bool AccountExists(string name) => ProcessRunner.Run("getent", ["passwd", name]).ExitCode == 0;

    public bool GroupExists(string name) => ProcessRunner.Run("getent", ["group", name]).ExitCode == 0;

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool FileExists(string path) => File.Exists(path);

    [SupportedOSPlatform("windows")]
    private static ServiceControllerStatus? QueryService()
    {
        try
        {
            using var controller = new ServiceController(WindowsServiceInstaller.ServiceName);
            return controller.Status;
        }
        catch (InvalidOperationException)
        {
            return null; // not installed
        }
    }
}

/// <summary>
/// Answers for a --dry-run preview of another operating system: a typical fresh machine (nothing installed yet,
/// the usual groups and directories present).
/// </summary>
internal sealed class PreviewSystemProbe : ISystemProbe
{
    public bool ServiceExists() => false;

    public bool ServiceRunning() => false;

    public bool AccountExists(string name) => false;

    public bool GroupExists(string name) => name == "dialout";

    public bool DirectoryExists(string path) => path.StartsWith("/etc/polkit-1/rules.d", StringComparison.Ordinal);

    public bool FileExists(string path) => path == "/usr/sbin/nologin";
}

/// <summary>Where the service runs from and which data it uses.</summary>
/// <param name="Executable">The program the service manager starts.</param>
/// <param name="ExecutableArguments">Arguments before "run" (the assembly when started through "dotnet").</param>
/// <param name="DataDirectory">The data directory.</param>
/// <param name="ConfigFile">The configuration file, or null for the default of the platform.</param>
/// <param name="Start">Start (or restart) the service after installing it.</param>
internal sealed record ServiceOptions(string Executable, IReadOnlyList<string> ExecutableArguments, string DataDirectory,
                                      string? ConfigFile, bool Start);
