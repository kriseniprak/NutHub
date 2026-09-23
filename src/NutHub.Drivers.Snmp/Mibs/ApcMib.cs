using static NutHub.Drivers.Snmp.Mibs.Mib;

namespace NutHub.Drivers.Snmp.Mibs;

/// <summary>
/// APC PowerNet-MIB (AP96xx network management cards, Smart-UPS, Symmetra).
/// Mapping from NUT drivers/apc-mib.c (version 1.62) and drivers/apc-iem-mib.h. Where NUT marks a read-only object
/// as writable (upsAdvTestLastDiagnosticsDate) it is read-only here; the reboot commands NUT keeps commented out
/// (upsAdvControlRebootShutdown) are enabled.
/// </summary>
internal static class ApcMib
{
    private const string Apc = "1.3.6.1.4.1.318.1.1.1.";

    private static readonly MibLookup BatteryStatus = Lookup(
        (1, ""), // unknown
        (2, ""), // batteryNormal
        (3, "LB"), // batteryLow
        (4, "LB"), // batteryInFaultCondition
        (5, "LB")); // noBatteryPresent

    private static readonly MibLookup PowerStatus = Lookup(
        (1, ""), // unknown
        (2, "OL"), // onLine
        (3, "OB"), // onBattery
        (4, "OL BOOST"), // onSmartBoost
        (5, "OFF"), // timedSleeping
        (6, "OFF"), // softwareBypass
        (7, "OFF"), // off
        (8, ""), // rebooting
        (9, "BYPASS"), // switchedBypass
        (10, "BYPASS"), // hardwareFailureBypass
        (11, "OFF"), // sleepingUntilPowerReturn
        (12, "OL TRIM"), // onSmartTrim
        (13, "OL ECO"), // ecoMode
        (14, "OL"), // hotStandby
        (15, "OL"), // onBatteryTest
        (16, "BYPASS"), // emergencyStaticBypass
        (17, "BYPASS"), // staticBypassStandby
        (18, ""), // powerSavingMode
        (19, "OL"), // spotMode
        (20, "OL ECO"), // eConversion
        (21, "OL"), // chargerSpotmode
        (22, "OL"), // inverterSpotmode
        (23, ""), // activeLoad
        (24, "OL"), // batteryDischargeSpotmode
        (25, "OL"), // inverterStandby
        (26, ""), // chargerOnly
        (27, ""), // distributedEnergyReserve
        (28, "OL")); // selfTest

    private static readonly MibLookup CalibrationStatus = Lookup(
        (1, ""), (2, ""), (3, "CAL"), (4, ""), (5, ""), (6, ""));

    private static readonly MibLookup ReplaceBattery = Lookup((1, ""), (2, "RB"));

    private static readonly MibLookup TestResult = Lookup(
        (1, "Ok"), (2, "Failed"), (3, "InvalidTest"), (4, "TestInProgress"));

    private static readonly MibLookup Sensitivity = Lookup((1, "auto"), (2, "low"), (3, "medium"), (4, "high"));

    private static readonly MibLookup TransferReason = Lookup(
        (1, "noTransfer"),
        (2, "highLineVoltage"),
        (3, "brownout"),
        (4, "blackout"),
        (5, "smallMomentarySag"),
        (6, "deepMomentarySag"),
        (7, "smallMomentarySpike"),
        (8, "largeMomentarySpike"),
        (9, "selfTest"),
        (10, "rateOfVoltageChange"));

    public static MibDefinition Definition { get; } = new()
    {
        Name = "apcc",
        DisplayName = "APC PowerNet (apcc)",
        Version = "1.62",

        // NUT compares against upsBasicIdentModel; APC cards announce 1.3.6.1.4.1.318.1.3.x.
        SysObjectIds = ["1.3.6.1.4.1.318"],
        ProbeOid = Apc + "1.1.1.0", // upsBasicIdentModel
        Entries = Build(),
    };

    private static List<MibEntry> Build()
    {
        var e = new List<MibEntry>(SystemGroup());

        e.Add(Fixed("ups.mfr", "APC"));
        e.Add(Text("ups.model", Apc + "1.1.1.0", MibFlags.Static, dfl: "Generic Powernet SNMP device")); // upsBasicIdentModel
        e.Add(Text("ups.serial", Apc + "1.2.3.0", MibFlags.Static)); // upsAdvIdentSerialNumber
        e.Add(Text("ups.mfr.date", Apc + "1.2.2.0", MibFlags.Static)); // upsAdvIdentDateOfManufacture

        e.Add(Num("input.voltage", Apc + "3.3.1.0", 0.1, MibFlags.NegativeInvalid | MibFlags.Unique)); // upsHighPrecInputLineVoltage
        e.Add(Num("input.voltage.maximum", Apc + "3.3.2.0", 0.1, MibFlags.NegativeInvalid | MibFlags.Unique));
        e.Add(Num("input.voltage.minimum", Apc + "3.3.3.0", 0.1, MibFlags.NegativeInvalid | MibFlags.Unique));
        e.Add(Num("input.voltage", Apc + "3.2.1.0")); // upsAdvInputLineVoltage
        e.Add(Num("input.voltage.maximum", Apc + "3.2.2.0"));
        e.Add(Num("input.voltage.minimum", Apc + "3.2.3.0"));
        e.Add(Num("input.phases", Apc + "9.2.2.1.2.1", 1, MibFlags.Static)); // upsPhaseNumInputPhases

        // Three-phase units (upsPhaseInputTable)
        e.Add(Num("input.L1-L2.voltage", Apc + "9.2.3.1.3.1.1.1", 1, MibFlags.NegativeInvalid));
        e.Add(Num("input.L2-L3.voltage", Apc + "9.2.3.1.3.1.1.2", 1, MibFlags.NegativeInvalid));
        e.Add(Num("input.L3-L1.voltage", Apc + "9.2.3.1.3.1.1.3", 1, MibFlags.NegativeInvalid));
        e.Add(Num("input.L1-L2.voltage.maximum", Apc + "9.2.3.1.4.1.1.1", 1, MibFlags.NegativeInvalid));
        e.Add(Num("input.L2-L3.voltage.maximum", Apc + "9.2.3.1.4.1.1.2", 1, MibFlags.NegativeInvalid));
        e.Add(Num("input.L3-L1.voltage.maximum", Apc + "9.2.3.1.4.1.1.3", 1, MibFlags.NegativeInvalid));
        e.Add(Num("input.L1-L2.voltage.minimum", Apc + "9.2.3.1.5.1.1.1", 1, MibFlags.NegativeInvalid));
        e.Add(Num("input.L2-L3.voltage.minimum", Apc + "9.2.3.1.5.1.1.2", 1, MibFlags.NegativeInvalid));
        e.Add(Num("input.L3-L1.voltage.minimum", Apc + "9.2.3.1.5.1.1.3", 1, MibFlags.NegativeInvalid));
        e.Add(Num("input.L1.current", Apc + "9.2.3.1.6.1.1.1", 0.1, MibFlags.NegativeInvalid));
        e.Add(Num("input.L2.current", Apc + "9.2.3.1.6.1.1.2", 0.1, MibFlags.NegativeInvalid));
        e.Add(Num("input.L3.current", Apc + "9.2.3.1.6.1.1.3", 0.1, MibFlags.NegativeInvalid));
        e.Add(Num("input.L1.current.maximum", Apc + "9.2.3.1.7.1.1.1", 0.1, MibFlags.NegativeInvalid));
        e.Add(Num("input.L2.current.maximum", Apc + "9.2.3.1.7.1.1.2", 0.1, MibFlags.NegativeInvalid));
        e.Add(Num("input.L3.current.maximum", Apc + "9.2.3.1.7.1.1.3", 0.1, MibFlags.NegativeInvalid));
        e.Add(Num("input.L1.current.minimum", Apc + "9.2.3.1.8.1.1.1", 0.1, MibFlags.NegativeInvalid));
        e.Add(Num("input.L2.current.minimum", Apc + "9.2.3.1.8.1.1.2", 0.1, MibFlags.NegativeInvalid));
        e.Add(Num("input.L3.current.minimum", Apc + "9.2.3.1.8.1.1.3", 0.1, MibFlags.NegativeInvalid));
        e.Add(Num("input.frequency", Apc + "9.2.2.1.4.1", 0.1, MibFlags.NegativeInvalid | MibFlags.Unique));
        e.Add(Num("input.frequency", Apc + "3.3.4.0", 0.1, MibFlags.NegativeInvalid | MibFlags.Unique)); // upsHighPrecInputFrequency
        e.Add(Num("input.frequency", Apc + "3.2.4.0")); // upsAdvInputFrequency

        e.Add(RwText("input.transfer.low", Apc + "5.2.3.0", 3, setType: SnmpSetType.Integer)); // upsAdvConfigLowTransferVolt
        e.Add(RwText("input.transfer.high", Apc + "5.2.2.0", 3, setType: SnmpSetType.Integer)); // upsAdvConfigHighTransferVolt
        e.Add(Text("input.transfer.reason", Apc + "3.2.5.0", lookup: TransferReason)); // upsAdvInputLineFailCause
        e.Add(RwText("input.sensitivity", Apc + "5.2.7.0", 6, setType: SnmpSetType.Integer, lookup: Sensitivity)); // upsAdvConfigSensitivity

        e.Add(Num("ups.power", Apc + "4.2.9.0")); // upsAdvOutputApparentPower
        e.Add(Num("ups.realpower", Apc + "4.2.8.0")); // upsAdvOutputActivePower

        e.Add(Status(Apc + "4.1.1.0", PowerStatus)); // upsBasicOutputStatus
        e.Add(Status(Apc + "2.1.1.0", BatteryStatus)); // upsBasicBatteryStatus
        e.Add(Status(Apc + "7.2.6.0", CalibrationStatus)); // upsAdvTestCalibrationResults
        e.Add(Status(Apc + "2.2.4.0", ReplaceBattery)); // upsAdvBatteryReplaceIndicator

        e.Add(Num("ups.temperature", Apc + "2.3.2.0", 0.1, MibFlags.Unique)); // upsHighPrecBatteryTemperature
        e.Add(Num("ups.temperature", Apc + "2.2.2.0")); // upsAdvBatteryTemperature
        e.Add(Num("ups.load", Apc + "4.3.3.0", 0.1, MibFlags.NegativeInvalid | MibFlags.Unique)); // upsHighPrecOutputLoad
        e.Add(Num("ups.load", Apc + "4.2.3.0")); // upsAdvOutputLoad
        e.Add(Text("ups.firmware", Apc + "1.2.1.0", MibFlags.Static)); // upsAdvIdentFirmwareRevision

        // NUT gives these TimeTicks objects a multiplier of 3 (a string length in a number column); seconds are right.
        e.Add(RwNum("ups.delay.shutdown", Apc + "5.2.10.0", setType: SnmpSetType.TimeTicks)); // upsAdvConfigShutoffDelay
        e.Add(RwNum("ups.delay.start", Apc + "5.2.9.0", setType: SnmpSetType.TimeTicks)); // upsAdvConfigReturnDelay

        e.Add(Num("battery.charge", Apc + "2.3.1.0", 0.1, MibFlags.NegativeInvalid | MibFlags.Unique)); // upsHighPrecBatteryCapacity
        e.Add(Num("battery.charge", Apc + "2.2.1.0")); // upsAdvBatteryCapacity
        e.Add(RwText("battery.charge.restart", Apc + "5.2.6.0", 3, setType: SnmpSetType.Integer)); // upsAdvConfigMinReturnCapacity
        e.Add(Num("battery.runtime", Apc + "2.2.3.0")); // upsAdvBatteryRunTimeRemaining (TimeTicks)
        e.Add(RwText("battery.runtime.low", Apc + "5.2.8.0", 6)); // upsAdvConfigLowBatteryRunTime (TimeTicks)
        e.Add(Num("battery.voltage", Apc + "2.3.4.0", 0.1, MibFlags.NegativeInvalid | MibFlags.Unique)); // upsHighPrecBatteryActualVoltage
        e.Add(Num("battery.voltage", Apc + "2.2.8.0")); // upsAdvBatteryActualVoltage
        e.Add(Num("battery.voltage.nominal", Apc + "2.2.7.0")); // upsAdvBatteryNominalVoltage
        e.Add(Num("battery.current", Apc + "2.3.5.0", 0.1, MibFlags.Unique)); // upsHighPrecBatteryCurrent
        e.Add(Num("battery.current", Apc + "2.2.9.0")); // upsAdvBatteryCurrent
        e.Add(Num("battery.current.total", Apc + "2.3.6.0", 0.1)); // upsHighPrecTotalDCCurrent
        e.Add(Num("battery.packs", Apc + "2.2.5.0")); // upsAdvBatteryNumOfBattPacks
        e.Add(Num("battery.packs.bad", Apc + "2.2.6.0")); // upsAdvBatteryNumOfBadBattPacks
        e.Add(RwText("battery.date", Apc + "2.1.3.0", 8, MibFlags.Static, SnmpSetType.OctetString)); // upsBasicBatteryLastReplaceDate
        e.Add(RwText("ups.id", Apc + "1.1.2.0", 8, MibFlags.Static, SnmpSetType.OctetString)); // upsBasicIdentName
        e.Add(Text("ups.test.result", Apc + "7.2.3.0", lookup: TestResult)); // upsAdvTestDiagnosticsResults
        e.Add(Text("ups.test.date", Apc + "7.2.4.0", MibFlags.SemiStatic)); // upsAdvTestLastDiagnosticsDate

        e.Add(Num("output.voltage", Apc + "4.3.1.0", 0.1, MibFlags.Unique)); // upsHighPrecOutputVoltage
        e.Add(Num("output.voltage", Apc + "4.2.1.0")); // upsAdvOutputVoltage
        e.Add(Num("output.phases", Apc + "9.3.2.1.2.1", 1, MibFlags.Static)); // upsPhaseNumOutputPhases
        e.Add(Num("output.frequency", Apc + "9.3.2.1.4.1", 0.1, MibFlags.NegativeInvalid | MibFlags.Unique));
        e.Add(Num("output.frequency", Apc + "4.3.2.0", 0.1, MibFlags.NegativeInvalid | MibFlags.Unique)); // upsHighPrecOutputFrequency
        e.Add(Num("output.frequency", Apc + "4.2.2.0")); // upsAdvOutputFrequency
        e.Add(Num("output.current", Apc + "4.3.4.0", 0.1, MibFlags.NegativeInvalid | MibFlags.Unique)); // upsHighPrecOutputCurrent
        e.Add(Num("output.current", Apc + "4.2.4.0")); // upsAdvOutputCurrent

        // Three-phase units (upsPhaseOutputTable)
        e.Add(Num("output.L1-L2.voltage", Apc + "9.3.3.1.3.1.1.1", 1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L2-L3.voltage", Apc + "9.3.3.1.3.1.1.2", 1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L3-L1.voltage", Apc + "9.3.3.1.3.1.1.3", 1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L1.current", Apc + "9.3.3.1.4.1.1.1", 0.1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L2.current", Apc + "9.3.3.1.4.1.1.2", 0.1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L3.current", Apc + "9.3.3.1.4.1.1.3", 0.1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L1.current.maximum", Apc + "9.3.3.1.5.1.1.1", 0.1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L2.current.maximum", Apc + "9.3.3.1.5.1.1.2", 0.1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L3.current.maximum", Apc + "9.3.3.1.5.1.1.3", 0.1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L1.current.minimum", Apc + "9.3.3.1.6.1.1.1", 0.1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L2.current.minimum", Apc + "9.3.3.1.6.1.1.2", 0.1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L3.current.minimum", Apc + "9.3.3.1.6.1.1.3", 0.1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L1.power", Apc + "9.3.3.1.7.1.1.1", 1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L2.power", Apc + "9.3.3.1.7.1.1.2", 1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L3.power", Apc + "9.3.3.1.7.1.1.3", 1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L1.power.maximum", Apc + "9.3.3.1.8.1.1.1", 1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L2.power.maximum", Apc + "9.3.3.1.8.1.1.2", 1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L3.power.maximum", Apc + "9.3.3.1.8.1.1.3", 1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L1.power.minimum", Apc + "9.3.3.1.9.1.1.1", 1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L2.power.minimum", Apc + "9.3.3.1.9.1.1.2", 1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L3.power.minimum", Apc + "9.3.3.1.9.1.1.3", 1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L1.power.percent", Apc + "9.3.3.1.10.1.1.1", 1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L2.power.percent", Apc + "9.3.3.1.10.1.1.2", 1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L3.power.percent", Apc + "9.3.3.1.10.1.1.3", 1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L1.power.maximum.percent", Apc + "9.3.3.1.11.1.1.1", 1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L2.power.maximum.percent", Apc + "9.3.3.1.11.1.1.2", 1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L3.power.maximum.percent", Apc + "9.3.3.1.11.1.1.3", 1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L1.power.minimum.percent", Apc + "9.3.3.1.12.1.1.1", 1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L2.power.minimum.percent", Apc + "9.3.3.1.12.1.1.2", 1, MibFlags.NegativeInvalid));
        e.Add(Num("output.L3.power.minimum.percent", Apc + "9.3.3.1.12.1.1.3", 1, MibFlags.NegativeInvalid));
        e.Add(RwText("output.voltage.nominal", Apc + "5.2.1.0", 3, setType: SnmpSetType.Integer)); // upsAdvConfigRatedOutputVoltage

        // Universal I/O ambient sensor
        e.Add(Num("ambient.temperature", "1.3.6.1.4.1.318.1.1.25.1.2.1.6.1.1", 1, MibFlags.NegativeInvalid));

        // Measure-UPS and environmental sensors (AP9612TH and others)
        e.Add(Num("ambient.temperature", "1.3.6.1.4.1.318.1.1.2.1.1.0"));
        e.Add(Num("ambient.1.temperature.alarm.high", "1.3.6.1.4.1.318.1.1.10.1.2.2.1.3.1"));
        e.Add(Num("ambient.1.temperature.alarm.low", "1.3.6.1.4.1.318.1.1.10.1.2.2.1.4.1"));
        e.Add(Num("ambient.humidity", "1.3.6.1.4.1.318.1.1.2.1.2.0"));
        e.Add(Num("ambient.1.humidity.alarm.high", "1.3.6.1.4.1.318.1.1.10.1.2.2.1.6.1"));
        e.Add(Num("ambient.1.humidity.alarm.low", "1.3.6.1.4.1.318.1.1.10.1.2.2.1.7.1"));

        // Integrated environment monitor probe: the unit object tells whether the reading is in Fahrenheit.
        e.Add(Num("ambient.temperature", "1.3.6.1.4.1.318.1.1.10.2.3.2.1.4.1") with
        {
            FahrenheitUnitOid = "1.3.6.1.4.1.318.1.1.10.2.3.2.1.5.1",
        });
        e.Add(Num("ambient.humidity", "1.3.6.1.4.1.318.1.1.10.2.3.2.1.6.1"));

        // Instant commands
        e.Add(Cmd("load.off", Apc + "6.2.1.0", "2", SnmpSetType.Integer)); // upsAdvControlUpsOff: turnUpsOff
        e.Add(Cmd("load.on", Apc + "6.2.6.0", "2", SnmpSetType.Integer)); // upsAdvControlTurnOnUPS: turnOnUPS
        e.Add(Cmd("shutdown.stayoff", Apc + "6.2.1.0", "3", SnmpSetType.Integer)); // upsAdvControlUpsOff: turnUpsOffGracefully
        e.Add(Cmd("shutdown.return", Apc + "6.1.1.0", "2", SnmpSetType.Integer)); // upsBasicControlConserveBattery: putUpsToSleep
        e.Add(Cmd("shutdown.reboot", Apc + "6.2.2.0", "2", SnmpSetType.Integer)); // upsAdvControlRebootShutdown: rebootShutdownUps
        e.Add(Cmd("shutdown.reboot.graceful", Apc + "6.2.2.0", "3", SnmpSetType.Integer)); // rebootShutdownUpsGracefully
        e.Add(Cmd("test.failure.start", Apc + "6.2.4.0", "2", SnmpSetType.Integer)); // upsAdvControlSimulatePowerFail
        e.Add(Cmd("test.panel.start", Apc + "6.2.5.0", "2", SnmpSetType.Integer)); // upsAdvControlFlashAndBeep
        e.Add(Cmd("bypass.start", Apc + "6.2.7.0", "2", SnmpSetType.Integer)); // upsAdvControlBypassSwitch
        e.Add(Cmd("bypass.stop", Apc + "6.2.7.0", "3", SnmpSetType.Integer));
        e.Add(Cmd("test.battery.start", Apc + "7.2.2.0", "2", SnmpSetType.Integer)); // upsAdvTestDiagnostics
        e.Add(Cmd("calibrate.start", Apc + "7.2.5.0", "2", SnmpSetType.Integer)); // upsAdvTestRuntimeCalibration
        e.Add(Cmd("calibrate.stop", Apc + "7.2.5.0", "3", SnmpSetType.Integer));
        e.Add(Cmd("reset.input.minmax", Apc + "9.1.1.0", "2", SnmpSetType.Integer)); // upsPhaseResetMaxMinValues
        return e;
    }
}
