using System.Globalization;
using NutHub.Drivers.Hid.Descriptors;
using NutHub.Drivers.Hid.Transport;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

/// <summary>APC Back-UPS and Smart-UPS (port of NUT drivers/apc-hid.c).</summary>
internal sealed partial class ApcSubdriver : UsbHidSubdriver
{
    /// <summary>Models that answer more bytes than their descriptor declares for some reports (NUT tweak_max_report).</summary>
    private static readonly string[] OverflowingModels = ["Back-UPS ES 525", "Back-UPS CS"];

    public override string Id => "apc";

    public override string DisplayName => "APC";

    public override IReadOnlyList<UsbDeviceId> SupportedDevices => DeviceIds;

    protected override HidUsageTable VendorUsages => VendorUsageTable;

    protected override HidMapping[] CreateMappings() => BuildMappings();

    public override string? FormatManufacturer(HidDeviceInfo device, IHidConversionContext context) =>
        device.Manufacturer ?? "APC";

    /// <summary>
    /// The product string holds model and firmware: "Back-UPS ES 700 FW:871.O2 .I USB FW:O2". The model is what
    /// precedes "FW:", the rest goes to ups.firmware and ups.firmware.aux.
    /// </summary>
    public override string? FormatModel(HidDeviceInfo device, IHidConversionContext context)
    {
        string model = device.Product ?? "unknown";
        int fw = model.IndexOf("FW:", StringComparison.Ordinal);
        if (fw < 0)
        {
            return model;
        }

        string firmware = model[(fw + 3)..];
        model = model[..fw].TrimEnd();
        int usbFw = firmware.IndexOf("USB FW:", StringComparison.Ordinal);
        if (usbFw >= 0)
        {
            context.SetVariable("ups.firmware.aux", firmware[(usbFw + 7)..].Trim());
            firmware = firmware[..usbFw];
        }

        context.SetVariable("ups.firmware", firmware.Trim());
        return model;
    }

    protected override void ApplyHook(string hook, HidDeviceInfo device)
    {
        switch (hook)
        {
            case "disable_interrupt_pipe":
                // 5G models use the interrupt pipe for a proprietary protocol.
                Quirks.UseInterruptPipe = false;
                break;
            case "general_apc_check":
                if (device.Product is { } product &&
                    OverflowingModels.Any(m => product.StartsWith(m, StringComparison.Ordinal)))
                {
                    Quirks.LargeReportBuffer = true;
                }

                break;
        }
    }

    /// <summary>
    /// Back-UPS BX...MI, BVK...M2 and BK...M2-CH units flap LB and RB for a few seconds during their internal
    /// calibration; NUT waits 3 s before believing those flags while on line power (usbhid-ups.c).
    /// </summary>
    protected override void OnAttached(HidDeviceInfo device)
    {
        string? vendor = device.Manufacturer;
        string product = device.Product ?? "";
        if (vendor is not ("APC" or "American Power Conversion") || product.Length == 0)
        {
            return;
        }

        int n = product.Length;
        bool bx = n > 6 && (product.Contains(" BX", StringComparison.Ordinal) || product.StartsWith("BX", StringComparison.Ordinal)) &&
                  product[n - 2] == 'M' && product[n - 1] == 'I';
        bool bvk = n > 7 && (product.Contains(" BVK", StringComparison.Ordinal) || product.StartsWith("BVK", StringComparison.Ordinal)) &&
                   product[n - 2] == 'M' && product[n - 1] == '2';
        bool bk = n > 9 && (product.Contains(" BK", StringComparison.Ordinal) || product.StartsWith("BK", StringComparison.Ordinal)) &&
                  product[n - 5] == 'M' && product[n - 4] == '2' && product[n - 3] is '-' or '_' &&
                  product[n - 2] == 'C' && product[n - 1] == 'H';
        if (bx || bvk || bk)
        {
            Quirks.LowReplaceDelaySeconds = 3;
        }
    }

    /// <summary>
    /// The Back-UPS XS 1400U declares input voltage limits too small for 230 V regions; widen them when the high
    /// transfer voltage exceeds them (NUT apc_fix_report_desc).
    /// </summary>
    public override bool FixReportDescriptor(HidDeviceInfo device, HidReportDescriptor descriptor)
    {
        if (device.VendorId != 0x051d || device.ProductId != 0x0002)
        {
            return false;
        }

        bool fixedSomething = false;
        HidField? hvt = descriptor.FindByReportAndUsage(0x33, PowerDeviceUsages.HighVoltageTransfer);
        if (hvt is null)
        {
            return false;
        }

        if (descriptor.FindByReportAndUsage(0x31, PowerDeviceUsages.Voltage) is { } voltage &&
            hvt.LogicalMaximum > voltage.LogicalMaximum)
        {
            voltage.LogicalMinimum = 0;
            voltage.LogicalMaximum = hvt.LogicalMaximum * 2;
            fixedSomething = true;
        }

        if (descriptor.FindByReportAndUsage(0x30, PowerDeviceUsages.ConfigVoltage) is { } config &&
            hvt.LogicalMaximum > config.LogicalMaximum)
        {
            config.LogicalMaximum = 255;
            fixedSomething = true;
        }

        return fixedSomething;
    }

    /// <summary>APC writes dates as hexadecimal-coded decimal: 0x102202 is 2002/10/22.</summary>
    private static string? ApcDateConversionFun(double value, IHidConversionContext context)
    {
        long v = HidValueCodec.Truncate(value);
        if (v == 0)
        {
            return "not set";
        }

        long year = (v & 0xF) + 10 * ((v >> 4) & 0xF);
        long month = ((v >> 16) & 0xF) + 10 * ((v >> 20) & 0xF);
        long day = ((v >> 8) & 0xF) + 10 * ((v >> 12) & 0xF);
        year += year >= 70 ? 1900 : 2000;
        return string.Create(CultureInfo.InvariantCulture, $"{year:0000}/{month:00}/{day:00}");
    }

    private static double? ApcDateConversionReverse(string? text, IHidConversionContext context)
    {
        if (!CommonLookups.TryParseDate(text, out int year, out int month, out int day))
        {
            return null;
        }

        if (year >= 2070 || month > 12 || day > 31)
        {
            return 0;
        }

        year %= 100;
        long date = (((year / 10) & 0x0F) << 4) + (year % 10);
        date += (((month / 10) & 0x0F) << 20) + ((month % 10) << 16);
        date += (((day / 10) & 0x0F) << 12) + ((day % 10) << 8);
        return date;
    }
}
