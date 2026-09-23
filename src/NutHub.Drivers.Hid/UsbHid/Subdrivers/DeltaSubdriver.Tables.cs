// Mapping tables ported from Network UPS Tools, drivers/delta_ups-hid.c (Delta UPS HID 0.7),
// GPL-2.0-or-later. Keep them in sync with that file rather than editing entries by hand.
using NutHub.Drivers.Hid.Descriptors;
using static NutHub.Drivers.Hid.UsbHid.HidMapFlags;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

internal sealed partial class DeltaSubdriver
{
    /// <summary>The NUT subdriver this one is ported from, reported as driver.version.data.</summary>
    public override string Version => "Delta UPS HID 0.7";

    /// <summary>Vendor-specific usage names (NUT delta_ups_usage_lkp).</summary>
    private static readonly HidUsageTable VendorUsageTable = new(
    [
        ("DELTA1", 0x00000000),
        ("DELTA2", 0xff000055),
        ("DeltaCustom", 0xffff0010),
        ("DELTA4", 0xffff0056),
        ("DeltaConfigTransferLowMax", 0xffff0057),
        ("DeltaConfigTransferLowMin", 0xffff0058),
        ("DeltaConfigTransferHighMax", 0xffff0059),
        ("DeltaConfigTransferHighMin", 0xffff005a),
        ("DELTA9", 0xffff0060),
        ("DeltaConfigExternalBatteryPack", 0xffff0061),
        ("DELTA11", 0xffff0062),
        ("DELTA12", 0xffff0063),
        ("DELTA13", 0xffff0064),
        ("DELTA14", 0xffff0065),
        ("DELTA15", 0xffff0066),
        ("DELTA16", 0xffff0067),
        ("DELTA17", 0xffff0068),
        ("DeltaModelName", 0xffff0075),
        ("DELTA19", 0xffff0076),
        ("DeltaUPSType", 0xffff007c),
        ("DELTA21", 0xffff007d),
        ("DeltaConfigStartPowerRestoreDelay", 0xffff0081),
        ("DeltaOutputSource", 0xffff0091),
        ("DELTA24", 0xffff0092),
        ("DELTA25", 0xffff0093),
        ("DELTA26", 0xffff0094),
        ("DELTA27", 0xffff0095),
        ("DELTA28", 0xffff0096),
        ("DELTA29", 0xffff0097),
        ("DELTA30", 0xffff0098),
        ("DELTA31", 0xffff0099),
        ("DELTA32", 0xffff009a),
        ("DeltaConfigSensitivity", 0xffff009b),
        ("DeltaConfigStartPowerRestore", 0xffff009c),
    ]);

    /// <summary>The USB ids this subdriver supports (NUT delta_ups_usb_device_table).</summary>
    private static readonly UsbDeviceId[] DeviceIds =
    [
        new(0x05dd, 0x041b),
    ];

    private static readonly NutLookup DeltaUpsOutputSourceInfo = NutLookup.Table([(0, "normal"), (1, "battery"), (2, "bypass/reserve"), (3, "reducing"), (4, "boosting"), (5, "manual bypass"), (6, "other"), (7, "no output"), (8, "on eco")]);

    private static readonly NutLookup DeltaUpsSensitivityInfo = NutLookup.Table([(0, "normal"), (1, "reduced"), (2, "low")]);

    /// <summary>Builds the HID to NUT mapping table; function lookups bind to this instance's state.</summary>
    private HidMapping[] BuildMappings()
    {
        NutLookup deltaUpsTypeInfo = NutLookup.Create([], DeltaUpsTypeFun, null);

        return
        [
            new("input.sensitivity", 0, "UPS.DeltaCustom.[1].DeltaConfigSensitivity", "%s", Rw, DeltaUpsSensitivityInfo),
            new("input.voltage.nominal", 0, "UPS.PowerSummary.Input.ConfigVoltage", "%.1f", SemiStatic, null),
            new("input.voltage", 0, "UPS.PowerSummary.Input.Voltage", "%.1f", QuickPoll, null),
            new("input.voltage", 0, "UPS.PowerConverter.Input.Voltage", "%.1f", QuickPoll, null),
            new("input.transfer.low", 0, "UPS.PowerConverter.Output.LowVoltageTransfer", "%.1f", Rw, null),
            new("input.transfer.high", 0, "UPS.PowerConverter.Output.HighVoltageTransfer", "%.1f", Rw, null),
            new("input.transfer.low.min", 0, "UPS.PowerConverter.Output.DeltaConfigTransferLowMin", "%.1f", Static, null),
            new("input.transfer.low.max", 0, "UPS.PowerConverter.Output.DeltaConfigTransferLowMax", "%.1f", Static, null),
            new("input.transfer.high.min", 0, "UPS.PowerConverter.Output.DeltaConfigTransferHighMin", "%.1f", Static, null),
            new("input.transfer.high.max", 0, "UPS.PowerConverter.Output.DeltaConfigTransferHighMax", "%.1f", Static, null),
            new("input.source", 0, "UPS.OutletSystem.Outlet.DeltaOutputSource", "%s", None, DeltaUpsOutputSourceInfo),
            new("input.frequency", 0, "UPS.PowerConverter.Input.Frequency", "%.1f", QuickPoll, null),
            new("battery.voltage.nominal", 0, "UPS.BatterySystem.Battery.ConfigVoltage", "%.1f", Static, null),
            new("battery.voltage", 0, "UPS.BatterySystem.Battery.Voltage", "%.1f", QuickPoll, null),
            new("battery.charge", 0, "UPS.PowerSummary.RemainingCapacity", "%.0f", QuickPoll, null),
            new("battery.charge", 0, "UPS.BatterySystem.Battery.RemainingCapacity", "%.0f", None, null),
            new("battery.charge.low", 5, "UPS.PowerSummary.RemainingCapacityLimit", "%.0f", Rw | Str | SemiStatic, null),
            new("battery.charge.warning", 0, "UPS.PowerSummary.WarningCapacityLimit", "%.0f", SemiStatic, null),
            new("battery.temperature", 0, "UPS.BatterySystem.Temperature", "%s", QuickPoll, CommonLookups.KelvinCelsiusConversion),
            new("battery.runtime", 0, "UPS.PowerSummary.RunTimeToEmpty", "%.0f", QuickPoll, null),
            new("battery.type", 0, "UPS.PowerSummary.iDeviceChemistry", "%s", Static, CommonLookups.StringidConversion),
            new("battery.capacity", 0, "UPS.PowerSummary.DesignCapacity", "%.0f", SemiStatic, null),
            new("battery.capacity", 0, "UPS.PowerSummary.FullChargeCapacity", "%.0f", SemiStatic, null),
            new("output.voltage.nominal", 0, "UPS.Flow.ConfigVoltage", "%.1f", SemiStatic, null),
            new("output.frequency.nominal", 0, "UPS.Flow.ConfigFrequency", "%.1f", SemiStatic, null),
            new("output.voltage", 0, "UPS.PowerConverter.Output.Voltage", "%.1f", QuickPoll, null),
            new("output.frequency", 0, "UPS.PowerConverter.Output.Frequency", "%.1f", QuickPoll, null),
            new("output.current", 0, "UPS.PowerConverter.Output.Current", "%.1f", QuickPoll, null),
            new("ups.beeper.status", 0, "UPS.PowerSummary.AudibleAlarmControl", "%s", QuickPoll, CommonLookups.BeeperInfo),
            new("ups.test.result", 0, "UPS.BatterySystem.Test", "%s", None, CommonLookups.TestReadInfo),
            new("ups.type", 0, "UPS.DeltaCustom.[1].DeltaUPSType", "%s", Static, deltaUpsTypeInfo),
            new("ups.start.auto", 0, "UPS.DeltaCustom.[1].DeltaConfigStartPowerRestore", "%s", Rw, CommonLookups.YesNoInfo),
            new("ups.power.nominal", 0, "UPS.Flow.ConfigApparentPower", "%.0f", Static, null),
            new("ups.realpower", 0, "UPS.OutletSystem.Outlet.ActivePower", "%.1f", QuickPoll, null),
            new("ups.realpower", 0, "UPS.PowerConverter.Output.ActivePower", "%.1f", QuickPoll, null),
            new("ups.load", 0, "UPS.OutletSystem.Outlet.PercentLoad", "%.1f", QuickPoll, null),
            new("ups.delay.start", 0, "UPS.OutletSystem.Outlet.DeltaConfigStartPowerRestoreDelay", "%.0f", Rw, null),
            new("ups.timer.start", 0, "UPS.OutletSystem.Outlet.DelayBeforeStartup", "%.0f", QuickPoll, null),
            new("ups.timer.shutdown", 0, "UPS.OutletSystem.Outlet.DelayBeforeShutdown", "%.0f", QuickPoll, null),
            new("ups.timer.reboot", 0, "UPS.OutletSystem.Outlet.DelayBeforeReboot", "%.0f", QuickPoll, null),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Good", null, QuickPoll, CommonLookups.OffInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.InternalFailure", null, QuickPoll, CommonLookups.CommfaultInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ShutdownImminent", null, QuickPoll, CommonLookups.ShutdownimmInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ACPresent", null, QuickPoll, CommonLookups.OnlineInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.BelowRemainingCapacityLimit", null, QuickPoll, CommonLookups.LowbattInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.FullyCharged", null, QuickPoll, CommonLookups.FullychargedInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Charging", null, QuickPoll, CommonLookups.ChargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Discharging", null, QuickPoll, CommonLookups.DischargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.FullyDischarged", null, QuickPoll, CommonLookups.DepletedInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.NeedReplacement", null, QuickPoll, CommonLookups.ReplacebattInfo),
            new("BOOL", 0, "UPS.PowerConverter.PresentStatus.VoltageOutOfRange", null, QuickPoll, CommonLookups.VrangeInfo),
            new("BOOL", 0, "UPS.PowerConverter.PresentStatus.Buck", null, QuickPoll, CommonLookups.TrimInfo),
            new("BOOL", 0, "UPS.PowerConverter.PresentStatus.Boost", null, QuickPoll, CommonLookups.BoostInfo),
            new("BOOL", 0, "UPS.PowerConverter.PresentStatus.Overload", null, QuickPoll, CommonLookups.OverloadInfo),
            new("BOOL", 0, "UPS.PowerConverter.PresentStatus.Used", null, QuickPoll, CommonLookups.NobatteryInfo),
            new("BOOL", 0, "UPS.PowerConverter.PresentStatus.OverTemperature", null, QuickPoll, CommonLookups.OverheatInfo),
            new("BOOL", 0, "UPS.PowerConverter.PresentStatus.InternalFailure", null, QuickPoll, CommonLookups.CommfaultInfo),
            new("BOOL", 0, "UPS.PowerConverter.PresentStatus.AwaitingPower", null, QuickPoll, CommonLookups.AwaitingpowerInfo),
            new("beeper.on", 0, "UPS.PowerSummary.AudibleAlarmControl", "2", Cmd, null),
            new("beeper.off", 0, "UPS.PowerSummary.AudibleAlarmControl", "3", Cmd, null),
            new("beeper.enable", 0, "UPS.PowerSummary.AudibleAlarmControl", "2", Cmd, null),
            new("beeper.disable", 0, "UPS.PowerSummary.AudibleAlarmControl", "1", Cmd, null),
            new("beeper.mute", 0, "UPS.PowerSummary.AudibleAlarmControl", "3", Cmd, null),
            new("test.battery.start.quick", 0, "UPS.BatterySystem.Test", "1", Cmd, null),
            new("test.battery.start.deep", 0, "UPS.BatterySystem.Test", "2", Cmd, null),
            new("test.battery.stop", 0, "UPS.BatterySystem.Test", "3", Cmd, null),
            new("load.on.delay", 0, "UPS.OutletSystem.Outlet.DelayBeforeStartup", "30", Cmd, null),
            new("load.off.delay", 0, "UPS.OutletSystem.Outlet.DelayBeforeShutdown", "20", Cmd, null),
            new("shutdown.stop", 0, "UPS.OutletSystem.Outlet.DelayBeforeShutdown", "-1", Cmd, null),
            new("shutdown.reboot", 0, "UPS.OutletSystem.Outlet.DelayBeforeReboot", "10", Cmd, null),
        ];
    }
}
