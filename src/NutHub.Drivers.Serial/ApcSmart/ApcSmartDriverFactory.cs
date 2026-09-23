using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NutHub.Core.Drivers;
using NutHub.Drivers.Serial.Common;
using NutHub.Drivers.Serial.Transport;

namespace NutHub.Drivers.Serial.ApcSmart;

/// <summary>
/// The "apcsmart" driver: APC Smart-UPS, Matrix-UPS and Back-UPS Pro models with a DB-9 serial port speaking the APC
/// Smart protocol (NUT apcsmart), on a local serial port or a TCP serial device server. Option names follow NUT in
/// camelCase where NUT has one (sdtype becomes shutdownType, awd wakeUpDelay, cshdelay csDelay).
/// </summary>
public sealed partial class ApcSmartDriverFactory : IUpsDriverFactory
{
    private const int DefaultTimeoutMs = 3000;

    public string Id => "apcsmart";

    public string DisplayName => "APC Smart (serial)";

    public string Description =>
        "APC Smart-UPS, Matrix-UPS and Back-UPS Pro with a DB-9 serial port and the APC 940-0024 \"smart\" cable, on a " +
        "serial port or a serial device server. For APC UPSes on USB use the USB HID driver.";

    public DriverPlatforms Platforms => DriverPlatforms.All;

    public bool SupportsDiscovery => true;

    public IReadOnlyList<DriverOption> Options { get; } =
    [
        TransportOptions.TransportOption(withUsb: false),
        TransportOptions.PortOption,
        TransportOptions.BaudRateOption(2400),
        TransportOptions.HostOption,
        TransportOptions.TcpPortOption,
        new()
        {
            Key = "shutdownType", Label = "Shutdown method", Type = DriverOptionType.Choice, Default = "0", Advanced = true,
            Help = "What shutdown.return without a parameter sends (NUT 'sdtype'). The default powers the load off and " +
                   "back on when the mains returns, whether the UPS is on battery or not.",
            Choices =
            [
                new("0", "Soft hibernate (S) on battery, hard hibernate (@) on line power (default)"),
                new("1", "Soft hibernate (S), hard hibernate (@) if it fails"),
                new("2", "Power off now (Z), stays off"),
                new("3", "Power off after the grace delay (K), stays off"),
                new("4", "Simulated power failure, then soft hibernate (CS, for Back-UPS CS)"),
                new("5", "Hard hibernate (@)"),
            ],
        },
        new()
        {
            Key = "wakeUpDelay", Label = "Extra wake-up delay (× 6 min)", Type = DriverOptionType.Integer, Advanced = true,
            Default = "0", Min = 0, Max = 999,
            Help = "Hard hibernate only (NUT 'awd'): how many 6-minute periods to wait after the mains returns before " +
                   "powering the load on again.",
        },
        new()
        {
            Key = "csDelay", Label = "Delay of the CS method (s)", Type = DriverOptionType.Decimal, Advanced = true,
            Default = "3.5", Min = 0, Max = 9.9,
            Help = "Pause between the simulated power failure and the soft hibernate command (NUT 'cshdelay').",
            VisibleWhen = new("shutdownType", ["4"]),
        },
        new()
        {
            Key = "timeoutMs", Label = "Reply timeout (ms)", Type = DriverOptionType.Integer, Advanced = true,
            Default = DefaultTimeoutMs.ToString(CultureInfo.InvariantCulture), Min = 500, Max = 30000,
            Help = "How long to wait for each reply; raise it for slow serial device servers.",
        },
    ];

    public Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<DiscoveredDevice>>(
            () => TransportOptions.DiscoverSerialPorts("APC Smart-UPS", "apc").ToList(), cancellationToken);

    public IUpsDriver Create(DriverCreateContext context)
    {
        ApcSmartSettings settings = ParseSettings(context.Read);
        ILogger logger = context.LoggerFactory.CreateLogger<ApcSmartDriver>();
        ISerialTransport transport = TransportFactory.Create(settings.Transport, context.TimeProvider, logger);
        return new ApcSmartDriver(context.UpsName, settings, transport, context.TimeProvider, logger);
    }

    public void ValidateOptions(DriverOptionReader read) => ParseSettings(read);

    /// <summary>Validates the options; every error names the option at fault.</summary>
    internal static ApcSmartSettings ParseSettings(DriverOptionReader read)
    {
        // DTR on, RTS off: what NUT sets for the 940-0095B cable; the 940-0024 smart cables do not use these lines.
        TransportSettings transport = TransportOptions.Parse(read, withUsb: false, defaultBaud: 2400, dtr: true, rts: false);

        string wakeUp = read.GetString("wakeUpDelay") ?? "0";
        if (!WakeUpDigits().IsMatch(wakeUp))
        {
            throw new DriverConfigurationException("The extra wake-up delay must be a whole number from 0 to 999.", "wakeUpDelay");
        }

        return new ApcSmartSettings
        {
            Transport = transport,
            ReplyTimeout = TimeSpan.FromMilliseconds(read.GetInt("timeoutMs", DefaultTimeoutMs, 500, 30000)),
            ShutdownType = int.Parse(read.GetChoice("shutdownType", "0", "0", "1", "2", "3", "4", "5"), CultureInfo.InvariantCulture),

            // Three digits always: the two-digit form is only for the explicit "at:nn" parameter of shutdown.return.
            WakeUpDelay = int.Parse(wakeUp, CultureInfo.InvariantCulture).ToString("000", CultureInfo.InvariantCulture),
            CsDelay = TimeSpan.FromSeconds(read.GetDouble("csDelay", 3.5, 0, 9.9)),
        };
    }

    [GeneratedRegex("^[0-9]{1,3}$")]
    private static partial Regex WakeUpDigits();
}
