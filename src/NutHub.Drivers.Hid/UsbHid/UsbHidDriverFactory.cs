using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Drivers;
using NutHub.Drivers.Hid.Transport;
using NutHub.Drivers.Hid.UsbHid.Subdrivers;
using NutHub.Hidraw;

namespace NutHub.Drivers.Hid.UsbHid;

/// <summary>
/// The "usbhid" driver: USB UPSes that implement the HID Power Device class, read through the Linux hidraw interface
/// or HidSharp (the Windows HID API) with the mapping tables of NUT's usbhid-ups subdrivers.
/// </summary>
public sealed class UsbHidDriverFactory : IUpsDriverFactory
{
    /// <summary>Discovery reads every HID descriptor on the machine; the web API gives it 30 seconds in total.</summary>
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(20);

    private readonly IHidDeviceSource _source;
    private readonly ILogger _logger;
    private readonly UsbHidTimings _timings;

    public UsbHidDriverFactory(ILoggerFactory? loggerFactory = null)
        : this(CreateSource(loggerFactory ?? NullLoggerFactory.Instance, HidBackend.Current),
               (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<UsbHidDriverFactory>())
    {
    }

    internal UsbHidDriverFactory(IHidDeviceSource source, ILogger logger, UsbHidTimings? timings = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timings = timings ?? UsbHidTimings.Default;
    }

    /// <summary>
    /// The HID layer: hidraw used directly on Linux, which needs no libudev and works in containers; HidSharp on
    /// Windows, or on Linux when NUTHUB_HID_BACKEND=hidsharp (see <see cref="HidBackend"/>).
    /// </summary>
    internal static IHidDeviceSource CreateSource(ILoggerFactory loggerFactory, HidBackendKind backend)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        if (backend == HidBackendKind.Hidraw && OperatingSystem.IsLinux())
        {
            return new HidrawDeviceSource(LibcHidrawSystem.Instance, loggerFactory.CreateLogger<HidrawDeviceSource>(),
                                          LinuxUsbStrings.GetString);
        }

        return new HidSharpDeviceSource(loggerFactory.CreateLogger<HidSharpDeviceSource>());
    }

    public string Id => "usbhid";

    public string DisplayName => "USB HID Power Device";

    public string Description =>
        "USB UPSes that follow the HID Power Device class: APC, Eaton/MGE, CyberPower, Tripp Lite, Belkin, Liebert, " +
        "PowerCOM, Delta, Salicru and many more (the equivalent of NUT usbhid-ups).";

    public DriverPlatforms Platforms => DriverPlatforms.Windows | DriverPlatforms.Linux;

    public bool SupportsDiscovery => true;

    public IReadOnlyList<DriverOption> Options { get; } =
    [
        new()
        {
            Key = "vendorId", Label = "USB vendor id",
            Help = "Hexadecimal, e.g. 051d for APC or 0764 for CyberPower. Leave the device fields empty to use the " +
                   "first UPS found.",
        },
        new() { Key = "productId", Label = "USB product id", Help = "Hexadecimal, e.g. 0002." },
        new()
        {
            Key = "serial", Label = "Serial number",
            Help = "Tells identical UPSes apart. Compared without regard to case.",
        },
        new() { Key = "product", Label = "Product name contains", Help = "Part of the USB product string, e.g. \"Back-UPS\"." },
        new()
        {
            Key = "devicePath", Label = "Device path", Advanced = true,
            Help = "The exact HID device path (\"/dev/hidraw0\", \"\\\\?\\hid#vid_...\"), only for identical UPSes " +
                   "without serial numbers. It can change when the UPS is reconnected.",
        },
        new()
        {
            Key = "subdriver", Label = "Subdriver", Type = DriverOptionType.Choice, Default = "auto",
            Choices = [new("auto", "Automatic (by USB vendor and product)"), .. SubdriverCatalog.Choices.Select(c => new DriverOptionChoice(c.Id, c.Label))],
            Help = "The vendor-specific mapping to use. Automatic picks it like NUT does.",
        },
        new()
        {
            Key = "onlineDischarge", Label = "On line and discharging means on battery", Type = DriverOptionType.Boolean,
            Default = "false", Advanced = true,
            Help = "For models (e.g. CyberPower UT) that report on line and discharging while they run on battery " +
                   "(NUT onlinedischarge_onbattery).",
        },
        new()
        {
            Key = "onlineDischargeCalibration", Label = "On line and discharging means calibrating",
            Type = DriverOptionType.Boolean, Default = "false", Advanced = true,
            Help = "For models (some APC) that report on line and discharging during a runtime calibration " +
                   "(NUT onlinedischarge_calibration).",
        },
        new()
        {
            Key = "offDelay", Label = "Shutdown delay (seconds)", Type = DriverOptionType.Integer, Min = 0, Max = 7200,
            Advanced = true,
            Help = "Initial ups.delay.shutdown: how long the UPS waits before cutting the power after a shutdown " +
                   "command (NUT offdelay). Empty keeps the subdriver default (usually 20).",
        },
        new()
        {
            Key = "onDelay", Label = "Restart delay (seconds)", Type = DriverOptionType.Integer, Min = 0, Max = 7200,
            Advanced = true,
            Help = "Initial ups.delay.start: how long the UPS waits before restoring the power once mains returns " +
                   "(NUT ondelay). Empty keeps the subdriver default (usually 30).",
        },
    ];

    public async Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(CancellationToken cancellationToken)
    {
        // Descriptor reads block; a dedicated thread keeps them off the thread pool.
        Task<IReadOnlyList<DiscoveredDevice>> discovery = Task.Factory.StartNew(
            () => UsbHidDeviceLocator.Discover(_source, _logger, cancellationToken),
            cancellationToken, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            return await discovery.WaitAsync(DiscoveryTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Listing the USB UPSes took longer than {Seconds} s and was abandoned.", DiscoveryTimeout.TotalSeconds);
            return [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Listing the USB UPSes failed.");
            return [];
        }
    }

    public IUpsDriver Create(DriverCreateContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        UsbHidSettings settings = ReadSettings(context.Read);
        return new UsbHidDriver(context.UpsName, settings, _source, context.LoggerFactory.CreateLogger<UsbHidDriver>(),
                                context.TimeProvider, _timings);
    }

    public void ValidateOptions(DriverOptionReader read) => ReadSettings(read);

    /// <summary>Validates the options; every error names its option so the web panel can point at the field.</summary>
    internal static UsbHidSettings ReadSettings(DriverOptionReader read)
    {
        return new UsbHidSettings
        {
            UsbReset = read.GetBool("usbReset", true),
            VendorId = ReadUsbId(read, "vendorId"),
            ProductId = ReadUsbId(read, "productId"),
            Serial = read.GetString("serial"),
            Product = read.GetString("product"),
            DevicePath = read.GetString("devicePath"),
            Subdriver = read.GetChoice("subdriver", "auto", ["auto", .. SubdriverCatalog.Ids]),
            OnlineDischargeOnBattery = read.GetBool("onlineDischarge", false),
            OnlineDischargeCalibration = read.GetBool("onlineDischargeCalibration", false),
            OffDelay = read.GetString("offDelay") is null ? null : read.GetInt("offDelay", 20, 0, 7200),
            OnDelay = read.GetString("onDelay") is null ? null : read.GetInt("onDelay", 30, 0, 7200),
        };
    }

    private static int? ReadUsbId(DriverOptionReader read, string key)
    {
        int? value = read.GetHex(key);
        if (value > 0xFFFF)
        {
            throw new DriverConfigurationException($"The option '{key}' must be a USB id of at most four hexadecimal digits.", key);
        }

        return value;
    }
}
