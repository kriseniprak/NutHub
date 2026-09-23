// The standard usage names come from Network UPS Tools, drivers/libhid.c (hid_usage_lkp), GPL-2.0-or-later,
// which follows "Universal Serial Bus Usage Tables for HID Power Devices" 1.0 (pages 0x84 and 0x85).
namespace NutHub.Drivers.Hid.Descriptors;

/// <summary>
/// Names of HID usages, used to write and read paths such as "UPS.PowerSummary.RemainingCapacity". Each subdriver
/// puts its vendor table in front of the standard one, exactly like NUT's usage_tables_t, so vendor names win.
/// </summary>
internal sealed class HidUsageTable
{
    private readonly (string Name, uint Code)[] _entries;
    private readonly Dictionary<string, uint> _byName;
    private readonly Dictionary<uint, string> _byCode;

    public HidUsageTable(IEnumerable<(string Name, uint Code)> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        _entries = entries.ToArray();
        _byName = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        _byCode = [];
        foreach (var (name, code) in _entries)
        {
            // First entry wins in both directions, as in NUT's linear lookups.
            _byName.TryAdd(name, code);
            _byCode.TryAdd(code, name);
        }
    }

    public static HidUsageTable Empty { get; } = new([]);

    /// <summary>The Power Device (0x84) and Battery System (0x85) usages.</summary>
    public static HidUsageTable Standard { get; } = new(
    [
        ("Undefined", 0x00840000),
        ("iName", 0x00840001),
        ("PresentStatus", 0x00840002),
        ("ChangedStatus", 0x00840003),
        ("UPS", 0x00840004),
        ("PowerSupply", 0x00840005),
        ("BatterySystem", 0x00840010),
        ("BatterySystemID", 0x00840011),
        ("Battery", 0x00840012),
        ("BatteryID", 0x00840013),
        ("Charger", 0x00840014),
        ("ChargerID", 0x00840015),
        ("PowerConverter", 0x00840016),
        ("PowerConverterID", 0x00840017),
        ("OutletSystem", 0x00840018),
        ("OutletSystemID", 0x00840019),
        ("Input", 0x0084001a),
        ("InputID", 0x0084001b),
        ("Output", 0x0084001c),
        ("OutputID", 0x0084001d),
        ("Flow", 0x0084001e),
        ("FlowID", 0x0084001f),
        ("Outlet", 0x00840020),
        ("OutletID", 0x00840021),
        ("Gang", 0x00840022),
        ("GangID", 0x00840023),
        ("PowerSummary", 0x00840024),
        ("PowerSummaryID", 0x00840025),
        ("Voltage", 0x00840030),
        ("Current", 0x00840031),
        ("Frequency", 0x00840032),
        ("ApparentPower", 0x00840033),
        ("ActivePower", 0x00840034),
        ("PercentLoad", 0x00840035),
        ("Temperature", 0x00840036),
        ("Humidity", 0x00840037),
        ("BadCount", 0x00840038),
        ("ConfigVoltage", 0x00840040),
        ("ConfigCurrent", 0x00840041),
        ("ConfigFrequency", 0x00840042),
        ("ConfigApparentPower", 0x00840043),
        ("ConfigActivePower", 0x00840044),
        ("ConfigPercentLoad", 0x00840045),
        ("ConfigTemperature", 0x00840046),
        ("ConfigHumidity", 0x00840047),
        ("SwitchOnControl", 0x00840050),
        ("SwitchOffControl", 0x00840051),
        ("ToggleControl", 0x00840052),
        ("LowVoltageTransfer", 0x00840053),
        ("HighVoltageTransfer", 0x00840054),
        ("DelayBeforeReboot", 0x00840055),
        ("DelayBeforeStartup", 0x00840056),
        ("DelayBeforeShutdown", 0x00840057),
        ("Test", 0x00840058),
        ("ModuleReset", 0x00840059),
        ("AudibleAlarmControl", 0x0084005a),
        ("Present", 0x00840060),
        ("Good", 0x00840061),
        ("InternalFailure", 0x00840062),
        ("VoltageOutOfRange", 0x00840063),
        ("FrequencyOutOfRange", 0x00840064),
        ("Overload", 0x00840065),
        ("OverCharged", 0x00840066),
        ("OverTemperature", 0x00840067),
        ("ShutdownRequested", 0x00840068),
        ("ShutdownImminent", 0x00840069),
        ("SwitchOn/Off", 0x0084006b),
        ("Switchable", 0x0084006c),
        ("Used", 0x0084006d),
        ("Boost", 0x0084006e),
        ("Buck", 0x0084006f),
        ("Initialized", 0x00840070),
        ("Tested", 0x00840071),
        ("AwaitingPower", 0x00840072),
        ("CommunicationLost", 0x00840073),
        ("iManufacturer", 0x008400fd),
        ("iProduct", 0x008400fe),
        ("iSerialNumber", 0x008400ff),
        ("Undefined", 0x00850000),
        ("SMBBatteryMode", 0x00850001),
        ("SMBBatteryStatus", 0x00850002),
        ("SMBAlarmWarning", 0x00850003),
        ("SMBChargerMode", 0x00850004),
        ("SMBChargerStatus", 0x00850005),
        ("SMBChargerSpecInfo", 0x00850006),
        ("SMBSelectorState", 0x00850007),
        ("SMBSelectorPresets", 0x00850008),
        ("SMBSelectorInfo", 0x00850009),
        ("OptionalMfgFunction1", 0x00850010),
        ("OptionalMfgFunction2", 0x00850011),
        ("OptionalMfgFunction3", 0x00850012),
        ("OptionalMfgFunction4", 0x00850013),
        ("OptionalMfgFunction5", 0x00850014),
        ("ConnectionToSMBus", 0x00850015),
        ("OutputConnection", 0x00850016),
        ("ChargerConnection", 0x00850017),
        ("BatteryInsertion", 0x00850018),
        ("Usenext", 0x00850019),
        ("OKToUse", 0x0085001a),
        ("BatterySupported", 0x0085001b),
        ("SelectorRevision", 0x0085001c),
        ("ChargingIndicator", 0x0085001d),
        ("ManufacturerAccess", 0x00850028),
        ("RemainingCapacityLimit", 0x00850029),
        ("RemainingTimeLimit", 0x0085002a),
        ("AtRate", 0x0085002b),
        ("CapacityMode", 0x0085002c),
        ("BroadcastToCharger", 0x0085002d),
        ("PrimaryBattery", 0x0085002e),
        ("ChargeController", 0x0085002f),
        ("TerminateCharge", 0x00850040),
        ("TerminateDischarge", 0x00850041),
        ("BelowRemainingCapacityLimit", 0x00850042),
        ("RemainingTimeLimitExpired", 0x00850043),
        ("Charging", 0x00850044),
        ("Discharging", 0x00850045),
        ("FullyCharged", 0x00850046),
        ("FullyDischarged", 0x00850047),
        ("ConditioningFlag", 0x00850048),
        ("AtRateOK", 0x00850049),
        ("SMBErrorCode", 0x0085004a),
        ("NeedReplacement", 0x0085004b),
        ("AtRateTimeToFull", 0x00850060),
        ("AtRateTimeToEmpty", 0x00850061),
        ("AverageCurrent", 0x00850062),
        ("Maxerror", 0x00850063),
        ("RelativeStateOfCharge", 0x00850064),
        ("AbsoluteStateOfCharge", 0x00850065),
        ("RemainingCapacity", 0x00850066),
        ("FullChargeCapacity", 0x00850067),
        ("RunTimeToEmpty", 0x00850068),
        ("AverageTimeToEmpty", 0x00850069),
        ("AverageTimeToFull", 0x0085006a),
        ("CycleCount", 0x0085006b),
        ("BattPackModelLevel", 0x00850080),
        ("InternalChargeController", 0x00850081),
        ("PrimaryBatterySupport", 0x00850082),
        ("DesignCapacity", 0x00850083),
        ("SpecificationInfo", 0x00850084),
        ("ManufacturerDate", 0x00850085),
        ("SerialNumber", 0x00850086),
        ("iManufacturerName", 0x00850087),
        ("iDevicename", 0x00850088),
        ("iDeviceChemistry", 0x00850089),
        ("ManufacturerData", 0x0085008a),
        ("Rechargeable", 0x0085008b),
        ("WarningCapacityLimit", 0x0085008c),
        ("CapacityGranularity1", 0x0085008d),
        ("CapacityGranularity2", 0x0085008e),
        ("iOEMInformation", 0x0085008f),
        ("InhibitCharge", 0x008500c0),
        ("EnablePolling", 0x008500c1),
        ("ResetToZero", 0x008500c2),
        ("ACPresent", 0x008500d0),
        ("BatteryPresent", 0x008500d1),
        ("PowerFail", 0x008500d2),
        ("AlarmInhibited", 0x008500d3),
        ("ThermistorUnderRange", 0x008500d4),
        ("ThermistorHot", 0x008500d5),
        ("ThermistorCold", 0x008500d6),
        ("ThermistorOverRange", 0x008500d7),
        ("VoltageOutOfRange", 0x008500d8),
        ("CurrentOutOfRange", 0x008500d9),
        ("CurrentNotRegulated", 0x008500da),
        ("VoltageNotRegulated", 0x008500db),
        ("MasterMode", 0x008500dc),
        ("ChargerSelectorSupport", 0x008500f0),
        ("ChargerSpec", 0x008500f1),
        ("Level2", 0x008500f2),
        ("Level3", 0x008500f3),
    ]);

    /// <summary>A table that looks names up in <paramref name="first"/>, then in <paramref name="then"/>.</summary>
    public static HidUsageTable Combine(HidUsageTable first, HidUsageTable then)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(then);
        return new HidUsageTable(first._entries.Concat(then._entries));
    }

    /// <summary>Case-insensitive, like NUT's strcasecmp lookup.</summary>
    public bool TryGetCode(string name, out uint code) => _byName.TryGetValue(name, out code);

    public string? GetName(uint code) => _byCode.GetValueOrDefault(code);
}
