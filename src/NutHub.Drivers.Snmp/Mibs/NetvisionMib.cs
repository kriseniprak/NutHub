using static NutHub.Drivers.Snmp.Mibs.Mib;

namespace NutHub.Drivers.Snmp.Mibs;

/// <summary>
/// Socomec Sicon NetVision network cards.
/// Mapping from NUT drivers/netvision-mib.c (version 0.45).
/// </summary>
internal static class NetvisionMib
{
    private const string Nv = "1.3.6.1.4.1.4555.1.1.1.1.";

    private static readonly MibLookup BatteryStatus = Lookup(
        (2, ""), // battery normal
        (3, "LB"), // battery low
        (4, "LB"), // battery depleted
        (5, "DISCHRG"), // battery discharging
        (6, "RB")); // battery failure

    private static readonly MibLookup OnBattery = Lookup((0, "OL"), (1, "OB"));

    private static readonly MibLookup OutputSource = Lookup(
        (1, ""), // unknown
        (2, ""), // inverter
        (3, "OL"), // mains
        (4, ""), // ecomode
        (5, "OL BYPASS"), // bypass
        (6, "OFF"), // standby
        (7, "OL BYPASS"), // maintenance bypass
        (8, "OFF"), // off
        (9, "")); // normal

    public static MibDefinition Definition { get; } = new()
    {
        Name = "netvision",
        DisplayName = "Socomec NetVision (netvision)",
        Version = "0.45",
        SysObjectIds = ["1.3.6.1.4.1.4555.1.1.1"],
        ProbeOid = Nv + "1.1.0", // upsIdentModel
        Entries = Build(),
    };

    private static List<MibEntry> Build()
    {
        var e = new List<MibEntry>(SystemGroup());

        e.Add(Text("ups.mfr", Nv + "1.3.0", MibFlags.Static, dfl: "SOCOMEC SICON UPS")); // upsIdentAgentSoftwareVersion
        e.Add(Text("ups.model", Nv + "1.1.0", MibFlags.Static, dfl: "Generic SNMP UPS")); // upsIdentModel
        e.Add(Text("ups.serial", Nv + "1.4.0", MibFlags.Static)); // upsIdentUpsSerialNumber
        e.Add(Text("ups.firmware.aux", Nv + "1.2.0", MibFlags.Static)); // upsIdentFirmwareVersion
        e.Add(Status(Nv + "2.1.0", BatteryStatus)); // upsBatteryStatus
        e.Add(Status(Nv + "4.1.0", OutputSource)); // upsOutputSource
        e.Add(Status(Nv + "6.3.2.0", OnBattery)); // upsAlarmOnBattery

        e.Add(Num("ups.load", Nv + "4.4.1.4.1", 1, MibFlags.Input1));

        e.Add(Num("input.phases", Nv + "3.1.0"));
        e.Add(Num("input.frequency", Nv + "3.2.0", 0.1));
        e.Add(Num("input.voltage", Nv + "3.3.1.5.1", 0.1, MibFlags.Input1));
        e.Add(Num("input.current", Nv + "3.3.1.3.1", 0.1, MibFlags.Input1));
        e.Add(Num("input.L1-N.voltage", Nv + "3.3.1.5.1", 0.1, MibFlags.Input3));
        e.Add(Num("input.L1.current", Nv + "3.3.1.3.1", 0.1, MibFlags.Input3));
        e.Add(Num("input.L2-N.voltage", Nv + "3.3.1.5.2", 0.1, MibFlags.Input3));
        e.Add(Num("input.L2.current", Nv + "3.3.1.3.2", 0.1, MibFlags.Input3));
        e.Add(Num("input.L3-N.voltage", Nv + "3.3.1.5.3", 0.1, MibFlags.Input3));
        e.Add(Num("input.L3.current", Nv + "3.3.1.3.3", 0.1, MibFlags.Input3));

        e.Add(Num("output.phases", Nv + "4.3.0"));
        e.Add(Num("output.frequency", Nv + "4.2.0", 0.1));
        e.Add(Num("output.voltage", Nv + "4.4.1.2.1", 0.1, MibFlags.Output1));
        e.Add(Num("output.current", Nv + "4.4.1.3.1", 0.1, MibFlags.Output1));
        e.Add(Num("output.load", Nv + "4.4.1.4.1", 1, MibFlags.Output1));
        e.Add(Num("output.L1-N.voltage", Nv + "4.4.1.2.1", 0.1, MibFlags.Output3));
        e.Add(Num("output.L1.current", Nv + "4.4.1.3.1", 0.1, MibFlags.Output3));
        e.Add(Num("output.L1.power.percent", Nv + "4.4.1.4.1", 1, MibFlags.Output3));
        e.Add(Num("output.L2-N.voltage", Nv + "4.4.1.2.2", 0.1, MibFlags.Output3));
        e.Add(Num("output.L2.current", Nv + "4.4.1.3.2", 0.1, MibFlags.Output3));
        e.Add(Num("output.L2.power.percent", Nv + "4.4.1.4.2", 1, MibFlags.Output3));
        e.Add(Num("output.L3-N.voltage", Nv + "4.4.1.2.3", 0.1, MibFlags.Output3));
        e.Add(Num("output.L3.current", Nv + "4.4.1.3.3", 0.1, MibFlags.Output3));
        e.Add(Num("output.L3.power.percent", Nv + "4.4.1.4.3", 1, MibFlags.Output3));

        e.Add(Num("input.bypass.phases", Nv + "5.2.0"));
        e.Add(Num("input.bypass.frequency", Nv + "5.1.0", 0.1));
        e.Add(Num("input.bypass.voltage", Nv + "5.3.1.2.1", 0.1, MibFlags.Bypass1));
        e.Add(Num("input.bypass.current", Nv + "5.3.1.3.1", 0.1, MibFlags.Bypass1));
        e.Add(Num("input.bypass.L1-N.voltage", Nv + "5.3.1.2.1", 0.1, MibFlags.Bypass3));
        e.Add(Num("input.bypass.L1.current", Nv + "5.3.1.3.1", 0.1, MibFlags.Bypass3));
        e.Add(Num("input.bypass.L2-N.voltage", Nv + "5.3.1.2.2", 0.1, MibFlags.Bypass3));
        e.Add(Num("input.bypass.L2.current", Nv + "5.3.1.3.2", 0.1, MibFlags.Bypass3));
        e.Add(Num("input.bypass.L3-N.voltage", Nv + "5.3.1.2.3", 0.1, MibFlags.Bypass3));
        e.Add(Num("input.bypass.L3.current", Nv + "5.3.1.3.3", 0.1, MibFlags.Bypass3));

        e.Add(Num("battery.charge", Nv + "2.4.0")); // upsEstimatedChargeRemaining
        e.Add(Num("battery.voltage", Nv + "2.5.0", 0.1)); // upsBatteryVoltage
        e.Add(Num("battery.runtime", Nv + "2.3.0", 60)); // upsEstimatedMinutesRemaining
        return e;
    }
}
