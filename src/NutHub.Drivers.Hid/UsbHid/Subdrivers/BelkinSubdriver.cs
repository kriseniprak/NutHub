using NutHub.Drivers.Hid.Descriptors;
using NutHub.Drivers.Hid.Transport;
using Microsoft.Extensions.Logging;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

/// <summary>
/// Belkin, and the Liebert PSI/GXT models that share its firmware (port of NUT drivers/belkin-hid.c). Belkin packs
/// the status into vendor bit fields; Liebert units report voltages with wrong exponents that are corrected from
/// the first plausible reading.
/// </summary>
internal sealed partial class BelkinSubdriver : UsbHidSubdriver
{
    private const int BelkinVendorId = 0x050d;
    private const int LiebertVendorId = 0x10af;

    private double _liebertConfigVoltageFactor = 1.0;
    private double _liebertLineVoltageFactor = 1.0;

    public override string Id => "belkin";

    public override string DisplayName => "Belkin / Liebert";

    public override IReadOnlyList<UsbDeviceId> SupportedDevices => DeviceIds;

    protected override HidUsageTable VendorUsages => VendorUsageTable;

    protected override HidMapping[] CreateMappings() => BuildMappings();

    public override bool Claim(HidDeviceInfo device, bool productIdGiven) => Check(device) switch
    {
        UsbSupport.Supported => true,
        // 050d:0218 is a Belkin USB hub-ish device, not a UPS.
        UsbSupport.PossiblySupported when device.VendorId == BelkinVendorId && device.ProductId == 0x0218 => false,
        UsbSupport.PossiblySupported when device.VendorId is BelkinVendorId or LiebertVendorId => productIdGiven,
        _ => false,
    };

    public override string? FormatModel(HidDeviceInfo device, IHidConversionContext context) =>
        string.IsNullOrEmpty(device.Product) ? "unknown" : device.Product;

    public override string? FormatManufacturer(HidDeviceInfo device, IHidConversionContext context)
    {
        string mfr = (device.Manufacturer ?? "Belkin").TrimStart(' ');
        return mfr.Length == 0 ? "Belkin" : mfr;
    }

    /// <summary>Old Belkin models have no USB serial number but report it through a HID string item.</summary>
    public override string? FormatSerial(HidDeviceInfo device, IHidConversionContext context) =>
        device.Serial ?? context.ReadItemString("UPS.PowerSummary.iSerialNumber");

    private static string? LiebertOnlineFun(double value, IHidConversionContext context) => value != 0 ? "online" : "!online";

    private static string? LiebertDischargingFun(double value, IHidConversionContext context) => value != 0 ? "dischrg" : "!dischrg";

    private static string? LiebertChargingFun(double value, IHidConversionContext context) => value != 0 ? "chrg" : "!chrg";

    private static string? LiebertLowbattFun(double value, IHidConversionContext context) => value != 0 ? "lowbatt" : "!lowbatt";

    private static string? LiebertReplacebattFun(double value, IHidConversionContext context) => value != 0 ? "replacebatt" : "!replacebatt";

    private static string? LiebertShutdownimmFun(double value, IHidConversionContext context) => value != 0 ? "shutdownimm" : "!shutdownimm";

    /// <summary>
    /// Liebert firmwares report the nominal voltage as 1e-7 (exponent off by 8 or so); when that exact value shows
    /// up, both nominal and line voltages are rescaled from then on.
    /// </summary>
    private string? LiebertConfigVoltageFun(double value, IHidConversionContext context)
    {
        if (value < 1)
        {
            if (Math.Abs(value - 1e-7) < 1e-9)
            {
                _liebertConfigVoltageFactor = 1e8;
                _liebertLineVoltageFactor = 1e7;
            }
            else
            {
                context.Logger.LogInformation("ConfigVoltage exponent looks wrong, but not correcting.");
            }
        }

        return CFormat.FormatDouble("%.1f", value * _liebertConfigVoltageFactor);
    }

    private string? LiebertLineVoltageFun(double value, IHidConversionContext context)
    {
        if (value < 1)
        {
            if (Math.Abs(value - 1e-5) < 4 * 1e-5)
            {
                _liebertLineVoltageFactor = 1e7;
            }
            else if (Math.Abs(value - 1e-3) < 4 * 1e-3)
            {
                _liebertLineVoltageFactor = 1e5;
            }
            else
            {
                context.Logger.LogInformation("LineVoltage exponent looks wrong, but not correcting.");
            }
        }

        return CFormat.FormatDouble("%.1f", value * _liebertLineVoltageFactor);
    }

    private static string? BelkinFirmwareConversionFun(double value, IHidConversionContext context) =>
        (HidValueCodec.Truncate(value) >> 4).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string? BelkinUpstypeConversionFun(double value, IHidConversionContext context) =>
        (HidValueCodec.Truncate(value) & 0x0F) switch
        {
            1 => "offline",
            2 => "line-interactive",
            3 => "simple online",
            4 => "simple offline",
            5 => "simple line-interactive",
            _ => "online",
        };

    private static string? BelkinSensitivityConversionFun(double value, IHidConversionContext context) =>
        HidValueCodec.Truncate(value) switch
        {
            1 => "reduced",
            2 => "low",
            _ => "normal",
        };

    private static string? BelkinOverloadConversionFun(double value, IHidConversionContext context) =>
        Bit(value, 0x0010) ? "overload" : "!overload";

    private static string? BelkinOverheatConversionFun(double value, IHidConversionContext context) =>
        Bit(value, 0x0040) ? "overheat" : "!overheat";

    private static string? BelkinCommfaultConversionFun(double value, IHidConversionContext context) =>
        Bit(value, 0x0080) ? "commfault" : "!commfault";

    private static string? BelkinAwaitingpowerConversionFun(double value, IHidConversionContext context) =>
        Bit(value, 0x2000) ? "awaitingpower" : "!awaitingpower";

    /// <summary>Bit 0 of the Belkin power status means "on battery".</summary>
    private static string? BelkinOnlineConversionFun(double value, IHidConversionContext context) =>
        Bit(value, 0x0001) ? "!online" : "online";

    private static string? BelkinLowbattConversionFun(double value, IHidConversionContext context) =>
        Bit(value, 0x0004) ? "lowbatt" : "!lowbatt";

    private static string? BelkinDepletedConversionFun(double value, IHidConversionContext context) =>
        Bit(value, 0x0040) ? "depleted" : "!depleted";

    private static string? BelkinReplacebattConversionFun(double value, IHidConversionContext context) =>
        Bit(value, 0x0080) ? "replacebatt" : "!replacebatt";

    private static bool Bit(double value, long mask) => (HidValueCodec.Truncate(value) & mask) != 0;
}
