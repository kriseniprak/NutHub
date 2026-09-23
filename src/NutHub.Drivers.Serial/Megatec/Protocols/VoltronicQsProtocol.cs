namespace NutHub.Drivers.Serial.Megatec.Protocols;

/// <summary>
/// Voltronic Power UPSes speaking the "QS" dialect (protocol letter V or H from M): QS status, F ratings and QI with
/// charge, runtime and transfer limits. Ported from NUT drivers/nutdrv_qx_voltronic-qs.c.
/// </summary>
internal sealed class VoltronicQsProtocol : QxProtocol
{
    public VoltronicQsProtocol(QxProtocolOptions options)
        : base(options)
    {
        var fields = new List<QxField>
        {
            new("ups.firmware.aux", "M\r", 2, '\0', 0, 0, QxFormat.Text, QxFlags.Static, Protocol),
        };
        fields.AddRange(BlazerCommon.Q1StatusFields("QS\r", frequencyName: "output.frequency", bypassBit: BlazerCommon.StatusBits));
        fields.AddRange(BlazerCommon.RatingFields("output.voltage.nominal", "output.current.nominal", "output.frequency.nominal"));

        // > [QI\r]  < [(100 00979 50.0 000.3 177 290 0 0000010000112000\r]
        fields.Add(new("battery.charge", "QI\r", 49, '(', 1, 3, QxFormat.Fixed(0)));
        fields.Add(new("battery.runtime", "QI\r", 49, '(', 5, 9, QxFormat.Fixed(0)));
        fields.Add(new("input.frequency", "QI\r", 49, '(', 11, 14, QxFormat.Fixed(1)));
        fields.Add(new("output.current", "QI\r", 49, '(', 16, 20, QxFormat.Fixed(1)));
        fields.Add(new("input.transfer.low", "QI\r", 49, '(', 22, 24, QxFormat.Fixed(0), QxFlags.Static));
        fields.Add(new("input.transfer.high", "QI\r", 49, '(', 26, 28, QxFormat.Fixed(0), QxFlags.Static));
        Fields = fields;

        // No "accepted" reply is defined: silence is success, anything else (e.g. an echo) is a refusal.
        Commands =
        [
            new("beeper.toggle", "Q\r", from: 1, to: 3),
            new("load.off", "S00R0000\r", from: 1, to: 3),
            new("load.on", "C\r", from: 1, to: 3),
            new("shutdown.return", "S%s\r", from: 1, to: 3, format: BlazerCommon.FormatCommand),
            new("shutdown.stayoff", "S%sR0000\r", from: 1, to: 3, format: BlazerCommon.FormatCommand),
            new("shutdown.stop", "C\r", from: 1, to: 3),
            new("test.battery.start.quick", "T\r", from: 1, to: 3),
        ];
    }

    public override string Name => "voltronic-qs";

    public override string Version => "Voltronic-QS 0.11";

    public override IReadOnlyList<QxField> Fields { get; }

    public override IReadOnlyList<QxCommand> Commands { get; }

    public override string? Accepted => null;

    public override IReadOnlyList<string> ClaimFields => ["ups.firmware.aux", "input.voltage"];

    public override (int Min, int Max) OnDelayRange => (60, 599940);

    public override (int Min, int Max) OffDelayRange => (12, 540);

    /// <summary>The protocol letter from M: V or H, published as "PM-V" / "PM-H".</summary>
    private static bool Protocol(QxField field, string raw, QxState state, out string value)
    {
        if (raw.Equals("V", StringComparison.OrdinalIgnoreCase) || raw.Equals("H", StringComparison.OrdinalIgnoreCase))
        {
            value = "PM-" + raw;
            return true;
        }

        value = string.Empty;
        return false;
    }
}
