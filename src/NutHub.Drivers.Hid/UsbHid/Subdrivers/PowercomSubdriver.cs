using System.Globalization;
using NutHub.Drivers.Hid.Descriptors;
using NutHub.Drivers.Hid.Transport;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

/// <summary>
/// PowerCOM (port of NUT drivers/powercom-hid.c). Shutdown and start delays are written as byte-swapped
/// minutes/seconds words, and the oldest models (0d9f:0001) only talk through input reports.
/// </summary>
internal sealed partial class PowercomSubdriver : UsbHidSubdriver
{
    public override string Id => "powercom";

    public override string DisplayName => "PowerCOM";

    public override IReadOnlyList<UsbDeviceId> SupportedDevices => DeviceIds;

    protected override HidUsageTable VendorUsages => VendorUsageTable;

    protected override HidMapping[] CreateMappings() => BuildMappings();

    public override bool Claim(HidDeviceInfo device, bool productIdGiven) => Check(device) switch
    {
        UsbSupport.Supported => true,
        // 0d9f:0002 is a serial (non-HID) PowerCOM protocol.
        UsbSupport.PossiblySupported => device.ProductId != 0x0002 && productIdGiven,
        _ => false,
    };

    protected override void OnAttached(HidDeviceInfo device)
    {
        if (device.ProductId == 0x0001)
        {
            Quirks.InterruptOnly = true;
        }
    }

    public override string? FormatManufacturer(HidDeviceInfo device, IHidConversionContext context) =>
        device.Manufacturer ?? "PowerCOM";

    /// <summary>The start delay is a byte-swapped count of minutes.</summary>
    private static string? PowercomStartupFun(double value, IHidConversionContext context)
    {
        int i = (ushort)HidValueCodec.Truncate(value);
        return (60 * (((i & 0x00FF) << 8) + (i >> 8))).ToString(CultureInfo.InvariantCulture);
    }

    private static double? PowercomStartupNuf(string? text, IHidConversionContext context)
    {
        int seconds = Seconds(text, context.Settings.OnDelay, context.GetVariable("ups.delay.start"), 120);
        if (seconds < 0)
        {
            return 0;
        }

        int minutes = (ushort)(seconds / 60);
        return (ushort)(minutes << 8) + (ushort)(minutes >> 8);
    }

    /// <summary>The shutdown delay is minutes in the high byte and seconds in the low byte.</summary>
    private static string? PowercomShutdownFun(double value, IHidConversionContext context)
    {
        int i = (ushort)HidValueCodec.Truncate(value);
        return (60 * (i >> 8) + (i & 0x00FF)).ToString(CultureInfo.InvariantCulture);
    }

    private static double? PowercomShutdownNuf(string? text, IHidConversionContext context) =>
        ShutdownWord(text, context, flag: 0x0040);

    /// <summary>Like shutdown, with the flag that keeps the output off afterwards.</summary>
    private static double? PowercomStayoffNuf(string? text, IHidConversionContext context) =>
        ShutdownWord(text, context, flag: 0x0080);

    private static double? ShutdownWord(string? text, IHidConversionContext context, int flag)
    {
        int seconds = Seconds(text, context.Settings.OffDelay, context.GetVariable("ups.delay.shutdown"), 60);
        if (seconds < 0 || seconds > ushort.MaxValue)
        {
            return 0;
        }

        int value = seconds == 0 ? 1 : seconds;
        return ((value / 60) << 8) + (value % 60) | flag;
    }

    /// <summary>The value given, else the configured delay, else the current variable, else the default.</summary>
    private static int Seconds(string? text, int? configured, string? variable, int fallback)
    {
        foreach (string? candidate in new[] { text, configured?.ToString(CultureInfo.InvariantCulture), variable })
        {
            if (!string.IsNullOrWhiteSpace(candidate) &&
                int.TryParse(candidate.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
            {
                return parsed;
            }
        }

        return fallback;
    }

    private static string? PowercomVoltageConversionFun(double value, IHidConversionContext context) =>
        CFormat.FormatDouble("%0.0f", value * 4);

    private static string? PowercomUpsfailConversionFun(double value, IHidConversionContext context) =>
        Bits(value, 0x0001) ? "fanfail" : "!fanfail";

    private static string? PowercomReplacebattConversionFun(double value, IHidConversionContext context) =>
        Bits(value, 0x0002) ? "replacebatt" : "!replacebatt";

    private static string? PowercomTestConversionFun(double value, IHidConversionContext context) =>
        Bits(value, 0x0004) ? "cal" : "!cal";

    private static string? PowercomShutdownimmConversionFun(double value, IHidConversionContext context) =>
        Bits(value, 0x0010) ? "shutdownimm" : "!shutdownimm";

    private static string? PowercomOnlineConversionFun(double value, IHidConversionContext context) =>
        Bits(value, 0x0001) ? "!online" : "online";

    private static string? PowercomLowbattConversionFun(double value, IHidConversionContext context) =>
        Bits(value, 0x0002) ? "lowbatt" : "!lowbatt";

    private static string? PowercomTrimConversionFun(double value, IHidConversionContext context) =>
        (HidValueCodec.Truncate(value) & 0x0018) == 0x0008 ? "trim" : "!trim";

    private static string? PowercomBoostConversionFun(double value, IHidConversionContext context) =>
        (HidValueCodec.Truncate(value) & 0x0018) == 0x0018 ? "boost" : "!boost";

    private static string? PowercomOverloadConversionFun(double value, IHidConversionContext context) =>
        Bits(value, 0x0020) ? "overload" : "!overload";

    private static bool Bits(double value, long mask) => (HidValueCodec.Truncate(value) & mask) != 0;
}
