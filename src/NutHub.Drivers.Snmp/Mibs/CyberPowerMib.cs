using static NutHub.Drivers.Snmp.Mibs.Mib;

namespace NutHub.Drivers.Snmp.Mibs;

/// <summary>
/// CyberPower CPS-MIB (RMCARD network cards).
/// Mapping from NUT drivers/cyberpower-mib.c (version 0.56), which registers it for both sysObjectIDs CyberPower
/// cards use. The power status "unknown" produces no token (NUT's table has the text "NULL" there).
/// </summary>
internal static class CyberPowerMib
{
    private const string Cps = "1.3.6.1.4.1.3808.1.1.1.";

    private static readonly MibLookup PowerStatus = Lookup(
        (2, "OL"), // onLine
        (3, "OB"), // onBattery
        (4, "OL BOOST"), // onBoost
        (5, "OFF"), // onSleep
        (6, "OFF"), // off
        (7, "OL"), // rebooting
        (8, "OL"), // onECO
        (9, "OL BYPASS"), // onBypass
        (10, "OL TRIM"), // onBuck
        (11, "OL OVER"), // onOverload
        (1, "")); // unknown

    private static readonly MibLookup BatteryStatus = Lookup((1, ""), (2, ""), (3, "LB"));

    private static readonly MibLookup CalibrationStatus = Lookup((1, ""), (2, ""), (3, "CAL"));

    private static readonly MibLookup ReplaceBattery = Lookup((1, ""), (2, "RB"));

    private static readonly MibLookup UpsAlarm = Lookup(
        (1, ""), // normal
        (2, "Temperature too high!"), // overheat
        (3, "Internal UPS fault!")); // hardwareFault

    private static readonly MibLookup TransferReason = Lookup(
        (1, "noTransfer"), (2, "highLineVoltage"), (3, "brownout"), (4, "selfTest"));

    private static readonly MibLookup TestResult = Lookup(
        (1, "Ok"), (2, "Failed"), (3, "InvalidTest"), (4, "TestInProgress"));

    public static MibDefinition Definition { get; } = new()
    {
        Name = "cyberpower",
        DisplayName = "CyberPower (cyberpower)",
        Version = "0.56",

        // CPS-MIB::ups, and the bare vendor OID some cards announce (NUT issue 1997).
        SysObjectIds = ["1.3.6.1.4.1.3808.1.1.1", "1.3.6.1.4.1.3808"],
        ProbeOid = Cps + "1.1.1.0", // upsBaseIdentModel
        Entries = Build(),
    };

    private static List<MibEntry> Build()
    {
        var e = new List<MibEntry>(SystemGroup());

        e.Add(Fixed("device.type", "ups"));
        e.Add(Fixed("ups.mfr", "CYBERPOWER"));
        e.Add(Text("ups.model", Cps + "1.1.1.0", MibFlags.Static, dfl: "CyberPower")); // upsBaseIdentModel
        e.Add(RwText("ups.id", Cps + "1.1.2.0", 8, MibFlags.Static, SnmpSetType.OctetString)); // upsBaseIdentName
        e.Add(Text("ups.serial", Cps + "1.2.3.0", MibFlags.Static)); // upsAdvanceIdentSerialNumber
        e.Add(Text("ups.firmware", Cps + "1.2.1.0", MibFlags.Static)); // upsAdvanceIdentFirmwareRevision
        e.Add(Text("ups.mfr.date", Cps + "1.2.2.0")); // upsAdvanceIdentDateOfManufacture

        e.Add(Status(Cps + "4.1.1.0", PowerStatus)); // upsBaseOutputStatus
        e.Add(Status(Cps + "2.1.1.0", BatteryStatus)); // upsBaseBatteryStatus
        e.Add(Status(Cps + "7.2.7.0", CalibrationStatus)); // upsAdvanceTestCalibrationResults
        e.Add(Status(Cps + "2.2.5.0", ReplaceBattery)); // upsAdvanceBatteryReplaceIndicator

        e.Add(Num("ups.load", Cps + "4.2.3.0")); // upsAdvanceOutputLoad
        e.Add(Num("ups.realpower", Cps + "4.2.5.0")); // upsAdvanceOutputPower
        e.Add(Num("ups.temperature", Cps + "10.2.0")); // upsEnvTemperature
        e.Add(Alarm("ups.alarm", Cps + "10.1.0", UpsAlarm)); // upsAdvanceStateSystemMessages

        e.Add(Num("battery.runtime", Cps + "2.2.4.0")); // upsAdvanceBatteryRunTimeRemaining (TimeTicks)
        e.Add(Num("battery.runtime.elapsed", Cps + "2.1.2.0")); // upsBaseBatteryTimeOnBattery
        e.Add(Num("battery.voltage", "1.3.6.1.2.1.33.1.2.5.0", 0.1)); // upsBatteryVoltage
        e.Add(Num("battery.voltage", Cps + "2.2.2.0", 0.1)); // upsAdvanceBatteryVoltage
        e.Add(Num("battery.voltage.nominal", Cps + "2.2.8.0")); // upsAdvanceBatteryNominalVoltage
        e.Add(Num("battery.current", Cps + "4.2.4.0", 0.1));
        e.Add(Num("battery.current", Cps + "2.2.7.0", 0.1)); // upsAdvanceBatteryCurrent
        e.Add(Num("battery.charge", Cps + "2.2.1.0")); // upsAdvanceBatteryCapacity
        e.Add(Num("battery.temperature", Cps + "2.2.3.0")); // upsAdvanceBatteryTemperature
        e.Add(Text("battery.date", Cps + "2.1.3.0", MibFlags.SemiStatic)); // upsBaseBatteryLastReplaceDate

        e.Add(Num("input.voltage", Cps + "3.2.1.0", 0.1)); // upsAdvanceInputLineVoltage
        e.Add(Num("input.frequency", Cps + "3.2.4.0", 0.1)); // upsAdvanceInputFrequency
        e.Add(Text("input.transfer.reason", Cps + "3.2.5.0", lookup: TransferReason)); // upsAdvanceInputLineFailCause
        e.Add(Num("output.voltage", Cps + "4.2.1.0", 0.1)); // upsAdvanceOutputVoltage
        e.Add(Num("output.frequency", Cps + "4.2.2.0", 0.1)); // upsAdvanceOutputFrequency
        e.Add(Num("output.current", Cps + "4.2.4.0", 0.1)); // upsAdvanceOutputCurrent

        e.Add(RwNum("ups.delay.start", Cps + "5.2.9.0", setType: SnmpSetType.TimeTicks)); // upsAdvanceConfigReturnDelay
        e.Add(Fixed("ups.delay.reboot", "0"));
        e.Add(RwNum("ups.delay.shutdown", Cps + "5.2.11.0", setType: SnmpSetType.TimeTicks)); // upsAdvanceConfigSleepDelay

        e.Add(Cmd("load.off", Cps + "6.2.1.0", "2", SnmpSetType.Integer)); // upsAdvanceControlUpsOff: turnUpsOff
        e.Add(Cmd("load.on", Cps + "6.2.6.0", "2", SnmpSetType.Integer)); // upsAdvanceControlTurnOnUPS
        e.Add(Cmd("shutdown.stayoff", Cps + "6.2.6.0", "3", SnmpSetType.Integer));
        e.Add(Cmd("shutdown.return", Cps + "6.2.3.0", "3", SnmpSetType.Integer)); // upsAdvanceControlUpsSleep
        e.Add(Cmd("test.failure.start", Cps + "6.2.4.0", "2", SnmpSetType.Integer)); // upsAdvanceControlSimulatePowerFail
        e.Add(Cmd("test.panel.start", Cps + "7.2.5.0", "2", SnmpSetType.Integer)); // upsAdvanceTestIndicators
        e.Add(Cmd("test.battery.start", Cps + "7.2.2.0", "2", SnmpSetType.Integer)); // upsAdvanceTestDiagnostics
        e.Add(Cmd("calibrate.start", Cps + "7.2.6.0", "2", SnmpSetType.Integer)); // upsAdvanceTestRuntimeCalibration
        e.Add(Cmd("calibrate.stop", Cps + "7.2.6.0", "3", SnmpSetType.Integer));
        e.Add(Text("ups.test.date", Cps + "7.2.4.0", MibFlags.SemiStatic)); // upsAdvanceTestLastDiagnosticsDate
        e.Add(Text("ups.test.result", Cps + "7.2.3.0", lookup: TestResult)); // upsAdvanceTestDiagnosticsResults
        return e;
    }
}
