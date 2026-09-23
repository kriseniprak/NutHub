using System.Globalization;
using NutHub.Drivers.Serial.Common;

namespace NutHub.Drivers.Serial.Megatec.Protocols;

/// <summary>
/// The parts shared by the Megatec-style protocols: the Q1 status bits, the shutdown/test command formats, the battery
/// voltage multiplication. Ported from NUT drivers/nutdrv_qx_blazer-common.c and drivers/nutdrv_qx.c.
/// </summary>
internal static class BlazerCommon
{
    /// <summary>
    /// The 8 status bits of a Q1-style reply ("b7b6b5b4b3b2b1b0" at positions 38-45), one field per bit, as NUT's
    /// blazer_process_status_bits reads them.
    /// </summary>
    public static bool StatusBits(QxField field, string raw, QxState state, out string value)
    {
        value = string.Empty;
        if (raw.Length != 1 || (raw[0] != '0' && raw[0] != '1'))
        {
            return false;
        }

        bool set = raw[0] == '1';
        switch (field.From)
        {
            case 38: // Utility fail (immediate)
                value = set ? "!OL" : "OL";
                return true;
            case 39: // Battery low
                value = set ? "LB" : "!LB";
                return true;
            case 40: // Bypass / boost or buck active
                value = set ? BypassBoostOrTrim(state) : string.Empty;
                return true;
            case 41: // UPS failed
                value = set ? "UPS selftest failed!" : string.Empty;
                return true;
            case 42: // UPS type
                value = set ? "offline / line interactive" : "online";
                return true;
            case 43: // Test in progress
                value = set ? "CAL" : "!CAL";
                return true;
            case 44: // Shutdown active
                value = set ? "FSD" : "!FSD";
                return true;
            case 45: // Beeper on
                value = set ? "enabled" : "disabled";
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// The "bypass/boost or buck active" bit does not say which one: NUT tells them apart by comparing the output with
    /// the input voltage. NUT fails the whole status when the ratio makes no sense (for example 0 V input on battery);
    /// here the bit is ignored instead, so such a reply does not make the data go stale.
    /// </summary>
    internal static string BypassBoostOrTrim(QxState state)
    {
        double vi = state.Number("input.voltage") ?? 0;
        double vo = state.Number("output.voltage") ?? 0;
        if (vi <= 0 || vo < 0.5 * vi || vo >= 1.5 * vi)
        {
            return string.Empty;
        }

        if (vo < 0.95 * vi)
        {
            return "TRIM";
        }

        return vo < 1.05 * vi ? "BYPASS" : "BOOST";
    }

    /// <summary>
    /// battery.voltage as reported, or multiplied by the battery packs when the user says the UPS reports a single
    /// pack (NUT qx_multiply_battvolt). The raw reading is kept for the charge estimation either way.
    /// </summary>
    public static bool BatteryVoltage(QxField field, string raw, QxState state, out string value)
    {
        value = string.Empty;
        if (!CText.IsNumericField(raw) || !CText.TryStrToD(raw, out double volts))
        {
            return false;
        }

        state.RawBatteryVoltage = volts;
        value = state.BatteryVoltageReportsOnePack && state.BatteryPacks >= 2
            ? NutHub.Core.Model.NutFormat.Fixed(volts * state.BatteryPacks, 2)
            : NutHub.Core.Model.NutFormat.Number(volts, 2);
        return true;
    }

    /// <summary>
    /// "S%s\r" / "S%sR0000\r" / "T%02d\r" of the Megatec-style protocols (NUT blazer_process_command). Shutdown delays
    /// come from ups.delay.shutdown (in tenths of a minute below one minute) and ups.delay.start (whole minutes).
    /// </summary>
    public static bool FormatCommand(QxCommand command, string? parameter, QxState state, out string text, out string error)
    {
        text = string.Empty;
        error = string.Empty;
        switch (command.Name)
        {
            case "shutdown.return":
            {
                long offDelay = DelayValue(state, "ups.delay.shutdown");
                long onDelayMinutes = DelayValue(state, "ups.delay.start") / 60;
                if (offDelay < 0)
                {
                    error = "ups.delay.shutdown must not be negative.";
                    return false;
                }

                string shutdown = FormatOffDelay(offDelay);
                string arg = onDelayMinutes <= 0
                    ? shutdown
                    : string.Create(CultureInfo.InvariantCulture, $"{shutdown}R{onDelayMinutes:0000}");
                text = command.Command.Replace("%s", arg, StringComparison.Ordinal);
                return true;
            }

            case "shutdown.stayoff":
            {
                long offDelay = DelayValue(state, "ups.delay.shutdown");
                if (offDelay < 0)
                {
                    error = "ups.delay.shutdown must not be negative.";
                    return false;
                }

                text = command.Command.Replace("%s", FormatOffDelay(offDelay), StringComparison.Ordinal);
                return true;
            }

            case "test.battery.start":
            {
                long delay = string.IsNullOrEmpty(parameter) ? 600 : CText.StrToL(parameter);
                if (delay < 60 || delay > 5940)
                {
                    error = "The battery test duration must be between 60 and 5940 seconds.";
                    return false;
                }

                text = command.Command.Replace("%02d", (delay / 60).ToString("00", CultureInfo.InvariantCulture), StringComparison.Ordinal);
                return true;
            }

            default:
                error = $"No format for {command.Name}.";
                return false;
        }
    }

    /// <summary>".2" to ".9" (tenths of a minute) below one minute, "01".."99" minutes above.</summary>
    internal static string FormatOffDelay(long offDelaySeconds) =>
        offDelaySeconds < 60
            ? string.Create(CultureInfo.InvariantCulture, $".{offDelaySeconds / 6}")
            : (offDelaySeconds / 60).ToString("00", CultureInfo.InvariantCulture);

    internal static long DelayValue(QxState state, string name) => CText.StrToL(state.Get(name) ?? "0");

    /// <summary>The command table of the Megatec protocol (megatec, megatec/old, mustek, zinto, q1).</summary>
    public static IReadOnlyList<QxCommand> MegatecCommands() =>
    [
        new("beeper.toggle", "Q\r"),
        new("load.off", "S00R0000\r"),
        new("load.on", "C\r"),
        new("shutdown.return", "S%s\r", format: FormatCommand),
        new("shutdown.stayoff", "S%sR0000\r", format: FormatCommand),
        new("shutdown.stop", "C\r"),
        new("test.battery.start", "T%02d\r", format: FormatCommand),
        new("test.battery.start.deep", "TL\r"),
        new("test.battery.start.quick", "T\r"),
        new("test.battery.stop", "CT\r"),
    ];

    /// <summary>
    /// The fields of a Q1-style status reply "(MMM.M NNN.N PPP.P QQQ RR.R S.SS TT.T b7..b0\r" for a given query.
    /// </summary>
    /// <param name="frequencyName">input.frequency for most protocols, output.frequency for voltronic-qs.</param>
    /// <param name="withBeeper">False for BestUPS, where the bit is always 0.</param>
    /// <param name="bypassBit">The processor of the bypass/boost/buck bit, or null to leave it out.</param>
    public static List<QxField> Q1StatusFields(string command, string frequencyName = "input.frequency",
                                               bool withBeeper = true, QxValueProcessor? bypassBit = null)
    {
        const int len = 47;
        const char lead = '(';
        var fields = new List<QxField>
        {
            new("input.voltage", command, len, lead, 1, 5, QxFormat.Fixed(1)),
            new("input.voltage.fault", command, len, lead, 7, 11, QxFormat.Fixed(1)),
            new("output.voltage", command, len, lead, 13, 17, QxFormat.Fixed(1)),
            new("ups.load", command, len, lead, 19, 21, QxFormat.Fixed(0)),
            new(frequencyName, command, len, lead, 23, 26, QxFormat.Fixed(1)),
            new("battery.voltage", command, len, lead, 28, 31, QxFormat.Fixed(2), process: BatteryVoltage),
            new("ups.temperature", command, len, lead, 33, 36, QxFormat.Fixed(1)),
            new("ups.status", command, len, lead, 38, 38, QxFormat.Text, QxFlags.QuickPoll, StatusBits),
            new("ups.status", command, len, lead, 39, 39, QxFormat.Text, QxFlags.QuickPoll, StatusBits),
        };

        if (bypassBit is not null)
        {
            fields.Add(new("ups.status", command, len, lead, 40, 40, QxFormat.Text, QxFlags.QuickPoll, bypassBit));
        }

        fields.Add(new("ups.alarm", command, len, lead, 41, 41, QxFormat.Text, QxFlags.None, StatusBits));
        fields.Add(new("ups.type", command, len, lead, 42, 42, QxFormat.Text, QxFlags.Static, StatusBits));
        fields.Add(new("ups.status", command, len, lead, 43, 43, QxFormat.Text, QxFlags.QuickPoll, StatusBits));
        fields.Add(new("ups.status", command, len, lead, 44, 44, QxFormat.Text, QxFlags.QuickPoll, StatusBits));
        if (withBeeper)
        {
            fields.Add(new("ups.beeper.status", command, len, lead, 45, 45, QxFormat.Text, QxFlags.None, StatusBits));
        }

        return fields;
    }

    /// <summary>The rating reply "#MMM.M QQQ SS.SS RR.R\r" to F.</summary>
    public static IEnumerable<QxField> RatingFields(string voltageName = "input.voltage.nominal",
                                                    string currentName = "input.current.nominal",
                                                    string frequencyName = "input.frequency.nominal",
                                                    int voltageDecimals = 0, int frequencyDecimals = 0)
    {
        yield return new QxField(voltageName, "F\r", 22, '#', 1, 5, QxFormat.Fixed(voltageDecimals), QxFlags.Static);
        yield return new QxField(currentName, "F\r", 22, '#', 7, 9, QxFormat.Fixed(1), QxFlags.Static);
        yield return new QxField("battery.voltage.nominal", "F\r", 22, '#', 11, 15, QxFormat.Fixed(1), QxFlags.Static);
        yield return new QxField(frequencyName, "F\r", 22, '#', 17, 20, QxFormat.Fixed(frequencyDecimals), QxFlags.Static);
    }

    /// <summary>
    /// The vendor reply "#Company_Name(15) UPS_Model(10) Version(10)\r" to I (or FW? for Zinto). The firmware runs to the
    /// end of the line and the minimum length is 38, so the 38-byte "Megatec IC" variant is accepted too.
    /// </summary>
    public static IEnumerable<QxField> VendorFields(string command = "I\r")
    {
        yield return new QxField("device.mfr", command, 38, '#', 1, 15, QxFormat.Text, QxFlags.Static | QxFlags.Trim);
        yield return new QxField("device.model", command, 38, '#', 17, 26, QxFormat.Text, QxFlags.Static | QxFlags.Trim);
        yield return new QxField("ups.firmware", command, 38, '#', 28, 0, QxFormat.Text, QxFlags.Static | QxFlags.Trim);
    }
}
