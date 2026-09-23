using NutHub.Drivers.Hid.Descriptors;
using NutHub.Drivers.Hid.Transport;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

/// <summary>Tripp Lite, and the HP and Delta models built on the same firmware (port of NUT drivers/tripplite-hid.c).</summary>
internal sealed partial class TrippLiteSubdriver : UsbHidSubdriver
{
    private const int HpVendorId = 0x03f0;
    private const int TrippLiteVendorId = 0x09ae;

    private double _batteryScale = 1.0;
    private double _voltageScale = 1.0;
    private double _frequencyScale = 1.0;
    private double _currentScale = 1.0;

    public override string Id => "tripplite";

    public override string DisplayName => "Tripp Lite";

    public override IReadOnlyList<UsbDeviceId> SupportedDevices => DeviceIds;

    protected override HidUsageTable VendorUsages => VendorUsageTable;

    protected override HidMapping[] CreateMappings() => BuildMappings();

    public override bool Claim(HidDeviceInfo device, bool productIdGiven) => Check(device) switch
    {
        UsbSupport.Supported => true,
        // 09ae:0001 is the old serial-over-USB protocol (tripplite_usb driver), never HID PDC.
        UsbSupport.PossiblySupported when device.VendorId == TrippLiteVendorId && device.ProductId == 0x0001 => false,
        UsbSupport.PossiblySupported when device.VendorId is HpVendorId or TrippLiteVendorId => productIdGiven,
        _ => false,
    };

    protected override void ApplyHook(string hook, HidDeviceInfo device)
    {
        switch (hook)
        {
            case "battery_scale_1dot0":
                _batteryScale = 1.0;
                break;
            case "battery_scale_0dot1":
                _batteryScale = 0.1;
                break;
            case "smart1500lcdt_scale":
                _batteryScale = 100000.0;
                _voltageScale = 100000.0;
                _frequencyScale = 0.01;
                _currentScale = 0.01;
                break;
        }
    }

    /// <summary>
    /// SMART1500LCD and relatives declare active power in the PDC power unit with exponent 0, which reads as
    /// 10^-7 W; the unit implies exponent 7 (NUT tripplite_fix_report_desc).
    /// </summary>
    public override bool FixReportDescriptor(HidDeviceInfo device, HidReportDescriptor descriptor)
    {
        bool changed = false;
        foreach (HidField field in descriptor.Fields)
        {
            if (field.Path.Length > 0 && field.Path.Last == PowerDeviceUsages.ActivePower &&
                field.Unit == PowerDeviceUsages.PowerUnit && field.UnitExponent == 0)
            {
                field.UnitExponent = 7;
                changed = true;
            }
        }

        return changed;
    }

    private static string? TrippliteChemistryFun(double value, IHidConversionContext context)
    {
        // These two models return garbage for the chemistry string index.
        if (context.GetVariable("ups.productid") is "1003" or "2005")
        {
            return "unknown";
        }

        return context.GetIndexedString((int)Math.Clamp(HidValueCodec.Truncate(value), 0, 255));
    }

    private string? TrippliteBattvoltFun(double value, IHidConversionContext context) =>
        CFormat.FormatDouble("%.1f", _batteryScale * value);

    private string? TrippliteIovoltFun(double value, IHidConversionContext context) =>
        CFormat.FormatDouble("%.1f", _voltageScale * value);

    private string? TrippliteIofreqFun(double value, IHidConversionContext context) =>
        CFormat.FormatDouble("%.1f", _frequencyScale * value);

    private string? TrippliteIoampFun(double value, IHidConversionContext context) =>
        CFormat.FormatDouble("%.1f", _currentScale * value);
}
