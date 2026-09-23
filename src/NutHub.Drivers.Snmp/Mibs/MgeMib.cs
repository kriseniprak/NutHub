using static NutHub.Drivers.Snmp.Mibs.Mib;

namespace NutHub.Drivers.Snmp.Mibs;

/// <summary>
/// MGE-UPS-MIB (MGE UPS Systems and Eaton Network Management Card "Network-MS").
/// Mapping from NUT drivers/mge-mib.c (version 0.55). Delays and the delayed shutdown use the RFC 1628 control
/// objects, as in NUT; ups.delay.shutdown / ups.delay.start are driver-side settings (NUT's ondelay/offdelay).
/// Per-outlet templates are not mapped.
/// </summary>
internal static class MgeMib
{
    private const string Mge = "1.3.6.1.4.1.705.1.";
    private const string Ietf = "1.3.6.1.2.1.33.1.";

    private static readonly MibLookup LowBattery = Lookup((1, "LB"), (2, ""));
    private static readonly MibLookup OnBattery = Lookup((1, "OB"), (2, "OL"));
    private static readonly MibLookup Bypass = Lookup((1, "BYPASS"), (2, ""));
    private static readonly MibLookup Boost = Lookup((1, "BOOST"), (2, ""));
    private static readonly MibLookup Trim = Lookup((1, "TRIM"), (2, ""));
    private static readonly MibLookup Overload = Lookup((1, "OVER"), (2, ""));
    private static readonly MibLookup ReplaceBattery = Lookup((1, "RB"), (2, ""));
    private static readonly MibLookup OutputOff = Lookup((1, "OFF"), (2, ""));

    private static readonly MibLookup TransferReason = Lookup(
        (1, ""),
        (2, "input voltage out of range"),
        (3, "input frequency out of range"),
        (4, "utility off"));

    private static readonly MibLookup TestResult = Lookup(
        (1, "done and passed"),
        (2, "done and warning"),
        (3, "done and error"),
        (4, "aborted"),
        (5, "in progress"),
        (6, "no test initiated"));

    private static readonly MibLookup BeeperStatus = Lookup((1, "disabled"), (2, "enabled"), (3, "muted"));
    private static readonly MibLookup YesNo = Lookup((1, "yes"), (2, "no"));

    private static readonly MibLookup PowerSource = Lookup(
        (1, ""), (2, "OFF"), (4, "BYPASS"), (5, "OB"), (6, "BOOST"), (7, "TRIM"));

    private static readonly MibLookup DryContacts = Lookup((-1, "unknown"), (1, "closed"), (2, "opened"));

    public static MibDefinition Definition { get; } = new()
    {
        Name = "mge",
        DisplayName = "MGE / Eaton Network-MS (mge)",
        Version = "0.55",
        SysObjectIds = ["1.3.6.1.4.1.705.1"],
        ProbeOid = Mge + "1.1.0", // upsmgIdentFamilyName
        Entries = Build(),
    };

    private static List<MibEntry> Build()
    {
        var e = new List<MibEntry>(SystemGroup());

        e.Add(Fixed("ups.mfr", "EATON"));
        e.Add(Text("ups.model", Mge + "1.1.0", MibFlags.Static, dfl: "Generic SNMP UPS")); // upsmgIdentFamilyName
        e.Add(Text("ups.serial", Mge + "1.7.0", MibFlags.Static)); // upsmgIdentSerialNumber
        e.Add(Text("ups.firmware", Mge + "1.4.0", MibFlags.Static)); // upsmgIdentFirmwareVersion
        e.Add(Text("ups.firmware.aux", Mge + "12.12.0", MibFlags.Static)); // upsmgAgentFirmwareVersion
        e.Add(Num("ups.load", Mge + "7.2.1.4.1", 1, MibFlags.Output1)); // mgoutputLoadPerPhase.1
        e.Add(Text("ups.beeper.status", Ietf + "9.8.0", lookup: BeeperStatus)); // upsConfigAudibleStatus
        e.Add(Num("ups.L1.load", Mge + "7.2.1.4.1", 1, MibFlags.Output3));
        e.Add(Num("ups.L2.load", Mge + "7.2.1.4.2", 1, MibFlags.Output3));
        e.Add(Num("ups.L3.load", Mge + "7.2.1.4.3", 1, MibFlags.Output3));
        e.Add(Text("ups.test.result", Ietf + "7.3.0", lookup: TestResult)); // upsTestResultsSummary
        e.Add(Setting("ups.delay.shutdown", "20"));
        e.Add(Setting("ups.delay.start", "30"));
        e.Add(Num("ups.timer.shutdown", Ietf + "8.2.0")); // upsShutdownAfterDelay
        e.Add(Num("ups.timer.start", Ietf + "8.3.0")); // upsStartupAfterDelay
        e.Add(Num("ups.timer.reboot", Ietf + "8.4.0")); // upsRebootWithDuration
        e.Add(RwText("ups.start.auto", Ietf + "8.5.0", 3, setType: SnmpSetType.Integer, lookup: YesNo)); // upsAutoRestart

        e.Add(Status(Mge + "5.11.0", ReplaceBattery)); // upsmgBatteryReplacement
        e.Add(Status(Mge + "5.14.0", LowBattery)); // upsmgBatteryLowBattery
        e.Add(Status(Mge + "5.16.0", LowBattery)); // upsmgBatteryLowCondition
        e.Add(Status(Mge + "7.3.0", OnBattery)); // upsmgOutputOnBattery
        e.Add(Status(Mge + "7.4.0", Bypass)); // upsmgOutputOnByPass
        e.Add(Status(Mge + "7.7.0", OutputOff)); // upsmgOutputUtilityOff
        e.Add(Status(Mge + "7.8.0", Boost)); // upsmgOutputOnBoost
        e.Add(Status(Mge + "7.10.0", Overload)); // upsmgOutputOverLoad
        e.Add(Status(Mge + "7.12.0", Trim)); // upsmgOutputOnBuck
        e.Add(Status(Ietf + "4.1.0", PowerSource)); // upsOutputSource

        e.Add(Num("input.phases", Mge + "6.1.0")); // upsmgInputPhaseNum
        e.Add(Num("input.voltage", Mge + "6.2.1.2.1", 0.1, MibFlags.Input1)); // mginputVoltage
        e.Add(Num("input.L1-N.voltage", Mge + "6.2.1.2.1", 0.1, MibFlags.Input3));
        e.Add(Num("input.L2-N.voltage", Mge + "6.2.1.2.2", 0.1, MibFlags.Input3));
        e.Add(Num("input.L3-N.voltage", Mge + "6.2.1.2.3", 0.1, MibFlags.Input3));
        e.Add(Num("input.frequency", Mge + "6.2.1.3.1", 0.1, MibFlags.Input1)); // mginputFrequency
        e.Add(Num("input.L1.frequency", Mge + "6.2.1.3.1", 0.1, MibFlags.Input3));
        e.Add(Num("input.L2.frequency", Mge + "6.2.1.3.2", 0.1, MibFlags.Input3));
        e.Add(Num("input.L3.frequency", Mge + "6.2.1.3.3", 0.1, MibFlags.Input3));
        e.Add(Num("input.voltage.minimum", Mge + "6.2.1.4.1", 0.1, MibFlags.Input1)); // mginputMinimumVoltage
        e.Add(Num("input.L1-N.voltage.minimum", Mge + "6.2.1.4.1", 0.1, MibFlags.Input3));
        e.Add(Num("input.L2-N.voltage.minimum", Mge + "6.2.1.4.2", 0.1, MibFlags.Input3));
        e.Add(Num("input.L3-N.voltage.minimum", Mge + "6.2.1.4.3", 0.1, MibFlags.Input3));
        e.Add(Num("input.voltage.maximum", Mge + "6.2.1.5.1", 0.1, MibFlags.Input1)); // mginputMaximumVoltage
        e.Add(Num("input.L1-N.voltage.maximum", Mge + "6.2.1.5.1", 0.1, MibFlags.Input3));
        e.Add(Num("input.L2-N.voltage.maximum", Mge + "6.2.1.5.2", 0.1, MibFlags.Input3));
        e.Add(Num("input.L3-N.voltage.maximum", Mge + "6.2.1.5.3", 0.1, MibFlags.Input3));
        e.Add(Num("input.current", Mge + "6.2.1.6.1", 0.1, MibFlags.Input1)); // mginputCurrent
        e.Add(Num("input.L1.current", Mge + "6.2.1.6.1", 0.1, MibFlags.Input3));
        e.Add(Num("input.L2.current", Mge + "6.2.1.6.2", 0.1, MibFlags.Input3));
        e.Add(Num("input.L3.current", Mge + "6.2.1.6.3", 0.1, MibFlags.Input3));
        e.Add(Text("input.transfer.reason", Mge + "6.4.0", lookup: TransferReason)); // upsmgInputLineFailCause

        e.Add(Num("output.phases", Mge + "7.1.0")); // upsmgOutputPhaseNum
        e.Add(Num("output.voltage", Mge + "7.2.1.2.1", 0.1, MibFlags.Output1)); // mgoutputVoltage
        e.Add(Num("output.L1-N.voltage", Mge + "7.2.1.2.1", 0.1, MibFlags.Output3));
        e.Add(Num("output.L2-N.voltage", Mge + "7.2.1.2.2", 0.1, MibFlags.Output3));
        e.Add(Num("output.L3-N.voltage", Mge + "7.2.1.2.3", 0.1, MibFlags.Output3));
        e.Add(Num("output.frequency", Mge + "7.2.1.3.1", 0.1, MibFlags.Output1)); // mgoutputFrequency
        e.Add(Num("output.L1.frequency", Mge + "7.2.1.3.1", 0.1, MibFlags.Output3));
        e.Add(Num("output.L2.frequency", Mge + "7.2.1.3.2", 0.1, MibFlags.Output3));
        e.Add(Num("output.L3.frequency", Mge + "7.2.1.3.3", 0.1, MibFlags.Output3));
        e.Add(Num("output.current", Mge + "7.2.1.5.1", 0.1, MibFlags.Output1)); // mgoutputCurrent
        e.Add(Num("output.L1.current", Mge + "7.2.1.5.1", 0.1, MibFlags.Output3));
        e.Add(Num("output.L2.current", Mge + "7.2.1.5.2", 0.1, MibFlags.Output3));
        e.Add(Num("output.L3.current", Mge + "7.2.1.5.3", 0.1, MibFlags.Output3));

        e.Add(Num("battery.charge", Mge + "5.2.0")); // upsmgBatteryLevel
        e.Add(Num("battery.runtime", Mge + "5.1.0")); // upsmgBatteryRemainingTime
        e.Add(Num("battery.runtime.low", Mge + "4.7.0")); // upsmgConfigLowBatteryTime
        e.Add(RwText("battery.charge.low", Mge + "4.8.0", 2, setType: SnmpSetType.Integer)); // upsmgConfigLowBatteryLevel
        e.Add(Num("battery.voltage", Mge + "5.5.0", 0.1)); // upsmgBatteryVoltage

        e.Add(Num("ambient.temperature", Mge + "8.1.0", 0.1)); // upsmgEnvironAmbientTemp
        e.Add(Num("ambient.humidity", Mge + "8.2.0", 0.1)); // upsmgEnvironAmbientHumidity
        e.Add(Text("ambient.contacts.1.status", Mge + "8.7.1.9.1", lookup: DryContacts));
        e.Add(Text("ambient.contacts.2.status", Mge + "8.7.1.10.1", lookup: DryContacts));

        e.Add(Cmd("test.battery.start", Mge + "10.4.0", "2", SnmpSetType.Integer)); // upsmgTestBatteryCalibration: start
        e.Add(Cmd("beeper.disable", Ietf + "9.8.0", "1", SnmpSetType.Integer));
        e.Add(Cmd("beeper.enable", Ietf + "9.8.0", "2", SnmpSetType.Integer));
        e.Add(Cmd("beeper.mute", Ietf + "9.8.0", "3", SnmpSetType.Integer));
        e.Add(Cmd("load.off.delay", Ietf + "8.2.0", null, SnmpSetType.Integer)); // upsShutdownAfterDelay
        e.Add(Cmd("load.on.delay", Ietf + "8.3.0", null, SnmpSetType.Integer)); // upsStartupAfterDelay
        return e;
    }
}
