using System.Runtime.Versioning;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Systemd;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Logging.EventLog;
using NutHub.Core;
using NutHub.Drivers.Hid;
using NutHub.Drivers.Net;
using NutHub.Drivers.Serial;
using NutHub.Drivers.Snmp;
using NutHub.FileLogging;
using NutHub.Protocol;
using NutHub.Services;
using NutHub.Storage;
using NutHub.Web;

namespace NutHub.Hosting;

/// <summary>How the process was started, which decides where the log goes.</summary>
internal enum HostRunMode
{
    /// <summary>In a terminal (or a container, with the output redirected).</summary>
    Console,

    /// <summary>By the Windows service control manager: no console, warnings to the Event Log.</summary>
    WindowsService,

    /// <summary>As a systemd unit: console output in the journal format, readiness through sd_notify.</summary>
    Systemd,

    /// <summary>A command-line tool (passwd, devices...): only warnings, on standard error.</summary>
    Tool,
}

/// <summary>What the lifecycle service needs to know about the way the host was set up.</summary>
internal sealed record HostEnvironmentInfo(HostRunMode Mode, string? LogDirectory);

/// <summary>
/// Builds the generic host with every NutHub project registered, the logging for the way the process runs, and the
/// shutdown timeout the drivers need to close their devices.
/// </summary>
internal static class NutHubHostFactory
{
    public const string ServiceName = "NutHub";
    public const string EventLogSource = "NutHub";

    /// <summary>Drivers get this long to close their devices on Ctrl+C, SIGTERM or a service stop.</summary>
    public static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(20);

    // Lowest-priority defaults: appsettings.json and environment variables (Logging__LogLevel__Default=Debug)
    // override them.
    private static readonly Dictionary<string, string?> LoggingDefaults = new()
    {
        ["Logging:LogLevel:Default"] = "Information",
        ["Logging:LogLevel:Microsoft"] = "Warning",
        ["Logging:LogLevel:Microsoft.Hosting.Lifetime"] = "Information",
        ["Logging:LogLevel:System.Net.Http.HttpClient"] = "Warning",

        // "No XML encryptor configured" on Linux: the key ring directory is already restricted to the service account.
        ["Logging:LogLevel:Microsoft.AspNetCore.DataProtection"] = "Error",
        ["Logging:EventLog:LogLevel:Default"] = "Warning",
    };

    public static HostRunMode DetectMode()
    {
        HostRunMode mode = WindowsServiceHelpers.IsWindowsService() ? HostRunMode.WindowsService
            : SystemdHelpers.IsSystemdService() ? HostRunMode.Systemd
            : HostRunMode.Console;
        NutHubInfo.IsService = mode != HostRunMode.Console;
        return mode;
    }

    /// <param name="paths">The data and configuration locations.</param>
    /// <param name="mode">How the process runs.</param>
    /// <param name="fileLogger">The file log, owned (and disposed) by the caller so it outlives the host.</param>
    public static HostApplicationBuilder CreateBuilder(NutHubPaths paths, HostRunMode mode,
                                                       FileLoggerProvider? fileLogger)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            // The command line is NutHub's own; configuration comes from appsettings.json and the environment.
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.Configuration.Sources.Insert(0, new MemoryConfigurationSource { InitialData = LoggingDefaults });
        ConfigureLogging(builder, mode, fileLogger);

        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = ShutdownTimeout);
        builder.Services.AddWindowsService(options => options.ServiceName = ServiceName);
        builder.Services.AddSystemd();
        builder.Services.AddSingleton(new HostEnvironmentInfo(mode, fileLogger?.Directory));

        builder.Services
            .AddNutHubCore(paths)
            .AddNutHubProtocol()
            .AddNutHubHidDrivers()
            .AddNutHubSerialDrivers()
            .AddNutHubSnmpDrivers()
            .AddNutHubNetworkDrivers()
            .AddNutHubStorage()
            .AddNutHubServices()
            .AddNutHubWeb();

        if (mode != HostRunMode.Tool)
        {
            // Last, so it sees every other service started before announcing the server.
            builder.Services.AddHostedService<ServerLifecycleService>();
        }

        return builder;
    }

    private static void ConfigureLogging(HostApplicationBuilder builder, HostRunMode mode, FileLoggerProvider? fileLogger)
    {
        // Before AddNutHubCore, which registers the in-memory log of the web panel as a provider.
        builder.Logging.ClearProviders();

        switch (mode)
        {
            case HostRunMode.Console:
                builder.Logging.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
                    options.IncludeScopes = false;
                });
                break;
            case HostRunMode.Systemd:
                // AddSystemd switches the console to the journal format (priority prefixes, no colours).
                builder.Logging.AddConsole();
                break;
            case HostRunMode.WindowsService:
                if (OperatingSystem.IsWindows())
                {
                    AddEventLog(builder.Logging);
                }

                break;
            case HostRunMode.Tool:
                builder.Logging.AddSimpleConsole(options =>
                {
                    options.SingleLine = true;
                    options.IncludeScopes = false;
                });
                builder.Services.Configure<ConsoleLoggerOptions>(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
                // A provider-specific rule added after the configuration binding wins over "Logging:LogLevel".
                builder.Logging.AddFilter<ConsoleLoggerProvider>(null, LogLevel.Warning);
                break;
        }

        if (fileLogger is not null)
        {
            builder.Logging.AddProvider(fileLogger);
        }
    }

    /// <summary>
    /// Warnings and errors of the service in the Application log, under the source "service install" registers
    /// (AddWindowsService would name it after the executable).
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static void AddEventLog(ILoggingBuilder logging) =>
        logging.AddEventLog(new EventLogSettings { SourceName = EventLogSource, LogName = "Application" });
}
