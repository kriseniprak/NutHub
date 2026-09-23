using Microsoft.Extensions.Logging;
using NutHub.Core.Model;

namespace NutHub.Drivers.Serial.Megatec;

/// <summary>
/// Estimates battery.charge (from the battery voltage) and battery.runtime (from a runtime calibration and the load)
/// for UPSes that do not report them, as NUT's nutdrv_qx does ("BATTERY CHARGE GUESSTIMATION" in nutdrv_qx(8)). Port of
/// qx_initbattery, qx_battery, qx_load and the estimation step of qx_ups_walk in NUT drivers/nutdrv_qx.c.
/// </summary>
/// <remarks>
/// The voltage used for the estimate is always the reported battery voltage multiplied by the detected or configured
/// number of packs, as the nutdrv_qx manual describes (batteryVoltageReportsOnePack only changes the published value).
/// </remarks>
internal sealed class BatteryEstimator
{
    private static readonly double[] PackCandidates = [120, 100, 80, 60, 48, 36, 30, 24, 18, 12, 8, 6, 4, 3, 2, 1, 0.5];

    private readonly MegatecSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    private double _high = -1;
    private double _low = -1;
    private double _nominal = -1;
    private double _packs = 1;
    private RuntimeCalibration? _calibration;
    private double _runtimeNominal;
    private double _runtimeEstimate;
    private double _loadEffective = 1;
    private long _lastPoll;

    public BatteryEstimator(MegatecSettings settings, TimeProvider time, ILogger logger)
    {
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    /// <summary>Whether battery.runtime is being estimated from the runtime calibration.</summary>
    public bool EstimatesRuntime => _calibration is not null;

    public double Packs => _packs;

    /// <summary>Called once after the first complete reading of the UPS.</summary>
    public void Initialize(QxState state)
    {
        _high = Apply(state, "battery.voltage.high", _settings.BatteryVoltageHigh);
        _low = Apply(state, "battery.voltage.low", _settings.BatteryVoltageLow);
        _nominal = Apply(state, "battery.voltage.nominal", _settings.BatteryVoltageNominal);

        // Without high/low limits but with a nominal voltage, guess them (12 V battery: 10.4 V empty, 13.0 V full).
        if (_nominal > 0 && (_low <= 0 || _high <= 0))
        {
            if (_low <= 0)
            {
                _low = 104 * _nominal / 120;
                state.Values["battery.voltage.low"] = NutFormat.Fixed(_low, 2);
            }

            if (_high <= 0)
            {
                _high = 130 * _nominal / 120;
                state.Values["battery.voltage.high"] = NutFormat.Fixed(_high, 2);
            }

            _logger.LogInformation("No battery high/low voltages known; guessing {Low:0.00} V / {High:0.00} V from the nominal {Nominal} V.",
                                   _low, _high, _nominal);
        }

        bool packsKnown = false;
        if (_settings.BatteryPacks is { } configured)
        {
            _packs = configured;
            packsKnown = true;
        }
        else if (state.Get("battery.packs") is { } reported && Common.CText.IsNumericField(reported) &&
                 Common.CText.TryStrToD(reported, out double packs) && packs > 0)
        {
            _packs = packs;
            packsKnown = true;
        }

        bool deviceCharge = state.Values.ContainsKey("battery.charge");
        bool deviceRuntime = state.Values.ContainsKey("battery.runtime");
        if (deviceCharge && deviceRuntime)
        {
            state.BatteryPacks = _packs;
            return;
        }

        if (!packsKnown)
        {
            DetectPacks(state);
        }

        state.BatteryPacks = _packs;
        ChargeFromVoltage(state, deviceCharge);

        if (_settings.RuntimeCalibration is not { } calibration)
        {
            if (!deviceRuntime)
            {
                _logger.LogInformation("Battery runtime will not be estimated (runtimeCal not set).");
            }

            return;
        }

        _runtimeNominal = calibration.NominalRuntime;
        double? charge = state.Number("battery.charge");
        if (charge is null && _nominal > 0)
        {
            _low = _nominal;
            _high = 1.15 * _nominal;
            ChargeFromVoltage(state, deviceCharge: false);
            charge = state.Number("battery.charge");
        }

        if (charge is null)
        {
            _logger.LogWarning("The initial battery charge cannot be determined (no battery voltage and nominal voltage); " +
                               "battery runtime will not be estimated.");
            return;
        }

        _calibration = calibration;
        _runtimeEstimate = _runtimeNominal * charge.Value / 100;
        _lastPoll = _time.GetTimestamp();
    }

    /// <summary>Called after every poll with what the UPS reported itself this time.</summary>
    public void Update(QxState state, bool chargeReported, bool runtimeReported)
    {
        if (chargeReported && runtimeReported)
        {
            return;
        }

        if (_calibration is null)
        {
            ChargeFromVoltage(state, chargeReported);
            return;
        }

        long now = _time.GetTimestamp();
        double elapsed = Math.Clamp(_time.GetElapsedTime(_lastPoll, now).TotalSeconds, 0, 86400);
        _lastPoll = now;

        if (state.Number("ups.load") is { } load)
        {
            _loadEffective = Math.Max(Math.Pow(load / 100, _calibration.Exponent), _settings.IdleLoad);
        }

        if (state.Has(QxStatusBits.Online))
        {
            _runtimeEstimate = Math.Min(_runtimeEstimate + _runtimeNominal * elapsed / _settings.ChargeTime, _runtimeNominal);
        }
        else
        {
            _runtimeEstimate = Math.Max(_runtimeEstimate - _loadEffective * elapsed, 0);
        }

        // An aged battery empties faster than the load model says: trust the voltage when it is lower.
        if (state.RawBatteryVoltage is { } raw && raw > 0 && _low > 0 && _high > _low)
        {
            double fromVoltage = Math.Clamp((raw * _packs - _low) / (_high - _low), 0, 1);
            if (fromVoltage < _runtimeEstimate / _runtimeNominal)
            {
                _runtimeEstimate = fromVoltage * _runtimeNominal;
            }
        }

        if (!chargeReported)
        {
            state.Values["battery.charge"] = NutFormat.Fixed(100 * _runtimeEstimate / _runtimeNominal, 0);
        }

        if (!runtimeReported && state.Number("ups.load") is not null)
        {
            state.Values["battery.runtime"] = NutFormat.Fixed(_runtimeEstimate / _loadEffective, 0);
        }
    }

    /// <summary>
    /// A user setting wins over the UPS's own value (the UPS can report nonsense, and Core overrides are applied too late
    /// for this estimate); it is published so clients see the values the estimate uses.
    /// </summary>
    private static double Apply(QxState state, string name, double? configured)
    {
        if (configured is { } value)
        {
            state.Values[name] = NutFormat.Number(value, 2);
            return value;
        }

        return state.Number(name) ?? -1;
    }

    private void DetectPacks(QxState state)
    {
        if (state.RawBatteryVoltage is not { } voltage || voltage <= 0 || _nominal <= 0)
        {
            _logger.LogInformation("Cannot detect the number of battery packs (nominal {Nominal} V, battery {Voltage} V).",
                                   _nominal, state.RawBatteryVoltage);
            return;
        }

        // The voltage quickly returns to at least nominal after a discharge; for overlapping ranges prefer the highest
        // multiplier.
        foreach (double candidate in PackCandidates)
        {
            if (candidate * voltage > 1.25 * _nominal)
            {
                continue;
            }

            if (candidate * voltage < 0.8 * _nominal)
            {
                break;
            }

            _packs = candidate;
            _logger.LogInformation("Detected {Packs} as the number of battery packs (nominal {Nominal} V, battery {Voltage} V).",
                                   candidate, _nominal, voltage);
            return;
        }

        _logger.LogInformation("Cannot detect the number of battery packs (nominal {Nominal} V, battery {Voltage} V).",
                               _nominal, voltage);
    }

    /// <summary>Linear charge between the low and high battery voltages (NUT qx_battery).</summary>
    private void ChargeFromVoltage(QxState state, bool deviceCharge)
    {
        if (deviceCharge || state.RawBatteryVoltage is not { } raw || _low <= 0 || _high <= _low)
        {
            return;
        }

        double charge = Math.Clamp(100 * (raw * _packs - _low) / (_high - _low), 0, 100);
        state.Values["battery.charge"] = NutFormat.Fixed(charge, 0);
    }
}
