using System.Globalization;
using System.Text;
using NutHub.Drivers.Serial.Common;

namespace NutHub.Drivers.Serial.Megatec.Protocols;

/// <summary>
/// Voltronic Power UPSes whose QS reply is binary (protocol letter P or T from M). The reply is first rewritten as hex
/// text, then read with fixed positions like the other dialects. Ported from NUT drivers/nutdrv_qx_voltronic-qs-hex.c.
/// </summary>
internal sealed class VoltronicQsHexProtocol : QxProtocol
{
    public VoltronicQsHexProtocol(QxProtocolOptions options)
        : base(options)
    {
        const string qs = "QS\r";
        const int len = 47;
        QxAnswerProcessor decode = DecodeQs;
        Fields =
        [
            new("ups.firmware.aux", "M\r", 2, '\0', 0, 0, QxFormat.Text, QxFlags.Static, Protocol),
            new("input.voltage", qs, len, '#', 1, 7, QxFormat.Fixed(1), QxFlags.None, Voltage, decode),
            new("output.voltage", qs, len, '#', 9, 15, QxFormat.Fixed(1), QxFlags.None, Voltage, decode),
            new("ups.load", qs, len, '#', 17, 18, QxFormat.Fixed(0), QxFlags.None, Load, decode),
            new("output.frequency", qs, len, '#', 20, 30, QxFormat.Fixed(1), QxFlags.None, Frequency, decode),
            new("battery.voltage", qs, len, '#', 32, 36, QxFormat.Fixed(2), QxFlags.None, BatteryVoltage, decode),
            new("ups.status", qs, len, '#', 38, 38, QxFormat.Text, QxFlags.QuickPoll, BlazerCommon.StatusBits, decode),
            new("ups.status", qs, len, '#', 39, 39, QxFormat.Text, QxFlags.QuickPoll, BlazerCommon.StatusBits, decode),
            new("ups.status", qs, len, '#', 40, 40, QxFormat.Text, QxFlags.QuickPoll, BlazerCommon.StatusBits, decode),
            new("ups.alarm", qs, len, '#', 41, 41, QxFormat.Text, QxFlags.None, BlazerCommon.StatusBits, decode),
            new("ups.type", qs, len, '#', 42, 42, QxFormat.Text, QxFlags.Static, BlazerCommon.StatusBits, decode),
            new("ups.status", qs, len, '#', 43, 43, QxFormat.Text, QxFlags.QuickPoll, BlazerCommon.StatusBits, decode),
            new("ups.status", qs, len, '#', 44, 44, QxFormat.Text, QxFlags.QuickPoll, BlazerCommon.StatusBits, decode),
            new("ups.beeper.status", qs, len, '#', 45, 45, QxFormat.Text, QxFlags.None, BlazerCommon.StatusBits, decode),

            // Rating bits, only in the longer reply of the 'T' protocol.
            new("output.frequency.nominal", qs, 56, '#', 47, 47, QxFormat.Fixed(1), QxFlags.Skip, RatingBits, decode),
            new("battery.voltage.nominal", qs, 56, '#', 48, 49, QxFormat.Fixed(1), QxFlags.Skip, RatingBits, decode),
            new("output.voltage.nominal", qs, 56, '#', 52, 54, QxFormat.Fixed(1), QxFlags.Skip, RatingBits, decode),
        ];

        Commands =
        [
            new("beeper.toggle", "Q\r"),
            new("load.off", "S00R0000\r"),
            new("load.on", "C\r"),
            new("shutdown.return", "S%s\r", format: BlazerCommon.FormatCommand),
            new("shutdown.stayoff", "S%sR0000\r", format: BlazerCommon.FormatCommand),
            new("shutdown.stop", "C\r"),
            new("test.battery.start.quick", "T\r", flags: QxFlags.Skip),
        ];
    }

    public override string Name => "voltronic-qs-hex";

    public override string Version => "Voltronic-QS-Hex 0.12";

    public override IReadOnlyList<QxField> Fields { get; }

    public override IReadOnlyList<QxCommand> Commands { get; }

    public override string? Accepted => null;

    public override string? Rejected => "N\r";

    public override IReadOnlyList<string> ClaimFields => ["ups.firmware.aux", "input.voltage"];

    public override (int Min, int Max) OnDelayRange => (60, 599940);

    public override (int Min, int Max) OffDelayRange => (12, 540);

    /// <summary>
    /// Rewrites the binary QS reply as text: each byte as two hex digits, bytes escaped with 0x28 restored, and the two
    /// last tokens (status and rating bits) as 8 binary digits. The result has 10 tokens (46 characters, 'P' protocol)
    /// or 11 (55 characters, 'T' protocol) (NUT voltronic_qs_hex_preprocess_qs_answer).
    /// </summary>
    internal static string? DecodeQs(string answer)
    {
        if (answer.Length == 0 || answer[0] != '#')
        {
            return null;
        }

        var refined = new StringBuilder("#", 64);
        int token = 1;
        for (int i = 1; i < answer.Length; i++)
        {
            char c = answer[i];
            if (c == ' ')
            {
                refined.Append(' ');
                token++;
                continue;
            }

            if (c == (char)0x28 && i + 1 < answer.Length)
            {
                int? escaped = answer[i + 1] switch
                {
                    (char)0x00 => 0x0D, // CR
                    (char)0x01 => 0x11, // XON
                    (char)0x02 => 0x13, // XOFF
                    (char)0x03 => 0x0A, // LF
                    (char)0x04 => 0x20, // space
                    _ => null,
                };
                if (escaped is not null)
                {
                    AppendByte(refined, escaped.Value, token);
                    i++;
                    continue;
                }
            }

            if (c == '\r')
            {
                break;
            }

            AppendByte(refined, c, token);
        }

        bool valid = (token == 10 && refined.Length == 46) || (token == 11 && refined.Length == 55);
        return valid ? refined.Append('\r').ToString() : null;
    }

    private static void AppendByte(StringBuilder refined, int value, int token)
    {
        if (token is 10 or 11)
        {
            refined.Append(Convert.ToString(value & 0xFF, 2).PadLeft(8, '0'));
        }
        else
        {
            refined.Append((value & 0xFF).ToString("x2", CultureInfo.InvariantCulture));
        }
    }

    /// <summary>
    /// The protocol letter from M: P or T. The 'T' protocol also has the quick battery test and the rating bits.
    /// </summary>
    private bool Protocol(QxField field, string raw, QxState state, out string value)
    {
        value = string.Empty;
        bool p = raw.Equals("P", StringComparison.OrdinalIgnoreCase);
        bool t = raw.Equals("T", StringComparison.OrdinalIgnoreCase);
        if (!p && !t)
        {
            return false;
        }

        value = "PM-" + raw;
        if (t)
        {
            foreach (QxCommand command in Commands.Where(c => c.Name == "test.battery.start.quick"))
            {
                command.Skip = false;
            }

            foreach (QxField rating in Fields.Where(f => f.MinLength == 56))
            {
                rating.Skip = false;
            }
        }

        return true;
    }

    /// <summary>"6C01 35": (0x6C01 * 0x35 / 51) / 256 volts.</summary>
    private static bool Voltage(QxField field, string raw, QxState state, out string value)
    {
        value = string.Empty;
        if (!TwoHexNumbers(raw, out long a, out long b))
        {
            return false;
        }

        long scaled = a * b / 51;
        value = field.Format.Apply(scaled / 256.0);
        return true;
    }

    private static bool Load(QxField field, string raw, QxState state, out string value)
    {
        value = string.Empty;
        if (raw.Length == 0 || !CText.OnlyChars(raw, "0123456789ABCDEFabcdef"))
        {
            return false;
        }

        value = CText.StrToL(raw, 16).ToString(CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>"519A 1312D0": 0x1312D0 / 0x519A hertz, capped at 99.9.</summary>
    private static bool Frequency(QxField field, string raw, QxState state, out string value)
    {
        value = string.Empty;
        if (!TwoHexNumbers(raw, out long a, out long b) || a == 0)
        {
            return false;
        }

        value = field.Format.Apply(Math.Min((double)b / a, 99.9));
        return true;
    }

    /// <summary>"E6 1E": 0xE6 * 0x1E / 510 volts.</summary>
    private static bool BatteryVoltage(QxField field, string raw, QxState state, out string value)
    {
        value = string.Empty;
        if (!TwoHexNumbers(raw, out long a, out long b))
        {
            return false;
        }

        double volts = a * b / 510.0;
        state.RawBatteryVoltage = volts;
        value = field.Format.Apply(volts);
        return true;
    }

    private static bool RatingBits(QxField field, string raw, QxState state, out string value)
    {
        value = string.Empty;
        if (raw.Length == 0 || !CText.OnlyChars(raw, "01"))
        {
            return false;
        }

        long bits = CText.StrToL(raw);
        double? rating = field.From switch
        {
            47 => bits == 0 ? 50 : 60,
            48 => bits switch { 0 => 12, 1 => 24, 10 => 36, _ => 48 },
            52 => bits switch { 0 => 110, 1 => 120, 10 => 220, 11 => 230, 100 => 240, _ => null },
            _ => null,
        };
        if (rating is null)
        {
            return false;
        }

        value = field.Format.Apply(rating.Value);
        return true;
    }

    private static bool TwoHexNumbers(string raw, out long first, out long second)
    {
        first = second = 0;
        if (!CText.OnlyChars(raw, "0123456789ABCDEFabcdef "))
        {
            return false;
        }

        if (!CText.TryStrToL(raw, 16, out first, out int end))
        {
            return false;
        }

        second = CText.TryStrToL(raw[end..], 16, out long b, out _) ? b : 0;
        return true;
    }
}
