using static NutHub.Drivers.Snmp.Mibs.Mib;

namespace NutHub.Drivers.Snmp.Mibs;

/// <summary>
/// Delta Electronics DeltaUPS-MIB.
/// Mapping from NUT drivers/delta_ups-mib.c (version 0.51); only the objects NUT maps to standard names (the rest
/// of that file publishes "unmapped.*" variables, which are left out).
/// </summary>
internal static class DeltaUpsMib
{
    private const string Delta = "1.3.6.1.4.1.2254.2.4.";

    private static readonly MibLookup UpsType = Lookup(
        (1, "on-line"), (2, "off-line"), (3, "line-interactive"), (4, "3phase"), (5, "split-phase"));

    private static readonly MibLookup PowerStatus = Lookup(
        (0, "OL"), // normal
        (1, "OB"), // battery
        (2, "BYPASS"), // bypass
        (3, "TRIM"), // reducing
        (4, "BOOST"), // boosting
        (5, "BYPASS"), // manualBypass
        (7, "OFF")); // none

    public static MibDefinition Definition { get; } = new()
    {
        Name = "delta_ups",
        DisplayName = "Delta (delta_ups)",
        Version = "0.51",
        SysObjectIds = ["1.3.6.1.4.1.2254.2.4"],
        ProbeOid = Delta + "1.2.0", // dupsIdentModel
        Entries = Build(),
    };

    private static List<MibEntry> Build()
    {
        var e = new List<MibEntry>(SystemGroup());

        e.Add(Text("ups.mfr", Delta + "1.1.0")); // dupsIdentManufacturer
        e.Add(Text("ups.model", Delta + "1.2.0")); // dupsIdentModel
        e.Add(Text("ups.firmware", Delta + "1.4.0")); // dupsIdentUPSSoftwareVersion
        e.Add(Text("ups.firmware.aux", Delta + "1.3.0")); // dupsIdentAgentSoftwareVersion
        e.Add(Num("ups.type", Delta + "1.19.0", 1, lookup: UpsType)); // dupsType
        e.Add(Num("ups.load", Delta + "5.7.0")); // dupsOutputLoad1
        e.Add(Num("ups.power", Delta + "1.7.0")); // dupsRatingOutputVA
        e.Add(Num("output.voltage.nominal", Delta + "1.8.0")); // dupsRatingOutputVoltage
        e.Add(Num("output.voltage", Delta + "5.4.0", 0.1)); // dupsOutputVoltage1
        e.Add(Num("output.frequency.nominal", Delta + "1.9.0")); // dupsRatingOutputFrequency
        e.Add(Num("output.current", Delta + "5.5.0", 0.1)); // dupsOutputCurrent1
        e.Add(Num("input.voltage.nominal", Delta + "1.10.0")); // dupsRatingInputVoltage
        e.Add(Num("input.voltage", Delta + "4.3.0", 0.1)); // dupsInputVoltage1
        e.Add(Num("input.frequency.nominal", Delta + "1.11.0")); // dupsRatingInputFrequency
        e.Add(Num("input.frequency", Delta + "4.2.0", 0.1)); // dupsInputFrequency
        e.Add(Status(Delta + "5.1.0", PowerStatus)); // dupsOutputSource
        return e;
    }
}
