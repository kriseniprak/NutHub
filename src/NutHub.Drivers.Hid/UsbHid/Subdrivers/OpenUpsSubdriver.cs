using System.Globalization;
using NutHub.Drivers.Hid.Descriptors;
using NutHub.Drivers.Hid.Transport;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

/// <summary>Mini-Box openUPS DC UPSes (port of NUT drivers/openups-hid.c).</summary>
internal sealed partial class OpenUpsSubdriver : UsbHidSubdriver
{
    /// <summary>Thermistor ADC readings for -40 °C to +125 °C in 5 °C steps.</summary>
    private static readonly int[] ThermistorTable =
    [
        0x31, 0x40, 0x53, 0x68, 0x82, 0xA0, 0xC3, 0xE9, 0x113, 0x13F, 0x16E, 0x19F, 0x1CF, 0x200, 0x22F, 0x25C,
        0x286, 0x2AE, 0x2D3, 0x2F4, 0x312, 0x32D, 0x345, 0x35A, 0x36D, 0x37E, 0x38C, 0x399, 0x3A5, 0x3AF,
        0x3B7, 0x3BF, 0x3C6, 0x3CC,
    ];

    private double _inputVoltageScale = 1;
    private double _outputVoltageScale = 1;
    private double _chargeCurrentScale = 1;
    private double _dischargeCurrentScale = 1;

    public override string Id => "openups";

    public override string DisplayName => "openUPS";

    public override IReadOnlyList<UsbDeviceId> SupportedDevices => DeviceIds;

    protected override HidUsageTable VendorUsages => VendorUsageTable;

    protected override HidMapping[] CreateMappings() => BuildMappings();

    public override string? FormatManufacturer(HidDeviceInfo device, IHidConversionContext context) =>
        device.Manufacturer ?? "openUPS";

    /// <summary>The two hardware revisions report raw ADC values with different scales (NUT get_voltage_multiplier).</summary>
    protected override void ApplyHook(string hook, HidDeviceInfo device)
    {
        switch (device.ProductId)
        {
            case 0xd004:
                _inputVoltageScale = 0.03545 * 100;
                _outputVoltageScale = 0.02571 * 100;
                _chargeCurrentScale = 0.8274 / 10;
                _dischargeCurrentScale = 16.113 / 10;
                break;
            case 0xd005:
                _inputVoltageScale = 0.1;
                _outputVoltageScale = 0.1;
                _chargeCurrentScale = 0.1;
                _dischargeCurrentScale = 0.1;
                break;
        }
    }

    private static string? OpenupsChargingFun(double value, IHidConversionContext context) => value != 0 ? "chrg" : "!chrg";

    private static string? OpenupsDischargingFun(double value, IHidConversionContext context) => value != 0 ? "dischrg" : "!dischrg";

    private static string? OpenupsOnlineFun(double value, IHidConversionContext context) => value != 0 ? "online" : "!online";

    private static string? OpenupsOffFun(double value, IHidConversionContext context) => value != 0 ? "!off" : "off";

    private string? OpenupsScaleVinFun(double value, IHidConversionContext context) =>
        CFormat.FormatDouble("%.2f", value * _inputVoltageScale);

    private string? OpenupsScaleVoutFun(double value, IHidConversionContext context) =>
        CFormat.FormatDouble("%.2f", value * _outputVoltageScale);

    private string? OpenupsScaleCchargeFun(double value, IHidConversionContext context) =>
        CFormat.FormatDouble("%.3f", value * _chargeCurrentScale);

    private string? OpenupsScaleCdischargeFun(double value, IHidConversionContext context) =>
        CFormat.FormatDouble("%.3f", value * _dischargeCurrentScale);

    /// <summary>Interpolates the thermistor table (NUT openups_temperature_fun).</summary>
    private static string? OpenupsTemperatureFun(double value, IHidConversionContext context)
    {
        long thermistor = HidValueCodec.Truncate(value * 100);
        if (thermistor <= ThermistorTable[0])
        {
            return "-40";
        }

        if (thermistor >= ThermistorTable[^1])
        {
            return "125";
        }

        int pos = 0;
        for (int i = ThermistorTable.Length - 1; i >= 0; i--)
        {
            if (thermistor >= ThermistorTable[i])
            {
                pos = i;
                break;
            }
        }

        if (thermistor == ThermistorTable[pos])
        {
            return (pos * 5 - 40).ToString(CultureInfo.InvariantCulture);
        }

        int t1 = pos * 5 - 40;
        int t2 = (pos + 1) * 5 - 40;
        int d1 = ThermistorTable[pos];
        int d2 = ThermistorTable[pos + 1];
        double temperature = (double)(thermistor - d1) * (t2 - t1) / (d2 - d1) + t1;
        return CFormat.FormatDouble("%.2f", temperature);
    }
}
