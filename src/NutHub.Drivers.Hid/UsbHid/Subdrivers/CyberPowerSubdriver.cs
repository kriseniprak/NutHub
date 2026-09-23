using System.Globalization;
using Microsoft.Extensions.Logging;
using NutHub.Drivers.Hid.Descriptors;
using NutHub.Drivers.Hid.Transport;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

/// <summary>CyberPower and Cyber Energy (port of NUT drivers/cps-hid.c).</summary>
internal sealed partial class CyberPowerSubdriver : UsbHidSubdriver
{
    private const long VoltageLogicalMaximum = 511;
    private const long NominalPowerLogicalMaximum = 2048;
    private const long BatteryVoltageLogicalMaximum = 4096;

    /// <summary>Above this ratio of battery voltage to nominal, the firmware reports 1.5 times the real value.</summary>
    private const double BatteryVoltageSanityRatio = 1.4;

    private bool _mightNeedBatteryScale;
    private bool _mightNeedFrequencyScale;
    private bool _batteryScaleChecked;
    private bool _inputFrequencyChecked;
    private bool _outputFrequencyChecked;
    private double _batteryScale = 1;
    private double _inputFrequencyScale = 1;
    private double _outputFrequencyScale = 1;

    public override string Id => "cps";

    public override string DisplayName => "CyberPower";

    public override IReadOnlyList<UsbDeviceId> SupportedDevices => DeviceIds;

    protected override HidUsageTable VendorUsages => VendorUsageTable;

    protected override HidMapping[] CreateMappings() => BuildMappings();

    public override string? FormatManufacturer(HidDeviceInfo device, IHidConversionContext context) =>
        device.Manufacturer ?? "CPS";

    protected override void ApplyHook(string hook, HidDeviceInfo device)
    {
        if (hook == "cps_battery_scale")
        {
            _mightNeedBatteryScale = true;
            _mightNeedFrequencyScale = true;
        }
    }

    /// <summary>
    /// Several models (CP900EPFCLCD, CP1500PFCLCDa...) copy the high transfer voltage limits onto the output
    /// voltage, declare an input voltage maximum too small for 230 V, or clip nominal power and battery voltage
    /// (NUT cps_fix_report_desc). Fields scaled by physical limits are left alone: widening them would change
    /// correct readings.
    /// </summary>
    public override bool FixReportDescriptor(HidDeviceInfo device, HidReportDescriptor descriptor)
    {
        if (device.VendorId != 0x0764 || (device.ProductId != 0x0501 && device.ProductId != 0x0601))
        {
            return false;
        }

        bool changed = false;
        if (descriptor.FindByReportAndUsage(0x10, PowerDeviceUsages.HighVoltageTransfer) is { } hvt)
        {
            if (descriptor.FindByReportAndUsage(0x12, PowerDeviceUsages.Voltage) is { } output &&
                hvt.LogicalMinimum == output.LogicalMinimum && hvt.LogicalMaximum == output.LogicalMaximum)
            {
                output.LogicalMinimum = 0;
                output.LogicalMaximum = VoltageLogicalMaximum;
                changed = true;
            }

            if (descriptor.FindByReportAndUsage(0x0F, PowerDeviceUsages.Voltage) is { } input &&
                ((hvt.LogicalMinimum == input.LogicalMinimum && hvt.LogicalMaximum == input.LogicalMaximum) ||
                 hvt.LogicalMaximum > input.LogicalMaximum) &&
                !PhysicalScaling(input))
            {
                input.LogicalMinimum = 0;
                input.LogicalMaximum = VoltageLogicalMaximum;
                changed = true;
            }

            if (descriptor.FindByReportAndUsage(0x0E, PowerDeviceUsages.ConfigVoltage) is { } nominal &&
                hvt.LogicalMaximum > nominal.LogicalMaximum && !PhysicalScaling(nominal))
            {
                nominal.LogicalMaximum = 255;
                changed = true;
            }
        }

        if (descriptor.FindByReportAndUsage(0x12, PowerDeviceUsages.Voltage) is { } outputVoltage &&
            descriptor.FindByReportAndUsage(0x0F, PowerDeviceUsages.Voltage) is { } inputVoltage &&
            (outputVoltage.LogicalMaximumAssumed || inputVoltage.LogicalMaximumAssumed))
        {
            // Maximums written as 0xFFFF in a 2-byte item read as -1 and were reinterpreted: align them.
            long outMax = outputVoltage.LogicalMaximum;
            long inMax = inputVoltage.LogicalMaximum;
            if (outputVoltage.LogicalMaximumAssumed && outMax < VoltageLogicalMaximum)
            {
                outMax = VoltageLogicalMaximum;
            }

            if (inputVoltage.LogicalMaximumAssumed && inMax < VoltageLogicalMaximum)
            {
                inMax = VoltageLogicalMaximum;
            }

            if (outputVoltage.LogicalMaximumAssumed && outMax < inMax)
            {
                outMax = inMax;
            }
            else if (inputVoltage.LogicalMaximumAssumed && inMax < outMax)
            {
                inMax = outMax;
            }

            inMax = LimitToSize(inputVoltage, inMax);
            outMax = LimitToSize(outputVoltage, outMax);
            if (inMax != inputVoltage.LogicalMaximum)
            {
                inputVoltage.LogicalMaximum = inMax;
                changed = true;
            }

            if (outMax != outputVoltage.LogicalMaximum)
            {
                outputVoltage.LogicalMaximum = outMax;
                changed = true;
            }
        }

        if (descriptor.FindByReportAndUsage(0x18, PowerDeviceUsages.ConfigActivePower) is { } power &&
            power.LogicalMaximum < NominalPowerLogicalMaximum)
        {
            power.LogicalMaximum = NominalPowerLogicalMaximum;
            changed = true;
        }

        if (descriptor.FindByReportAndUsage(0x0A, PowerDeviceUsages.Voltage) is { } battery &&
            battery.LogicalMaximum < BatteryVoltageLogicalMaximum && !PhysicalScaling(battery))
        {
            battery.LogicalMaximum = BatteryVoltageLogicalMaximum;
            changed = true;
        }

        if (descriptor.FindByReportAndUsage(0x09, PowerDeviceUsages.ConfigVoltage) is { } batteryNominal &&
            batteryNominal.LogicalMaximum < BatteryVoltageLogicalMaximum && !PhysicalScaling(batteryNominal))
        {
            batteryNominal.LogicalMaximum = BatteryVoltageLogicalMaximum;
            changed = true;
        }

        return changed;
    }

    private static long LimitToSize(HidField field, long max)
    {
        if (field.LogicalMaximumAssumed && field.BitSize > 1 && field.BitSize < 63)
        {
            long sizeMax = (1L << field.BitSize) - 1;
            return Math.Min(max, sizeMax);
        }

        return max;
    }

    /// <summary>Mirrors the condition under which logical values are rescaled to physical ones.</summary>
    private static bool PhysicalScaling(HidField field) =>
        field.HasPhysicalMaximum && field.HasPhysicalMinimum &&
        !(field.PhysicalMaximum == 0 && field.PhysicalMinimum == 0);

    private string? CpsInputFreqFun(double value, IHidConversionContext context)
    {
        if (_mightNeedFrequencyScale && !_inputFrequencyChecked)
        {
            AdjustFrequencyScale(value, input: true, context);
        }

        return CFormat.FormatDouble("%.1f", _inputFrequencyScale * value);
    }

    private string? CpsOutputFreqFun(double value, IHidConversionContext context)
    {
        if (_mightNeedFrequencyScale && !_outputFrequencyChecked)
        {
            AdjustFrequencyScale(value, input: false, context);
        }

        return CFormat.FormatDouble("%.1f", _outputFrequencyScale * value);
    }

    /// <summary>Some firmwares send frequencies in 0.1 Hz without saying so: detect it from the plausible range.</summary>
    private void AdjustFrequencyScale(double reported, bool input, IHidConversionContext context)
    {
        string prefix = input ? "input" : "output";
        double nominal = Number(context.GetVariable(prefix + ".frequency.nominal"));
        double low = Number(context.GetVariable(prefix + ".frequency.low"));
        double high = Number(context.GetVariable(prefix + ".frequency.high"));
        if (nominal == 0)
        {
            nominal = (low > 45 && low <= 50) || (high >= 50 && high <= 55) ||
                      (reported > 45 && reported <= 55) || (reported > 450 && reported <= 550) ? 50
                : (low > 55 && low <= 60) || (high >= 60 && high <= 65) ||
                  (reported > 55 && reported <= 65) || (reported > 550 && reported <= 650) ? 60
                : 0;
        }

        if (low == 0)
        {
            low = nominal == 0 ? 45.0 : nominal * 0.95;
        }

        if (high == 0)
        {
            high = nominal == 0 ? 65.0 : nominal * 1.05;
        }

        double? scale = reported >= low && reported <= high ? 1.0
            : reported / 10.0 >= low && reported / 10.0 <= high ? 0.1
            : null;
        if (scale is not double s)
        {
            return;
        }

        if (input)
        {
            _inputFrequencyScale = s;
            _inputFrequencyChecked = true;
        }
        else
        {
            _outputFrequencyScale = s;
            _outputFrequencyChecked = true;
        }
    }

    /// <summary>Some models (CP1500PFCLCD...) report 1.5 times the battery voltage; scale by 2/3 when implausible.</summary>
    private string? CpsBattvoltFun(double value, IHidConversionContext context)
    {
        if (_mightNeedBatteryScale && !_batteryScaleChecked)
        {
            double nominal = Number(context.GetVariable("battery.voltage.nominal"));
            if (nominal != 0)
            {
                if (value / nominal > BatteryVoltageSanityRatio)
                {
                    context.Logger.LogInformation("Battery voltage readings will be scaled by 2/3.");
                    _batteryScale = 2.0 / 3;
                }

                _batteryScaleChecked = true;
            }
        }

        return CFormat.FormatDouble("%.1f", _batteryScale * value);
    }

    private static string? CpsBattchargeFun(double value, IHidConversionContext context) =>
        CFormat.FormatDouble("%.0f", Math.Min(value, 100.0));

    private static string? CpsBattstatusFun(double value, IHidConversionContext context) =>
        CFormat.FormatDouble("%.0f", value) + "%";

    private static double Number(string? text) =>
        text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
}
