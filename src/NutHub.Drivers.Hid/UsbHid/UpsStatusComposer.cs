using System.Globalization;
using Microsoft.Extensions.Logging;

namespace NutHub.Drivers.Hid.UsbHid;

/// <summary>The NUT view of the status bits: ups.status tokens, transfer reason, alarms, mode buzzwords.</summary>
internal sealed record ComposedStatus(
    IReadOnlyList<string> Tokens,
    string? TransferReason,
    IReadOnlyList<string> Alarms,
    IReadOnlyList<string> Buzzwords);

/// <summary>
/// Turns the raw status bits into ups.status exactly as NUT's usbhid-ups does (ups_status_set and ups_alarm_set in
/// drivers/usbhid-ups.c), including its handling of devices that report on line and discharging together, the
/// CHRG inference from battery.charge, and the optional delay before LB/RB for APC models that flap them during
/// self-calibration. Holds timing state, so use one instance per connected device.
/// </summary>
internal sealed class UpsStatusComposer(
    TimeProvider time,
    ILogger logger,
    bool onlineDischargeOnBattery,
    bool onlineDischargeCalibration,
    int lowReplaceDelaySeconds = 0,
    bool lowReplaceDelayWithoutCalibrating = false)
{
    private static readonly TimeSpan OnlineDischargeLogInterval = TimeSpan.FromMinutes(10);

    private DateTimeOffset? _lastLowBatteryStart;
    private DateTimeOffset? _lastReplaceBatteryStart;
    private DateTimeOffset? _calibrationStart;
    private DateTimeOffset? _onlineDischargeLogged;

    public int LowReplaceDelaySeconds { get; set; } = lowReplaceDelaySeconds;

    public ComposedStatus Compose(UpsStatusBits bits, string? batteryCharge, string upsDescription)
    {
        DateTimeOffset now = time.GetUtcNow();
        var tokens = new List<string>();
        var buzzwords = new List<string>();
        void Set(string token)
        {
            if (!tokens.Contains(token))
            {
                tokens.Add(token);
            }
        }

        bool Has(UpsStatusBits flag) => (bits & flag) != 0;

        string? reason = Has(UpsStatusBits.VoltageOutOfRange) ? "input voltage out of range"
            : Has(UpsStatusBits.FrequencyOutOfRange) ? "input frequency out of range"
            : null;

        // Calibration first: it looks like OB or OFF on many models and must not look like a real outage.
        if (Has(UpsStatusBits.Calibrating))
        {
            SetCalibrating(Set, now);
        }

        if (!Has(UpsStatusBits.Discharging))
        {
            _onlineDischargeLogged = null;
        }

        if (Has(UpsStatusBits.Offline))
        {
            Set("OB");
        }

        if (!Has(UpsStatusBits.Online))
        {
            if (Has(UpsStatusBits.Offline) || Has(UpsStatusBits.Discharging))
            {
                // Without a power state, discharging means on battery.
                Set("OB");
            }
        }
        else if (Has(UpsStatusBits.Discharging))
        {
            if (onlineDischargeCalibration)
            {
                SetCalibrating(Set, now);
            }

            if (onlineDischargeOnBattery)
            {
                Set("OB");
            }

            if (!onlineDischargeCalibration && !onlineDischargeOnBattery)
            {
                if (!Has(UpsStatusBits.Calibrating) &&
                    (_onlineDischargeLogged is null || now - _onlineDischargeLogged >= OnlineDischargeLogInterval))
                {
                    _onlineDischargeLogged = now;
                    logger.LogWarning(
                        "{Ups} reports being on line and discharging at the same time. If it is calibrating, enable the " +
                        "'onlineDischargeCalibration' option; some models (e.g. CyberPower UT) report this when they " +
                        "actually run on battery: then enable 'onlineDischarge'.", upsDescription);
                }

                Set("OL");
            }
        }
        else
        {
            Set("OL");
        }

        bool calibrating = tokens.Contains("CAL");
        if (Has(UpsStatusBits.Discharging) && !Has(UpsStatusBits.Depleted))
        {
            Set("DISCHRG");
        }

        if (Has(UpsStatusBits.Charging))
        {
            if (Has(UpsStatusBits.NotFullyCharged))
            {
                Set("CHRG");
            }
            else if (!Has(UpsStatusBits.FullyCharged) && TryParseCharge(batteryCharge, out int charge) &&
                     charge > 0 && charge < 100)
            {
                // The device does not say whether the battery is full: trust the charge level instead.
                Set("CHRG");
            }
        }

        if (Has(UpsStatusBits.LowBattery | UpsStatusBits.TimeLimitExpired | UpsStatusBits.ShutdownImminent))
        {
            if (Immediate(calibrating, bits))
            {
                Set("LB");
            }
            else
            {
                _lastLowBatteryStart ??= now;
                if ((now - _lastLowBatteryStart.Value).TotalSeconds > LowReplaceDelaySeconds)
                {
                    Set("LB");
                }
            }
        }
        else
        {
            _lastLowBatteryStart = null;
        }

        if (Has(UpsStatusBits.Overload))
        {
            Set("OVER");
        }

        if (Has(UpsStatusBits.ReplaceBattery | UpsStatusBits.NoBattery))
        {
            if (Immediate(calibrating, bits) || _lastLowBatteryStart is null)
            {
                Set("RB");
            }
            else
            {
                _lastReplaceBatteryStart ??= now;
                if ((now - _lastReplaceBatteryStart.Value).TotalSeconds > LowReplaceDelaySeconds)
                {
                    Set("RB");
                }
            }
        }
        else
        {
            _lastReplaceBatteryStart = null;
        }

        if (Has(UpsStatusBits.Trim))
        {
            Set("TRIM");
        }

        if (Has(UpsStatusBits.Boost))
        {
            Set("BOOST");
        }

        if (Has(UpsStatusBits.BypassAuto | UpsStatusBits.BypassManual))
        {
            Set("BYPASS");
        }

        if (Has(UpsStatusBits.EcoMode))
        {
            buzzwords.Add("vendor:default:ECO");
        }

        if (Has(UpsStatusBits.EssMode))
        {
            buzzwords.Add("vendor:default:ESS");
        }

        if (Has(UpsStatusBits.Off))
        {
            Set("OFF");
        }

        if (!calibrating)
        {
            _calibrationStart = null;
        }

        return new ComposedStatus(tokens, reason, Alarms(bits), buzzwords);
    }

    /// <summary>The alarms implied by the status bits, in NUT's order (ups_alarm_set).</summary>
    public static IReadOnlyList<string> Alarms(UpsStatusBits bits)
    {
        var alarms = new List<string>();
        void Add(UpsStatusBits flag, string text)
        {
            if ((bits & flag) != 0)
            {
                alarms.Add(text);
            }
        }

        Add(UpsStatusBits.ReplaceBattery, "Replace battery!");
        Add(UpsStatusBits.ShutdownImminent, "Shutdown imminent!");
        Add(UpsStatusBits.FanFailure, "Fan failure!");
        Add(UpsStatusBits.NoBattery, "No battery installed!");
        Add(UpsStatusBits.BatteryVoltageLow, "Battery voltage too low!");
        Add(UpsStatusBits.BatteryVoltageHigh, "Battery voltage too high!");
        Add(UpsStatusBits.ChargerFailure, "Battery charger fail!");
        Add(UpsStatusBits.Overheat, "Temperature too high!");
        Add(UpsStatusBits.CommFault, "Internal UPS fault!");
        Add(UpsStatusBits.AwaitingPower, "Awaiting power!");
        Add(UpsStatusBits.BypassAuto, "Automatic bypass mode!");
        Add(UpsStatusBits.BypassManual, "Manual bypass mode!");
        return alarms;
    }

    /// <summary>
    /// LB and RB are reported at once unless a delay is configured and the UPS is calibrating on line power; a
    /// real power failure never waits.
    /// </summary>
    private bool Immediate(bool calibrating, UpsStatusBits bits) =>
        LowReplaceDelaySeconds < 1 ||
        (!calibrating && !lowReplaceDelayWithoutCalibrating) ||
        (bits & UpsStatusBits.Online) == 0;

    private void SetCalibrating(Action<string> set, DateTimeOffset now)
    {
        _calibrationStart ??= now;
        set("CAL");
    }

    private static bool TryParseCharge(string? text, out int charge)
    {
        charge = 0;
        if (text is null || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
        {
            return false;
        }

        charge = (int)value;
        return true;
    }
}
