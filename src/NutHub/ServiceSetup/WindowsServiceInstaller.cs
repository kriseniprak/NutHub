using System.Diagnostics;
using System.Runtime.Versioning;
using System.ServiceProcess;
using NutHub.Cli;
using NutHub.Hosting;

namespace NutHub.ServiceSetup;

/// <summary>
/// The NutHub Windows service, through sc.exe: automatic start, restarted by the service manager 5, 10 and 30 s
/// after a failure, and an Event Log source for the warnings and errors it logs.
/// </summary>
internal sealed class WindowsServiceInstaller(ServiceOptions options, ISystemProbe probe)
{
    public const string ServiceName = NutHubHostFactory.ServiceName;
    public const string DisplayName = "NutHub UPS server";
    public const string Description =
        "UPS server compatible with Network UPS Tools (NUT), with a web panel. https://github.com/kriseniprak/NutHub";

    private static readonly TimeSpan StateTimeout = TimeSpan.FromSeconds(60);

    private static string ScExe =>
        OperatingSystem.IsWindows() ? Path.Combine(Environment.SystemDirectory, "sc.exe") : "sc.exe";

    /// <summary>The command line the service manager runs, with every path quoted.</summary>
    public string BinaryPathName
    {
        get
        {
            var parts = new List<string> { Quote(options.Executable) };
            parts.AddRange(options.ExecutableArguments.Select(Quote));
            parts.Add("run");
            parts.Add("--data-dir");
            parts.Add(Quote(options.DataDirectory));
            if (options.ConfigFile is not null)
            {
                parts.Add("--config");
                parts.Add(Quote(options.ConfigFile));
            }

            return string.Join(' ', parts);
        }
    }

    public IReadOnlyList<SetupStep> Install()
    {
        var steps = new List<SetupStep>();
        string[] common = ["binPath=", BinaryPathName, "start=", "auto", "DisplayName=", DisplayName];
        steps.Add(probe.ServiceExists()
            ? new CommandStep("Updating the NutHub service", ScExe, ["config", ServiceName, .. common])
            : new CommandStep("Registering the NutHub service", ScExe, ["create", ServiceName, .. common]));
        steps.Add(new CommandStep("Setting its description", ScExe, ["description", ServiceName, Description]));
        steps.Add(new CommandStep("Restarting it 5, 10 and 30 s after a failure", ScExe,
                                  ["failure", ServiceName, "reset=", "86400", "actions=",
                                   "restart/5000/restart/10000/restart/30000"]));
        steps.Add(new CommandStep("Applying the restart also when it stops with an error", ScExe,
                                  ["failureflag", ServiceName, "1"]));
        steps.Add(new ActionStep($"Registering the Event Log source {NutHubHostFactory.EventLogSource}",
                                 $"(create the event source \"{NutHubHostFactory.EventLogSource}\" in the Application log)",
                                 CreateEventSource));
        if (options.Start)
        {
            steps.AddRange(Start());
        }
        else
        {
            steps.Add(new NoteStep("The service will start at the next boot, or now with: nuthub service start"));
        }

        return steps;
    }

    public IReadOnlyList<SetupStep> Uninstall()
    {
        var steps = new List<SetupStep>();
        if (!probe.ServiceExists())
        {
            steps.Add(new NoteStep("The NutHub service is not installed."));
        }
        else
        {
            if (probe.ServiceRunning())
            {
                steps.AddRange(Stop());
            }

            steps.Add(new CommandStep("Removing the NutHub service", ScExe, ["delete", ServiceName]));
        }

        steps.Add(new ActionStep($"Removing the Event Log source {NutHubHostFactory.EventLogSource}",
                                 $"(delete the event source \"{NutHubHostFactory.EventLogSource}\")", DeleteEventSource));
        steps.Add(new NoteStep($"The configuration and data are kept in {options.DataDirectory}."));
        return steps;
    }

    public IReadOnlyList<SetupStep> Start() =>
    [
        new CommandStep("Starting the NutHub service", ScExe, ["start", ServiceName]),
        new ActionStep("Waiting for it to run", "(wait until the service runs)",
                       () => WaitFor(running: true)),
    ];

    public IReadOnlyList<SetupStep> Stop() =>
    [
        new CommandStep("Stopping the NutHub service", ScExe, ["stop", ServiceName], IgnoreFailure: true),
        new ActionStep("Waiting for it to stop", "(wait until the service has stopped)",
                       () => WaitFor(running: false)),
    ];

    public IReadOnlyList<SetupStep> Status() =>
    [
        new CommandStep("Service state", ScExe, ["query", ServiceName], ShowOutput: true),
        new CommandStep("Service configuration", ScExe, ["qc", ServiceName], ShowOutput: true),
    ];

    private static string Quote(string value)
    {
        // A trailing backslash would escape the closing quote.
        string trimmed = value.Length > 3 ? value.TrimEnd('\\') : value;
        return $"\"{trimmed}\"";
    }

    private static void WaitFor(bool running)
    {
        if (OperatingSystem.IsWindows())
        {
            WaitForCore(running ? ServiceControllerStatus.Running : ServiceControllerStatus.Stopped);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void WaitForCore(ServiceControllerStatus status)
    {
        using var controller = new ServiceController(ServiceName);
        try
        {
            controller.WaitForStatus(status, StateTimeout);
        }
        catch (System.ServiceProcess.TimeoutException)
        {
            throw new CommandException(
                $"The NutHub service is still {controller.Status.ToString().ToLowerInvariant()} after " +
                $"{StateTimeout.TotalSeconds:0} s. See the Application log (source NutHub) and the log files in " +
                @"%ProgramData%\NutHub\logs.");
        }
    }

    private static void CreateEventSource()
    {
        if (OperatingSystem.IsWindows())
        {
            CreateEventSourceCore();
        }
    }

    private static void DeleteEventSource()
    {
        if (OperatingSystem.IsWindows())
        {
            DeleteEventSourceCore();
        }
    }

    [SupportedOSPlatform("windows")]
    private static void CreateEventSourceCore()
    {
        string source = NutHubHostFactory.EventLogSource;
        if (EventLog.SourceExists(source))
        {
            return;
        }

        var data = new EventSourceCreationData(source, "Application");
        // The message file of .NET Framework, present on every Windows: without one the Event Viewer prefixes each
        // entry with "The description for Event ID 0 cannot be found".
        string messages = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                                       @"Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll");
        if (File.Exists(messages))
        {
            data.MessageResourceFile = messages;
        }

        EventLog.CreateEventSource(data);
    }

    [SupportedOSPlatform("windows")]
    private static void DeleteEventSourceCore()
    {
        string source = NutHubHostFactory.EventLogSource;
        if (EventLog.SourceExists(source))
        {
            EventLog.DeleteEventSource(source);
        }
    }
}
