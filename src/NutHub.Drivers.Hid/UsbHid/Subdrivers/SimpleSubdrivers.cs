using NutHub.Drivers.Hid.Descriptors;
using NutHub.Drivers.Hid.Transport;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

/// <summary>Arduino boards running the HIDPowerDevice library (port of NUT drivers/arduino-hid.c).</summary>
internal sealed partial class ArduinoSubdriver : UsbHidSubdriver
{
    public override string Id => "arduino";

    public override string DisplayName => "Arduino";

    public override IReadOnlyList<UsbDeviceId> SupportedDevices => DeviceIds;

    protected override HidUsageTable VendorUsages => VendorUsageTable;

    protected override HidMapping[] CreateMappings() => BuildMappings();

    public override string? FormatManufacturer(HidDeviceInfo device, IHidConversionContext context) =>
        device.Manufacturer ?? "Arduino";
}

/// <summary>Phoenixtec-built Liebert units (port of NUT drivers/liebert-hid.c).</summary>
internal sealed partial class LiebertSubdriver : UsbHidSubdriver
{
    public override string Id => "liebert";

    public override string DisplayName => "Liebert (Phoenixtec)";

    public override IReadOnlyList<UsbDeviceId> SupportedDevices => DeviceIds;

    protected override HidUsageTable VendorUsages => VendorUsageTable;

    protected override HidMapping[] CreateMappings() => BuildMappings();

    public override string? FormatManufacturer(HidDeviceInfo device, IHidConversionContext context) =>
        device.Manufacturer ?? "Liebert";
}

/// <summary>Powervar (port of NUT drivers/powervar-hid.c).</summary>
internal sealed partial class PowervarSubdriver : UsbHidSubdriver
{
    public override string Id => "powervar";

    public override string DisplayName => "Powervar";

    public override IReadOnlyList<UsbDeviceId> SupportedDevices => DeviceIds;

    protected override HidUsageTable VendorUsages => VendorUsageTable;

    protected override HidMapping[] CreateMappings() => BuildMappings();

    public override string? FormatManufacturer(HidDeviceInfo device, IHidConversionContext context) =>
        device.Manufacturer ?? "Powervar";
}

/// <summary>Salicru (port of NUT drivers/salicru-hid.c).</summary>
internal sealed partial class SalicruSubdriver : UsbHidSubdriver
{
    public override string Id => "salicru";

    public override string DisplayName => "Salicru";

    public override IReadOnlyList<UsbDeviceId> SupportedDevices => DeviceIds;

    protected override HidUsageTable VendorUsages => VendorUsageTable;

    protected override HidMapping[] CreateMappings() => BuildMappings();

    public override string? FormatManufacturer(HidDeviceInfo device, IHidConversionContext context) =>
        device.Manufacturer ?? "Salicru";
}

/// <summary>iDowell and GoldenMate lithium UPSes (port of NUT drivers/idowell-hid.c).</summary>
internal sealed partial class IDowellSubdriver : UsbHidSubdriver
{
    private const int PhoenixtecVendorId = 0x06da;
    private const int IDowellVendorId = 0x075d;

    public override string Id => "idowell";

    public override string DisplayName => "iDowell / GoldenMate";

    public override IReadOnlyList<UsbDeviceId> SupportedDevices => DeviceIds;

    protected override HidUsageTable VendorUsages => VendorUsageTable;

    protected override HidMapping[] CreateMappings() => BuildMappings();

    /// <summary>06da:ffff is shared with other Phoenixtec brands; GoldenMate identifies itself by its strings.</summary>
    public override bool Claim(HidDeviceInfo device, bool productIdGiven) => Check(device) switch
    {
        UsbSupport.Supported => device.VendorId != PhoenixtecVendorId || IsGoldenMate(device),
        UsbSupport.PossiblySupported => productIdGiven,
        _ => false,
    };

    private static bool IsGoldenMate(HidDeviceInfo device) =>
        Contains(device.Manufacturer, "BMS") || Contains(device.Product, "Smart-Battery");

    public override string? FormatManufacturer(HidDeviceInfo device, IHidConversionContext context) =>
        device.Manufacturer ?? "iDowell";

    /// <summary>The runtime and voltage maximums are encoded as negative numbers (NUT idowell_fix_report_desc).</summary>
    public override bool FixReportDescriptor(HidDeviceInfo device, HidReportDescriptor descriptor)
    {
        if (!(device.VendorId == PhoenixtecVendorId && device.ProductId == 0xffff) &&
            !(device.VendorId == IDowellVendorId && device.ProductId == 0x0300))
        {
            return false;
        }

        bool changed = false;
        if (descriptor.FindByReportAndUsage(0x02, PowerDeviceUsages.RunTimeToEmpty) is { } runtime &&
            runtime.LogicalMaximum < runtime.LogicalMinimum)
        {
            runtime.LogicalMaximum = 0x75FFFFFF;
            runtime.LogicalMaximumAssumed = true;
            changed = true;
        }

        if (descriptor.FindByReportAndUsage(0x02, PowerDeviceUsages.Voltage) is { } voltage &&
            voltage.LogicalMaximum < voltage.LogicalMinimum)
        {
            voltage.LogicalMaximum = 65535;
            voltage.LogicalMaximumAssumed = true;
            changed = true;
        }

        return changed;
    }
}

/// <summary>Delta UPSes with their own HID dialect (port of NUT drivers/delta_ups-hid.c).</summary>
internal sealed partial class DeltaSubdriver : UsbHidSubdriver
{
    private static readonly string[] UpsTypes = ["online", "offline", "line-interactive", "3-phase", "split-phase"];

    public override string Id => "delta";

    public override string DisplayName => "Delta";

    public override IReadOnlyList<UsbDeviceId> SupportedDevices => DeviceIds;

    protected override HidUsageTable VendorUsages => VendorUsageTable;

    protected override HidMapping[] CreateMappings() => BuildMappings();

    public override string? FormatManufacturer(HidDeviceInfo device, IHidConversionContext context) =>
        device.Manufacturer ?? "Delta";

    public override string? FormatModel(HidDeviceInfo device, IHidConversionContext context)
    {
        string? model = context.ReadItemString("UPS.DeltaCustom.[1].DeltaModelName");
        return string.IsNullOrEmpty(model) ? device.Product : model;
    }

    private static string? DeltaUpsTypeFun(double value, IHidConversionContext context)
    {
        long type = HidValueCodec.Truncate(value) & 0xF;
        if (type == 6)
        {
            type = 4;
        }
        else if (type is > 2 and <= 5)
        {
            type -= 2;
        }

        return type is >= 0 and <= 4 ? UpsTypes[type] : null;
    }
}

/// <summary>EcoFlow power stations in UPS mode (port of NUT drivers/ecoflow-hid.c, HID part only).</summary>
internal sealed partial class EcoFlowSubdriver : UsbHidSubdriver
{
    public override string Id => "ecoflow";

    public override string DisplayName => "EcoFlow";

    public override IReadOnlyList<UsbDeviceId> SupportedDevices => DeviceIds;

    protected override HidUsageTable VendorUsages => VendorUsageTable;

    protected override HidMapping[] CreateMappings() => BuildMappings();

    public override string? FormatManufacturer(HidDeviceInfo device, IHidConversionContext context) =>
        device.Manufacturer ?? "EcoFlow";

    /// <summary>The runtime is reported in minutes.</summary>
    private static string? EcoflowBatteryRuntimeConversion(double value, IHidConversionContext context) =>
        value >= 0 ? CFormat.FormatDouble("%.0f", value * 60.0) : null;
}

/// <summary>Legrand UPSes and PDUs (port of NUT drivers/legrand-hid.c).</summary>
internal sealed partial class LegrandSubdriver : UsbHidSubdriver
{
    public override string Id => "legrand";

    public override string DisplayName => "Legrand";

    public override IReadOnlyList<UsbDeviceId> SupportedDevices => DeviceIds;

    protected override HidUsageTable VendorUsages => VendorUsageTable;

    protected override HidMapping[] CreateMappings() => BuildMappings();

    public override string? FormatManufacturer(HidDeviceInfo device, IHidConversionContext context) =>
        device.Manufacturer ?? "Legrand";

    protected override void ApplyHook(string hook, HidDeviceInfo device)
    {
        if (hook == "disable_interrupt_pipe")
        {
            Quirks.UseInterruptPipe = false;
        }
    }

    // The firmware declares exponents that are off by powers of ten.
    private static string? LegrandTimes10(double value, IHidConversionContext context) => CFormat.FormatDouble("%0.1f", value * 10);

    private static string? LegrandTimes100k(double value, IHidConversionContext context) => CFormat.FormatDouble("%0.1f", value * 100000);

    private static string? LegrandTimes1M(double value, IHidConversionContext context) => CFormat.FormatDouble("%0.1f", value * 1000000);

    private static string? LegrandTimes10M(double value, IHidConversionContext context) => CFormat.FormatDouble("%0.1f", value * 10000000);
}
