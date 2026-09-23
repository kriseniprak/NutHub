// Mapping tables ported from Network UPS Tools, drivers/apc-hid.c (APC HID 0.102),
// GPL-2.0-or-later. Keep them in sync with that file rather than editing entries by hand.
using NutHub.Drivers.Hid.Descriptors;
using static NutHub.Drivers.Hid.UsbHid.HidMapFlags;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

internal sealed partial class ApcSubdriver
{
    /// <summary>The NUT subdriver this one is ported from, reported as driver.version.data.</summary>
    public override string Version => "APC HID 0.102";

    /// <summary>Vendor-specific usage names (NUT apc_usage_lkp).</summary>
    private static readonly HidUsageTable VendorUsageTable = new(
    [
        ("APCGeneralCollection", 0xff860005),
        ("APCEnvironment", 0xff860006),
        ("APCProbe1", 0xff860007),
        ("APCProbe2", 0xff860008),
        ("APCBattReplaceDate", 0xff860016),
        ("APCBattCapBeforeStartup", 0xff860019),
        ("APC_UPS_FirmwareRevision", 0xff860042),
        ("APCLineFailCause", 0xff860052),
        ("APCStatusFlag", 0xff860060),
        ("APCSensitivity", 0xff860061),
        ("APCPanelTest", 0xff860072),
        ("APCShutdownAfterDelay", 0xff860076),
        ("APC_USB_FirmwareRevision", 0xff860079),
        ("APCDelayBeforeReboot", 0xff86007c),
        ("APCDelayBeforeShutdown", 0xff86007d),
        ("APCDelayBeforeStartup", 0xff86007e),
        ("BUPHibernate", 0x00850058),
        ("BUPBattCapBeforeStartup", 0x00860012),
        ("BUPDelayBeforeStartup", 0x00860076),
        ("BUPSelfTest", 0x00860010),
    ]);

    /// <summary>The USB ids this subdriver supports (NUT apc_usb_device_table).</summary>
    private static readonly UsbDeviceId[] DeviceIds =
    [
        new(0x051d, 0x0000),
        new(0x051d, 0x0002, "general_apc_check"),
        new(0x051d, 0x0003, "disable_interrupt_pipe"),
        new(0x051d, 0x0004, "disable_interrupt_pipe"),
        new(0x051d, 0x0005),
    ];

    private static readonly NutLookup ApcLinefailcauseFrangeInfo = NutLookup.Table([(7, "frange"), (0, "!frange")]);

    private static readonly NutLookup ApcLinefailcauseVrangeInfo = NutLookup.Table([(1, "vrange"), (2, "vrange"), (4, "vrange"), (8, "vrange"), (9, "vrange"), (0, "!vrange")]);

    private static readonly NutLookup ApcSensitivityInfo = NutLookup.Table([(0, "low"), (1, "medium"), (2, "high")]);

    private static readonly NutLookup ApcstatusflagInfo = NutLookup.Table([(8, "!off"), (16, "!off"), (0, "off")]);

    /// <summary>Builds the HID to NUT mapping table; function lookups bind to this instance's state.</summary>
    private HidMapping[] BuildMappings()
    {
        NutLookup apcDateConversion = NutLookup.Create([], ApcDateConversionFun, ApcDateConversionReverse);

        return
        [
            new("battery.charge", 0, "UPS.PowerSummary.RemainingCapacity", "%.0f", None, null),
            new("battery.charge.low", 10, "UPS.PowerSummary.RemainingCapacityLimit", "%.0f", Rw | Str | SemiStatic, null),
            new("battery.charge.warning", 0, "UPS.PowerSummary.WarningCapacityLimit", "%.0f", None, null),
            new("battery.runtime", 0, "UPS.Battery.RunTimeToEmpty", "%.0f", None, null),
            new("battery.runtime", 0, "UPS.PowerSummary.RunTimeToEmpty", "%.0f", None, null),
            new("battery.runtime.low", 10, "UPS.Battery.RemainingTimeLimit", "%.0f", Rw | Str | SemiStatic, null),
            new("battery.runtime.low", 10, "UPS.PowerSummary.RemainingTimeLimit", "%.0f", Rw | Str | SemiStatic, null),
            new("battery.voltage", 0, "UPS.Battery.Voltage", "%.1f", None, null),
            new("battery.voltage", 0, "UPS.PowerSummary.Voltage", "%.1f", None, null),
            new("battery.voltage.nominal", 0, "UPS.Battery.ConfigVoltage", "%.1f", None, null),
            new("battery.voltage.nominal", 0, "UPS.PowerSummary.ConfigVoltage", "%.1f", None, null),
            new("battery.temperature", 0, "UPS.Battery.Temperature", "%s", None, CommonLookups.KelvinCelsiusConversion),
            new("battery.type", 0, "UPS.PowerSummary.iDeviceChemistry", "%s", None, CommonLookups.StringidConversion),
            new("battery.mfr.date", 10, "UPS.Battery.ManufacturerDate", "%s", Rw | Str | SemiStatic, CommonLookups.DateConversion),
            new("battery.mfr.date", 0, "UPS.PowerSummary.APCBattReplaceDate", "%s", None, apcDateConversion),
            new("battery.date", 0, "UPS.Battery.APCBattReplaceDate", "%s", None, apcDateConversion),
            new("ups.load", 0, "UPS.Output.PercentLoad", "%.1f", None, null),
            new("ups.load", 0, "UPS.PowerConverter.PercentLoad", "%.0f", None, null),
            new("ups.delay.start", 10, "UPS.PowerSummary.DelayBeforeStartup", "30", Rw | Str | Absent, null),
            new("ups.delay.shutdown", 10, "UPS.PowerSummary.DelayBeforeShutdown", "20", Rw | Str | Absent, null),
            new("ups.timer.start", 0, "UPS.PowerSummary.DelayBeforeStartup", "%.0f", QuickPoll, null),
            new("ups.timer.shutdown", 0, "UPS.PowerSummary.DelayBeforeShutdown", "%.0f", QuickPoll, null),
            new("ups.timer.reboot", 0, "UPS.PowerSummary.DelayBeforeReboot", "%.0f", QuickPoll, null),
            new("ups.delay.start", 10, "UPS.Output.DelayBeforeStartup", "30", Rw | Str | Absent, null),
            new("ups.delay.shutdown", 10, "UPS.Output.DelayBeforeShutdown", "20", Rw | Str | Absent, null),
            new("ups.timer.start", 0, "UPS.Output.DelayBeforeStartup", "%.0f", QuickPoll, null),
            new("ups.timer.shutdown", 0, "UPS.Output.DelayBeforeShutdown", "%.0f", QuickPoll, null),
            new("ups.timer.reboot", 0, "UPS.Output.DelayBeforeReboot", "%.0f", QuickPoll, null),
            new("ups.delay.start", 10, "UPS.APCGeneralCollection.APCDelayBeforeStartup", "30", Rw | Str | Absent, null),
            new("ups.delay.shutdown", 10, "UPS.APCGeneralCollection.APCDelayBeforeShutdown", "20", Rw | Str | Absent, null),
            new("ups.timer.start", 0, "UPS.APCGeneralCollection.APCDelayBeforeStartup", "%.0f", QuickPoll, null),
            new("ups.timer.shutdown", 0, "UPS.APCGeneralCollection.APCDelayBeforeShutdown", "%.0f", QuickPoll, null),
            new("ups.timer.reboot", 0, "UPS.APCGeneralCollection.APCDelayBeforeReboot", "%.0f", QuickPoll, null),
            new("ups.test.result", 0, "UPS.Battery.Test", "%s", None, CommonLookups.TestReadInfo),
            new("ups.beeper.status", 0, "UPS.PowerSummary.AudibleAlarmControl", "%s", None, CommonLookups.BeeperInfo),
            new("ups.mfr.date", 0, "UPS.ManufacturerDate", "%s", None, CommonLookups.DateConversion),
            new("ups.mfr.date", 0, "UPS.PowerSummary.ManufacturerDate", "%s", None, CommonLookups.DateConversion),
            new("ups.realpower.nominal", 0, "UPS.PowerConverter.ConfigActivePower", "%.0f", None, null),
            new("ups.realpower.nominal", 0, "UPS.Output.ConfigActivePower", "%.0f", None, null),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ACPresent", null, QuickPoll, CommonLookups.OnlineInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Discharging", null, QuickPoll, CommonLookups.DischargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Charging", null, QuickPoll, CommonLookups.ChargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ShutdownImminent", null, None, CommonLookups.ShutdownimmInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.BelowRemainingCapacityLimit", null, QuickPoll, CommonLookups.LowbattInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Overload", null, None, CommonLookups.OverloadInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.NeedReplacement", null, None, CommonLookups.ReplacebattInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.RemainingTimeLimitExpired", null, None, CommonLookups.TimelimitexpiredInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.BatteryPresent", null, None, CommonLookups.NobatteryInfo),
            new("BOOL", 0, "UPS.PowerSummary.Charging", null, QuickPoll, CommonLookups.ChargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.Discharging", null, QuickPoll, CommonLookups.DischargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.ACPresent", null, QuickPoll, CommonLookups.OnlineInfo),
            new("BOOL", 0, "UPS.PowerSummary.BelowRemainingCapacityLimit", null, QuickPoll, CommonLookups.LowbattInfo),
            new("BOOL", 0, "UPS.PowerSummary.ShutdownImminent", null, None, CommonLookups.ShutdownimmInfo),
            new("BOOL", 0, "UPS.PowerSummary.APCStatusFlag", null, QuickPoll, ApcstatusflagInfo),
            new("BOOL", 0, "UPS.Input.APCLineFailCause", null, None, ApcLinefailcauseVrangeInfo),
            new("BOOL", 0, "UPS.Input.APCLineFailCause", null, None, ApcLinefailcauseFrangeInfo),
            new("input.voltage", 0, "UPS.Input.Voltage", "%.1f", None, null),
            new("input.voltage.nominal", 0, "UPS.Input.ConfigVoltage", "%.0f", None, null),
            new("input.transfer.low", 10, "UPS.Output.LowVoltageTransfer", "%.0f", Rw | Str | SemiStatic, null),
            new("input.transfer.high", 10, "UPS.Output.HighVoltageTransfer", "%.0f", Rw | Str | SemiStatic, null),
            new("input.transfer.low", 10, "UPS.Input.LowVoltageTransfer", "%.0f", Rw | Str | SemiStatic, null),
            new("input.transfer.high", 10, "UPS.Input.HighVoltageTransfer", "%.0f", Rw | Str | SemiStatic, null),
            new("input.sensitivity", 10, "UPS.Input.APCSensitivity", "%s", Rw | Str | SemiStatic, ApcSensitivityInfo),
            new("output.voltage", 0, "UPS.Output.Voltage", "%.1f", None, null),
            new("output.voltage.nominal", 0, "UPS.Output.ConfigVoltage", "%.1f", None, null),
            new("output.current", 0, "UPS.Output.Current", "%.2f", None, null),
            new("output.frequency", 0, "UPS.Output.Frequency", "%.1f", None, null),
            new("ambient.temperature", 0, "UPS.APCEnvironment.APCProbe1.Temperature", "%s", None, CommonLookups.KelvinCelsiusConversion),
            new("ambient.humidity", 0, "UPS.APCEnvironment.APCProbe1.Humidity", "%.1f", None, null),
            new("test.battery.start.quick", 0, "UPS.BatterySystem.Battery.Test", "1", Cmd, null),
            new("test.battery.start.quick", 0, "UPS.Battery.Test", "1", Cmd, null),
            new("test.battery.start.deep", 0, "UPS.BatterySystem.Battery.Test", "2", Cmd, null),
            new("test.battery.start.deep", 0, "UPS.Battery.Test", "2", Cmd, null),
            new("test.battery.stop", 0, "UPS.BatterySystem.Battery.Test", "3", Cmd, null),
            new("test.battery.stop", 0, "UPS.Battery.Test", "3", Cmd, null),
            new("test.panel.start", 0, "UPS.APCPanelTest", "1", Cmd, null),
            new("test.panel.stop", 0, "UPS.APCPanelTest", "0", Cmd, null),
            new("test.panel.start", 0, "UPS.PowerSummary.APCPanelTest", "1", Cmd, null),
            new("test.panel.stop", 0, "UPS.PowerSummary.APCPanelTest", "0", Cmd, null),
            new("load.off.delay", 0, "UPS.PowerSummary.DelayBeforeShutdown", "20", Cmd, null),
            new("load.on.delay", 0, "UPS.PowerSummary.DelayBeforeStartup", "30", Cmd, null),
            new("shutdown.stop", 0, "UPS.PowerSummary.DelayBeforeShutdown", "-1", Cmd, null),
            new("shutdown.reboot", 0, "UPS.PowerSummary.DelayBeforeReboot", "10", Cmd, null),
            new("load.off.delay", 0, "UPS.Output.DelayBeforeShutdown", "20", Cmd, null),
            new("load.on.delay", 0, "UPS.Output.DelayBeforeStartup", "30", Cmd, null),
            new("shutdown.stop", 0, "UPS.Output.DelayBeforeShutdown", "-1", Cmd, null),
            new("shutdown.reboot", 0, "UPS.Output.DelayBeforeReboot", "10", Cmd, null),
            new("load.off.delay", 0, "UPS.APCGeneralCollection.APCDelayBeforeShutdown", "20", Cmd, null),
            new("load.on.delay", 0, "UPS.APCGeneralCollection.APCDelayBeforeStartup", "30", Cmd, null),
            new("shutdown.stop", 0, "UPS.APCGeneralCollection.APCDelayBeforeShutdown", "-1", Cmd, null),
            new("shutdown.reboot", 0, "UPS.APCGeneralCollection.APCDelayBeforeReboot", "10", Cmd, null),
            new("shutdown.return", 0, "UPS.APCGeneralCollection.APCDelayBeforeReboot", "1", Cmd, null),
            new("shutdown.return", 0, "UPS.Output.APCDelayBeforeReboot", "1", Cmd, null),
            new("beeper.on", 0, "UPS.PowerSummary.AudibleAlarmControl", "2", Cmd, null),
            new("beeper.off", 0, "UPS.PowerSummary.AudibleAlarmControl", "3", Cmd, null),
            new("beeper.enable", 0, "UPS.PowerSummary.AudibleAlarmControl", "2", Cmd, null),
            new("beeper.disable", 0, "UPS.PowerSummary.AudibleAlarmControl", "1", Cmd, null),
            new("beeper.mute", 0, "UPS.PowerSummary.AudibleAlarmControl", "3", Cmd, null),
        ];
    }
}
