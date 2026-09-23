using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Drivers;

namespace NutHub.Drivers.Hid.WinBattery;

/// <summary>
/// The "winbattery" driver: a UPS that Windows already manages as a battery (HidBatt). It works where the usbhid
/// driver cannot open the device, at the price of fewer variables and no commands.
/// </summary>
public sealed class WinBatteryDriverFactory : IUpsDriverFactory
{
    private readonly IBatterySource? _source;
    private readonly ILogger _logger;
    private readonly WinBatteryTimings _timings;

    public WinBatteryDriverFactory(ILoggerFactory? loggerFactory = null)
        : this(CreateSource(loggerFactory ?? NullLoggerFactory.Instance),
               (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<WinBatteryDriverFactory>())
    {
    }

    /// <param name="source">The batteries; null on systems without the Windows battery class.</param>
    internal WinBatteryDriverFactory(IBatterySource? source, ILogger logger, WinBatteryTimings? timings = null)
    {
        _source = source;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timings = timings ?? WinBatteryTimings.Default;
    }

    public string Id => "winbattery";

    public string DisplayName => "Windows battery";

    public string Description =>
        "Any UPS that Windows shows as a battery (Windows only). Use it when the USB HID driver cannot open the UPS; " +
        "it reports charge, runtime and power state, without commands.";

    public DriverPlatforms Platforms => DriverPlatforms.Windows;

    public bool SupportsDiscovery => true;

    public IReadOnlyList<DriverOption> Options { get; } =
    [
        new()
        {
            Key = "battery", Label = "Battery",
            Help = "The unique id or part of the name of the battery to read. Empty takes the first UPS battery.",
        },
        new()
        {
            Key = "includeSystemBatteries", Label = "Include laptop batteries", Type = DriverOptionType.Boolean,
            Default = "false", Advanced = true,
            Help = "Also accept batteries that Windows does not flag as short-term (UPS) batteries.",
        },
    ];

    public async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(CancellationToken cancellationToken)
    {
        if (_source is null)
        {
            return [];
        }

        IBatterySource source = _source;
        try
        {
            IReadOnlyList<BatteryDevice> batteries = await Task.Factory.StartNew(
                    source.GetBatteries, cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default)
                .WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            return batteries.OrderByDescending(b => b.IsShortTerm).Select(ToDiscovered).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Listing the Windows batteries failed.");
            return [];
        }
    }

    public IUpsDriver Create(DriverCreateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_source is null)
        {
            throw new DriverConfigurationException("The winbattery driver works on Windows only.");
        }

        DriverOptionReader read = context.Read;
        var settings = new WinBatterySettings
        {
            Battery = read.GetString("battery"),
            IncludeSystemBatteries = read.GetBool("includeSystemBatteries", false),
        };
        return new WinBatteryDriver(context.UpsName, settings, _source, context.LoggerFactory.CreateLogger<WinBatteryDriver>(),
                                    context.TimeProvider, _timings);
    }

    /// <summary>Every battery is listed, UPS ones first; laptop batteries come with the option that admits them.</summary>
    internal static DiscoveredDevice ToDiscovered(BatteryDevice battery)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if ((battery.UniqueId ?? battery.Name) is { } id)
        {
            options["battery"] = id;
        }

        if (!battery.IsShortTerm)
        {
            options["includeSystemBatteries"] = "true";
        }

        string kind = battery.IsShortTerm ? "UPS battery" : "System battery (not a UPS)";
        string detail = battery.Serial is null ? kind : $"{kind} · serial number {battery.Serial}";
        return new DiscoveredDevice(battery.Description, detail, options, SuggestName(battery));
    }

    private static string SuggestName(BatteryDevice battery)
    {
        string name = new string((battery.Name ?? "ups").ToLowerInvariant()
                .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray())
            .Trim('-');
        while (name.Contains("--", StringComparison.Ordinal))
        {
            name = name.Replace("--", "-", StringComparison.Ordinal);
        }

        if (name.Length > 32)
        {
            name = name[..32].TrimEnd('-');
        }

        return name.Length == 0 ? "ups" : name;
    }

    private static IBatterySource? CreateSource(ILoggerFactory loggerFactory) =>
        OperatingSystem.IsWindows() ? new WindowsBatterySource(loggerFactory.CreateLogger<WindowsBatterySource>()) : null;
}
