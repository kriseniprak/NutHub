using System.Globalization;
using Microsoft.Extensions.Logging;
using NutHub.Core.Drivers;
using NutHub.Drivers.Serial.Common;
using NutHub.Drivers.Serial.Transport;

namespace NutHub.Drivers.Serial.Megatec;

/// <summary>
/// The "megatec" driver: UPSes speaking the Megatec Q1 protocol family (NUT nutdrv_qx, blazer_ser and blazer_usb), over
/// a serial port, a TCP serial device server or a USB-serial HID bridge. Option names follow the nutdrv_qx ones in
/// camelCase (ondelay becomes onDelay, runtimecal becomes runtimeCal...).
/// </summary>
public sealed class MegatecDriverFactory : IUpsDriverFactory
{
    private static readonly TimeSpan DefaultSerialTimeout = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan DefaultUsbTimeout = TimeSpan.FromMilliseconds(3000);

    public string Id => "megatec";

    public string DisplayName => "Megatec / Q1 (Voltronic)";

    public string Description =>
        "UPSes using the Megatec Q1 protocol and its Voltronic variants (Tecnoware, Atlantis, Mecer, PowerWalker, " +
        "Powercool and many OEM brands), on a serial port, a serial device server or a USB cable with a USB-serial bridge.";

    public DriverPlatforms Platforms => DriverPlatforms.All;

    public bool SupportsDiscovery => true;

    public IReadOnlyList<DriverOption> Options { get; } =
    [
        TransportOptions.TransportOption(withUsb: true),
        TransportOptions.PortOption,
        TransportOptions.BaudRateOption(2400),
        new()
        {
            Key = "cablePower", Label = "Cable power (DTR / RTS)", Type = DriverOptionType.Choice, Default = "normal",
            Advanced = true, VisibleWhen = new("transport", [TransportOptions.Serial]),
            Help = "Some cables take their power from the DTR/RTS lines of the port.",
            Choices =
            [
                new("normal", "DTR on, RTS off (default)"),
                new("reverse", "DTR off, RTS on"),
                new("both", "DTR and RTS on"),
                new("none", "DTR and RTS off"),
            ],
        },
        TransportOptions.HostOption,
        TransportOptions.TcpPortOption,
        new()
        {
            Key = "vendorId", Label = "USB vendor id", VisibleWhen = new("transport", [TransportOptions.Usb]),
            Help = "Hexadecimal, e.g. 0665. Empty: the first known Megatec USB bridge found.",
        },
        new()
        {
            Key = "productId", Label = "USB product id", VisibleWhen = new("transport", [TransportOptions.Usb]),
            Help = "Hexadecimal, e.g. 5161.",
        },
        new()
        {
            Key = "serial", Label = "USB serial number", Advanced = true, VisibleWhen = new("transport", [TransportOptions.Usb]),
            Help = "Tells apart several identical USB UPSes.",
        },
        new()
        {
            Key = "usbSubdriver", Label = "USB bridge type", Type = DriverOptionType.Choice, Default = "auto", Advanced = true,
            VisibleWhen = new("transport", [TransportOptions.Usb]),
            Help = "How the USB cable carries the serial data (NUT 'subdriver'); automatic from the USB ids.",
            Choices =
            [
                new("auto", "Automatic"),
                new("cypress", "cypress"),
                new("phoenix", "phoenix"),
                new("ippon", "ippon"),
                new("sgs", "sgs"),
            ],
        },
        new()
        {
            Key = "protocol", Label = "Protocol", Type = DriverOptionType.Choice, Default = QxProtocols.Auto,
            Help = "Automatic detection tries every protocol and can take half a minute; the detected one is written in the log.",
            Choices =
            [
                new(QxProtocols.Auto, "Automatic detection"),
                new("megatec", "megatec (Q1, F, I)"),
                new("megatec/old", "megatec/old (D, F, I)"),
                new("mustek", "mustek (QS, F, I)"),
                new("zinto", "zinto (Q1, F, FW?)"),
                new("q1", "q1 (Q1 only)"),
                new("bestups", "bestups (Best Power, Sola Australia)"),
                new("voltronic-qs", "voltronic-qs"),
                new("voltronic-qs-hex", "voltronic-qs-hex"),
                new("voltronic", "voltronic (QPI/QGS)"),
            ],
        },
        new()
        {
            Key = "onDelay", Label = "Restart delay after power returns (s)", Type = DriverOptionType.Integer,
            Default = "180", Min = 0, Max = 599940,
            Help = "Initial ups.delay.start, rounded down to whole minutes. Some early firmware needs at least 3 minutes.",
        },
        new()
        {
            Key = "offDelay", Label = "Shutdown delay (s)", Type = DriverOptionType.Integer, Default = "30", Min = 12, Max = 5940,
            Help = "Initial ups.delay.shutdown: multiples of 6 s below one minute, whole minutes above.",
        },
        new()
        {
            Key = "batteryVoltageHigh", Label = "Battery voltage when full (V)", Type = DriverOptionType.Decimal,
            Advanced = true, Min = 0, Max = 1000,
            Help = "With the empty voltage, lets NutHub estimate battery.charge for UPSes that do not report it.",
        },
        new()
        {
            Key = "batteryVoltageLow", Label = "Battery voltage when empty (V)", Type = DriverOptionType.Decimal,
            Advanced = true, Min = 0, Max = 1000,
        },
        new()
        {
            Key = "batteryVoltageNominal", Label = "Nominal battery voltage (V)", Type = DriverOptionType.Decimal,
            Advanced = true, Min = 0, Max = 1000,
            Help = "Replaces a wrong value reported by the UPS; the full/empty voltages are guessed from it when not set.",
        },
        new()
        {
            Key = "batteryPacks", Label = "Battery packs", Type = DriverOptionType.Decimal, Advanced = true, Min = 0.5, Max = 200,
            Help = "The number the reported battery voltage must be multiplied by (e.g. 12 for a 24 V battery reported " +
                   "as 2 V per cell). Detected automatically when not set.",
        },
        new()
        {
            Key = "batteryVoltageReportsOnePack", Label = "Publish battery voltage × packs", Type = DriverOptionType.Boolean,
            Default = "false", Advanced = true,
            Help = "For UPSes that report the voltage of one pack or cell: publish battery.voltage multiplied by the packs.",
        },
        new()
        {
            Key = "runtimeCal", Label = "Runtime calibration", Advanced = true,
            Help = "Enables the battery.runtime estimate: runtime at a high load (s), that load (%), runtime at a low load " +
                   "(s), that load (%). Example: 240,100,720,50.",
        },
        new()
        {
            Key = "chargeTime", Label = "Battery recharge time (s)", Type = DriverOptionType.Integer, Default = "43200",
            Advanced = true, Min = 1, Max = 604800, Help = "Used with the runtime calibration.",
        },
        new()
        {
            Key = "idleLoad", Label = "Minimum load for the runtime estimate (%)", Type = DriverOptionType.Decimal,
            Default = "10", Advanced = true, Min = 0.1, Max = 100, Help = "Used with the runtime calibration.",
        },
        new()
        {
            Key = "ignoreSab", Label = "Ignore the 'Shutdown Active' bit", Type = DriverOptionType.Boolean, Default = "false",
            Advanced = true,
            Help = "Some UPSes always report a shutdown in progress, which puts FSD in the status and shuts every client down.",
        },
        new()
        {
            Key = "noRating", Label = "Do not query ratings (F)", Type = DriverOptionType.Boolean, Default = "false",
            Advanced = true,
        },
        new()
        {
            Key = "noVendor", Label = "Do not query vendor information (I)", Type = DriverOptionType.Boolean,
            Default = "false", Advanced = true,
        },
        new()
        {
            Key = "timeoutMs", Label = "Reply timeout (ms)", Type = DriverOptionType.Integer, Advanced = true, Min = 200,
            Max = 30000, Help = "How long to wait for each reply: 1500 by default, 3000 over USB.",
        },
    ];

    public Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<DiscoveredDevice>>(() =>
        {
            var devices = new List<DiscoveredDevice>();
            devices.AddRange(DiscoverUsbBridges());
            cancellationToken.ThrowIfCancellationRequested();
            devices.AddRange(TransportOptions.DiscoverSerialPorts("Megatec/Q1 UPS", "megatec"));
            return devices;
        }, cancellationToken);

    public IUpsDriver Create(DriverCreateContext context)
    {
        MegatecSettings settings = ParseSettings(context.Read);
        ILogger logger = context.LoggerFactory.CreateLogger<MegatecDriver>();
        ISerialTransport transport = TransportFactory.Create(settings.Transport, context.TimeProvider, logger);
        return new MegatecDriver(context.UpsName, settings, transport, context.TimeProvider, logger);
    }

    public void ValidateOptions(DriverOptionReader read) => ParseSettings(read);

    /// <summary>Validates the options; every error names the option at fault.</summary>
    internal static MegatecSettings ParseSettings(DriverOptionReader read)
    {
        (bool dtr, bool rts) = read.GetChoice("cablePower", "normal", "normal", "reverse", "both", "none") switch
        {
            "reverse" => (false, true),
            "both" => (true, true),
            "none" => (false, false),
            _ => (true, false),
        };

        TransportSettings transport = TransportOptions.Parse(read, withUsb: true, defaultBaud: 2400, dtr, rts);
        string protocol = read.GetChoice("protocol", QxProtocols.Auto, [QxProtocols.Auto, .. QxProtocols.Names]);

        RuntimeCalibration? calibration = null;
        if (read.GetString("runtimeCal") is { } text)
        {
            try
            {
                calibration = RuntimeCalibration.Parse(text);
            }
            catch (FormatException ex)
            {
                throw new DriverConfigurationException("Runtime calibration: " + ex.Message, "runtimeCal");
            }
        }

        double? high = OptionalDouble(read, "batteryVoltageHigh", 0, 1000);
        double? low = OptionalDouble(read, "batteryVoltageLow", 0, 1000);
        if (high is not null && low is not null && high <= low)
        {
            throw new DriverConfigurationException("The full battery voltage must be higher than the empty one.", "batteryVoltageHigh");
        }

        TimeSpan defaultTimeout = transport is UsbBridgeSettings ? DefaultUsbTimeout : DefaultSerialTimeout;
        return new MegatecSettings
        {
            Transport = transport,
            Protocol = protocol,
            ReplyTimeout = TimeSpan.FromMilliseconds(read.GetInt("timeoutMs", (int)defaultTimeout.TotalMilliseconds, 200, 30000)),
            OnDelay = read.GetInt("onDelay", 180, 0, 599940),
            OffDelay = read.GetInt("offDelay", 30, 12, 5940),
            BatteryVoltageHigh = high,
            BatteryVoltageLow = low,
            BatteryVoltageNominal = OptionalDouble(read, "batteryVoltageNominal", 0, 1000),
            BatteryPacks = OptionalDouble(read, "batteryPacks", 0.5, 200),
            BatteryVoltageReportsOnePack = read.GetBool("batteryVoltageReportsOnePack", false),
            RuntimeCalibration = calibration,
            ChargeTime = read.GetInt("chargeTime", 43200, 1, 604800),
            IdleLoad = read.GetDouble("idleLoad", 10, 0.1, 100) / 100,
            IgnoreShutdownActive = read.GetBool("ignoreSab", false),
            NoRating = read.GetBool("noRating", false),
            NoVendor = read.GetBool("noVendor", false),
        };
    }

    private static double? OptionalDouble(DriverOptionReader read, string key, double min, double max) =>
        read.GetString(key) is null ? null : read.GetDouble(key, 0, min, max);

    private static IEnumerable<DiscoveredDevice> DiscoverUsbBridges()
    {
        List<(int Vendor, int Product, string? Serial)> devices;
        try
        {
            IHidBridgeBackend backend = HidBridgeBackend.Create();
            devices = backend.GetDevices(null, null)
                .Where(d => UsbBridgeCatalog.Find(d.VendorId, d.ProductId) is { IsSupported: true })
                .Select(d => (d.VendorId, d.ProductId, backend.GetSerialNumber(d)))
                .ToList();
        }
        catch (Exception ex) when (HidBridgeTransport.IsHidPlatformFailure(ex))
        {
            // No HID access here (no hidraw, no libudev...): the serial ports are still worth listing.
            return [];
        }

        var result = new List<DiscoveredDevice>();
        foreach (IGrouping<(int, int, string?), (int, int, string?)> group in devices.GroupBy(d => d))
        {
            (int vendor, int product, string? serial) = group.Key;
            UsbBridgeInfo info = UsbBridgeCatalog.Find(vendor, product)!;
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["transport"] = TransportOptions.Usb,
                ["vendorId"] = vendor.ToString("x4", CultureInfo.InvariantCulture),
                ["productId"] = product.ToString("x4", CultureInfo.InvariantCulture),
            };
            if (!string.IsNullOrWhiteSpace(serial))
            {
                options["serial"] = serial;
            }

            string detail = $"{info.Devices}; {info.Kind!.Value.ToString().ToLowerInvariant()} bridge" +
                            (string.IsNullOrWhiteSpace(serial) ? "" : $", serial {serial}");
            result.Add(new DiscoveredDevice($"Megatec/Q1 UPS on USB {info.Id}", detail, options, "megatec"));
        }

        return result;
    }
}
