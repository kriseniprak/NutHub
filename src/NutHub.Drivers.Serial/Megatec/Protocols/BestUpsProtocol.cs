using System.Globalization;
using NutHub.Core.Model;
using NutHub.Drivers.Serial.Common;

namespace NutHub.Drivers.Serial.Megatec.Protocols;

/// <summary>
/// Best Power Fortress / Patriot / Axxium and Sola Australia UPSes: Q1 status plus ID (model and ratings), RT (runtime),
/// BP? (battery packs) and M (transfer voltages). Ported from NUT drivers/nutdrv_qx_bestups.c. The writable battery
/// packs and the pins shutdown mode of the NUT subdriver are not exposed.
/// </summary>
internal sealed class BestUpsProtocol : QxProtocol
{
    private const string InvertedBypassBit = "bestups.inverted-bbb";

    // M reply index -> (low, boost, nominal, buck, high); rows 0-9 for 120 V "U" models, 10-19 for 230 V "E" models.
    private static readonly int[,] VoltageSettings =
    {
        { 96, 109, 120, 130, 146 }, { 96, 109, 120, 138, 156 }, { 90, 104, 120, 130, 146 }, { 90, 104, 120, 138, 156 },
        { 90, 104, 110, 120, 130 }, { 90, 104, 110, 130, 146 }, { 90, 96, 110, 120, 130 }, { 90, 96, 110, 130, 146 },
        { 96, 109, 128, 146, 156 }, { 90, 104, 128, 146, 156 },
        { 200, 222, 240, 250, 284 }, { 200, 222, 240, 264, 290 }, { 188, 210, 240, 250, 284 }, { 188, 210, 240, 264, 290 },
        { 188, 210, 230, 244, 270 }, { 188, 210, 230, 250, 284 }, { 180, 200, 230, 244, 270 }, { 180, 200, 230, 250, 284 },
        { 165, 188, 208, 222, 244 }, { 165, 188, 208, 244, 270 },
    };

    public BestUpsProtocol(QxProtocolOptions options)
        : base(options)
    {
        var fields = BlazerCommon.Q1StatusFields("Q1\r", withBeeper: false);

        // The bypass/boost/buck bit depends on the other status bits, so it is processed last (as in NUT).
        fields.Add(new QxField("ups.status", "Q1\r", 47, '(', 40, 40, QxFormat.Text, QxFlags.QuickPoll, BypassBit));

        fields.Add(new QxField("device.mfr", "ID\r", 28, '\0', 0, 2, QxFormat.Text, QxFlags.Static, Manufacturer, NormaliseId));
        fields.Add(new QxField("device.model", "ID\r", 28, '\0', 0, 2, QxFormat.Text, QxFlags.Static, Model, NormaliseId));
        fields.Add(new QxField("ups.power.nominal", "ID\r", 28, '\0', 4, 7, QxFormat.Fixed(0), QxFlags.Static, answer: NormaliseId));
        fields.Add(new QxField("input.voltage.nominal", "ID\r", 28, '\0', 9, 11, QxFormat.Fixed(0), QxFlags.Static, answer: NormaliseId));
        fields.Add(new QxField("output.voltage.nominal", "ID\r", 28, '\0', 13, 15, QxFormat.Fixed(0), QxFlags.Static, answer: NormaliseId));
        fields.Add(new QxField("battery.voltage.low", "ID\r", 28, '\0', 17, 20, QxFormat.Fixed(1), QxFlags.SemiStatic, answer: NormaliseId));
        fields.Add(new QxField("battery.voltage.high", "ID\r", 28, '\0', 22, 26, QxFormat.Fixed(1), QxFlags.SemiStatic, answer: NormaliseId));
        fields.Add(new QxField("battery.runtime", "RT\r", 4, '\0', 0, 2, QxFormat.Fixed(0), QxFlags.Skip, Runtime));
        fields.Add(new QxField("battery.packs", "BP?\r", 3, '\0', 0, 1, QxFormat.Fixed(0), QxFlags.SemiStatic | QxFlags.Skip, Packs));

        foreach (string name in new[]
                 {
                     "input.transfer.low", "input.transfer.boost.low", "input.transfer.boost.high", "input.voltage.nominal",
                     "output.voltage.nominal", "input.transfer.trim.low", "input.transfer.trim.high", "input.transfer.high",
                 })
        {
            fields.Add(new QxField(name, "M\r", 2, '\0', 0, 0, QxFormat.Fixed(0), QxFlags.None, TransferVoltages));
        }

        Fields = fields;
        Commands =
        [
            new("shutdown.return", "S%s\r", format: BlazerCommon.FormatCommand),
            new("shutdown.stayoff", "S%s\r", format: BlazerCommon.FormatCommand),
            new("shutdown.stop", "C\r"),
            new("load.on", "C\r"),
            new("load.off", "S00R0000\r"),
            new("test.battery.start", "T%02d\r", format: BlazerCommon.FormatCommand),
            new("test.battery.start.deep", "TL\r"),
            new("test.battery.start.quick", "T\r"),
            new("test.battery.stop", "CT\r"),
        ];
    }

    public override string Name => "bestups";

    public override string Version => "BestUPS 0.08";

    public override IReadOnlyList<QxField> Fields { get; }

    public override IReadOnlyList<QxCommand> Commands { get; }

    public override string? Accepted => null;

    public override IReadOnlyList<string> ClaimFields => ["input.voltage", "device.model"];

    public override (int Min, int Max) OnDelayRange => (60, 599940);

    public override (int Min, int Max) OffDelayRange => (12, 5940);

    /// <summary>
    /// Pads the ID reply so every field starts at a fixed position: "FOR,750,120,120,20.0,27.6\r" becomes
    /// "FOR, 750,120,120,20.0, 27.6\r" (NUT bestups_preprocess_id_answer).
    /// </summary>
    internal static string? NormaliseId(string answer)
    {
        // NUT rejects replies longer than 27 bytes although its own comment lists a valid 28-byte one
        // ("FOR,3000,120,120,20.0,100.6\r"); the final length check below is what guarantees the layout.
        if (answer.Length is < 25 or > 28)
        {
            return null;
        }

        string[] tokens = answer.Split(',');
        if (tokens.Length != 6)
        {
            return null;
        }

        string refined = string.Create(CultureInfo.InvariantCulture,
            $"{tokens[0]},{tokens[1],4},{tokens[2]},{tokens[3]},{tokens[4]},{tokens[5],6}");
        return refined.Length == 28 ? refined : null;
    }

    private bool BypassBit(QxField field, string raw, QxState state, out string value)
    {
        if (raw.Length != 1 || (raw[0] != '0' && raw[0] != '1'))
        {
            value = string.Empty;
            return false;
        }

        char bit = raw[0];

        // Not reliable during a battery test, a shutdown or on battery: ignored then.
        if (!state.Has(QxStatusBits.Online) || state.Has(QxStatusBits.Calibrating) || state.Has(QxStatusBits.ShutdownImminent))
        {
            bit = '0';
        }
        else if (state.Flags.Contains(InvertedBypassBit))
        {
            bit = bit == '1' ? '0' : '1';
        }

        return BlazerCommon.StatusBits(field, bit.ToString(), state, out value);
    }

    private static bool Manufacturer(QxField field, string raw, QxState state, out string value)
    {
        value = raw switch
        {
            "AX1" or "FOR" or "FTC" or "PR2" or "PRO" => "Best Power",
            "325" or "520" or "620" => "Sola Australia",
            _ => "Unknown",
        };
        return true;
    }

    private bool Model(QxField field, string raw, QxState state, out string value)
    {
        switch (raw)
        {
            case "AX1":
                value = "Axxium Rackmount";
                break;
            case "FOR":
                value = "Fortress";
                break;
            case "FTC":
                value = "Fortress Telecom";
                break;
            case "PR2":
                value = "Patriot Pro II";
                state.Flags.Add(InvertedBypassBit);
                break;
            case "PRO":
                value = "Patriot Pro";
                state.Flags.Add(InvertedBypassBit);
                break;
            case "320" or "325" or "520" or "525" or "620":
                value = "Sola " + raw;
                break;
            default:
                value = $"Unknown ({raw})";
                break;
        }

        // The runtime query does not exist on the Patriot Pro / Sola 320 series; the packs query only on Axxium / Sola 620.
        if (raw is not ("PRO" or "320"))
        {
            FindField("battery.runtime")!.Skip = false;
        }

        if (raw is "AX1" or "620")
        {
            FindField("battery.packs")!.Skip = false;
        }

        return true;
    }

    private static bool Runtime(QxField field, string raw, QxState state, out string value)
    {
        value = string.Empty;
        if (!CText.IsNumericField(raw))
        {
            return false;
        }

        // Reported in minutes; NUT wants seconds.
        value = NutFormat.Fixed(CText.StrToD(raw) * 60, 0);
        return true;
    }

    private static bool Packs(QxField field, string raw, QxState state, out string value)
    {
        value = string.Empty;
        if (!CText.OnlyChars(raw, "0123456789 "))
        {
            return false;
        }

        long packs = CText.StrToL(raw);
        if (packs is < 0 or > int.MaxValue)
        {
            return false;
        }

        value = packs.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TransferVoltages(QxField field, string raw, QxState state, out string value)
    {
        value = string.Empty;
        if (raw.Length == 0 || !CText.OnlyChars(raw, "0123456789"))
        {
            return false;
        }

        long index = CText.StrToL(raw);
        if (index is < 0 or > 9)
        {
            return false;
        }

        double? nominal = state.Number("input.voltage.nominal") ?? state.Number("output.voltage.nominal");
        if (nominal is null)
        {
            return false;
        }

        if (nominal > 160)
        {
            index += 10;
        }

        int column = field.Name switch
        {
            "input.transfer.low" or "input.transfer.boost.low" => 0,
            "input.transfer.boost.high" => 1,
            "input.voltage.nominal" or "output.voltage.nominal" => 2,
            "input.transfer.trim.low" => 3,
            "input.transfer.trim.high" or "input.transfer.high" => 4,
            _ => -1,
        };
        if (column < 0)
        {
            return false;
        }

        value = VoltageSettings[index, column].ToString(CultureInfo.InvariantCulture);
        return true;
    }
}
