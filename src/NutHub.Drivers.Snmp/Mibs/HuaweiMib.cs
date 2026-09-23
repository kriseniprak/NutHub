using static NutHub.Drivers.Snmp.Mibs.Mib;

namespace NutHub.Drivers.Snmp.Mibs;

/// <summary>
/// Huawei UPS2000 / UPS5000 cards (HUAWEI-UPS-MIB).
/// Mapping from NUT drivers/huawei-mib.c (version 0.40). Two entries NUT maps to the same name by mistake are given
/// the names their OIDs mean: the second and third per-phase active power, and the third power factor.
/// </summary>
internal static class HuaweiMib
{
    private const string Hw = "1.3.6.1.4.1.2011.6.174.1.";

    private static readonly MibLookup SupplyMethod = Lookup(
        (1, ""), // no supply
        (2, "OL BYPASS"),
        (3, "OL"),
        (4, "OB"),
        (5, ""), // combined
        (6, "OL ECO"),
        (7, "OB ECO"));

    private static readonly MibLookup BatteryState = Lookup(
        (1, ""), // not connected
        (2, ""), // not charging or discharging
        (3, ""), // hibernation
        (4, ""), // float
        (5, "CHRG"), // equalized charging
        (6, "DISCHRG"));

    private static readonly MibLookup VoltageRating = Lookup(
        (1, "200"), (2, "208"), (3, "220"), (4, "380"), (5, "400"), (6, "415"), (7, "480"), (8, "600"), (9, "690"));

    private static readonly MibLookup FrequencyRating = Lookup((1, "50"), (2, "60"));

    private static readonly MibLookup PowerRating = Lookup(
        (1, "80000"), (2, "100000"), (3, "120000"), (4, "160000"), (5, "200000"), (6, "30000"), (7, "40000"),
        (8, "60000"), (9, "2400000"), (10, "2500000"), (11, "2800000"), (12, "3000000"));

    private static readonly MibLookup TestResult = Lookup(
        (1, "done and passed"),
        (2, "done and warning"),
        (3, "done and error"),
        (4, "aborted"),
        (5, "in progress"),
        (6, "no test initiated"));

    public static MibDefinition Definition { get; } = new()
    {
        Name = "huawei",
        DisplayName = "Huawei (huawei)",
        Version = "0.40",

        // The cards run Net-SNMP on Linux and announce its generic sysObjectID; the model probe confirms.
        SysObjectIds = ["1.3.6.1.4.1.8072.3.2.10", "1.3.6.1.4.1.2011"],
        ProbeOid = Hw + "2.100.1.2.1", // hwUpsDeviceModel
        Entries = Build(),
    };

    private static List<MibEntry> Build()
    {
        var e = new List<MibEntry>(SystemGroup());

        e.Add(Fixed("ups.mfr", "Huawei"));
        e.Add(Text("ups.model", Hw + "2.100.1.2.1", MibFlags.Static, dfl: "Generic SNMP UPS"));
        e.Add(Text("ups.id", Hw + "1.1.1.2.0", MibFlags.Static));
        e.Add(Num("ups.time", Hw + "11.1.0"));
        e.Add(Text("ups.firmware", Hw + "2.100.1.3.1", MibFlags.Static));
        e.Add(Text("ups.serial", Hw + "2.100.1.5.1", MibFlags.Static));

        e.Add(Status(Hw + "2.101.1.1.1", SupplyMethod)); // hwUpsStateSupplyMethod
        e.Add(Status(Hw + "2.101.1.3.1", BatteryState)); // hwUpsBattStatus

        e.Add(Text("ups.test.result", "1.3.6.1.2.1.33.1.7.3.0", lookup: TestResult));

        // Huawei cards are three-phase; NUT fixes the phase counts rather than reading them.
        e.Add(Fixed("input.phases", "3"));
        e.Add(Num("input.L1-N.voltage", Hw + "3.100.1.1.1", 0.1));
        e.Add(Num("input.L2-N.voltage", Hw + "3.100.1.2.1", 0.1));
        e.Add(Num("input.L3-N.voltage", Hw + "3.100.1.3.1", 0.1));
        e.Add(Num("input.frequency", Hw + "3.100.1.4.1", 0.01));
        e.Add(Num("input.L1.current", Hw + "3.100.1.5.1", 0.1));
        e.Add(Num("input.L2.current", Hw + "3.100.1.6.1", 0.1));
        e.Add(Num("input.L3.current", Hw + "3.100.1.7.1", 0.1));
        e.Add(Num("input.L1.powerfactor", Hw + "3.100.1.8.1", 0.01));
        e.Add(Num("input.L2.powerfactor", Hw + "3.100.1.9.1", 0.01));
        e.Add(Num("input.L3.powerfactor", Hw + "3.100.1.10.1", 0.01));

        e.Add(Num("input.bypass.L1-N.voltage", Hw + "5.100.1.1.1", 0.1));
        e.Add(Num("input.bypass.L2-N.voltage", Hw + "5.100.1.2.1", 0.1));
        e.Add(Num("input.bypass.L3-N.voltage", Hw + "5.100.1.3.1", 0.1));
        e.Add(Num("input.bypass.frequency", Hw + "5.100.1.4.1", 0.01));

        e.Add(Fixed("output.phases", "3"));
        e.Add(Num("output.L1-N.voltage", Hw + "4.100.1.1.1", 0.1));
        e.Add(Num("output.L2-N.voltage", Hw + "4.100.1.2.1", 0.1));
        e.Add(Num("output.L3-N.voltage", Hw + "4.100.1.3.1", 0.1));
        e.Add(Num("output.L1.current", Hw + "4.100.1.4.1", 0.1));
        e.Add(Num("output.L2.current", Hw + "4.100.1.5.1", 0.1));
        e.Add(Num("output.L3.current", Hw + "4.100.1.6.1", 0.1));
        e.Add(Num("output.frequency", Hw + "4.100.1.7.1", 0.01));
        e.Add(Num("output.L1.realpower", Hw + "4.100.1.8.1", 0.1));
        e.Add(Num("output.L2.realpower", Hw + "4.100.1.9.1", 0.1));
        e.Add(Num("output.L3.realpower", Hw + "4.100.1.10.1", 0.1));
        e.Add(Num("output.L1.power", Hw + "4.100.1.11.1", 0.1));
        e.Add(Num("output.L2.power", Hw + "4.100.1.12.1", 0.1));
        e.Add(Num("output.L3.power", Hw + "4.100.1.13.1", 0.1));
        e.Add(Num("output.L1.power.percent", Hw + "4.100.1.14.1", 0.1));
        e.Add(Num("output.L2.power.percent", Hw + "4.100.1.15.1", 0.1));
        e.Add(Num("output.L3.power.percent", Hw + "4.100.1.16.1", 0.1));
        e.Add(Text("output.voltage.nominal", Hw + "4.100.1.17.1", MibFlags.Static, VoltageRating));
        e.Add(Text("output.frequency.nominal", Hw + "4.100.1.18.1", MibFlags.Static, FrequencyRating));
        e.Add(Text("output.power.nominal", Hw + "2.100.1.6.1", MibFlags.Static, PowerRating));
        e.Add(Num("output.L1.powerfactor", Hw + "4.100.1.19.1", 0.01));
        e.Add(Num("output.L2.powerfactor", Hw + "4.100.1.20.1", 0.01));
        e.Add(Num("output.L3.powerfactor", Hw + "4.100.1.21.1", 0.01));

        e.Add(Num("battery.voltage", Hw + "6.100.1.1.1", 0.1));
        e.Add(Num("battery.current", Hw + "6.100.1.2.1", 0.1));
        e.Add(Num("battery.charge", Hw + "6.100.1.3.1"));
        e.Add(Num("battery.runtime", Hw + "6.100.1.4.1"));
        return e;
    }
}
