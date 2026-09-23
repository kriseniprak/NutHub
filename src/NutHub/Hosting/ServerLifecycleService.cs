using System.Runtime.InteropServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NutHub.Core;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Core.Runtime;

namespace NutHub.Hosting;

/// <summary>
/// Announces the server: what runs where at startup (log and, on first start, the generated administrator password
/// on the console), and the ServerStarted / ServerStopping events of the event log.
/// </summary>
internal sealed class ServerLifecycleService(
    EventHub hub,
    JsonConfigStore config,
    NutHubPaths paths,
    INutServerStatus nutServer,
    HostEnvironmentInfo environment,
    TimeProvider time,
    ILogger<ServerLifecycleService> logger) : IHostedLifecycleService
{
    // How long shutdown waits for the ServerStopping event to reach the event store before the recorder stops.
    private static readonly TimeSpan RecordTimeout = TimeSpan.FromSeconds(3);

    public Task StartingAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("NutHub {Version} starting on {Os} ({Architecture}), {Framework}, process {ProcessId}{Mode}.",
                              NutHubInfo.Version, RuntimeInformation.OSDescription, RuntimeInformation.OSArchitecture,
                              RuntimeInformation.FrameworkDescription, Environment.ProcessId, ModeText());
        logger.LogInformation("Data directory: {DataDirectory}; configuration: {ConfigFile}; logs: {LogDirectory}",
                              paths.DataDirectory, paths.ConfigFile, environment.LogDirectory ?? "(console only)");
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        NutHubConfig current = config.Current;
        IReadOnlyList<string> urls = PanelAddresses.WebUrls(current.Web);
        if (urls.Count > 0)
        {
            logger.LogInformation("Web panel: {Urls}", string.Join("  ", urls));
        }
        else
        {
            logger.LogInformation("The web panel is disabled in the configuration.");
        }

        if (current.Nut.Enabled)
        {
            IReadOnlyList<string> endpoints = nutServer.Enabled && nutServer.Endpoints.Count > 0
                ? nutServer.Endpoints
                : current.Nut.Listen.Select(l => l.ToString()).ToList();
            logger.LogInformation("NUT server: {Endpoints}; {UpsCount} UPS configured.",
                                  string.Join(", ", endpoints), current.Ups.Count);
        }
        else
        {
            logger.LogInformation("The NUT server is disabled in the configuration; {UpsCount} UPS configured.",
                                  current.Ups.Count);
        }

        hub.Publish(new UpsEventMessage(UpsEvent.Create(
            UpsEventType.ServerStarted, time.GetUtcNow(), null, $"NutHub {NutHubInfo.Version} started.", "system",
            new Dictionary<string, string>
            {
                ["version"] = NutHubInfo.Version,
                ["os"] = RuntimeInformation.OSDescription,
            })));

        if (config.GeneratedAdminPassword is { } password && environment.Mode == HostRunMode.Console &&
            !Console.IsOutputRedirected)
        {
            string user = current.WebUsers.LastOrDefault(u => u.Role == WebRole.Admin && u.MustChangePassword)?.Name
                          ?? "admin";
            PrintPasswordBox(user, password, urls.FirstOrDefault());
        }

        return Task.CompletedTask;
    }

    public async Task StoppingAsync(CancellationToken cancellationToken)
    {
        UpsEvent stopping = UpsEvent.Create(UpsEventType.ServerStopping, time.GetUtcNow(), null,
                                            "NutHub is stopping.", "system");
        var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using IDisposable subscription = hub.Subscribe(message =>
        {
            if (message is EventRecordedMessage { Event.Type: UpsEventType.ServerStopping })
            {
                recorded.TrySetResult();
            }
        });

        logger.LogInformation("NutHub is stopping.");
        hub.Publish(new UpsEventMessage(stopping));
        try
        {
            // The event recorder stops with the other services right after this; give it the time to store the
            // event so the event log shows the shutdown.
            await recorded.Task.WaitAsync(RecordTimeout, time, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            logger.LogDebug("The ServerStopping event was not confirmed as recorded.");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken)
    {
        logger.LogInformation("NutHub stopped.");
        return Task.CompletedTask;
    }

    private string ModeText() => environment.Mode switch
    {
        HostRunMode.WindowsService => " as a Windows service",
        HostRunMode.Systemd => " as a systemd service",
        _ => "",
    };

    /// <summary>
    /// Written straight to the console, never to the log: the password must not end up in log files.
    /// </summary>
    private void PrintPasswordBox(string user, string password, string? url)
    {
        string[] lines =
        [
            "First start: sign in to the web panel with",
            "",
            $"    User:      {user}",
            $"    Password:  {password}",
            "",
            url is null ? "" : $"at {url}",
            "You will be asked to choose a new password.",
            "The password is also saved in",
            paths.InitialPasswordFile,
            "(delete that file once you have signed in).",
        ];

        int width = lines.Max(l => l.Length) + 4;
        string border = "+" + new string('-', width - 2) + "+";
        var output = Console.Out;
        output.WriteLine();
        output.WriteLine(border);
        foreach (string line in lines)
        {
            output.WriteLine("| " + line.PadRight(width - 4) + " |");
        }

        output.WriteLine(border);
        output.WriteLine();
        output.Flush();
    }
}
