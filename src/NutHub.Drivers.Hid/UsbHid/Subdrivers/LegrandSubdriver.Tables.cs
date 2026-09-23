// Mapping tables ported from Network UPS Tools, drivers/legrand-hid.c (Legrand HID 0.31),
// GPL-2.0-or-later. Keep them in sync with that file rather than editing entries by hand.
using NutHub.Drivers.Hid.Descriptors;
using static NutHub.Drivers.Hid.UsbHid.HidMapFlags;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

internal sealed partial class LegrandSubdriver
{
    /// <summary>The NUT subdriver this one is ported from, reported as driver.version.data.</summary>
    public override string Version => "Legrand HID 0.31";

    /// <summary>Vendor-specific usage names (NUT legrand_usage_lkp).</summary>
    private static readonly HidUsageTable VendorUsageTable = HidUsageTable.Empty;

    /// <summary>The USB ids this subdriver supports (NUT legrand_usb_device_table).</summary>
    private static readonly UsbDeviceId[] DeviceIds =
    [
        new(0x1cb0, 0x0038, "disable_interrupt_pipe"),
        new(0x1cb0, 0x0032, "disable_interrupt_pipe"),
    ];

    /// <summary>Builds the HID to NUT mapping table; function lookups bind to this instance's state.</summary>
    private HidMapping[] BuildMappings()
    {
        NutLookup legrandTimes100kInfo = NutLookup.Create([], LegrandTimes100k, null);
        NutLookup legrandTimes10MInfo = NutLookup.Create([], LegrandTimes10M, null);
        NutLookup legrandTimes10Info = NutLookup.Create([], LegrandTimes10, null);
        NutLookup legrandTimes1MInfo = NutLookup.Create([], LegrandTimes1M, null);

        return
        [
            new("input.voltage", 0, "UPS.Input.Voltage", "%.0f", None, null),
            new("input.voltage", 0, "UPS.PowerConverter.Input.Voltage", "%.0f", None, legrandTimes1MInfo),
            new("input.transfer.high", 0, "UPS.Input.HighVoltageTransfer", "%.0f", Static, null),
            new("input.transfer.high", 0, "UPS.PowerConverter.Output.HighVoltageTransfer", "%.0f", Static, legrandTimes10Info),
            new("input.transfer.low", 0, "UPS.Input.LowVoltageTransfer", "%.0f", Static, null),
            new("input.transfer.low", 0, "UPS.PowerConverter.Output.LowVoltageTransfer", "%.0f", Static, null),
            new("input.voltage.nominal", 0, "UPS.Input.ConfigVoltage", "%.0f", Static, null),
            new("input.voltage.nominal", 0, "UPS.Flow.ConfigVoltage", "%.0f", Static, null),
            new("battery.voltage.nominal", 0, "UPS.PowerSummary.ConfigVoltage", "%.0f", Static, CommonLookups.DivideBy10Conversion),
            new("battery.voltage.nominal", 0, "UPS.BatterySystem.Battery.ConfigVoltage", "%.0f", Static, null),
            new("battery.voltage", 0, "UPS.PowerSummary.Voltage", "%.0f", None, CommonLookups.DivideBy10Conversion),
            new("battery.voltage", 0, "UPS.BatterySystem.Battery.Voltage", "%.0f", None, legrandTimes100kInfo),
            new("battery.charge", 0, "UPS.PowerSummary.RemainingCapacity", "%.0f", None, null),
            new("battery.runtime", 0, "UPS.PowerSummary.RuntimeToEmpty", "%.0f", None, null),
            new("battery.charge.warning", 0, "UPS.PowerSummary.WarningCapacityLimit", "%.0f", Static, null),
            new("battery.charge.low", 0, "UPS.PowerSummary.RemainingCapacityLimit", "%.0f", Static, null),
            new("output.voltage", 0, "UPS.Output.Voltage", "%.0f", None, null),
            new("output.voltage", 0, "UPS.PowerConverter.Output.Voltage", "%.0f", None, legrandTimes10MInfo),
            new("output.frequency", 0, "UPS.Output.Frequency", "%.0f", None, null),
            new("ups.load", 0, "UPS.Output.PercentLoad", "%.0f", None, null),
            new("ups.load", 0, "UPS.OutletSystem.Outlet.PercentLoad", "%.0f", None, null),
            new("ups.realpower.nominal", 0, "UPS.Output.ConfigActivePower", "%.0f", Static, null),
            new("ups.realpower.nominal", 0, "UPS.Flow.ConfigApparentPower", "%.0f", Static, null),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ACPresent", null, QuickPoll, CommonLookups.OnlineInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.BelowRemainingCapacityLimit", null, QuickPoll, CommonLookups.LowbattInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Charging", null, QuickPoll, CommonLookups.ChargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Discharging", null, QuickPoll, CommonLookups.DischargingInfo),
            new("BOOL", 0, "UPS.Output.Overload", null, QuickPoll, CommonLookups.OverloadInfo),
            new("ups.delay.shutdown", 10, "UPS.OutletSystem.Outlet.DelayBeforeShutdown", "20", Rw | Str | Absent, null),
            new("ups.delay.start", 10, "UPS.OutletSystem.Outlet.DelayBeforeStartup", "30", Rw | Str | Absent, null),
            new("ups.delay.shutdown", 10, "UPS.Output.DelayBeforeShutdown", "20", Rw | Str | Absent, null),
            new("ups.delay.start", 10, "UPS.Output.DelayBeforeStartup", "30", Rw | Str | Absent, null),
            new("load.off.delay", 0, "UPS.OutletSystem.Outlet.DelayBeforeShutdown", "20", Cmd, null),
            new("load.on.delay", 0, "UPS.OutletSystem.Outlet.DelayBeforeStartup", "30", Cmd, null),
            new("load.off.delay", 0, "UPS.Output.DelayBeforeShutdown", "20", Cmd, null),
            new("load.on.delay", 0, "UPS.Output.DelayBeforeStartup", "30", Cmd, null),
            new("test.battery.start.quick", 0, "UPS.BatterySystem.Battery.Test", "1", Cmd, null),
            new("test.battery.start.deep", 0, "UPS.BatterySystem.Battery.Test", "2", Cmd, null),
            new("test.battery.stop", 0, "UPS.BatterySystem.Battery.Test", "3", Cmd, null),
            new("test.battery.start.quick", 0, "UPS.Output.Test", "1", Cmd, null),
            new("test.battery.start.deep", 0, "UPS.Output.Test", "2", Cmd, null),
            new("test.battery.stop", 0, "UPS.Output.Test", "3", Cmd, null),
            new("ups.beeper.status", 0, "UPS.PowerSummary.AudibleAlarmControl", "%s", SemiStatic, CommonLookups.BeeperInfo),
            new("beeper.disable", 0, "UPS.PowerSummary.AudibleAlarmControl", "1", Cmd, null),
            new("beeper.enable", 0, "UPS.PowerSummary.AudibleAlarmControl", "2", Cmd, null),
            new("beeper.mute", 0, "UPS.PowerSummary.AudibleAlarmControl", "3", Cmd, null),
        ];
    }
}
