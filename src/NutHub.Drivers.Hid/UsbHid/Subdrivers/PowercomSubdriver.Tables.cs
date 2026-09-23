// Mapping tables ported from Network UPS Tools, drivers/powercom-hid.c (PowerCOM HID 0.76),
// GPL-2.0-or-later. Keep them in sync with that file rather than editing entries by hand.
using NutHub.Drivers.Hid.Descriptors;
using static NutHub.Drivers.Hid.UsbHid.HidMapFlags;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

internal sealed partial class PowercomSubdriver
{
    /// <summary>The NUT subdriver this one is ported from, reported as driver.version.data.</summary>
    public override string Version => "PowerCOM HID 0.76";

    /// <summary>Vendor-specific usage names (NUT powercom_usage_lkp).</summary>
    private static readonly HidUsageTable VendorUsageTable = new(
    [
        ("PowercomUPS", 0x00020004),
        ("PowercomBatterySystem", 0x00020010),
        ("PowercomPowerConverter", 0x00020016),
        ("PowercomInput", 0x0002001a),
        ("PowercomOutput", 0x0002001c),
        ("PowercomVoltage", 0x00020030),
        ("PowercomFrequency", 0x00020032),
        ("PowercomPercentLoad", 0x00020035),
        ("PowercomTemperature", 0x00020036),
        ("PowercomDelayBeforeStartup", 0x00020056),
        ("PowercomDelayBeforeShutdown", 0x00020057),
        ("PowercomTest", 0x00020058),
        ("PowercomShutdownRequested", 0x00020068),
        ("PowercomInternalChargeController", 0x00020081),
        ("PowercomPrimaryBatterySupport", 0x00020082),
        ("PowercomDesignCapacity", 0x00020083),
        ("PowercomSpecificationInfo", 0x00020084),
        ("PowercomManufacturerDate", 0x00020085),
        ("PowercomSerialNumber", 0x00020086),
        ("PowercomManufacturerName", 0x00020087),
        ("POWERCOM1", 0x0084002f),
        ("POWERCOM2", 0xff860060),
        ("POWERCOM3", 0xff860080),
        ("PCMDelayBeforeStartup", 0x00ff0056),
        ("PCMDelayBeforeShutdown", 0x00ff0057),
    ]);

    /// <summary>The USB ids this subdriver supports (NUT powercom_usb_device_table).</summary>
    private static readonly UsbDeviceId[] DeviceIds =
    [
        new(0x0d9f, 0x00a2),
        new(0x0d9f, 0x00a3),
        new(0x0d9f, 0x00a4),
        new(0x0d9f, 0x00a5),
        new(0x0d9f, 0x00a6),
        new(0x0d9f, 0x0004),
        new(0x0d9f, 0x0001),
    ];

    private static readonly NutLookup PowercomBeeperInfo = NutLookup.Table([(1, "enabled"), (2, "disabled")]);

    /// <summary>Builds the HID to NUT mapping table; function lookups bind to this instance's state.</summary>
    private HidMapping[] BuildMappings()
    {
        NutLookup powercomBoostConversion = NutLookup.Create([], PowercomBoostConversionFun, null);
        NutLookup powercomLowbattConversion = NutLookup.Create([], PowercomLowbattConversionFun, null);
        NutLookup powercomOnlineConversion = NutLookup.Create([], PowercomOnlineConversionFun, null);
        NutLookup powercomOverloadConversion = NutLookup.Create([], PowercomOverloadConversionFun, null);
        NutLookup powercomReplacebattConversion = NutLookup.Create([], PowercomReplacebattConversionFun, null);
        NutLookup powercomShutdownInfo = NutLookup.Create([], PowercomShutdownFun, PowercomShutdownNuf);
        NutLookup powercomShutdownimmConversion = NutLookup.Create([], PowercomShutdownimmConversionFun, null);
        NutLookup powercomStartupInfo = NutLookup.Create([], PowercomStartupFun, PowercomStartupNuf);
        NutLookup powercomStayoffInfo = NutLookup.Create([], null, PowercomStayoffNuf);
        NutLookup powercomTestConversion = NutLookup.Create([], PowercomTestConversionFun, null);
        NutLookup powercomTrimConversion = NutLookup.Create([], PowercomTrimConversionFun, null);
        NutLookup powercomUpsfailConversion = NutLookup.Create([], PowercomUpsfailConversionFun, null);
        NutLookup powercomVoltageConversion = NutLookup.Create([], PowercomVoltageConversionFun, null);

        return
        [
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ACPresent", null, None, CommonLookups.OnlineInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.BatteryPresent", null, None, CommonLookups.NobatteryInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.BelowRemainingCapacityLimit", null, None, CommonLookups.LowbattInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Charging", null, None, CommonLookups.ChargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.CommunicationLost", null, None, CommonLookups.CommfaultInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Discharging", null, None, CommonLookups.DischargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.NeedReplacement", null, None, CommonLookups.ReplacebattInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Overload", null, None, CommonLookups.OverloadInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.RemainingTimeLimitExpired", null, None, CommonLookups.TimelimitexpiredInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ShutdownImminent", null, None, CommonLookups.ShutdownimmInfo),
            new("BOOL", 0, "UPS.PresentStatus.ACPresent", null, None, CommonLookups.OnlineInfo),
            new("BOOL", 0, "UPS.PresentStatus.BatteryPresent", null, None, CommonLookups.NobatteryInfo),
            new("BOOL", 0, "UPS.PresentStatus.BelowRemainingCapacityLimit", null, None, CommonLookups.LowbattInfo),
            new("BOOL", 0, "UPS.PresentStatus.Boost", null, None, CommonLookups.BoostInfo),
            new("BOOL", 0, "UPS.PresentStatus.Buck", null, None, CommonLookups.TrimInfo),
            new("BOOL", 0, "UPS.PresentStatus.Charging", null, None, CommonLookups.ChargingInfo),
            new("BOOL", 0, "UPS.PresentStatus.CommunicationLost", null, None, CommonLookups.CommfaultInfo),
            new("BOOL", 0, "UPS.PresentStatus.Discharging", null, None, CommonLookups.DischargingInfo),
            new("BOOL", 0, "UPS.PresentStatus.NeedReplacement", null, None, CommonLookups.ReplacebattInfo),
            new("BOOL", 0, "UPS.PresentStatus.Overload", null, None, CommonLookups.OverloadInfo),
            new("BOOL", 0, "UPS.PresentStatus.RemainingTimeLimitExpired", null, None, CommonLookups.TimelimitexpiredInfo),
            new("BOOL", 0, "UPS.PresentStatus.ShutdownImminent", null, None, CommonLookups.ShutdownimmInfo),
            new("battery.charge", 0, "UPS.PowerSummary.RemainingCapacity", "%.0f", None, null),
            new("battery.charge", 0, "UPS.Battery.RemainingCapacity", "%.0f", None, null),
            new("battery.charge.low", 0, "UPS.PowerSummary.RemainingCapacityLimit", "%.0f", None, null),
            new("battery.charge.warning", 0, "UPS.PowerSummary.WarningCapacityLimit", "%.0f", None, null),
            new("battery.runtime", 0, "UPS.PowerSummary.RunTimeToEmpty", "%.0f", None, null),
            new("battery.mfr.date", 0, "UPS.Battery.ManufacturerDate", "%s", Static, CommonLookups.DateConversion),
            new("battery.type", 0, "UPS.PowerSummary.iDeviceChemistry", "%s", Static, CommonLookups.StringidConversion),
            new("ups.beeper.status", 0, "UPS.PowerSummary.AudibleAlarmControl", "%s", None, PowercomBeeperInfo),
            new("ups.beeper.status", 0, "UPS.AudibleAlarmControl", "%s", None, PowercomBeeperInfo),
            new("ups.load", 0, "UPS.Output.PercentLoad", "%.0f", None, null),
            new("ups.date", 0, "UPS.PowerSummary.ManufacturerDate", "%s", Static, CommonLookups.DateConversion),
            new("ups.test.result", 0, "UPS.Battery.Test", "%s", None, CommonLookups.TestReadInfo),
            new("ups.delay.start", 8, "UPS.PowerSummary.DelayBeforeStartup", "60", Rw | Str | Absent, null),
            new("ups.timer.start", 0, "UPS.PowerSummary.DelayBeforeStartup", "%.0f", None, powercomStartupInfo),
            new("ups.delay.shutdown", 8, "UPS.PowerSummary.DelayBeforeShutdown", "20", Rw | Str | Absent, null),
            new("ups.timer.shutdown", 0, "UPS.PowerSummary.DelayBeforeShutdown", "%.0f", QuickPoll, powercomShutdownInfo),
            new("ups.delay.shutdown", 8, "UPS.PowerSummary.PCMDelayBeforeShutdown", "20", Rw | Str | Absent, null),
            new("ups.timer.shutdown", 0, "UPS.PowerSummary.PCMDelayBeforeShutdown", "%.0f", QuickPoll, powercomShutdownInfo),
            new("input.voltage", 0, "UPS.Input.Voltage", "%.1f", None, null),
            new("input.voltage.nominal", 0, "UPS.Input.ConfigVoltage", "%.0f", Static, null),
            new("input.frequency", 0, "UPS.Input.Frequency", "%.1f", None, null),
            new("output.voltage", 0, "UPS.Output.Voltage", "%.1f", None, null),
            new("output.voltage.nominal", 0, "UPS.Output.ConfigVoltage", "%.0f", Static, null),
            new("output.frequency", 0, "UPS.Output.Frequency", "%.1f", None, null),
            new("beeper.toggle", 0, "UPS.PowerSummary.AudibleAlarmControl", "1", Cmd, null),
            new("beeper.enable", 0, "UPS.PowerSummary.AudibleAlarmControl", "1", Cmd, null),
            new("beeper.disable", 0, "UPS.PowerSummary.AudibleAlarmControl", "0", Cmd, null),
            new("test.battery.start.quick", 0, "UPS.Battery.Test", "1", Cmd, null),
            new("load.on.delay", 0, "UPS.PowerSummary.DelayBeforeStartup", null, Cmd, powercomStartupInfo),
            new("shutdown.return", 0, "UPS.PowerSummary.DelayBeforeShutdown", null, Cmd, powercomShutdownInfo),
            new("shutdown.stayoff", 0, "UPS.PowerSummary.DelayBeforeShutdown", null, Cmd, powercomStayoffInfo),
            new("load.on", 0, "UPS.PowerSummary.PCMDelayBeforeStartup", "0", Cmd, powercomStartupInfo),
            new("load.off", 0, "UPS.PowerSummary.PCMDelayBeforeShutdown", "0", Cmd, powercomStayoffInfo),
            new("shutdown.return", 0, "UPS.PowerSummary.PCMDelayBeforeShutdown", null, Cmd, powercomShutdownInfo),
            new("shutdown.stayoff", 0, "UPS.PowerSummary.PCMDelayBeforeShutdown", null, Cmd, powercomStayoffInfo),
            new("ups.serial", 0, "PowercomUPS.PowercomSerialNumber", "%s", None, CommonLookups.StringidConversion),
            new("ups.mfr", 0, "PowercomUPS.PowercomManufacturerName", "%s", None, CommonLookups.StringidConversion),
            new("ups.mfr.date", 0, "PowercomUPS.PowercomManufacturerDate", "%s", None, CommonLookups.DateConversion),
            new("battery.temperature", 0, "PowercomUPS.PowercomBatterySystem.PowercomTemperature", "%.0f", None, null),
            new("battery.temperature", 0, "UPS.Battery.Temperature", "%.1f", None, null),
            new("battery.charge", 0, "PowercomUPS.PowercomBatterySystem.PowercomVoltage", "%.0f", None, null),
            new("input.frequency", 0, "PowercomUPS.PowercomPowerConverter.PowercomInput.PowercomFrequency", "%.0f", None, null),
            new("input.voltage", 0, "PowercomUPS.PowercomPowerConverter.PowercomInput.PowercomVoltage", "%.0f", None, powercomVoltageConversion),
            new("output.voltage", 0, "PowercomUPS.PowercomPowerConverter.PowercomOutput.PowercomVoltage", "%.0f", None, powercomVoltageConversion),
            new("ups.load", 0, "PowercomUPS.PowercomPowerConverter.PowercomOutput.PowercomPercentLoad", "%.0f", None, null),
            new("BOOL", 0, "PowercomUPS.PowercomPowerConverter.PowercomOutput.PowercomInternalChargeController", null, QuickPoll, powercomUpsfailConversion),
            new("BOOL", 0, "PowercomUPS.PowercomPowerConverter.PowercomOutput.PowercomInternalChargeController", null, QuickPoll, powercomReplacebattConversion),
            new("BOOL", 0, "PowercomUPS.PowercomPowerConverter.PowercomOutput.PowercomInternalChargeController", null, QuickPoll, powercomTestConversion),
            new("BOOL", 0, "PowercomUPS.PowercomPowerConverter.PowercomOutput.PowercomInternalChargeController", null, QuickPoll, powercomShutdownimmConversion),
            new("BOOL", 0, "PowercomUPS.PowercomPowerConverter.PowercomOutput.PowercomPrimaryBatterySupport", null, QuickPoll, powercomOnlineConversion),
            new("BOOL", 0, "PowercomUPS.PowercomPowerConverter.PowercomOutput.PowercomPrimaryBatterySupport", null, QuickPoll, powercomLowbattConversion),
            new("BOOL", 0, "PowercomUPS.PowercomPowerConverter.PowercomOutput.PowercomPrimaryBatterySupport", null, QuickPoll, powercomTrimConversion),
            new("BOOL", 0, "PowercomUPS.PowercomPowerConverter.PowercomOutput.PowercomPrimaryBatterySupport", null, QuickPoll, powercomBoostConversion),
            new("BOOL", 0, "PowercomUPS.PowercomPowerConverter.PowercomOutput.PowercomPrimaryBatterySupport", null, QuickPoll, powercomOverloadConversion),
            new("output.frequency", 0, "PowercomUPS.PowercomPowerConverter.PowercomOutput.PowercomFrequency", "%.0f", None, null),
            new("ups.test.result", 0, "PowercomUPS.PowercomPowerConverter.PowercomTest", "%s", None, CommonLookups.TestReadInfo),
            new("ups.delay.shutdown", 0, "PowercomUPS.PowercomPowerConverter.PowercomDelayBeforeShutdown", "%.0f", None, null),
            new("ups.delay.start", 0, "PowercomUPS.PowercomPowerConverter.PowercomDelayBeforeStartup", "%.0f", None, null),
            new("load.off", 0, "PowercomUPS.PowercomPowerConverter.PowercomDelayBeforeShutdown", "0", Cmd, null),
        ];
    }
}
