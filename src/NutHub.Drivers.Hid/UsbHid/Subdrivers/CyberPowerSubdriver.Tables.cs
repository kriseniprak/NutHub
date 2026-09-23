// Mapping tables ported from Network UPS Tools, drivers/cps-hid.c (CyberPower HID 0.87),
// GPL-2.0-or-later. Keep them in sync with that file rather than editing entries by hand.
using NutHub.Drivers.Hid.Descriptors;
using static NutHub.Drivers.Hid.UsbHid.HidMapFlags;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

internal sealed partial class CyberPowerSubdriver
{
    /// <summary>The NUT subdriver this one is ported from, reported as driver.version.data.</summary>
    public override string Version => "CyberPower HID 0.87";

    /// <summary>Vendor-specific usage names (NUT cps_usage_lkp).</summary>
    private static readonly HidUsageTable VendorUsageTable = new(
    [
        ("CPSFirmwareVersion", 0xff0100d0),
        ("CPSInputSensitivity", 0xff010043),
    ]);

    /// <summary>The USB ids this subdriver supports (NUT cps_usb_device_table).</summary>
    private static readonly UsbDeviceId[] DeviceIds =
    [
        new(0x0764, 0x0005),
        new(0x0764, 0x0501, "cps_battery_scale"),
        new(0x0764, 0x0601),
        new(0x0483, 0xa430),
    ];

    private static readonly NutLookup CpsSensitivityInfo = NutLookup.Table([(0, "low"), (1, "normal"), (2, "high")]);

    /// <summary>Builds the HID to NUT mapping table; function lookups bind to this instance's state.</summary>
    private HidMapping[] BuildMappings()
    {
        NutLookup cpsBattcharge = NutLookup.Create([], CpsBattchargeFun, null);
        NutLookup cpsBattstatus = NutLookup.Create([], CpsBattstatusFun, null);
        NutLookup cpsBattvolt = NutLookup.Create([], CpsBattvoltFun, null);
        NutLookup cpsInputFreq = NutLookup.Create([], CpsInputFreqFun, null);
        NutLookup cpsOutputFreq = NutLookup.Create([], CpsOutputFreqFun, null);

        return
        [
            new("battery.type", 0, "UPS.PowerSummary.iDeviceChemistry", "%s", None, CommonLookups.StringidConversion),
            new("battery.mfr.date", 10, "UPS.Battery.ManufacturerDate", "%s", Rw | Str | SemiStatic, CommonLookups.DateConversion),
            new("battery.mfr.date", 0, "UPS.PowerSummary.iOEMInformation", "%s", None, CommonLookups.StringidConversion),
            new("battery.charge.warning", 0, "UPS.PowerSummary.WarningCapacityLimit", "%.0f", None, null),
            new("battery.charge.low", 10, "UPS.PowerSummary.RemainingCapacityLimit", "%.0f", Rw | Str | SemiStatic, null),
            new("battery.charge", 0, "UPS.PowerSummary.RemainingCapacity", "%s", None, cpsBattcharge),
            new("battery.runtime", 0, "UPS.PowerSummary.RunTimeToEmpty", "%.0f", None, null),
            new("battery.runtime.low", 10, "UPS.PowerSummary.RemainingTimeLimit", "%.0f", Rw | Str | SemiStatic, null),
            new("battery.voltage.nominal", 0, "UPS.PowerSummary.ConfigVoltage", "%.0f", None, null),
            new("battery.voltage", 0, "UPS.PowerSummary.Voltage", "%s", None, cpsBattvolt),
            new("battery.status", 0, "UPS.PowerSummary.FullChargeCapacity", "%s", None, cpsBattstatus),
            new("ups.load", 0, "UPS.Output.PercentLoad", "%.0f", None, null),
            new("ups.beeper.status", 0, "UPS.PowerSummary.AudibleAlarmControl", "%s", None, CommonLookups.BeeperInfo),
            new("ups.test.result", 0, "UPS.Output.Test", "%s", None, CommonLookups.TestReadInfo),
            new("ups.power", 0, "UPS.Output.ApparentPower", "%.0f", None, null),
            new("ups.power.nominal", 0, "UPS.Output.ConfigApparentPower", "%.0f", None, null),
            new("ups.realpower", 0, "UPS.Output.ActivePower", "%.0f", None, null),
            new("ups.realpower.nominal", 0, "UPS.Output.ConfigActivePower", "%.0f", None, null),
            new("ups.delay.start", 10, "UPS.Output.DelayBeforeStartup", "120", Rw | Str | Absent, null),
            new("ups.delay.shutdown", 10, "UPS.Output.DelayBeforeShutdown", "60", Rw | Str | Absent, null),
            new("ups.timer.start", 0, "UPS.Output.DelayBeforeStartup", "%.0f", QuickPoll, null),
            new("ups.timer.shutdown", 0, "UPS.Output.DelayBeforeShutdown", "%.0f", QuickPoll, null),
            new("ups.timer.reboot", 0, "UPS.Output.DelayBeforeReboot", "%.0f", QuickPoll, null),
            new("ups.firmware", 0, "UPS.PowerSummary.CPSFirmwareVersion", "%s", Static, CommonLookups.StringidConversion),
            new("ups.temperature", 0, "UPS.PowerSummary.Temperature", "%s", None, CommonLookups.KelvinCelsiusConversion),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ACPresent", null, QuickPoll, CommonLookups.OnlineInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Charging", null, QuickPoll, CommonLookups.ChargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Discharging", null, QuickPoll, CommonLookups.DischargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.BelowRemainingCapacityLimit", null, QuickPoll, CommonLookups.LowbattInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.FullyCharged", null, None, CommonLookups.FullychargedInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.RemainingTimeLimitExpired", null, None, CommonLookups.TimelimitexpiredInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.NeedReplacement", null, None, CommonLookups.ReplacebattInfo),
            new("BOOL", 0, "UPS.Output.Boost", null, None, CommonLookups.BoostInfo),
            new("BOOL", 0, "UPS.Output.Overload", null, None, CommonLookups.OverloadInfo),
            new("input.frequency", 0, "UPS.Input.Frequency", "%.1f", None, cpsInputFreq),
            new("input.voltage.nominal", 0, "UPS.Input.ConfigVoltage", "%.0f", None, null),
            new("input.voltage", 0, "UPS.Input.Voltage", "%.1f", None, null),
            new("input.transfer.low", 10, "UPS.Input.LowVoltageTransfer", "%.0f", Rw | Str | SemiStatic, null),
            new("input.transfer.high", 10, "UPS.Input.HighVoltageTransfer", "%.0f", Rw | Str | SemiStatic, null),
            new("input.transfer.low", 10, "UPS.Output.LowVoltageTransfer", "%.0f", Rw | Str | SemiStatic, null),
            new("input.transfer.high", 10, "UPS.Output.HighVoltageTransfer", "%.0f", Rw | Str | SemiStatic, null),
            new("input.sensitivity", 0, "UPS.Output.CPSInputSensitivity", "%s", Rw | Str | SemiStatic | Enumerated, CpsSensitivityInfo),
            new("output.frequency", 0, "UPS.Output.Frequency", "%.1f", None, cpsOutputFreq),
            new("output.voltage", 0, "UPS.Output.Voltage", "%.1f", None, null),
            new("output.voltage.nominal", 0, "UPS.Output.ConfigVoltage", "%.0f", None, null),
            new("test.battery.start.quick", 0, "UPS.Output.Test", "1", Cmd, null),
            new("test.battery.start.deep", 0, "UPS.Output.Test", "2", Cmd, null),
            new("test.battery.stop", 0, "UPS.Output.Test", "3", Cmd, null),
            new("load.off.delay", 0, "UPS.Output.DelayBeforeShutdown", "60", Cmd, null),
            new("load.on.delay", 0, "UPS.Output.DelayBeforeStartup", "120", Cmd, null),
            new("shutdown.stop", 0, "UPS.Output.DelayBeforeShutdown", "-1", Cmd, null),
            new("shutdown.reboot", 0, "UPS.Output.DelayBeforeReboot", "10", Cmd, null),
            new("beeper.on", 0, "UPS.PowerSummary.AudibleAlarmControl", "2", Cmd, null),
            new("beeper.off", 0, "UPS.PowerSummary.AudibleAlarmControl", "3", Cmd, null),
            new("beeper.enable", 0, "UPS.PowerSummary.AudibleAlarmControl", "2", Cmd, null),
            new("beeper.disable", 0, "UPS.PowerSummary.AudibleAlarmControl", "1", Cmd, null),
            new("beeper.mute", 0, "UPS.PowerSummary.AudibleAlarmControl", "3", Cmd, null),
        ];
    }
}
