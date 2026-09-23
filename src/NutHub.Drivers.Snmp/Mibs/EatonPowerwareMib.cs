using static NutHub.Drivers.Snmp.Mibs.Mib;

namespace NutHub.Drivers.Snmp.Mibs;

/// <summary>
/// Eaton / Powerware XUPS-MIB (ConnectUPS, Network-M2 and Gigabit network cards).
/// Mapping from NUT drivers/eaton-ups-pwnm2-mib.c (version 0.106; "pw" in older NUT releases, "eaton_pw_nm2" now),
/// including its alarm table. Differences: the scalar command objects NUT writes without their ".0" instance
/// (xupsTestBattery, xupsLoadShedAndRestart) are written with it, per the MIB; per-outlet templates are not mapped.
/// </summary>
internal static class EatonPowerwareMib
{
    private const string Pw = "1.3.6.1.4.1.534.1.";
    private const string Ietf = "1.3.6.1.2.1.33.1.";

    private static readonly MibLookup AlarmOnBattery = Lookup((1, "OB"), (2, ""));
    private static readonly MibLookup AlarmLowBattery = Lookup((1, "LB"), (2, ""));

    private static readonly MibLookup PowerStatus = Lookup(
        (1, ""), // other
        (2, "OFF"), // none
        (3, "OL"), // normal
        (4, "BYPASS"), // bypass
        (5, "OB"), // battery
        (6, "OL BOOST"), // booster
        (7, "OL TRIM"), // reducer
        (8, "OL"), // parallelCapacity
        (9, "OL"), // parallelRedundant
        (10, "OL"), // highEfficiencyMode
        (11, "BYPASS"), // maintenanceBypass (listed twice in NUT; the first wins)
        (11, "OL"));

    private static readonly MibLookup Topology = Lookup(
        (0x0000, ""),
        (0x0010, "Off-line switcher, Single Phase"),
        (0x0020, "Line-Interactive UPS, Single Phase"),
        (0x0021, "Line-Interactive UPS, Two Phase"),
        (0x0022, "Line-Interactive UPS, Three Phase"),
        (0x0030, "Dual AC Input, On-Line UPS, Single Phase"),
        (0x0031, "Dual AC Input, On-Line UPS, Two Phase"),
        (0x0032, "Dual AC Input, On-Line UPS, Three Phase"),
        (0x0040, "On-Line UPS, Single Phase"),
        (0x0041, "On-Line UPS, Two Phase"),
        (0x0042, "On-Line UPS, Three Phase"),
        (0x0050, "Parallel Redundant On-Line UPS, Single Phase"),
        (0x0051, "Parallel Redundant On-Line UPS, Two Phase"),
        (0x0052, "Parallel Redundant On-Line UPS, Three Phase"),
        (0x0060, "Parallel for Capacity On-Line UPS, Single Phase"),
        (0x0061, "Parallel for Capacity On-Line UPS, Two Phase"),
        (0x0062, "Parallel for Capacity On-Line UPS, Three Phase"),
        (0x0102, "System Bypass Module, Three Phase"),
        (0x0122, "Hot-Tie Cabinet, Three Phase"),
        (0x0200, "Outlet Controller, Single Phase"),
        (0x0222, "Dual AC Input Static Switch Module, 3 Phase"));

    private static readonly MibLookup BatteryAbmStatus = Lookup((1, "CHRG"), (2, "DISCHRG"));

    private static readonly MibLookup AbmStatusText = Lookup(
        (1, "charging"), (2, "discharging"), (3, "floating"), (4, "resting"), (5, "unknown"), (6, "disabled"));

    private static readonly MibLookup BatteryTest = Lookup(
        (1, "Unknown"),
        (2, "Done and passed"),
        (3, "Done and error"),
        (4, "In progress"),
        (5, "Not supported"),
        (6, "Inhibited"),
        (7, "Scheduled"));

    private static readonly MibLookup YesNo = Lookup((1, "yes"), (2, "no"));

    private static readonly MibAlarmTable Alarms = new()
    {
        CountOid = Pw + "7.1.0", // xupsAlarms
        DescriptionColumnOid = Pw + "7.2.1.2", // xupsAlarmDescr
        Alarms =
        [
            new(Pw + "7.4", "LB", null),
            new(Pw + "7.7", "OVER", "Output overload!"),
            new(Pw + "7.8", null, "Internal failure!"),
            new(Pw + "7.9", null, "Battery discharged!"),
            new(Pw + "7.10", null, "Inverter failure!"),
            new(Pw + "7.11", "BYPASS", "On bypass!"),
            new(Pw + "7.12", null, "Bypass not available!"),
            new(Pw + "7.13", "OFF", "Output off!"),
            new(Pw + "7.14", null, "Input failure!"),
            new(Pw + "7.15", null, "Building alarm!"),
            new(Pw + "7.16", null, "Shutdown imminent!"),
            new(Pw + "7.17", null, "On inverter!"),
            new(Pw + "7.20", null, "Breaker open!"),
            new(Pw + "7.23", "RB", "Battery bad!"),
            new(Pw + "7.24", "OFF", "Output off as requested!"),
            new(Pw + "7.25", null, "Diagnostic test failure!"),
            new(Pw + "7.26", null, "Communication with UPS lost!"),
            new(Pw + "7.27", null, "Shutdown pending!"),
            new(Pw + "7.29", null, "Bad ambient temperature!"),
            new(Pw + "7.30", null, "Redundancy lost!"),
            new(Pw + "7.31", null, "Bad temperature!"),
            new(Pw + "7.32", null, "Charger failure!"),
            new(Pw + "7.33", null, "Fan failure!"),
            new(Pw + "7.34", null, "Fuse failure!"),
            new(Pw + "7.35", null, "Powerswitch failure!"),
            new(Pw + "7.36", null, "Parallel or composite module failure!"),
            new(Pw + "7.37", null, "Using alternative power source!"),
            new(Pw + "7.38", null, "Alternative power source unavailable!"),
            new(Pw + "7.40", null, "Bad remote temperature!"),
            new(Pw + "7.41", null, "Bad remote humidity!"),
            new(Pw + "7.42", null, "Bad output condition!"),
            new(Pw + "7.43", null, "Awaiting power!"),
            new(Pw + "7.44", "BYPASS", "On maintenance bypass!"),
        ],
    };

    public static MibDefinition Definition { get; } = new()
    {
        Name = "pw",
        Aliases = ["eaton_pw_nm2"],
        DisplayName = "Eaton / Powerware XUPS-MIB (pw)",
        Version = "0.106",
        SysObjectIds = ["1.3.6.1.4.1.534"],
        ProbeOid = Pw + "1.2.0", // xupsIdentModel
        Entries = Build(),
        AlarmTable = Alarms,
    };

    private static List<MibEntry> Build()
    {
        var e = new List<MibEntry>(SystemGroup());

        e.Add(Text("ups.mfr", Pw + "1.1.0", MibFlags.Static)); // xupsIdentManufacturer
        e.Add(Text("ups.model", Pw + "1.2.0", MibFlags.Static)); // xupsIdentModel
        e.Add(Text("ups.firmware", Pw + "1.3.0")); // xupsIdentSoftwareVersion
        e.Add(Text("ups.firmware.aux", Ietf + "1.4.0")); // upsIdentAgentSoftwareVersion
        e.Add(Text("ups.serial", Ietf + "1.5.0", MibFlags.Static)); // upsIdentName, where Eaton cards put the serial
        e.Add(Num("ups.load", Pw + "4.1.0")); // xupsOutputLoad
        e.Add(Num("ups.power", Pw + "4.4.1.4.1")); // xupsOutputWatts.1
        e.Add(Num("ups.power", Pw + "4.4.1.4.1.0")); // the same, as some cards index it

        e.Add(Status(Pw + "4.5.0", PowerStatus)); // xupsOutputSource
        e.Add(Status(Pw + "7.3", AlarmOnBattery)); // xupsOnBattery (NUT reads the well-known alarm itself)
        e.Add(Status(Pw + "7.4", AlarmLowBattery)); // xupsLowBattery
        e.Add(Status(Pw + "2.5.0", BatteryAbmStatus)); // xupsBatteryAbmStatus

        e.Add(Text("ups.type", Pw + "13.1.0", MibFlags.Static, Topology)); // xupsTopologyType
        e.Add(Num("ups.realpower.nominal", Pw + "10.3.0")); // xupsConfigOutputWatts
        e.Add(Num("ups.power.nominal", Ietf + "9.5.0")); // upsConfigOutputVA
        e.Add(Num("ups.temperature", Pw + "6.1.0")); // xupsEnvAmbientTemp
        e.Add(RwNum("ups.temperature.low", Pw + "6.2.0")); // xupsEnvAmbientLowerLimit
        e.Add(RwNum("ups.temperature.high", Pw + "6.3.0")); // xupsEnvAmbientUpperLimit
        e.Add(Text("ups.test.result", Pw + "8.2.0", lookup: BatteryTest)); // xupsTestBatteryStatus
        e.Add(RwText("ups.start.auto", Ietf + "8.5.0", 3, setType: SnmpSetType.Integer, lookup: YesNo)); // upsAutoRestart
        e.Add(Text("battery.charger.status", Pw + "2.5.0", lookup: AbmStatusText)); // xupsBatteryAbmStatus

        e.Add(Num("battery.charge", Pw + "2.4.0")); // xupsBatCapacity
        e.Add(Num("battery.runtime", Pw + "2.1.0")); // xupsBatTimeRemaining
        e.Add(Num("battery.voltage", Pw + "2.2.0")); // xupsBatVoltage
        e.Add(Num("battery.current", Pw + "2.3.0", 0.1)); // xupsBatCurrent
        e.Add(Num("battery.runtime.low", Ietf + "9.7.0", 60)); // upsConfigLowBattTime
        e.Add(Text("battery.date", Pw + "2.6.0", converter: MibConverter.UsDateToIso)); // xupsBatteryLastReplacedDate

        e.Add(Num("output.phases", Pw + "4.3.0")); // xupsOutputNumPhases
        e.Add(Num("output.frequency", Pw + "4.2.0", 0.1)); // xupsOutputFrequency
        e.Add(Num("output.frequency.nominal", Pw + "10.4.0", 0.1)); // xupsConfigOutputFreq
        e.Add(Num("output.voltage", Pw + "4.4.1.2.1", 1, MibFlags.Output1)); // xupsOutputVoltage.1
        e.Add(Num("output.voltage", Pw + "4.4.1.2.1.0", 1, MibFlags.Output1));
        e.Add(Num("output.voltage.nominal", Pw + "10.1.0")); // xupsConfigOutputVoltage
        e.Add(Num("output.voltage.low", Pw + "10.6.0")); // xupsConfigLowOutputVoltageLimit
        e.Add(Num("output.voltage.high", Pw + "10.7.0")); // xupsConfigHighOutputVoltageLimit
        e.Add(Num("output.current", Pw + "4.4.1.3.1", 1, MibFlags.Output1)); // xupsOutputCurrent.1
        e.Add(Num("output.current", Pw + "4.4.1.3.1.0", 1, MibFlags.Output1));
        e.Add(Num("output.realpower", Pw + "4.4.1.4.1", 1, MibFlags.Output1)); // xupsOutputWatts.1
        e.Add(Num("output.realpower", Pw + "4.4.1.4.1.0"));
        e.Add(Num("output.realpower.nominal", Pw + "10.3.0")); // xupsConfigOutputWatts
        e.Add(Num("output.L1-N.voltage", Pw + "4.4.1.2.1", 1, MibFlags.Output3));
        e.Add(Num("output.L2-N.voltage", Pw + "4.4.1.2.2", 1, MibFlags.Output3));
        e.Add(Num("output.L3-N.voltage", Pw + "4.4.1.2.3", 1, MibFlags.Output3));
        e.Add(Num("output.L1.current", Pw + "4.4.1.3.1", 1, MibFlags.Output3));
        e.Add(Num("output.L2.current", Pw + "4.4.1.3.2", 1, MibFlags.Output3));
        e.Add(Num("output.L3.current", Pw + "4.4.1.3.3", 1, MibFlags.Output3));
        e.Add(Num("output.L1.realpower", Pw + "4.4.1.4.1", 1, MibFlags.Output3));
        e.Add(Num("output.L2.realpower", Pw + "4.4.1.4.2", 1, MibFlags.Output3));
        e.Add(Num("output.L3.realpower", Pw + "4.4.1.4.3", 1, MibFlags.Output3));
        e.Add(Num("output.L1.power.percent", Ietf + "4.4.1.5.1", 1, MibFlags.Output3)); // upsOutputPercentLoad
        e.Add(Num("output.L2.power.percent", Ietf + "4.4.1.5.2", 1, MibFlags.Output3));
        e.Add(Num("output.L3.power.percent", Ietf + "4.4.1.5.3", 1, MibFlags.Output3));

        e.Add(Num("input.phases", Pw + "3.3.0")); // xupsInputNumPhases
        e.Add(Num("input.frequency", Pw + "3.1.0", 0.1)); // xupsInputFrequency
        e.Add(Num("input.voltage", Pw + "3.4.1.2.0", 1, MibFlags.Input1)); // xupsInputVoltage
        e.Add(Num("input.voltage", Pw + "3.4.1.2.1", 1, MibFlags.Input1));
        e.Add(Num("input.voltage.nominal", Pw + "10.2.0")); // xupsConfigInputVoltage
        e.Add(Num("input.current", Pw + "3.4.1.3.0", 0.1, MibFlags.Input1)); // xupsInputCurrent
        e.Add(Num("input.L1-N.voltage", Pw + "3.4.1.2.1", 1, MibFlags.Input3));
        e.Add(Num("input.L2-N.voltage", Pw + "3.4.1.2.2", 1, MibFlags.Input3));
        e.Add(Num("input.L3-N.voltage", Pw + "3.4.1.2.3", 1, MibFlags.Input3));
        e.Add(Num("input.L1.current", Pw + "3.4.1.3.1", 1, MibFlags.Input3));
        e.Add(Num("input.L2.current", Pw + "3.4.1.3.2", 1, MibFlags.Input3));
        e.Add(Num("input.L3.current", Pw + "3.4.1.3.3", 1, MibFlags.Input3));
        e.Add(Num("input.L1.realpower", Pw + "3.4.1.4.1", 1, MibFlags.Input3));
        e.Add(Num("input.L2.realpower", Pw + "3.4.1.4.2", 1, MibFlags.Input3));
        e.Add(Num("input.L3.realpower", Pw + "3.4.1.4.3", 1, MibFlags.Input3));
        e.Add(Num("input.quality", Pw + "3.2.0")); // xupsInputLineBads

        e.Add(Num("input.bypass.frequency", Pw + "5.1.0", 0.1)); // xupsBypassFrequency
        e.Add(Num("input.bypass.voltage", Pw + "5.3.1.2.0", 1, MibFlags.Input1)); // xupsBypassVoltage
        e.Add(Num("input.bypass.voltage", Pw + "5.3.1.2.1.0", 1, MibFlags.Input1));
        e.Add(Num("input.bypass.L1-N.voltage", Pw + "5.3.1.2.1", 1, MibFlags.Input3));
        e.Add(Num("input.bypass.L2-N.voltage", Pw + "5.3.1.2.2", 1, MibFlags.Input3));
        e.Add(Num("input.bypass.L3-N.voltage", Pw + "5.3.1.2.3", 1, MibFlags.Input3));

        e.Add(Fixed("outlet.id", "0")); // the main outlet
        e.Add(Text("outlet.switchable", Pw + "9.7.0", MibFlags.Static, YesNo)); // xupsSwitchable
        e.Add(Num("outlet.count", Pw + "12.1.0", 1, MibFlags.Static)); // xupsNumReceptacles

        // xupsControlOutputOffDelay / xupsControlOutputOnDelay count seconds; NUT's defaults for the delayed forms.
        e.Add(Setting("ups.delay.shutdown", "30"));
        e.Add(Setting("ups.delay.start", "20"));

        e.Add(Cmd("test.battery.start.quick", Pw + "8.1.0", "1", SnmpSetType.Integer)); // xupsTestBattery: startTest
        e.Add(Cmd("shutdown.return", Pw + "9.6.0", "0", SnmpSetType.Integer)); // xupsLoadShedAndRestart
        e.Add(Cmd("shutdown.stop", Pw + "9.1.0", "0", SnmpSetType.Integer)); // xupsControlOutputOffDelay
        e.Add(Cmd("load.off", Pw + "9.1.0", "1", SnmpSetType.Integer));
        e.Add(Cmd("load.off.delay", Pw + "9.1.0", null, SnmpSetType.Integer));
        e.Add(Cmd("load.on", Pw + "9.2.0", "1", SnmpSetType.Integer)); // xupsControlOutputOnDelay
        e.Add(Cmd("load.on.delay", Pw + "9.2.0", null, SnmpSetType.Integer));
        return e;
    }
}
