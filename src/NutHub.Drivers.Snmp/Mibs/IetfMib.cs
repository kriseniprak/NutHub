using static NutHub.Drivers.Snmp.Mibs.Mib;

namespace NutHub.Drivers.Snmp.Mibs;

/// <summary>
/// RFC 1628 UPS-MIB, implemented by most network cards next to their vendor MIB.
/// Mapping from NUT drivers/ietf-mib.c (version 1.55), including the per-line input, output and bypass tables of
/// three-phase units. Additions, following RFC 1628: the test commands write the OBJECT IDENTIFIER values the
/// standard defines for upsTestId (NUT writes an INTEGER, which agents refuse), delayed shutdown / startup commands,
/// and shutdown.return / shutdown.stayoff built on upsAutoRestart and upsShutdownAfterDelay.
/// </summary>
internal static class IetfMib
{
    private const string Ups = "1.3.6.1.2.1.33.1.";

    private static readonly MibLookup BatteryStatus = Lookup(
        (1, ""), // unknown
        (2, ""), // batteryNormal
        (3, "LB"), // batteryLow
        (4, "LB")); // batteryDepleted

    private static readonly MibLookup PowerSource = Lookup(
        (1, ""), // other
        (2, "OFF"), // none
        (3, "OL"), // normal
        (4, "OL BYPASS"), // bypass
        (5, "OB"), // battery
        (6, "OL BOOST"), // booster
        (7, "OL TRIM")); // reducer

    // upsTestId is an OBJECT IDENTIFIER; its last arc selects the test (upsTestNoTestsInitiated.. upsTestDeepBatteryCalibration).
    private static readonly MibLookup TestActive = Lookup((1, ""), (2, ""), (3, "TEST"), (4, "TEST"), (5, "CAL"));

    private static readonly MibLookup TestResult = Lookup(
        (1, "done and passed"),
        (2, "done and warning"),
        (3, "done and error"),
        (4, "aborted"),
        (5, "in progress"),
        (6, "no test initiated"));

    private static readonly MibLookup YesNo = Lookup((1, "yes"), (2, "no"));

    private static readonly MibLookup BeeperStatus = Lookup((1, "disabled"), (2, "enabled"), (3, "muted"));

    /// <summary>
    /// The active alarms (upsAlarmTable): each row's upsAlarmDescr is one of the well-known alarms of RFC 1628
    /// (upsWellKnownAlarms, 1.3.6.1.2.1.33.1.6.3.n). Tokens and messages follow NUT's Eaton alarm table.
    /// </summary>
    public static MibAlarmTable AlarmTable { get; } = new()
    {
        CountOid = Ups + "6.1.0", // upsAlarmsPresent
        DescriptionColumnOid = Ups + "6.2.1.2", // upsAlarmDescr
        Alarms =
        [
            new(Ups + "6.3.1", "RB", "Battery bad!"), // upsAlarmBatteryBad
            new(Ups + "6.3.2", null, null), // upsAlarmOnBattery: already reported by upsOutputSource
            new(Ups + "6.3.3", "LB", null), // upsAlarmLowBattery
            new(Ups + "6.3.4", "LB", "Battery depleted!"), // upsAlarmDepletedBattery
            new(Ups + "6.3.5", null, "Bad temperature!"), // upsAlarmTempBad
            new(Ups + "6.3.6", null, null), // upsAlarmInputBad: a power failure, reported as OB
            new(Ups + "6.3.7", null, "Bad output condition!"), // upsAlarmOutputBad
            new(Ups + "6.3.8", "OVER", "Output overload!"), // upsAlarmOutputOverload
            new(Ups + "6.3.9", "BYPASS", "On bypass!"), // upsAlarmOnBypass
            new(Ups + "6.3.10", null, "Bypass not available!"), // upsAlarmBypassBad
            new(Ups + "6.3.11", "OFF", null), // upsAlarmOutputOffAsRequested
            new(Ups + "6.3.12", "OFF", null), // upsAlarmUpsOffAsRequested
            new(Ups + "6.3.13", null, "Charger failure!"), // upsAlarmChargerFailed
            new(Ups + "6.3.14", "OFF", "Output off!"), // upsAlarmUpsOutputOff
            new(Ups + "6.3.15", "OFF", "UPS system off!"), // upsAlarmUpsSystemOff
            new(Ups + "6.3.16", null, "Fan failure!"), // upsAlarmFanFailure
            new(Ups + "6.3.17", null, "Fuse failure!"), // upsAlarmFuseFailure
            new(Ups + "6.3.18", null, "General fault!"), // upsAlarmGeneralFault
            new(Ups + "6.3.19", null, "Diagnostic test failure!"), // upsAlarmDiagnosticTestFailed
            new(Ups + "6.3.20", null, "Communication with UPS lost!"), // upsAlarmCommunicationsLost
            new(Ups + "6.3.21", null, "Awaiting power!"), // upsAlarmAwaitingPower
            new(Ups + "6.3.22", null, "Shutdown pending!"), // upsAlarmShutdownPending
            new(Ups + "6.3.23", null, "Shutdown imminent!"), // upsAlarmShutdownImminent
            new(Ups + "6.3.24", "TEST", null), // upsAlarmTestInProgress
        ],
    };

    /// <summary>
    /// RFC 1628 composite commands: upsAutoRestart decides whether the UPS comes back once mains power returns
    /// after upsShutdownAfterDelay has switched the output off.
    /// </summary>
    public static IReadOnlyList<MibComposite> Composites { get; } =
    [
        new("shutdown.return", [MibStep.Write("ups.start.auto", "yes"), MibStep.Command("load.off.delay")]),
        new("shutdown.stayoff", [MibStep.Write("ups.start.auto", "no"), MibStep.Command("load.off.delay")]),
    ];

    public static IReadOnlyList<MibEntry> Entries { get; } = Build();

    public static MibDefinition Definition { get; } = new()
    {
        Name = "ietf",
        DisplayName = "RFC 1628 UPS-MIB (ietf)",
        Version = "1.55",
        SysObjectIds = ["1.3.6.1.2.1.33"],
        ProbeOid = Ups + "1.1.0", // upsIdentManufacturer, NUT's oid_auto_check for this MIB
        Entries = Entries,
        AlarmTable = AlarmTable,
        Composites = Composites,
    };

    /// <summary>Tripp Lite cards announce their own sysObjectID but speak RFC 1628 (NUT tripplite_ietf).</summary>
    public static MibDefinition TrippLite { get; } = Definition with
    {
        Name = "tripplite",
        DisplayName = "Tripp Lite (RFC 1628, tripplite)",
        SysObjectIds = ["1.3.6.1.4.1.850.1"],

        // Same objects as "ietf": probing cannot tell them apart, only the sysObjectID can.
        Probeable = false,
    };

    private static List<MibEntry> Build()
    {
        var e = new List<MibEntry>(SystemGroup());

        // Identification group
        e.Add(Text("ups.mfr", Ups + "1.1.0", MibFlags.Static, dfl: "Generic")); // upsIdentManufacturer
        e.Add(Text("ups.model", Ups + "1.2.0", MibFlags.Static, dfl: "Generic SNMP UPS")); // upsIdentModel
        e.Add(Text("ups.firmware", Ups + "1.3.0", MibFlags.Static)); // upsIdentUPSSoftwareVersion
        e.Add(Text("ups.firmware.aux", Ups + "1.4.0", MibFlags.Static)); // upsIdentAgentSoftwareVersion

        // Battery group
        e.Add(Status(Ups + "2.1.0", BatteryStatus)); // upsBatteryStatus
        e.Add(Num("battery.runtime", Ups + "2.3.0", 60)); // upsEstimatedMinutesRemaining
        e.Add(Num("battery.charge", Ups + "2.4.0")); // upsEstimatedChargeRemaining
        e.Add(Num("battery.voltage", Ups + "2.5.0", 0.1)); // upsBatteryVoltage
        e.Add(Num("battery.current", Ups + "2.6.0", 0.1, MibFlags.NegativeInvalid)); // upsBatteryCurrent
        e.Add(Num("battery.temperature", Ups + "2.7.0")); // upsBatteryTemperature

        // Input group
        e.Add(Num("input.phases", Ups + "3.2.0")); // upsInputNumLines
        e.Add(Num("input.frequency", Ups + "3.3.1.2.1", 0.1, MibFlags.Input1)); // upsInputFrequency
        e.Add(Num("input.L1.frequency", Ups + "3.3.1.2.1", 0.1, MibFlags.Input3));
        e.Add(Num("input.L2.frequency", Ups + "3.3.1.2.2", 0.1, MibFlags.Input3));
        e.Add(Num("input.L3.frequency", Ups + "3.3.1.2.3", 0.1, MibFlags.Input3));
        e.Add(Num("input.voltage", Ups + "3.3.1.3.1", 1, MibFlags.Input1)); // upsInputVoltage
        e.Add(Num("input.L1-N.voltage", Ups + "3.3.1.3.1", 1, MibFlags.Input3));
        e.Add(Num("input.L2-N.voltage", Ups + "3.3.1.3.2", 1, MibFlags.Input3));
        e.Add(Num("input.L3-N.voltage", Ups + "3.3.1.3.3", 1, MibFlags.Input3));
        e.Add(Num("input.current", Ups + "3.3.1.4.1", 0.1, MibFlags.Input1 | MibFlags.NegativeInvalid)); // upsInputCurrent
        e.Add(Num("input.L1.current", Ups + "3.3.1.4.1", 0.1, MibFlags.Input3));
        e.Add(Num("input.L2.current", Ups + "3.3.1.4.2", 0.1, MibFlags.Input3));
        e.Add(Num("input.L3.current", Ups + "3.3.1.4.3", 0.1, MibFlags.Input3));
        e.Add(Num("input.realpower", Ups + "3.3.1.5.1", 1, MibFlags.Input1 | MibFlags.NegativeInvalid)); // upsInputTruePower
        e.Add(Num("input.L1.realpower", Ups + "3.3.1.5.1", 1, MibFlags.Input3));
        e.Add(Num("input.L2.realpower", Ups + "3.3.1.5.2", 1, MibFlags.Input3));
        e.Add(Num("input.L3.realpower", Ups + "3.3.1.5.3", 1, MibFlags.Input3));

        // Output group
        e.Add(Status(Ups + "4.1.0", PowerSource)); // upsOutputSource
        e.Add(Num("output.frequency", Ups + "4.2.0", 0.1)); // upsOutputFrequency
        e.Add(Num("output.phases", Ups + "4.3.0")); // upsOutputNumLines
        e.Add(Num("output.voltage", Ups + "4.4.1.2.1", 1, MibFlags.Output1)); // upsOutputVoltage
        e.Add(Num("output.L1-N.voltage", Ups + "4.4.1.2.1", 1, MibFlags.Output3));
        e.Add(Num("output.L2-N.voltage", Ups + "4.4.1.2.2", 1, MibFlags.Output3));
        e.Add(Num("output.L3-N.voltage", Ups + "4.4.1.2.3", 1, MibFlags.Output3));
        e.Add(Num("output.current", Ups + "4.4.1.3.1", 0.1, MibFlags.Output1 | MibFlags.NegativeInvalid)); // upsOutputCurrent
        e.Add(Num("output.L1.current", Ups + "4.4.1.3.1", 0.1, MibFlags.Output3));
        e.Add(Num("output.L2.current", Ups + "4.4.1.3.2", 0.1, MibFlags.Output3));
        e.Add(Num("output.L3.current", Ups + "4.4.1.3.3", 0.1, MibFlags.Output3));
        e.Add(Num("output.realpower", Ups + "4.4.1.4.1", 1, MibFlags.Output1 | MibFlags.NegativeInvalid)); // upsOutputPower
        e.Add(Num("output.L1.realpower", Ups + "4.4.1.4.1", 1, MibFlags.Output3));
        e.Add(Num("output.L2.realpower", Ups + "4.4.1.4.2", 1, MibFlags.Output3));
        e.Add(Num("output.L3.realpower", Ups + "4.4.1.4.3", 1, MibFlags.Output3));
        e.Add(Num("ups.load", Ups + "4.4.1.5.1", 1, MibFlags.Output1)); // upsOutputPercentLoad
        e.Add(Num("output.L1.power.percent", Ups + "4.4.1.5.1", 1, MibFlags.Output3));
        e.Add(Num("output.L2.power.percent", Ups + "4.4.1.5.2", 1, MibFlags.Output3));
        e.Add(Num("output.L3.power.percent", Ups + "4.4.1.5.3", 1, MibFlags.Output3));

        // Bypass group
        e.Add(Num("input.bypass.phases", Ups + "5.2.0", 1, MibFlags.NegativeInvalid)); // upsBypassNumLines
        e.Add(Num("input.bypass.frequency", Ups + "5.1.0", 0.1, MibFlags.Bypass1 | MibFlags.Bypass3 | MibFlags.NegativeInvalid)); // upsBypassFrequency
        e.Add(Num("input.bypass.voltage", Ups + "5.3.1.2.1", 1, MibFlags.Bypass1)); // upsBypassVoltage
        e.Add(Num("input.bypass.L1-N.voltage", Ups + "5.3.1.2.1", 1, MibFlags.Bypass3));
        e.Add(Num("input.bypass.L2-N.voltage", Ups + "5.3.1.2.2", 1, MibFlags.Bypass3));
        e.Add(Num("input.bypass.L3-N.voltage", Ups + "5.3.1.2.3", 1, MibFlags.Bypass3));
        e.Add(Num("input.bypass.current", Ups + "5.3.1.3.1", 0.1, MibFlags.Bypass1)); // upsBypassCurrent
        e.Add(Num("input.bypass.L1.current", Ups + "5.3.1.3.1", 0.1, MibFlags.Bypass3));
        e.Add(Num("input.bypass.L2.current", Ups + "5.3.1.3.2", 0.1, MibFlags.Bypass3));
        e.Add(Num("input.bypass.L3.current", Ups + "5.3.1.3.3", 0.1, MibFlags.Bypass3));
        e.Add(Num("input.bypass.realpower", Ups + "5.3.1.4.1", 1, MibFlags.Bypass1)); // upsBypassPower
        e.Add(Num("input.bypass.L1.realpower", Ups + "5.3.1.4.1", 1, MibFlags.Bypass3));
        e.Add(Num("input.bypass.L2.realpower", Ups + "5.3.1.4.2", 1, MibFlags.Bypass3));
        e.Add(Num("input.bypass.L3.realpower", Ups + "5.3.1.4.3", 1, MibFlags.Bypass3));

        // Alarm group: see AlarmTable. NUT reads the well-known alarm OID upsAlarmOutputOverload (6.3.8) as if it
        // were an object instance, which agents never answer; the alarm table is where active alarms are listed.

        // Test group
        e.Add(Status(Ups + "7.1.0", TestActive)); // upsTestId
        e.Add(Cmd("test.battery.stop", Ups + "7.1.0", Ups + "7.7.2", SnmpSetType.ObjectIdentifier)); // upsTestAbortTestInProgress
        e.Add(Cmd("test.battery.start", Ups + "7.1.0", Ups + "7.7.3", SnmpSetType.ObjectIdentifier)); // upsTestGeneralSystemsTest
        e.Add(Cmd("test.battery.start.quick", Ups + "7.1.0", Ups + "7.7.4", SnmpSetType.ObjectIdentifier)); // upsTestQuickBatteryTest
        e.Add(Cmd("test.battery.start.deep", Ups + "7.1.0", Ups + "7.7.5", SnmpSetType.ObjectIdentifier)); // upsTestDeepBatteryCalibration
        e.Add(Text("ups.test.result", Ups + "7.3.0", lookup: TestResult)); // upsTestResultsSummary

        // Control group. The timers are exposed read-only: writing upsShutdownAfterDelay starts a shutdown, which
        // belongs to the load.off.delay command, not to a variable write.
        e.Add(Num("ups.timer.shutdown", Ups + "8.2.0")); // upsShutdownAfterDelay
        e.Add(Cmd("load.off", Ups + "8.2.0", "0", SnmpSetType.Integer));
        e.Add(Cmd("load.off.delay", Ups + "8.2.0", null, SnmpSetType.Integer));
        e.Add(Cmd("shutdown.stop", Ups + "8.2.0", "-1", SnmpSetType.Integer)); // -1 aborts a countdown (RFC 1628)
        e.Add(Num("ups.timer.start", Ups + "8.3.0")); // upsStartupAfterDelay
        e.Add(Cmd("load.on", Ups + "8.3.0", "0", SnmpSetType.Integer));
        e.Add(Cmd("load.on.delay", Ups + "8.3.0", null, SnmpSetType.Integer));
        e.Add(Num("ups.timer.reboot", Ups + "8.4.0")); // upsRebootWithDuration
        e.Add(RwText("ups.start.auto", Ups + "8.5.0", 3, setType: SnmpSetType.Integer, lookup: YesNo)); // upsAutoRestart
        e.Add(Setting("ups.delay.shutdown", "20"));
        e.Add(Setting("ups.delay.start", "30"));

        // Configuration group
        e.Add(Num("input.voltage.nominal", Ups + "9.1.0", 1, MibFlags.NegativeInvalid)); // upsConfigInputVoltage
        e.Add(Num("input.frequency.nominal", Ups + "9.2.0", 0.1, MibFlags.NegativeInvalid)); // upsConfigInputFreq
        e.Add(Num("output.voltage.nominal", Ups + "9.3.0")); // upsConfigOutputVoltage
        e.Add(Num("output.frequency.nominal", Ups + "9.4.0", 0.1)); // upsConfigOutputFreq
        e.Add(Num("output.power.nominal", Ups + "9.5.0", 1, MibFlags.NegativeInvalid)); // upsConfigOutputVA
        e.Add(Num("output.realpower.nominal", Ups + "9.6.0", 1, MibFlags.NegativeInvalid)); // upsConfigOutputPower
        e.Add(Num("battery.runtime.low", Ups + "9.7.0", 60)); // upsConfigLowBattTime
        e.Add(Text("ups.beeper.status", Ups + "9.8.0", lookup: BeeperStatus)); // upsConfigAudibleStatus
        e.Add(Cmd("beeper.disable", Ups + "9.8.0", "1", SnmpSetType.Integer));
        e.Add(Cmd("beeper.enable", Ups + "9.8.0", "2", SnmpSetType.Integer));
        e.Add(Cmd("beeper.mute", Ups + "9.8.0", "3", SnmpSetType.Integer));
        e.Add(Num("input.transfer.low", Ups + "9.9.0")); // upsConfigLowVoltageTransferPoint
        e.Add(Num("input.transfer.high", Ups + "9.10.0")); // upsConfigHighVoltageTransferPoint
        return e;
    }
}
