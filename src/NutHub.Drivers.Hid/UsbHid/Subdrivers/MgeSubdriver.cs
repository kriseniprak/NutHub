using System.Globalization;
using NutHub.Drivers.Hid.Descriptors;
using NutHub.Drivers.Hid.Transport;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

/// <summary>
/// Eaton, MGE, Powerware, Dell, HP, IBM and the OEM relatives (port of NUT drivers/mge-hid.c). The model family,
/// found from the product and model strings, decides which values some conversions trust; the Advanced Battery
/// Monitoring charger state replaces the plain charging flags when it is active.
/// </summary>
internal sealed partial class MgeSubdriver : UsbHidSubdriver
{
    private const int DellVendorId = 0x047c;
    private const int HpVendorId = 0x03f0;
    private const int PhoenixtecVendorId = 0x06da;
    private const int KstarVendorId = 0x09d6;
    private const int CountryEurope = 0;

    private MgeModelType _type = MgeModelType.Default;
    private int _country = -1;
    private AbmState _abm = AbmState.Unknown;
    private AbmPath _abmPath = AbmPath.Unknown;
    private long _nominalOutputVoltage = -1;

    private enum AbmState
    {
        Unknown,
        Disabled,
        Enabled,
    }

    private enum AbmPath
    {
        Unknown,
        Status,
        Mode,
    }

    public override string Id => "mge";

    public override string DisplayName => "Eaton / MGE";

    public override IReadOnlyList<UsbDeviceId> SupportedDevices => DeviceIds;

    protected override HidUsageTable VendorUsages => VendorUsageTable;

    protected override HidMapping[] CreateMappings() => BuildMappings();

    /// <summary>The family is the high byte of the model type, as NUT tests it ((mge_type &amp; 0xFF00)).</summary>
    private int Family => (int)_type & 0xFF00;

    public override bool Claim(HidDeviceInfo device, bool productIdGiven)
    {
        switch (Check(device))
        {
            case UsbSupport.PossiblySupported:
                return device.VendorId switch
                {
                    HpVendorId or DellVendorId => productIdGiven,
                    PhoenixtecVendorId => IsAeg(device),
                    KstarVendorId => Contains(device.Manufacturer, "KSTAR"),
                    _ => productIdGiven,
                };
            case UsbSupport.Supported:
                return device.VendorId switch
                {
                    // Phoenixtec's 06da:ffff is shared by several brands: only AEG units speak the MGE dialect.
                    PhoenixtecVendorId => IsAeg(device),
                    KstarVendorId => Contains(device.Manufacturer, "KSTAR"),
                    _ => true,
                };
            default:
                return false;
        }
    }

    private static bool IsAeg(HidDeviceInfo device) => Contains(device.Manufacturer, "AEG") || Contains(device.Product, "AEG");

    public override string? FormatManufacturer(HidDeviceInfo device, IHidConversionContext context) =>
        device.Manufacturer ?? "Eaton";

    /// <summary>
    /// The USB product string is only the range ("Ellipse ECO"); the HID iModel string or the rated power
    /// complete it, and the pair selects the model type used by the conversions.
    /// </summary>
    public override string? FormatModel(HidDeviceInfo device, IHidConversionContext context)
    {
        if (device.VendorId == DellVendorId)
        {
            return device.Product;
        }

        string product = device.Product ?? "unknown";
        string? model = context.ReadItemString("UPS.PowerSummary.iModel");
        if (string.IsNullOrEmpty(model) && context.TryReadValue("UPS.Flow.[4].ConfigApparentPower", out double power))
        {
            model = ((int)power).ToString(CultureInfo.InvariantCulture);
        }

        return string.IsNullOrEmpty(model) ? device.Product : ModelName(product, model);
    }

    private string ModelName(string product, string model)
    {
        foreach (MgeModel entry in Models)
        {
            if (entry.Product == product && entry.Model == model)
            {
                _type = entry.Type;
                return entry.Name ?? $"{product} {model}";
            }
        }

        // Unknown models keep the default (online) type, as in NUT.
        return $"{product} {model}";
    }

    private string? EatonAbmEnabledFun(double value, IHidConversionContext context)
    {
        _abm = (int)value switch
        {
            0 => AbmState.Disabled,
            1 => AbmState.Enabled,
            _ => AbmState.Unknown,
        };
        return null;
    }

    private string? EatonAbmPathModeFun(double value, IHidConversionContext context) => SelectAbmPath(AbmPath.Mode);

    private string? EatonAbmPathStatusFun(double value, IHidConversionContext context) => SelectAbmPath(AbmPath.Status);

    /// <summary>Firmwares expose the charger state through Charger.Mode or Charger.Status; the first one seen wins.</summary>
    private string? SelectAbmPath(AbmPath path)
    {
        if (_abm != AbmState.Enabled)
        {
            _abmPath = AbmPath.Unknown;
        }
        else if (_abmPath == AbmPath.Unknown)
        {
            _abmPath = path;
        }

        return null;
    }

    private string? EatonAbmStatusFun(double value, IHidConversionContext context)
    {
        if (_abm != AbmState.Enabled)
        {
            context.RemoveVariable("battery.charger.status");
            return null;
        }

        return _abmPath == AbmPath.Status
            ? (int)value switch
            {
                1 => "charging",
                2 => "floating",
                3 => "resting",
                4 => "discharging",
                6 => "off",
                _ => null,
            }
            : (int)value switch
            {
                1 => "charging",
                2 => "discharging",
                3 => "floating",
                4 => "resting",
                6 => "off",
                _ => null,
            };
    }

    private string? EatonAbmChrgDischrgFun(double value, IHidConversionContext context)
    {
        if (_abm != AbmState.Enabled)
        {
            return null;
        }

        return _abmPath == AbmPath.Status
            ? (int)value switch
            {
                1 or 2 => "chrg",
                4 => "dischrg",
                _ => null,
            }
            : (int)value switch
            {
                1 or 3 => "chrg",
                2 => "dischrg",
                _ => null,
            };
    }

    /// <summary>With ABM active, discharging comes from the charger state instead.</summary>
    private string? EatonAbmCheckDischrgFun(double value, IHidConversionContext context) =>
        _abm != AbmState.Enabled && value == 1 ? "dischrg" : "!dischrg";

    private string? EatonAbmCheckChrgFun(double value, IHidConversionContext context)
    {
        if (_abm == AbmState.Enabled || value != 1)
        {
            return "!chrg";
        }

        // Some firmwares keep "charging" set on a full battery.
        return Number(context.GetVariable("battery.charge")) is double charge && charge >= 100 ? "!chrg" : "chrg";
    }

    /// <summary>Seconds since 1970, shown in local time like NUT's localtime().</summary>
    private static string? MgeDateConversionFun(double value, IHidConversionContext context) =>
        LocalTime(value, context)?.ToString("yyyy'/'MM'/'dd", CultureInfo.InvariantCulture);

    private static string? MgeTimeConversionFun(double value, IHidConversionContext context) =>
        LocalTime(value, context)?.ToString("HH':'mm':'ss", CultureInfo.InvariantCulture);

    private static double? MgeDateConversionNuf(string? text, IHidConversionContext context) =>
        ToEpoch($"{text} {context.GetVariable("ups.time")}", context);

    private static double? MgeTimeConversionNuf(string? text, IHidConversionContext context) =>
        ToEpoch($"{context.GetVariable("ups.date")} {text}", context);

    private static DateTimeOffset? LocalTime(double value, IHidConversionContext context)
    {
        long seconds = HidValueCodec.Truncate(value);
        if (seconds < -62135596800 || seconds > 253402300799)
        {
            return null;
        }

        return TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(seconds), context.TimeProvider.LocalTimeZone);
    }

    private static double? ToEpoch(string text, IHidConversionContext context)
    {
        if (!DateTime.TryParseExact(text.Trim(), "yyyy'/'MM'/'dd HH':'mm':'ss", CultureInfo.InvariantCulture,
                                    DateTimeStyles.None, out DateTime local))
        {
            return null;
        }

        TimeSpan offset = context.TimeProvider.LocalTimeZone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUnixTimeSeconds();
    }

    private string? MgeBatteryVoltageNominalFun(double value, IHidConversionContext context)
    {
        switch (Family)
        {
            case (int)MgeModelType.Evolution:
                if (_type == MgeModelType.Evolution650)
                {
                    value = 12.0;
                }

                break;
            case (int)MgeModelType.PulsarM:
            case (int)MgeModelType.Eaton5P:
            case (int)MgeModelType.Eaton9E:
                break;
            default:
                return null;
        }

        return CFormat.FormatDouble("%.0f", value);
    }

    private string? MgeBatteryVoltageFun(double value, IHidConversionContext context) =>
        Family is (int)MgeModelType.Evolution or (int)MgeModelType.PulsarM or (int)MgeModelType.Eaton5P or (int)MgeModelType.Eaton9E
            ? CFormat.FormatDouble("%.1f", value)
            : null;

    private static string? MgePowerfactorConversionFun(double value, IHidConversionContext context) =>
        CFormat.FormatDouble("%.2f", value / 100);

    private static string? MgeBatteryCapacityFun(double value, IHidConversionContext context) =>
        CFormat.FormatDouble("%.2f", value / 3600);

    /// <summary>
    /// Only Pegasus units (and 3S units outside Europe) have these outlet settings. 3S shares the Pegasus high
    /// byte, so NUT's family switch sends it through the Pegasus branch; kept as is.
    /// </summary>
    private bool PegasusSettingsApply() => Family switch
    {
        (int)MgeModelType.Pegasus => true,
        (int)MgeModelType.ThreeS => _country != CountryEurope,
        _ => false,
    };

    private string? EatonCheckPegasusFun(double value, IHidConversionContext context) =>
        PegasusSettingsApply() ? CFormat.FormatDouble("%.0f", value) : null;

    private string? PegasusYesNoInfoFun(double value, IHidConversionContext context) =>
        PegasusSettingsApply() ? (value == 0 ? "no" : "yes") : null;

    private double? PegasusYesNoInfoNuf(string? text, IHidConversionContext context) =>
        PegasusSettingsApply() && text is not null && text.StartsWith("yes", StringComparison.Ordinal) ? 1 : 0;

    private string? EatonCheckCountryFun(double value, IHidConversionContext context)
    {
        _country = (int)value;
        return null;
    }

    /// <summary>Real power estimated from load, rated power and power factor (0.8 when unknown).</summary>
    private static string? EatonComputeRealpowerFun(double value, IHidConversionContext context)
    {
        string? load = context.GetVariable("ups.load");
        string? nominal = context.GetVariable("ups.power.nominal");
        if (load is null || nominal is null)
        {
            return null;
        }

        double powerFactor = Number(context.GetVariable("output.powerfactor")) ?? 0.80;
        double realPower = Math.Round((int)(Number(load) ?? 0) * 0.01 * (int)(Number(nominal) ?? 0) * powerFactor,
                                      MidpointRounding.AwayFromZero);
        return CFormat.FormatDouble("%.0f", realPower);
    }

    /// <summary>
    /// Offers only nominal voltages compatible with the one the UPS had at start (a 230 V unit can be set to 220 or
    /// 240, not 120).
    /// </summary>
    private string? NominalOutputVoltageFun(double value, IHidConversionContext context)
    {
        if (_nominalOutputVoltage < 0)
        {
            _nominalOutputVoltage = (long)value;
        }

        long v = (long)value;
        bool accepted = _nominalOutputVoltage switch
        {
            100 or 110 or 120 or 127 => v is 100 or 110 or 120 or 127,
            200 or 208 => v is 200 or 208 || (v is 220 or 230 or 240 && Family >= (int)MgeModelType.Default),
            220 or 230 or 240 => v is 220 or 230 or 240 || (v is 200 or 208 && Family >= (int)MgeModelType.Default),
            _ => true,
        };
        return accepted ? CFormat.FormatDouble("%.0f", value) : null;
    }

    private static string? EatonConverterOnlineFun(double value, IHidConversionContext context)
    {
        if ((context.StatusBits & UpsStatusBits.Off) != 0)
        {
            return null;
        }

        return value == 0 ? "!online" : "online";
    }

    /// <summary>input.eco.switchable: 0 normal, 1 ECO (only when the bypass input is within limits), 2 ESS.</summary>
    private static string? EatonInputBuzzwordmodeReport(double value, IHidConversionContext context) =>
        (long)value switch
        {
            0 => "normal",
            1 => EcoModeCheckRange(context),
            2 => Buzzword(context, "ESS"),
            _ => null,
        };

    private static string Buzzword(IHidConversionContext context, string mode)
    {
        context.AddBuzzword("vendor:mge-hid:" + mode);
        return mode;
    }

    private static double? EatonInputBuzzwordmodeSetvarNuf(string? text, IHidConversionContext context) => text switch
    {
        "normal" or "vendor:mge-hid:normal" => 0,
        "ECO" or "vendor:mge-hid:ECO" => 1,
        "ESS" or "vendor:mge-hid:ESS" => 2,
        _ => null,
    };

    /// <summary>ECO mode only makes sense while the bypass voltage and frequency are within the ECO limits.</summary>
    private static string? EcoModeCheckRange(IHidConversionContext context)
    {
        double? bypassVoltage = Number(context.GetVariable("input.bypass.voltage"));
        double? bypassFrequency = Number(context.GetVariable("input.bypass.frequency"));
        double? outVoltage = Number(context.GetVariable("output.voltage.nominal"));
        double? outFrequency = Number(context.GetVariable("output.frequency.nominal"));
        if (bypassVoltage is null || bypassFrequency is null || outVoltage is null || outFrequency is null)
        {
            context.SetVariable("input.eco.switchable", "normal");
            return null;
        }

        double low = Number(context.GetVariable("input.transfer.eco.low")) ?? 0;
        double high = Number(context.GetVariable("input.transfer.eco.high")) ?? 0;
        double range = Number(context.GetVariable("input.transfer.frequency.eco.range")) ?? 0;
        if (InRange(bypassVoltage.Value, bypassFrequency.Value, outVoltage.Value, outFrequency.Value, low, high,
                    range, defaultRange: 5, lowFactor: 0.95, highFactor: 1.05))
        {
            return Buzzword(context, "ECO");
        }

        context.SetVariable("input.eco.switchable", "normal");
        context.AddBuzzword("vendor:mge-hid:normal");
        return null;
    }

    /// <summary>input.bypass.switch.on: "on" only when the bypass input is usable, else the off switch reads "off".</summary>
    private static string? EatonInputBypassCheckRange(double value, IHidConversionContext context)
    {
        if (value == 0)
        {
            return "disabled";
        }

        if (value != 1)
        {
            return null;
        }

        double? bypassVoltage = Number(context.GetVariable("input.bypass.voltage"));
        double? bypassFrequency = Number(context.GetVariable("input.bypass.frequency"));
        double? outVoltage = Number(context.GetVariable("output.voltage.nominal"));
        double? outFrequency = Number(context.GetVariable("output.frequency.nominal"));
        if (bypassVoltage is null || bypassFrequency is null || outVoltage is null || outFrequency is null)
        {
            context.SetVariable("input.bypass.switch.off", "off");
            return null;
        }

        double low = Number(context.GetVariable("input.transfer.bypass.low")) ?? 0;
        double high = Number(context.GetVariable("input.transfer.bypass.high")) ?? 0;
        double range = Number(context.GetVariable("input.transfer.frequency.bypass.range")) ?? 0;
        if (InRange(bypassVoltage.Value, bypassFrequency.Value, outVoltage.Value, outFrequency.Value, low, high,
                    range, defaultRange: 10, lowFactor: 0.8, highFactor: 1.15))
        {
            return "on";
        }

        context.SetVariable("input.bypass.switch.off", "off");
        return null;
    }

    private static bool InRange(double voltage, double frequency, double nominalVoltage, double nominalFrequency,
                                double lowTransfer, double highTransfer, double frequencyRange, double defaultRange,
                                double lowFactor, double highFactor)
    {
        double percent = frequencyRange > 0 ? frequencyRange : defaultRange;
        double lowFrequency = nominalFrequency - nominalFrequency / 100 * percent;
        double highFrequency = nominalFrequency + nominalFrequency / 100 * percent;
        bool limits = lowTransfer > 0 && highTransfer > 0;
        double lowVoltage = limits ? lowTransfer : nominalVoltage * lowFactor;
        double highVoltage = limits ? highTransfer : nominalVoltage * highFactor;
        return voltage >= lowVoltage && voltage <= highVoltage && frequency >= lowFrequency && frequency <= highFrequency;
    }

    private static double? Number(string? text) =>
        text is not null && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : null;

    private sealed record MgeModel(string Product, string Model, MgeModelType Type, string? Name);

    /// <summary>Product and model strings to model names and types (NUT mge_model_names).</summary>
    private static readonly MgeModel[] Models =
    [
        new("ELLIPSE", "300", MgeModelType.DefaultOffline, "ellipse 300"),
        new("ELLIPSE", "500", MgeModelType.DefaultOffline, "ellipse 500"),
        new("ELLIPSE", "650", MgeModelType.DefaultOffline, "ellipse 650"),
        new("ELLIPSE", "800", MgeModelType.DefaultOffline, "ellipse 800"),
        new("ELLIPSE", "1200", MgeModelType.DefaultOffline, "ellipse 1200"),
        new("ellipse", "PR500", MgeModelType.DefaultOffline, "ellipse premium 500"),
        new("ellipse", "PR650", MgeModelType.DefaultOffline, "ellipse premium 650"),
        new("ellipse", "PR800", MgeModelType.DefaultOffline, "ellipse premium 800"),
        new("ellipse", "PR1200", MgeModelType.DefaultOffline, "ellipse premium 1200"),
        new("ELLIPSE", "600", MgeModelType.DefaultOffline, "Ellipse 600"),
        new("ELLIPSE", "750", MgeModelType.DefaultOffline, "Ellipse 750"),
        new("ELLIPSE", "1000", MgeModelType.DefaultOffline, "Ellipse 1000"),
        new("ELLIPSE", "1500", MgeModelType.DefaultOffline, "Ellipse 1500"),
        new("Ellipse MAX", "600", MgeModelType.DefaultOffline, null),
        new("Ellipse MAX", "850", MgeModelType.DefaultOffline, null),
        new("Ellipse MAX", "1100", MgeModelType.DefaultOffline, null),
        new("Ellipse MAX", "1500", MgeModelType.DefaultOffline, null),
        new("PROTECTIONCENTER", "420", MgeModelType.DefaultOffline, "Protection Center 420"),
        new("PROTECTIONCENTER", "500", MgeModelType.DefaultOffline, "Protection Center 500"),
        new("PROTECTIONCENTER", "675", MgeModelType.DefaultOffline, "Protection Center 675"),
        new("Protection Station", "500", MgeModelType.Pegasus, null),
        new("Protection Station", "650", MgeModelType.Pegasus, null),
        new("Protection Station", "800", MgeModelType.Pegasus, null),
        new("Ellipse ECO", "650", MgeModelType.Pegasus, null),
        new("Ellipse ECO", "800", MgeModelType.Pegasus, null),
        new("Ellipse ECO", "1200", MgeModelType.Pegasus, null),
        new("Ellipse ECO", "1600", MgeModelType.Pegasus, null),
        new("3S", "450", MgeModelType.DefaultOffline, null),
        new("3S", "550", MgeModelType.DefaultOffline, null),
        new("3S", "700", MgeModelType.ThreeS, null),
        new("3S", "750", MgeModelType.ThreeS, null),
        new("Evolution", "500", MgeModelType.Default, "Pulsar Evolution 500"),
        new("Evolution", "800", MgeModelType.Default, "Pulsar Evolution 800"),
        new("Evolution", "1100", MgeModelType.Default, "Pulsar Evolution 1100"),
        new("Evolution", "1500", MgeModelType.Default, "Pulsar Evolution 1500"),
        new("Evolution", "2200", MgeModelType.Default, "Pulsar Evolution 2200"),
        new("Evolution", "3000", MgeModelType.Default, "Pulsar Evolution 3000"),
        new("Evolution", "3000XL", MgeModelType.Default, "Pulsar Evolution 3000 XL"),
        new("Evolution", "650", MgeModelType.Evolution650, null),
        new("Evolution", "850", MgeModelType.Evolution850, null),
        new("Evolution", "1150", MgeModelType.Evolution1150, null),
        new("Evolution", "S 1250", MgeModelType.EvolutionS1250, null),
        new("Evolution", "1550", MgeModelType.Evolution1550, null),
        new("Evolution", "S 1750", MgeModelType.EvolutionS1750, null),
        new("Evolution", "2000", MgeModelType.Evolution2000, null),
        new("Evolution", "S 2500", MgeModelType.EvolutionS2500, null),
        new("Evolution", "S 3000", MgeModelType.EvolutionS3000, null),
        new("Eaton 5P", "650", MgeModelType.Eaton5P, "5P 650"),
        new("Eaton 5P", "850", MgeModelType.Eaton5P, "5P 850"),
        new("Eaton 5P", "1150", MgeModelType.Eaton5P, "5P 1150"),
        new("Eaton 5P", "1550", MgeModelType.Eaton5P, "5P 1550"),
        new("Eaton 5P", "650iR G2", MgeModelType.Eaton5P, null),
        new("Eaton 5P", "850iR G2", MgeModelType.Eaton5P, null),
        new("Eaton 5P", "1150iR G2", MgeModelType.Eaton5P, null),
        new("Eaton 5P", "1550iR G2", MgeModelType.Eaton5P, null),
        new("Eaton 5P", "650i G2", MgeModelType.Eaton5P, null),
        new("Eaton 5P", "850i G2", MgeModelType.Eaton5P, null),
        new("Eaton 5P", "1150i G2", MgeModelType.Eaton5P, null),
        new("Eaton 5P", "1550i G2", MgeModelType.Eaton5P, null),
        new("Eaton 5PX", "1500", MgeModelType.Eaton5P, null),
        new("Eaton 5PX", "2200", MgeModelType.Eaton5P, null),
        new("Eaton 5PX", "3000", MgeModelType.Eaton5P, null),
        new("Eaton 5SC", "500", MgeModelType.Eaton5P, null),
        new("Eaton 5SC", "750", MgeModelType.Eaton5P, null),
        new("Eaton 5SC", "1000", MgeModelType.Eaton5P, null),
        new("Eaton 5SC", "1500", MgeModelType.Eaton5P, null),
        new("Eaton 5SC", "2200", MgeModelType.Eaton5P, null),
        new("Eaton 5SC", "3000", MgeModelType.Eaton5P, null),
        new("Ellipse PRO", "1200 ", MgeModelType.Eaton5P, "Eaton 5S1200"),
        new("Ellipse PRO", "1500 ", MgeModelType.Eaton5P, "Eaton 5S1500"),
        new("Ellipse PRO", "1600 ", MgeModelType.Eaton5P, "Eaton 5S1600"),
        new("Eaton 9E", "1000", MgeModelType.Eaton9E, "9E1000"),
        new("Eaton 9E", "1000i", MgeModelType.Eaton9E, "9E1000i"),
        new("Eaton 9E", "1000iau", MgeModelType.Eaton9E, "9E1000iau"),
        new("Eaton 9E", "1000ir", MgeModelType.Eaton9E, "9E1000ir"),
        new("Eaton 9E", "2000", MgeModelType.Eaton9E, "9E2000"),
        new("Eaton 9E", "2000i", MgeModelType.Eaton9E, "9E2000i"),
        new("Eaton 9E", "2000iau", MgeModelType.Eaton9E, "9E2000iau"),
        new("Eaton 9E", "2000ir", MgeModelType.Eaton9E, "9E2000ir"),
        new("Eaton 9E", "3000", MgeModelType.Eaton9E, "9E3000"),
        new("Eaton 9E", "3000i", MgeModelType.Eaton9E, "9E3000i"),
        new("Eaton 9E", "3000iau", MgeModelType.Eaton9E, "9E3000iau"),
        new("Eaton 9E", "3000ir", MgeModelType.Eaton9E, "9E3000ir"),
        new("Eaton 9E", "3000ixl", MgeModelType.Eaton9E, "9E3000ixl"),
        new("Eaton 9E", "3000ixlau", MgeModelType.Eaton9E, "9E3000ixlau"),
        new("unknown", "1000", MgeModelType.Eaton9E, "9E1000i (presumed)"),
        new("unknown", "2000", MgeModelType.Eaton9E, "9E2000i (presumed)"),
        new("unknown", "3000", MgeModelType.Eaton9E, "9E3000i (presumed)"),
        new("Eaton 9SX", "700i", MgeModelType.Eaton9E, "9SX700i"),
        new("Eaton 9SX", "1000i", MgeModelType.Eaton9E, "9SX1000i"),
        new("Eaton 9SX", "1000im", MgeModelType.Eaton9E, "9SX1000im"),
        new("Eaton 9SX", "1500i", MgeModelType.Eaton9E, "9SX1500i"),
        new("Eaton 9SX", "2000i", MgeModelType.Eaton9E, "9SX2000i"),
        new("Eaton 9SX", "3000i", MgeModelType.Eaton9E, "9SX3000i"),
        new("Eaton 9SX", "3000im", MgeModelType.Eaton9E, "9SX3000im"),
        new("Eaton 9SX", "1000ir", MgeModelType.Eaton9E, "9SX1000ir"),
        new("Eaton 9SX", "1500ir", MgeModelType.Eaton9E, "9SX1500ir"),
        new("Eaton 9SX", "2000ir", MgeModelType.Eaton9E, "9SX2000ir"),
        new("Eaton 9SX", "3000ir", MgeModelType.Eaton9E, "9SX3000ir"),
        new("Eaton 9PX", "1000irt2u", MgeModelType.Eaton9E, "9px1000irt2u"),
        new("Eaton 9PX", "1500irt2u", MgeModelType.Eaton9E, "9px1500irt2u"),
        new("Eaton 9PX", "1500irtm", MgeModelType.Eaton9E, "9px1500irtm"),
        new("Eaton 9PX", "2200irt2u", MgeModelType.Eaton9E, "9px2200irt2u"),
        new("Eaton 9PX", "2200irt3u", MgeModelType.Eaton9E, "9px2200irt3u"),
        new("Eaton 9PX", "3000irt2u", MgeModelType.Eaton9E, "9px3000irt2u"),
        new("Eaton 9PX", "3000irt3u", MgeModelType.Eaton9E, "9px3000irt3u"),
        new("Eaton 9PX", "3000irtm", MgeModelType.Eaton9E, "9px3000irtm"),
        new("PULSAR M", "2200", MgeModelType.PulsarM2200, null),
        new("PULSAR M", "3000", MgeModelType.PulsarM3000, null),
        new("PULSAR M", "3000 XL", MgeModelType.PulsarM3000Xl, null),
        new("EX", "2200", MgeModelType.PulsarM2200, null),
        new("EX", "3000", MgeModelType.PulsarM3000, null),
        new("EX", "3000 XL", MgeModelType.PulsarM3000, null),
        new("PULSAR", "MX4000", MgeModelType.Default, "Pulsar MX 4000 RT"),
        new("PULSAR", "MX5000", MgeModelType.Default, "Pulsar MX 5000 RT"),
        new("NOVA AVR", "500", MgeModelType.Default, "Nova 500 AVR"),
        new("NOVA AVR", "600", MgeModelType.Default, "Nova 600 AVR"),
        new("NOVA AVR", "625", MgeModelType.Default, "Nova 625 AVR"),
        new("NOVA AVR", "1100", MgeModelType.Default, "Nova 1100 AVR"),
        new("NOVA AVR", "1250", MgeModelType.Default, "Nova 1250 AVR"),
        new("EXtreme", "700C", MgeModelType.Default, "Pulsar EXtreme 700C"),
        new("EXtreme", "1000C", MgeModelType.Default, "Pulsar EXtreme 1000C"),
        new("EXtreme", "1500C", MgeModelType.Default, "Pulsar EXtreme 1500C"),
        new("EXtreme", "1500CCLA", MgeModelType.Default, "Pulsar EXtreme 1500C CLA"),
        new("EXtreme", "2200C", MgeModelType.Default, "Pulsar EXtreme 2200C"),
        new("EXtreme", "3200C", MgeModelType.Default, "Pulsar EXtreme 3200C"),
        new("EX", "700RT", MgeModelType.Default, "Pulsar EX 700 RT"),
        new("EX", "1000RT", MgeModelType.Default, "Pulsar EX 1000 RT"),
        new("EX", "1500RT", MgeModelType.Default, "Pulsar EX 1500 RT"),
        new("EX", "2200RT", MgeModelType.Default, "Pulsar EX 2200 RT"),
        new("EX", "3200RT", MgeModelType.Default, "Pulsar EX 3200 RT"),
        new("EX", "5RT31", MgeModelType.Default, "EX 5 RT 3:1"),
        new("EX", "7RT31", MgeModelType.Default, "EX 7 RT 3:1"),
        new("EX", "11RT31", MgeModelType.Default, "EX 11 RT 3:1"),
        new("EX", "5RT", MgeModelType.Default, "EX 5 RT"),
        new("EX", "7RT", MgeModelType.Default, "EX 7 RT"),
        new("EX", "11RT", MgeModelType.Default, "EX 11 RT"),
        new("GALAXY", "3000_10", MgeModelType.Default, "Galaxy 3000 10 kVA"),
        new("GALAXY", "3000_15", MgeModelType.Default, "Galaxy 3000 15 kVA"),
        new("GALAXY", "3000_20", MgeModelType.Default, "Galaxy 3000 20 kVA"),
        new("GALAXY", "3000_30", MgeModelType.Default, "Galaxy 3000 30 kVA"),
    ];
}

/// <summary>The model families of NUT mge-hid.c (models_type_t); the high byte is the family.</summary>
internal enum MgeModelType
{
    DefaultOffline = 0,
    Pegasus = 0x100,
    ThreeS = 0x110,
    Default = 0x200,
    Evolution = 0x300,
    Evolution650 = 0x301,
    Evolution850 = 0x302,
    Evolution1150 = 0x303,
    EvolutionS1250 = 0x304,
    Evolution1550 = 0x305,
    EvolutionS1750 = 0x306,
    Evolution2000 = 0x307,
    EvolutionS2500 = 0x308,
    EvolutionS3000 = 0x309,
    PulsarM = 0x400,
    PulsarM2200 = 0x401,
    PulsarM3000 = 0x402,
    PulsarM3000Xl = 0x403,
    Eaton5P = 0x500,
    Eaton9E = 0x900,
}
