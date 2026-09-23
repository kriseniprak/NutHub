using Microsoft.Extensions.Logging;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Drivers.Hid.Transport;

namespace NutHub.Drivers.Hid.WinBattery;

/// <summary>The validated options of one winbattery UPS.</summary>
internal sealed record WinBatterySettings
{
    /// <summary>Unique id (exact) or device name (substring) of the battery; null takes the first UPS battery.</summary>
    public string? Battery { get; init; }

    /// <summary>Also accept batteries not flagged as short-term, i.e. laptop batteries.</summary>
    public bool IncludeSystemBatteries { get; init; }
}

/// <summary>Delays and limits of the winbattery driver; tests shorten them.</summary>
internal sealed record WinBatteryTimings
{
    public static WinBatteryTimings Default { get; } = new();

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Battery IOCTLs answer from the driver's cache in microseconds; a longer wait means a hung stack.</summary>
    public TimeSpan ReadTimeout { get; init; } = TimeSpan.FromSeconds(15);
}

/// <summary>
/// Reads a UPS through the Windows battery class: what the HidBatt driver makes of a USB UPS that Windows already
/// uses for its own power management. Read-only: the battery class has no commands and no settings.
/// </summary>
internal sealed class WinBatteryDriver(
    string upsName, WinBatterySettings settings, IBatterySource source, ILogger logger, TimeProvider time,
    WinBatteryTimings? timings = null) : IUpsDriver
{
    private readonly WinBatteryTimings _timings = timings ?? WinBatteryTimings.Default;
    private volatile bool _connected;
    private int _disposed;

    /// <summary>Picks the battery the options designate among those present.</summary>
    public static BatteryDevice? Select(IReadOnlyList<BatteryDevice> batteries, WinBatterySettings settings)
    {
        ArgumentNullException.ThrowIfNull(batteries);
        ArgumentNullException.ThrowIfNull(settings);
        IEnumerable<BatteryDevice> eligible = batteries.Where(b => settings.IncludeSystemBatteries || b.IsShortTerm);
        if (settings.Battery is not { } wanted)
        {
            return eligible.FirstOrDefault();
        }

        List<BatteryDevice> list = eligible.ToList();
        return list.FirstOrDefault(b => string.Equals(b.UniqueId, wanted, StringComparison.OrdinalIgnoreCase)) ??
               list.FirstOrDefault(b => b.Name?.Contains(wanted, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    public async Task RunAsync(IDriverContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        var link = new DriverLinkReporter(context, logger, upsName);
        context.ReportConnecting("Looking for the UPS among the Windows batteries.");
        while (!cancellationToken.IsCancellationRequested)
        {
            using var worker = new DeviceWorker($"winbattery {upsName}");
            IBatteryHandle? handle = null;
            try
            {
                (BatteryDevice battery, handle) = await worker.InvokeAsync(() => Connect(), cancellationToken)
                                                              .WaitAsync(_timings.ReadTimeout, time, cancellationToken)
                                                              .ConfigureAwait(false);
                logger.LogInformation("{Ups}: reading the Windows battery {Battery}.", upsName, battery.Description);
                using var timer = new PeriodicTimer(context.PollInterval, time);
                do
                {
                    IBatteryHandle open = handle;
                    BatteryReading reading = await worker.InvokeAsync(open.Read, cancellationToken)
                                                         .WaitAsync(_timings.ReadTimeout, time, cancellationToken)
                                                         .ConfigureAwait(false);
                    context.Publish(new DriverUpdate
                    {
                        Variables = WinBatteryMapper.BuildVariables(battery, reading),
                        VariableInfo = new Dictionary<string, VariableInfo>(),
                        Commands = [],
                    });
                    _connected = true;
                    link.Connected();
                }
                while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (TimeoutException)
            {
                link.Problem("The Windows battery driver stopped answering; reconnecting.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                link.Problem(ex.Message);
            }
            finally
            {
                _connected = false;
                if (handle is not null)
                {
                    IBatteryHandle toClose = handle;
                    try
                    {
                        await worker.InvokeAsync(toClose.Dispose).WaitAsync(TimeSpan.FromSeconds(2), time).ConfigureAwait(false);
                    }
                    catch (Exception ex) when (ex is TimeoutException or ObjectDisposedException or IOException)
                    {
                        toClose.Dispose();
                    }
                }
            }

            try
            {
                await Task.Delay(_timings.RetryDelay, time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public Task<CommandResult> InstantCommandAsync(string command, string? parameter, CancellationToken cancellationToken) =>
        Task.FromResult(_connected
            ? CommandResult.NotSupported("The Windows battery class has no instant commands.")
            : CommandResult.NotConnected());

    public Task<CommandResult> SetVariableAsync(string name, string value, CancellationToken cancellationToken) =>
        Task.FromResult(CommandResult.NotSupported("The Windows battery class has no writable variables."));

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private (BatteryDevice Battery, IBatteryHandle Handle) Connect()
    {
        IReadOnlyList<BatteryDevice> batteries = source.GetBatteries();
        BatteryDevice battery = Select(batteries, settings) ?? throw new IOException(NotFoundMessage(batteries));
        return (battery, source.Open(battery));
    }

    private string NotFoundMessage(IReadOnlyList<BatteryDevice> batteries)
    {
        if (settings.Battery is { } wanted)
        {
            return $"No battery with the unique id or name '{wanted}' was found among the {batteries.Count} Windows batteries.";
        }

        return batteries.Count == 0
            ? "Windows shows no battery. Check that the UPS is connected by USB and appears under \"Batteries\" in " +
              "Device Manager."
            : "Windows shows no UPS battery (only system batteries). Enable 'includeSystemBatteries' to read those.";
    }
}
