using static NutHub.Drivers.Snmp.Mibs.Mib;

namespace NutHub.Drivers.Snmp.Mibs;

/// <summary>
/// Phoenixtec / XPPC-MIB (network cards of Phoenixtec-built UPSes sold under many brands).
/// Mapping from NUT drivers/xppc-mib.c (version 0.42).
/// </summary>
internal static class XppcMib
{
    private const string Xppc = "1.3.6.1.4.1.935.1.1.1.";

    private static readonly MibLookup BatteryStatus = Lookup((1, ""), (2, ""), (3, "LB"));

    private static readonly MibLookup PowerStatus = Lookup(
        (1, ""), // unknown
        (2, "OL"), // onLine
        (3, "OB"), // onBattery
        (4, "OL BOOST"), // onBoost
        (5, "OFF"), // sleeping
        (6, "BYPASS"), // onBypass
        (7, ""), // rebooting
        (8, "OL"), // standBy
        (9, "OL TRIM")); // onBuck

    public static MibDefinition Definition { get; } = new()
    {
        Name = "xppc",
        DisplayName = "Phoenixtec (xppc)",
        Version = "0.42",
        SysObjectIds = ["1.3.6.1.4.1.935"],
        ProbeOid = Xppc + "1.1.1.0", // upsBaseIdentModel
        Entries = Build(),
    };

    private static List<MibEntry> Build()
    {
        var e = new List<MibEntry>(SystemGroup());

        e.Add(Fixed("ups.mfr", "Tripp Lite / Phoenixtec"));
        e.Add(Text("ups.model", Xppc + "1.1.1.0", dfl: "Generic Phoenixtec SNMP device"));
        e.Add(Text("ups.firmware.aux", Xppc + "1.2.4.0"));
        e.Add(Status(Xppc + "2.1.1.0", BatteryStatus)); // upsBaseBatteryStatus
        e.Add(Num("battery.charge", Xppc + "2.2.1.0")); // upsSmartBatteryCapacity
        e.Add(Num("battery.voltage", Xppc + "2.2.2.0", 0.1)); // upsSmartBatteryVoltage
        e.Add(Num("ups.temperature", Xppc + "2.2.3.0", 0.1)); // upsSmartBatteryTemperature
        e.Add(Num("battery.runtime", Xppc + "2.2.4.0")); // upsSmartBatteryRunTimeRemaining
        e.Add(Num("input.voltage", Xppc + "3.2.1.0", 0.1)); // upsSmartInputLineVoltage
        e.Add(Num("input.frequency", Xppc + "3.2.4.0", 0.1)); // upsSmartInputFrequency
        e.Add(Status(Xppc + "4.1.1.0", PowerStatus)); // upsBaseOutputStatus
        e.Add(Num("output.voltage", Xppc + "4.2.1.0", 0.1)); // upsSmartOutputVoltage
        e.Add(Num("output.frequency", Xppc + "4.2.2.0", 0.1)); // upsSmartOutputFrequency
        e.Add(Num("ups.load", Xppc + "4.2.3.0")); // upsSmartOutputLoad
        e.Add(Num("output.voltage.nominal", Xppc + "5.2.1.0", 0.1)); // upsSmartConfigRatedOutputVoltage
        return e;
    }
}
