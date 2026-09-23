using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using NutHub.Core.Drivers;

namespace NutHub.Cli;

/// <summary>
/// "nuthub devices": runs the discovery of every driver that supports it and prints what it finds, to troubleshoot
/// USB and serial connections (permissions, drivers claimed by the operating system...).
/// </summary>
internal static class DevicesCommand
{
    private const string Usage = "nuthub devices [--timeout SECONDS]";

    public static async Task<int> ExecuteAsync(IReadOnlyList<string> args)
    {
        ParsedArguments parsed = ArgumentParser.Parse(args, "devices", [], ["--timeout", "--data-dir", "--config"]);
        parsed.ExpectPositionals(0, Usage);
        TimeSpan timeout = TimeSpan.FromSeconds(20);
        if (parsed.Get("--timeout") is { } text)
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) ||
                seconds is < 1 or > 600)
            {
                throw new UsageException("--timeout must be a number of seconds between 1 and 600.");
            }

            timeout = TimeSpan.FromSeconds(seconds);
        }

        using ToolHost tool = ToolHost.Create(parsed);
        var catalog = tool.Services.GetRequiredService<IDriverCatalog>();
        List<IUpsDriverFactory> searchable = [.. catalog.All.Where(f => f.SupportsDiscovery)];
        if (searchable.Count == 0)
        {
            Console.WriteLine("No driver of this build can search for devices.");
            return ExitCodes.Ok;
        }

        Console.WriteLine($"Searching with {searchable.Count} drivers (up to {timeout.TotalSeconds:0} s)...");
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        // In parallel: serial port probing can take seconds per port.
        var searches = searchable.Select(f => (Factory: f, Task: DiscoverAsync(catalog, f, timeout, cancel.Token))).ToList();
        int found = 0;
        foreach (var (factory, task) in searches)
        {
            DiscoveryOutcome outcome = await task.ConfigureAwait(false);
            Console.WriteLine();
            Console.WriteLine($"{factory.DisplayName} ({factory.Id}): {outcome.Summary}");
            foreach (DiscoveredDevice device in outcome.Devices)
            {
                found++;
                Console.WriteLine($"  - {device.Title}");
                if (!string.IsNullOrWhiteSpace(device.Detail))
                {
                    Console.WriteLine($"    {device.Detail}");
                }

                var secret = factory.Options.Where(o => o.IsSecret).Select(o => o.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                string options = string.Join(" ", device.Options.Select(o =>
                    $"{o.Key}={(secret.Contains(o.Key) ? "******" : Quote(o.Value))}"));
                Console.WriteLine($"    driver={factory.Id}" + (options.Length > 0 ? " " + options : "") +
                                  (device.SuggestedName is { } n ? $"  (suggested name: {n})" : ""));
            }
        }

        Console.WriteLine();
        Console.WriteLine(found == 0
            ? "No device found. Check the cable, the permissions (Linux: udev rules, dialout group) and that no other " +
              "UPS software holds the device."
            : $"{found} device(s) found: add them in the web panel (Administration, UPS) with these options.");
        return ExitCodes.Ok;
    }

    private static async Task<DiscoveryOutcome> DiscoverAsync(IDriverCatalog catalog, IUpsDriverFactory factory,
                                                              TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!catalog.IsSupportedHere(factory))
        {
            return new DiscoveryOutcome([], "not available on this operating system");
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            // Some discoveries block before their first await; keep them off the loop that prints results.
            IReadOnlyList<DiscoveredDevice> devices =
                await Task.Run(() => factory.DiscoverAsync(limit.Token), limit.Token).ConfigureAwait(false);
            return new DiscoveryOutcome(devices, devices.Count == 0 ? "nothing found" : $"{devices.Count} found");
        }
        catch (OperationCanceledException)
        {
            return new DiscoveryOutcome([], cancellationToken.IsCancellationRequested
                                                ? "cancelled"
                                                : $"no answer within {timeout.TotalSeconds:0} s");
        }
        catch (Exception ex)
        {
            return new DiscoveryOutcome([], $"search failed: {ex.Message}");
        }
    }

    private static string Quote(string value) =>
        value.Length == 0 || value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;

    private sealed record DiscoveryOutcome(IReadOnlyList<DiscoveredDevice> Devices, string Summary);
}
