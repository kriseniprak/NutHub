using System.Globalization;
using NutHub.Drivers.Serial.Transport;

namespace NutHub.Drivers.Serial.Megatec;

/// <summary>The validated options of one Megatec/Q1 UPS.</summary>
internal sealed record MegatecSettings
{
    public required TransportSettings Transport { get; init; }

    /// <summary>"auto" or one of <see cref="QxProtocols.Names"/>.</summary>
    public string Protocol { get; init; } = QxProtocols.Auto;

    /// <summary>How long to wait for a complete reply to one command.</summary>
    public TimeSpan ReplyTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>ups.delay.start at start-up, seconds (NUT ondelay).</summary>
    public int OnDelay { get; init; } = 180;

    /// <summary>ups.delay.shutdown at start-up, seconds (NUT offdelay).</summary>
    public int OffDelay { get; init; } = 30;

    public double? BatteryVoltageHigh { get; init; }

    public double? BatteryVoltageLow { get; init; }

    public double? BatteryVoltageNominal { get; init; }

    /// <summary>Number of battery packs (or cells) the reported battery voltage must be multiplied by.</summary>
    public double? BatteryPacks { get; init; }

    /// <summary>Publish battery.voltage multiplied by the packs (NUT battery_voltage_reports_one_pack).</summary>
    public bool BatteryVoltageReportsOnePack { get; init; }

    public RuntimeCalibration? RuntimeCalibration { get; init; }

    /// <summary>Seconds to recharge an empty battery (NUT chargetime), used with <see cref="RuntimeCalibration"/>.</summary>
    public int ChargeTime { get; init; } = 43200;

    /// <summary>Minimum load, as a fraction (0.1 = 10 %), for the runtime estimate (NUT idleload).</summary>
    public double IdleLoad { get; init; } = 0.1;

    /// <summary>Ignore the "Shutdown Active" status bit, which some UPSes always report (NUT ignoresab).</summary>
    public bool IgnoreShutdownActive { get; init; }

    /// <summary>Do not query the rating information, F (NUT norating).</summary>
    public bool NoRating { get; init; }

    /// <summary>Do not query the vendor information, I or FW? (NUT novendor).</summary>
    public bool NoVendor { get; init; }
}

/// <summary>
/// Two known runtimes at two loads (NUT runtimecal = "runtime1,load1,runtime2,load2"), from which the driver derives
/// how runtime scales with load: runtime = nominal / (load/100)^exponent.
/// </summary>
internal sealed record RuntimeCalibration(double RuntimeHigh, double LoadHigh, double RuntimeLow, double LoadLow)
{
    /// <summary>The exponent of the load in the runtime formula.</summary>
    public double Exponent => Math.Log(RuntimeLow / RuntimeHigh) / Math.Log(LoadHigh / LoadLow);

    /// <summary>The runtime at 100 % load, seconds.</summary>
    public double NominalRuntime => RuntimeHigh * Math.Pow(LoadHigh / 100, Exponent);

    /// <summary>Parses and validates the NUT format; the checks are those of nutdrv_qx.c qx_initbattery.</summary>
    /// <exception cref="FormatException">The text is not four numbers or they are out of range.</exception>
    public static RuntimeCalibration Parse(string text)
    {
        string[] parts = text.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 4)
        {
            throw new FormatException("Enter four numbers: runtime at the higher load (s), that load (%), runtime at the lower load (s), that load (%), e.g. 240,100,720,50.");
        }

        var values = new double[4];
        for (int i = 0; i < 4; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) ||
                double.IsNaN(values[i]) || double.IsInfinity(values[i]))
            {
                throw new FormatException($"'{parts[i]}' is not a number.");
            }
        }

        var cal = new RuntimeCalibration(values[0], values[1], values[2], values[3]);
        if (cal.RuntimeHigh <= 0 || cal.RuntimeLow < cal.RuntimeHigh)
        {
            throw new FormatException("The runtimes must be positive, and the runtime at the lower load at least as long as the one at the higher load.");
        }

        if (cal.LoadHigh > 100 || cal.LoadLow <= 0 || cal.LoadLow >= cal.LoadHigh)
        {
            throw new FormatException("The loads must be between 0 and 100 %, the first one higher than the second.");
        }

        return cal;
    }
}
