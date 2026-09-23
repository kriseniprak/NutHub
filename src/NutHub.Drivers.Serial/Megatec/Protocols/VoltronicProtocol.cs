using System.Globalization;
using System.Text;
using NutHub.Drivers.Serial.Common;

namespace NutHub.Drivers.Serial.Megatec.Protocols;

/// <summary>
/// Voltronic Power "P" protocol UPSes (QPI answers PI00...PI99): QGS status, QMOD working mode, QWS warnings, QBV
/// battery, QRI/QMD ratings, QMF/QVFW/QID identification. Ported from NUT drivers/nutdrv_qx_voltronic.c.
/// </summary>
/// <remarks>
/// Left out, compared with NUT: the capability flags (QFLAG and the settings they enable: ups.start.auto,
/// beeper.enable/disable, ECO/bypass limits, bypass.start/stop), the programmable outlets (QSK/QSKT), the three-phase
/// readings (Q3xx), the fault details (QFS), the P31 battery type and grid range, phase angle and parallel queries, and
/// the writable battery settings (BATN, BATGN, W0E).
/// </remarks>
internal sealed class VoltronicProtocol : QxProtocol
{
    private const string ProtocolKey = "voltronic.protocol";
    private static readonly int[] SupportedProtocols = [0, 1, 2, 3, 8, 9, 10, 13, 14, 31, 71, 99];

    public VoltronicProtocol(QxProtocolOptions options)
        : base(options)
    {
        var fields = new List<QxField>
        {
            // > [QPI\r]  < [(PI00\r]
            new("ups.firmware.aux", "QPI\r", 6, '(', 1, 4, QxFormat.Text, QxFlags.Static, ProtocolId),

            // > [QRI\r]  < [(230.0 004 024.0 50.0\r]
            new("output.voltage.nominal", "QRI\r", 22, '(', 1, 5, QxFormat.Fixed(1), QxFlags.Static),
            new("output.current.nominal", "QRI\r", 22, '(', 7, 9, QxFormat.Fixed(0), QxFlags.Static),
            new("battery.voltage.nominal", "QRI\r", 22, '(', 11, 15, QxFormat.Fixed(1), QxFlags.SemiStatic),
            new("output.frequency.nominal", "QRI\r", 22, '(', 17, 20, QxFormat.Fixed(1), QxFlags.Static),

            // > [QMD\r]  < [(#######OLHVT1K0 ###1000 80 1/1 230 230 02 12.0\r]
            new("device.model", "QMD\r", 48, '(', 1, 15, QxFormat.Text, QxFlags.Static | QxFlags.Trim),
            new("ups.power.nominal", "QMD\r", 48, '(', 17, 23, QxFormat.Text, QxFlags.Static | QxFlags.Trim),
            new("output.powerfactor", "QMD\r", 48, '(', 25, 26, QxFormat.Fixed(1), QxFlags.Static, PowerFactor),
            new("input.phases", "QMD\r", 48, '(', 28, 28, QxFormat.Fixed(0), QxFlags.Static),
            new("output.phases", "QMD\r", 48, '(', 30, 30, QxFormat.Fixed(0), QxFlags.Static),
            new("input.voltage.nominal", "QMD\r", 48, '(', 32, 34, QxFormat.Fixed(1), QxFlags.Static),
            new("output.voltage.nominal", "QMD\r", 48, '(', 36, 38, QxFormat.Fixed(1), QxFlags.Static),
        };

        // > [F\r]  < [#220.0 000 024.0 50.0\r]
        fields.AddRange(BlazerCommon.RatingFields(voltageDecimals: 1, frequencyDecimals: 1));

        // > [QMF\r] < [(#######BOH\r]   > [QVFW\r] < [(VERFW:00322.02\r]   > [QID\r] < [(12345679012345\r]
        fields.Add(new("device.mfr", "QMF\r", 2, '(', 1, 0, QxFormat.Text, QxFlags.Static | QxFlags.Trim));
        fields.Add(new("ups.firmware", "QVFW\r", 16, '(', 7, 14, QxFormat.Text, QxFlags.Static));
        fields.Add(new("device.serial", "QID\r", 2, '(', 1, 0, QxFormat.Text, QxFlags.Static, SerialNumber));

        // > [I\r]  < [#-------------   ------     VT12046Q  \r]
        fields.Add(new("device.mfr", "I\r", 39, '#', 1, 15, QxFormat.Text, QxFlags.Static | QxFlags.Trim));
        fields.Add(new("device.model", "I\r", 39, '#', 17, 26, QxFormat.Text, QxFlags.Static | QxFlags.Trim));
        fields.Add(new("ups.firmware", "I\r", 39, '#', 28, 37, QxFormat.Text, QxFlags.Static | QxFlags.Trim));

        // > [QGS\r]
        // < [(234.9 50.0 229.8 50.0 000.0 000 369.1 ---.- 026.5 ---.- 018.8 100000000001\r]
        const string qgs = "QGS\r";
        fields.Add(new("input.voltage", qgs, 76, '(', 1, 5, QxFormat.Fixed(1)));
        fields.Add(new("input.frequency", qgs, 76, '(', 7, 10, QxFormat.Fixed(1)));
        fields.Add(new("output.voltage", qgs, 76, '(', 12, 16, QxFormat.Fixed(1)));
        fields.Add(new("output.frequency", qgs, 76, '(', 18, 21, QxFormat.Fixed(1)));
        fields.Add(new("output.current", qgs, 76, '(', 23, 27, QxFormat.Fixed(1)));
        fields.Add(new("ups.load", qgs, 76, '(', 29, 31, QxFormat.Fixed(0)));
        fields.Add(new("battery.voltage", qgs, 76, '(', 45, 49, QxFormat.Fixed(2), QxFlags.None, BlazerCommon.BatteryVoltage));
        fields.Add(new("ups.temperature", qgs, 76, '(', 57, 61, QxFormat.Fixed(1)));
        fields.Add(new("ups.type", qgs, 76, '(', 63, 64, QxFormat.Text, QxFlags.SemiStatic, StatusBits));
        fields.Add(new("ups.status", qgs, 76, '(', 65, 65, QxFormat.Text, QxFlags.QuickPoll, StatusBits));
        fields.Add(new("ups.status", qgs, 76, '(', 66, 66, QxFormat.Text, QxFlags.QuickPoll, StatusBits));
        fields.Add(new("ups.status", qgs, 76, '(', 67, 67, QxFormat.Text, QxFlags.QuickPoll, StatusBits));
        fields.Add(new("ups.alarm", qgs, 76, '(', 67, 67, QxFormat.Text, QxFlags.None, StatusBits));
        fields.Add(new("ups.alarm", qgs, 76, '(', 68, 68, QxFormat.Text, QxFlags.None, StatusBits));
        fields.Add(new("ups.status", qgs, 76, '(', 70, 70, QxFormat.Text, QxFlags.QuickPoll, StatusBits));
        fields.Add(new("ups.status", qgs, 76, '(', 71, 71, QxFormat.Text, QxFlags.QuickPoll, StatusBits));
        fields.Add(new("ups.beeper.status", qgs, 76, '(', 72, 72, QxFormat.Text, QxFlags.None, StatusBits));

        // > [QMOD\r]  < [(S\r]
        fields.Add(new("ups.alarm", "QMOD\r", 3, '(', 1, 1, QxFormat.Text, QxFlags.None, Mode));
        fields.Add(new("ups.status", "QMOD\r", 3, '(', 1, 1, QxFormat.Text, QxFlags.None, Mode));

        // > [QWS\r]  < [(0000000100000000000000000000000000000000000000000000000000000000\r]
        fields.Add(new("ups.alarm", "QWS\r", 66, '(', 1, 64, QxFormat.Text, QxFlags.None, Warnings));

        // > [QBV\r]  < [(026.5 02 01 068 255\r]
        fields.Add(new("battery.voltage", "QBV\r", 21, '(', 1, 5, QxFormat.Fixed(2), QxFlags.None, BlazerCommon.BatteryVoltage));
        fields.Add(new("battery_number", "QBV\r", 21, '(', 7, 9, QxFormat.Fixed(0), QxFlags.SemiStatic | QxFlags.NoNut));
        fields.Add(new("battery.packs", "QBV\r", 21, '(', 10, 11, QxFormat.Fixed(0), QxFlags.SemiStatic));
        fields.Add(new("battery.charge", "QBV\r", 21, '(', 13, 15, QxFormat.Fixed(0)));
        fields.Add(new("battery.runtime", "QBV\r", 21, '(', 17, 0, QxFormat.Fixed(0), QxFlags.None, RuntimeMinutes));
        Fields = fields;

        Commands =
        [
            new("load.off", "SOFF\r", 5, '(', 1, 3),
            new("load.on", "SON\r", 5, '(', 1, 3),
            new("shutdown.return", "S%s\r", 5, '(', 1, 3, FormatCommand),
            new("shutdown.stayoff", "S%sR0000\r", 5, '(', 1, 3, FormatCommand),
            new("shutdown.stop", "CS\r", 5, '(', 1, 3),
            new("test.battery.start", "T%s\r", 5, '(', 1, 3, FormatCommand),
            new("test.battery.start.deep", "TL\r", 5, '(', 1, 3),
            new("test.battery.start.quick", "T\r", 5, '(', 1, 3),
            new("test.battery.stop", "CT\r", 5, '(', 1, 3),
            new("beeper.toggle", "BZ%s\r", 5, '(', 1, 3, FormatCommand),
        ];
    }

    public override string Name => "voltronic";

    public override string Version => "Voltronic 0.15";

    public override IReadOnlyList<QxField> Fields { get; }

    public override IReadOnlyList<QxCommand> Commands { get; }

    public override string? Rejected => "(NAK\r";

    public override IReadOnlyList<string> ClaimFields => ["input.voltage", "ups.firmware.aux"];

    public override (int Min, int Max) OffDelayRange => (12, 5940);

    protected override bool IsShutdownActiveBit(QxField field) => false;

    /// <summary>"PIxx": only the protocol numbers the NUT subdriver knows (voltronic_protocol).</summary>
    private static bool ProtocolId(QxField field, string raw, QxState state, out string value)
    {
        value = string.Empty;
        if (raw.Length < 3 || !raw.StartsWith("PI", StringComparison.OrdinalIgnoreCase) ||
            !CText.OnlyChars(raw[2..], "01234789"))
        {
            return false;
        }

        long protocol = CText.StrToL(raw[2..]);
        if (!SupportedProtocols.Contains((int)protocol))
        {
            return false;
        }

        state.Internal[ProtocolKey] = protocol.ToString(CultureInfo.InvariantCulture);
        value = string.Create(CultureInfo.InvariantCulture, $"P{protocol:00}");
        return true;
    }

    /// <summary>The power factor is reported as a percentage: 80 means 0.8.</summary>
    private static bool PowerFactor(QxField field, string raw, QxState state, out string value)
    {
        value = string.Empty;
        if (!CText.IsNumericField(raw))
        {
            return false;
        }

        value = field.Format.Apply(CText.StrToD(raw) * 0.01);
        return true;
    }

    /// <summary>An all-zero serial number means "not set": leave device.serial out.</summary>
    private static bool SerialNumber(QxField field, string raw, QxState state, out string value)
    {
        value = raw;
        return raw.Length > 0 && !CText.OnlyChars(raw, "0");
    }

    private static bool RuntimeMinutes(QxField field, string raw, QxState state, out string value)
    {
        value = string.Empty;
        if (!CText.IsNumericField(raw))
        {
            return false;
        }

        value = field.Format.Apply(CText.StrToD(raw) * 60);
        return true;
    }

    /// <summary>The 12 status flags at the end of QGS (NUT voltronic_status).</summary>
    private static bool StatusBits(QxField field, string raw, QxState state, out string value)
    {
        value = string.Empty;

        // PI71 fills the type with "--" and unused bits with "-".
        if (raw == "--" && field.From == 63)
        {
            value = "online";
            return true;
        }

        if (raw == "-")
        {
            return true;
        }

        if (raw.Length == 0 || !CText.OnlyChars(raw, "01"))
        {
            return false;
        }

        bool set = raw[0] == '1';
        switch (field.From)
        {
            case 63:
                value = CText.StrToL(raw) switch { 0 => "offline", 1 => "line-interactive", 10 => "online", _ => "" };
                return value.Length > 0;
            case 65:
                value = set ? "!OL" : "OL";
                return true;
            case 66:
                value = set ? "LB" : "!LB";
                return true;
            case 67:
                if (set)
                {
                    string mode = BlazerCommon.BypassBoostOrTrim(state);
                    bool avrOnly = state.Internal.TryGetValue(ProtocolKey, out string? p) && p is "0" or "8";
                    if (mode == "BYPASS" && avrOnly)
                    {
                        // P00/P08 report their AVR stage with this bit: an alarm, not a bypass.
                        value = field.IsAlarm ? "UPS is in AVR Mode." : string.Empty;
                    }
                    else
                    {
                        value = field.IsStatus ? mode : string.Empty;
                    }
                }

                return true;
            case 68:
                // NUT compares the character with the number 1 here, so it never reports it; the intent is clear.
                value = set ? "UPS Fault!" : string.Empty;
                return true;
            case 70:
                value = set ? "CAL" : "!CAL";
                return true;
            case 71:
                value = set ? "FSD" : "!FSD";
                return true;
            case 72:
                value = set ? "disabled" : "enabled";
                return true;
            default:
                return false;
        }
    }

    /// <summary>The working mode letter of QMOD (NUT voltronic_mode).</summary>
    private static bool Mode(QxField field, string raw, QxState state, out string value)
    {
        value = string.Empty;
        string? status = null;
        string? alarm = null;
        switch (raw.Length > 0 ? raw[0] : '\0')
        {
            case 'P':
                alarm = "UPS is going ON.";
                break;
            case 'S':
                status = "OFF";
                break;
            case 'Y':
                status = "BYPASS";
                break;
            case 'L':
                status = "OL";
                break;
            case 'B':
                status = "!OL";
                break;
            case 'T':
                status = "CAL";
                break;
            case 'F':
                alarm = "Fault reported by UPS.";
                break;
            case 'E':
                // ECO: the load runs from the mains through the bypass while the inverter stands by.
                status = "ECO";
                break;
            case 'C':
                // Frequency converter mode: nothing to report in NUT terms.
                break;
            case 'D':
                alarm = "UPS is shutting down!";
                status = "FSD";
                break;
            default:
                return false;
        }

        if (field.IsAlarm && alarm is not null)
        {
            value = alarm;
        }
        else if (field.IsStatus && status is not null)
        {
            value = status;
        }

        return true;
    }

    /// <summary>
    /// The 64 warning flags of QWS as one alarm text; low battery, overload and battery replacement also set the status
    /// (NUT voltronic_warning).
    /// </summary>
    private static bool Warnings(QxField field, string raw, QxState state, out string value)
    {
        value = string.Empty;
        if (!CText.OnlyChars(raw, "01"))
        {
            return false;
        }

        if (CText.OnlyChars(raw, "0"))
        {
            return true;
        }

        var known = new List<string>();
        var knownBits = new List<int>();
        var unknownBits = new List<int>();
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != '1')
            {
                continue;
            }

            string? warning = WarningText(i);
            if (warning is null)
            {
                unknownBits.Add(i + 1);
                continue;
            }

            known.Add(warning);
            knownBits.Add(i + 1);
            switch (i)
            {
                case 7:
                    state.UpdateStatus("LB");
                    break;
                case 8:
                    state.UpdateStatus("OVER");
                    break;
                case 55:
                    state.UpdateStatus("RB");
                    break;
            }
        }

        static string Bits(IEnumerable<int> bits) => string.Join(", ", bits.Select(b => "#" + b.ToString("00", CultureInfo.InvariantCulture)));

        var explicitText = new StringBuilder(string.Join(' ', known));
        if (unknownBits.Count > 0)
        {
            explicitText.Append(explicitText.Length > 0 ? " " : "").Append($"Unknown warnings [bit: {Bits(unknownBits)}]");
        }

        // Too many warnings for one variable: fall back to the bit numbers, as NUT does.
        string text = explicitText.Length < 224
            ? explicitText.ToString()
            : $"Known (see log or manual) [bit: {Bits(knownBits)}]" +
              (unknownBits.Count > 0 ? $"; Unknown warnings [bit: {Bits(unknownBits)}]" : "");
        value = "UPS warnings: " + text;
        return true;
    }

    private static string? WarningText(int bit) => bit switch
    {
        0 => "Battery disconnected.",
        1 => "Neutral not connected.",
        2 => "Site fault.",
        3 => "Phase sequence incorrect.",
        4 => "Phase sequence incorrect in bypass.",
        5 => "Input frequency unstable in bypass.",
        6 => "Battery overcharged.",
        7 => "Low battery.",
        8 => "Overload alarm.",
        9 => "Fan alarm.",
        10 => "EPO enabled.",
        11 => "Unable to turn on UPS.",
        12 => "Over temperature alarm.",
        13 => "Charger alarm.",
        14 => "Remote auto shutdown.",
        15 => "L1 input fuse not working.",
        16 => "L2 input fuse not working.",
        17 => "L3 input fuse not working.",
        18 => "Positive PFC abnormal in L1.",
        19 => "Negative PFC abnormal in L1.",
        20 => "Positive PFC abnormal in L2.",
        21 => "Negative PFC abnormal in L2.",
        22 => "Positive PFC abnormal in L3.",
        23 => "Negative PFC abnormal in L3.",
        24 => "Abnormal in CAN-bus communication.",
        25 => "Abnormal in synchronous signal circuit.",
        26 => "Abnormal in synchronous pulse signal circuit.",
        27 => "Abnormal in host signal circuit.",
        28 => "Male connector of parallel cable not connected well.",
        29 => "Female connector of parallel cable not connected well.",
        30 => "Parallel cable not connected well.",
        31 => "Battery connection not consistent in parallel systems.",
        32 => "AC connection not consistent in parallel systems.",
        33 => "Bypass connection not consistent in parallel systems.",
        34 => "UPS model types not consistent in parallel systems.",
        35 => "Capacity of UPSes not consistent in parallel systems.",
        36 => "Auto restart setting not consistent in parallel systems.",
        37 => "Battery cell over charge.",
        38 => "Battery protection setting not consistent in parallel systems.",
        39 => "Battery detection setting not consistent in parallel systems.",
        40 => "Bypass not allowed setting not consistent in parallel systems.",
        41 => "Converter setting not consistent in parallel systems.",
        42 => "High loss point for frequency in bypass mode not consistent in parallel systems.",
        43 => "Low loss point for frequency in bypass mode not consistent in parallel systems.",
        44 => "High loss point for voltage in bypass mode not consistent in parallel systems.",
        45 => "Low loss point for voltage in bypass mode not consistent in parallel systems.",
        46 => "High loss point for frequency in AC mode not consistent in parallel systems.",
        47 => "Low loss point for frequency in AC mode not consistent in parallel systems.",
        48 => "High loss point for voltage in AC mode not consistent in parallel systems.",
        49 => "Low loss point for voltage in AC mode not consistent in parallel systems.",
        50 => "Warning for locking in bypass mode after 3 consecutive overloads within 30 min.",
        51 => "Warning for three-phase AC input current unbalance.",
        52 => "Warning for a three-phase input current unbalance detected in battery mode.",
        53 => "Warning for Inverter inter-current unbalance.",
        54 => "Programmable outlets cut off pre-alarm.",
        55 => "Warning for Battery replace.",
        56 => "Abnormal warning on input phase angle.",
        57 => "Warning!! Cover of maintain switch is open.",
        61 => "EEPROM operation error.",
        _ => null,
    };

    /// <summary>Shutdown, test and beeper commands (NUT voltronic_process_command).</summary>
    private static bool FormatCommand(QxCommand command, string? parameter, QxState state, out string text, out string error)
    {
        text = string.Empty;
        error = string.Empty;
        string arg;
        switch (command.Name)
        {
            case "shutdown.return":
            {
                long offDelay = BlazerCommon.DelayValue(state, "ups.delay.shutdown");
                long onDelayMinutes = BlazerCommon.DelayValue(state, "ups.delay.start") / 60;
                string shutdown = BlazerCommon.FormatOffDelay(offDelay);
                arg = onDelayMinutes == 0 ? shutdown : string.Create(CultureInfo.InvariantCulture, $"{shutdown}R{onDelayMinutes:0000}");
                break;
            }

            case "shutdown.stayoff":
                arg = BlazerCommon.FormatOffDelay(BlazerCommon.DelayValue(state, "ups.delay.shutdown"));
                break;

            case "test.battery.start":
            {
                if (parameter is not null && !CText.OnlyChars(parameter, "0123456789"))
                {
                    error = "The battery test duration must be a whole number of seconds.";
                    return false;
                }

                long delay = string.IsNullOrEmpty(parameter) ? 600 : CText.StrToL(parameter);
                if (delay is < 12 or > 5940)
                {
                    error = "The battery test duration must be between 12 and 5940 seconds.";
                    return false;
                }

                arg = delay < 60
                    ? string.Create(CultureInfo.InvariantCulture, $".{delay / 6}")
                    : (delay / 60).ToString("00", CultureInfo.InvariantCulture);
                break;
            }

            case "beeper.toggle":
                switch (state.Get("ups.beeper.status"))
                {
                    case "enabled":
                        arg = "OFF";
                        break;
                    case "disabled" or "muted":
                        arg = "ON";
                        break;
                    default:
                        error = "The beeper state is not known yet.";
                        return false;
                }

                break;

            default:
                error = $"No format for {command.Name}.";
                return false;
        }

        text = command.Command.Replace("%s", arg, StringComparison.Ordinal);
        return true;
    }
}
