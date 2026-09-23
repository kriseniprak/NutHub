// Mapping tables ported from Network UPS Tools, drivers/belkin-hid.c (Belkin/Liebert HID 0.24),
// GPL-2.0-or-later. Keep them in sync with that file rather than editing entries by hand.
using NutHub.Drivers.Hid.Descriptors;
using static NutHub.Drivers.Hid.UsbHid.HidMapFlags;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

internal sealed partial class BelkinSubdriver
{
    /// <summary>The NUT subdriver this one is ported from, reported as driver.version.data.</summary>
    public override string Version => "Belkin/Liebert HID 0.24";

    /// <summary>Vendor-specific usage names (NUT belkin_usage_lkp).</summary>
    private static readonly HidUsageTable VendorUsageTable = new(
    [
        ("BELKINConfig", 0x00860026),
        ("BELKINConfigVoltage", 0x00860040),
        ("BELKINConfigFrequency", 0x00860042),
        ("BELKINConfigApparentPower", 0x00860043),
        ("BELKINConfigBatteryVoltage", 0x00860044),
        ("BELKINConfigOverloadTransfer", 0x00860045),
        ("BELKINLowVoltageTransfer", 0x00860053),
        ("BELKINHighVoltageTransfer", 0x00860054),
        ("BELKINLowVoltageTransferMax", 0x0086005b),
        ("BELKINLowVoltageTransferMin", 0x0086005c),
        ("BELKINHighVoltageTransferMax", 0x0086005d),
        ("BELKINHighVoltageTransferMin", 0x0086005e),
        ("BELKINControls", 0x00860027),
        ("BELKINLoadOn", 0x00860050),
        ("BELKINLoadOff", 0x00860051),
        ("BELKINLoadToggle", 0x00860052),
        ("BELKINDelayBeforeReboot", 0x00860055),
        ("BELKINDelayBeforeStartup", 0x00860056),
        ("BELKINDelayBeforeShutdown", 0x00860057),
        ("BELKINTest", 0x00860058),
        ("BELKINAudibleAlarmControl", 0x0086005a),
        ("BELKINDevice", 0x00860029),
        ("BELKINVoltageSensitivity", 0x00860074),
        ("BELKINModelString", 0x00860075),
        ("BELKINModelStringOffset", 0x00860076),
        ("BELKINUPSType", 0x0086007c),
        ("BELKINPowerState", 0x0086002a),
        ("BELKINInput", 0x0086001a),
        ("BELKINOutput", 0x0086001c),
        ("BELKINBatterySystem", 0x00860010),
        ("BELKINVoltage", 0x00860030),
        ("BELKINFrequency", 0x00860032),
        ("BELKINPower", 0x00860034),
        ("BELKINPercentLoad", 0x00860035),
        ("BELKINTemperature", 0x00860036),
        ("BELKINCharge", 0x00860039),
        ("BELKINRunTimeToEmpty", 0x0086006c),
        ("BELKINStatus", 0x00860028),
        ("BELKINBatteryStatus", 0x00860022),
        ("BELKINPowerStatus", 0x00860021),
    ]);

    /// <summary>The USB ids this subdriver supports (NUT belkin_usb_device_table).</summary>
    private static readonly UsbDeviceId[] DeviceIds =
    [
        new(0x050d, 0x0980),
        new(0x050d, 0x0900),
        new(0x050d, 0x0910),
        new(0x050d, 0x0912),
        new(0x050d, 0x0551),
        new(0x050d, 0x0750),
        new(0x050d, 0x0751),
        new(0x050d, 0x0375),
        new(0x050d, 0x0f51),
        new(0x050d, 0x1100),
        new(0x10af, 0x0000),
        new(0x10af, 0x0001),
        new(0x10af, 0x0002),
        new(0x10af, 0x0004),
        new(0x10af, 0x0008),
    ];

    private static readonly NutLookup BelkinTestInfo = NutLookup.Table([(0, "No test initiated"), (1, "Done and passed"), (2, "Done and warning"), (3, "Done and error"), (4, "Aborted"), (5, "In progress")]);

    /// <summary>Builds the HID to NUT mapping table; function lookups bind to this instance's state.</summary>
    private HidMapping[] BuildMappings()
    {
        NutLookup belkinAwaitingpowerConversion = NutLookup.Create([], BelkinAwaitingpowerConversionFun, null);
        NutLookup belkinCommfaultConversion = NutLookup.Create([], BelkinCommfaultConversionFun, null);
        NutLookup belkinDepletedConversion = NutLookup.Create([], BelkinDepletedConversionFun, null);
        NutLookup belkinFirmwareConversion = NutLookup.Create([], BelkinFirmwareConversionFun, null);
        NutLookup belkinLowbattConversion = NutLookup.Create([], BelkinLowbattConversionFun, null);
        NutLookup belkinOnlineConversion = NutLookup.Create([], BelkinOnlineConversionFun, null);
        NutLookup belkinOverheatConversion = NutLookup.Create([], BelkinOverheatConversionFun, null);
        NutLookup belkinOverloadConversion = NutLookup.Create([], BelkinOverloadConversionFun, null);
        NutLookup belkinReplacebattConversion = NutLookup.Create([], BelkinReplacebattConversionFun, null);
        NutLookup belkinSensitivityConversion = NutLookup.Create([], BelkinSensitivityConversionFun, null);
        NutLookup belkinUpstypeConversion = NutLookup.Create([], BelkinUpstypeConversionFun, null);
        NutLookup liebertChargingInfo = NutLookup.Create([], LiebertChargingFun, null);
        NutLookup liebertConfigVoltageInfo = NutLookup.Create([], LiebertConfigVoltageFun, null);
        NutLookup liebertDischargingInfo = NutLookup.Create([], LiebertDischargingFun, null);
        NutLookup liebertLineVoltageInfo = NutLookup.Create([], LiebertLineVoltageFun, null);
        NutLookup liebertLowbattInfo = NutLookup.Create([], LiebertLowbattFun, null);
        NutLookup liebertOnlineInfo = NutLookup.Create([], LiebertOnlineFun, null);
        NutLookup liebertReplacebattInfo = NutLookup.Create([], LiebertReplacebattFun, null);
        NutLookup liebertShutdownimmInfo = NutLookup.Create([], LiebertShutdownimmFun, null);

        return
        [
            new("battery.charge", 0, "UPS.BELKINBatterySystem.BELKINCharge", "%.0f", None, null),
            new("battery.charge.low", 0, "UPS.PowerSummary.RemainingCapacityLimit", "%.0f", None, null),
            new("battery.charge.warning", 0, "UPS.PowerSummary.WarningCapacityLimit", "%.0f", None, null),
            new("battery.runtime", 0, "UPS.PowerSummary.RunTimeToEmpty", "%.0f", None, null),
            new("battery.type", 0, "UPS.PowerSummary.iDeviceChemistry", "%s", None, CommonLookups.StringidConversion),
            new("battery.voltage", 0, "UPS.BELKINBatterySystem.BELKINVoltage", "%s", None, CommonLookups.DivideBy10Conversion),
            new("battery.voltage.nominal", 0, "UPS.BELKINConfig.BELKINConfigBatteryVoltage", "%.0f", None, null),
            new("input.frequency", 0, "UPS.BELKINPowerState.BELKINInput.BELKINFrequency", "%s", None, CommonLookups.DivideBy10Conversion),
            new("input.frequency.nominal", 0, "UPS.BELKINConfig.BELKINConfigFrequency", "%.0f", None, null),
            new("input.sensitivity", 10, "UPS.BELKINDevice.BELKINVoltageSensitivity", "%s", Rw | Str, belkinSensitivityConversion),
            new("input.transfer.high", 10, "UPS.BELKINConfig.BELKINHighVoltageTransfer", "%.0f", Rw | Str, null),
            new("input.transfer.high.max", 0, "UPS.BELKINConfig.BELKINHighVoltageTransferMax", "%.0f", None, null),
            new("input.transfer.high.min", 0, "UPS.BELKINConfig.BELKINHighVoltageTransferMin", "%.0f", None, null),
            new("input.transfer.low", 10, "UPS.BELKINConfig.BELKINLowVoltageTransfer", "%.0f", Rw | Str, null),
            new("input.transfer.low.max", 0, "UPS.BELKINConfig.BELKINLowVoltageTransferMax", "%.0f", None, null),
            new("input.transfer.low.min", 0, "UPS.BELKINConfig.BELKINLowVoltageTransferMin", "%.0f", None, null),
            new("input.voltage", 0, "UPS.BELKINPowerState.BELKINInput.BELKINVoltage", "%s", None, CommonLookups.DivideBy10Conversion),
            new("input.voltage.nominal", 0, "UPS.BELKINConfig.BELKINConfigVoltage", "%.0f", None, null),
            new("output.frequency", 0, "UPS.BELKINPowerState.BELKINOutput.BELKINFrequency", "%s", None, CommonLookups.DivideBy10Conversion),
            new("output.voltage", 0, "UPS.BELKINPowerState.BELKINOutput.BELKINVoltage", "%s", None, CommonLookups.DivideBy10Conversion),
            new("ups.beeper.status", 0, "UPS.BELKINControls.BELKINAudibleAlarmControl", "%s", None, CommonLookups.BeeperInfo),
            new("ups.delay.start", 10, "UPS.BELKINControls.BELKINDelayBeforeStartup", "30", Rw | Str | Absent, null),
            new("ups.delay.shutdown", 10, "UPS.BELKINControls.BELKINDelayBeforeShutdown", "20", Rw | Str | Absent, null),
            new("ups.timer.start", 0, "UPS.BELKINControls.BELKINDelayBeforeStartup", "%.0f", QuickPoll, null),
            new("ups.timer.shutdown", 0, "UPS.BELKINControls.BELKINDelayBeforeShutdown", "%.0f", QuickPoll, null),
            new("ups.timer.reboot", 0, "UPS.BELKINControls.BELKINDelayBeforeReboot", "%.0f", QuickPoll, null),
            new("ups.firmware", 0, "UPS.BELKINDevice.BELKINUPSType", "%s", None, belkinFirmwareConversion),
            new("ups.load", 0, "UPS.BELKINPowerState.BELKINOutput.BELKINPercentLoad", "%.0f", None, null),
            new("ups.load.high", 0, "UPS.BELKINConfig.BELKINConfigOverloadTransfer", "%.0f", None, null),
            new("ups.mfr.date", 0, "UPS.PowerSummary.ManufacturerDate", "%s", None, CommonLookups.DateConversion),
            new("ups.power.nominal", 0, "UPS.BELKINConfig.BELKINConfigApparentPower", "%.0f", None, null),
            new("ups.serial", 0, "UPS.PowerSummary.iSerialNumber", "%s", None, CommonLookups.StringidConversion),
            new("ups.test.result", 0, "UPS.BELKINControls.BELKINTest", "%s", None, BelkinTestInfo),
            new("ups.type", 0, "UPS.BELKINDevice.BELKINUPSType", "%s", None, belkinUpstypeConversion),
            new("battery.charge", 0, "UPS.PowerSummary.RemainingCapacity", "%.0f", QuickPoll, null),
            new("input.frequency", 0, "UPS.Input.Frequency", "%s", None, CommonLookups.DivideBy10Conversion),
            new("input.voltage", 0, "UPS.Input.Voltage", "%s", None, liebertLineVoltageInfo),
            new("output.voltage", 0, "UPS.Output.Voltage", "%s", None, liebertLineVoltageInfo),
            new("battery.voltage", 0, "UPS.PowerSummary.Voltage", "%s", None, liebertLineVoltageInfo),
            new("battery.voltage.nominal", 0, "UPS.PowerSummary.ConfigVoltage", "%s", Static, liebertConfigVoltageInfo),
            new("ups.load", 0, "UPS.Output.PercentLoad", "%.0f", None, null),
            new("input.voltage.nominal", 0, "UPS.Flow.ConfigVoltage", "%.0f", None, null),
            new("input.frequency", 0, "UPS.PowerConverter.Input.Frequency", "%s", None, CommonLookups.DivideBy100Conversion),
            new("input.voltage", 0, "UPS.PowerConverter.Input.Voltage", "%s", None, liebertLineVoltageInfo),
            new("output.voltage.nominal", 0, "UPS.Flow.ConfigVoltage", "%.0f", None, null),
            new("output.frequency", 0, "UPS.PowerConverter.Output.Frequency", "%s", None, CommonLookups.DivideBy100Conversion),
            new("output.voltage", 0, "UPS.PowerConverter.Output.Voltage", "%s", None, liebertLineVoltageInfo),
            new("ups.load", 0, "UPS.OutletSystem.Outlet.PercentLoad", "%.0f", None, null),
            new("battery.voltage", 0, "UPS.BatterySystem.Battery.Voltage", "%s", None, liebertLineVoltageInfo),
            new("battery.voltage.nominal", 0, "UPS.BatterySystem.Battery.ConfigVoltage", "%.0f", None, null),
            new("battery.capacity", 0, "UPS.Flow.ConfigApparentPower", "%.0f", None, null),
            new("BOOL", 0, "UPS.PowerSummary.Discharging", null, QuickPoll, liebertDischargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.Charging", null, QuickPoll, liebertChargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.ShutdownImminent", null, None, liebertShutdownimmInfo),
            new("BOOL", 0, "UPS.PowerSummary.ACPresent", null, QuickPoll, liebertOnlineInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Discharging", null, QuickPoll, liebertDischargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Charging", null, QuickPoll, liebertChargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ShutdownImminent", null, None, liebertShutdownimmInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ACPresent", null, QuickPoll, liebertOnlineInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.NeedReplacement", null, QuickPoll, liebertReplacebattInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.BelowRemainingCapacityLimit", null, QuickPoll, liebertLowbattInfo),
            new("BOOL", 0, "UPS.BELKINStatus.BELKINPowerStatus", null, None, belkinOverloadConversion),
            new("BOOL", 0, "UPS.BELKINStatus.BELKINPowerStatus", null, None, belkinOverheatConversion),
            new("BOOL", 0, "UPS.BELKINStatus.BELKINPowerStatus", null, None, belkinCommfaultConversion),
            new("BOOL", 0, "UPS.BELKINStatus.BELKINPowerStatus", null, None, belkinAwaitingpowerConversion),
            new("BOOL", 0, "UPS.BELKINStatus.BELKINPowerStatus", null, QuickPoll, belkinOnlineConversion),
            new("BOOL", 0, "UPS.BELKINStatus.BELKINBatteryStatus", null, QuickPoll, belkinDepletedConversion),
            new("BOOL", 0, "UPS.BELKINStatus.BELKINBatteryStatus", null, None, belkinReplacebattConversion),
            new("BOOL", 0, "UPS.BELKINStatus.BELKINBatteryStatus", null, QuickPoll, belkinLowbattConversion),
            new("test.battery.start.quick", 0, "UPS.BELKINControls.BELKINTest", "1", Cmd, null),
            new("test.battery.start.deep", 0, "UPS.BELKINControls.BELKINTest", "2", Cmd, null),
            new("test.battery.stop", 0, "UPS.BELKINControls.BELKINTest", "3", Cmd, null),
            new("beeper.on", 0, "UPS.BELKINControls.BELKINAudibleAlarmControl", "2", Cmd, null),
            new("beeper.off", 0, "UPS.BELKINControls.BELKINAudibleAlarmControl", "3", Cmd, null),
            new("beeper.disable", 0, "UPS.BELKINControls.BELKINAudibleAlarmControl", "1", Cmd, null),
            new("beeper.enable", 0, "UPS.BELKINControls.BELKINAudibleAlarmControl", "2", Cmd, null),
            new("beeper.mute", 0, "UPS.BELKINControls.BELKINAudibleAlarmControl", "3", Cmd, null),
            new("load.off", 0, "UPS.BELKINControls.BELKINDelayBeforeShutdown", "1", Cmd, null),
            new("load.on", 0, "UPS.BELKINControls.BELKINDelayBeforeStartup", "1", Cmd, null),
            new("load.off.delay", 0, "UPS.BELKINControls.BELKINDelayBeforeShutdown", "20", Cmd, null),
            new("load.on.delay", 0, "UPS.BELKINControls.BELKINDelayBeforeStartup", "30", Cmd, null),
            new("shutdown.stop", 0, "UPS.BELKINControls.BELKINDelayBeforeShutdown", "-1", Cmd, null),
            new("shutdown.reboot", 0, "UPS.BELKINControls.BELKINDelayBeforeReboot", "10", Cmd, null),
        ];
    }
}
