using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NutHub.Core;
using NutHub.Core.Configuration;
using NutHub.FileLogging;
using NutHub.Hosting;

namespace NutHub.Cli;

/// <summary>
/// "nuthub run": the server itself, in the foreground or under the Windows service manager / systemd.
/// </summary>
internal static class RunCommand
{
    private const string Usage = "nuthub run [--data-dir DIR] [--config FILE]";

    public static async Task<int> ExecuteAsync(IReadOnlyList<string> args)
    {
        ParsedArguments parsed = ArgumentParser.Parse(args, "run", flags: [], valued: ["--data-dir", "--config"]);
        parsed.ExpectPositionals(0, Usage);

        NutHubPaths paths = NutHubPaths.Resolve(parsed.Get("--data-dir"), parsed.Get("--config"));
        HostRunMode mode = NutHubHostFactory.DetectMode();

        // The directories first, with their restricted permissions: the file log must not create the data
        // directory with default (readable by everyone) permissions.
        try
        {
            paths.EnsureCreated();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return Fail(mode, null, null, $"Cannot create or access {paths.DataDirectory}: {ex.Message}");
        }

        using var fileLogger = new FileLoggerProvider(paths.LogDirectory, TimeProvider.System);
        IHost host;
        try
        {
            host = NutHubHostFactory.CreateBuilder(paths, mode, fileLogger).Build();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return Fail(mode, null, fileLogger, $"NutHub could not be set up: {ex.Message}");
        }

        using (host)
        {
            ILogger logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("NutHub");

            // Load the configuration now rather than lazily inside a hosted service, so a broken file gives one clear
            // message instead of a host start failure.
            try
            {
                _ = host.Services.GetRequiredService<IConfigStore>().Current;
            }
            catch (ConfigLoadException ex)
            {
                return Fail(mode, logger, fileLogger, ex.Message.Contains(paths.ConfigFile, StringComparison.Ordinal)
                                                          ? ex.Message
                                                          : $"{paths.ConfigFile}: {ex.Message}");
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or CryptographicException
                                           or FormatException)
            {
                return Fail(mode, logger, fileLogger,
                            $"Cannot read the configuration or the secret key in {paths.DataDirectory}: {ex.Message}");
            }

            try
            {
                // Not RunAsync: it disposes the host, and the services are inspected below.
                await host.StartAsync().ConfigureAwait(false);
                await host.WaitForShutdownAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogCritical(ex, "NutHub stopped because of an unexpected error.");
                if (mode == HostRunMode.Console)
                {
                    Console.Error.WriteLine($"nuthub: stopped because of an unexpected error: {ex.Message} " +
                                            $"(details in {paths.LogDirectory})");
                }

                return ExitCodes.Error;
            }

            // A background service that crashed stops the host "normally"; report it, so the service manager
            // restarts NutHub (Restart=on-failure, service recovery actions).
            if (host.Services.GetServices<IHostedService>().OfType<BackgroundService>()
                    .Any(s => s.ExecuteTask is { IsFaulted: true }))
            {
                logger.LogCritical("NutHub stopped because one of its services failed; see the errors above.");
                return ExitCodes.Error;
            }

            return ExitCodes.Ok;
        }
    }

    /// <summary>
    /// Reports a startup failure where whoever started NutHub will look: the console (and the journal under
    /// systemd), the Windows Event Log for a service, and the log file.
    /// </summary>
    private static int Fail(HostRunMode mode, ILogger? hostLogger, FileLoggerProvider? fileLogger, string message)
    {
        if (mode == HostRunMode.WindowsService)
        {
            if (hostLogger is not null)
            {
                // Event Log and file.
                hostLogger.LogCritical("NutHub cannot start: {Message}", message);
            }
            else
            {
                WriteEventLog(message);
                fileLogger?.CreateLogger("NutHub").LogCritical("NutHub cannot start: {Message}", message);
            }
        }
        else
        {
            Console.Error.WriteLine($"nuthub: {message}");
            fileLogger?.CreateLogger("NutHub").LogCritical("NutHub cannot start: {Message}", message);
        }

        return ExitCodes.Error;
    }

    private static void WriteEventLog(string message)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            EventLog.WriteEntry(NutHubHostFactory.EventLogSource, "NutHub cannot start: " + message,
                                EventLogEntryType.Error);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception
                                       or System.Security.SecurityException)
        {
            // No event source and no right to create one: the service manager still records the failed start.
        }
    }
}
