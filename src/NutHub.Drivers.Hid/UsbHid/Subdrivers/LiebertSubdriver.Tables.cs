// Mapping tables ported from Network UPS Tools, drivers/liebert-hid.c (Phoenixtec/Liebert HID 0.42),
// GPL-2.0-or-later. Keep them in sync with that file rather than editing entries by hand.
using NutHub.Drivers.Hid.Descriptors;
using static NutHub.Drivers.Hid.UsbHid.HidMapFlags;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

internal sealed partial class LiebertSubdriver
{
    /// <summary>The NUT subdriver this one is ported from, reported as driver.version.data.</summary>
    public override string Version => "Phoenixtec/Liebert HID 0.42";

    /// <summary>Vendor-specific usage names (NUT liebert_usage_lkp).</summary>
    private static readonly HidUsageTable VendorUsageTable = HidUsageTable.Empty;

    /// <summary>The USB ids this subdriver supports (NUT liebert_usb_device_table).</summary>
    private static readonly UsbDeviceId[] DeviceIds =
    [
        new(0x06da, 0xffff),
    ];

    /// <summary>Builds the HID to NUT mapping table; function lookups bind to this instance's state.</summary>
    private HidMapping[] BuildMappings()
    {
        return
        [
            new("battery.voltage", 0, "UPS.PowerSummary.Voltage", "%.2f", None, null),
            new("battery.charge", 0, "UPS.PowerSummary.RemainingCapacity", "%.0f", None, null),
            new("experimental.battery.capacity", 0, "UPS.PowerSummary.FullChargeCapacity", "%.0f", None, null),
            new("experimental.battery.capacity.nominal", 0, "UPS.PowerSummary.DesignCapacity", "%.0f", None, null),
            new("battery.runtime", 0, "UPS.PowerSummary.RunTimeToEmpty", "%.0f", None, null),
            new("battery.type", 0, "UPS.PowerSummary.iDeviceChemistry", "%s", None, CommonLookups.StringidConversion),
            new("ups.load", 0, "UPS.PowerSummary.PercentLoad", "%.0f", None, null),
            new("ups.power.nominal", 0, "UPS.Flow.[4].ConfigApparentPower", "%.0f", SemiStatic, null),
            new("ups.test.result", 0, "UPS.BatterySystem.Battery.Test", "%s", None, CommonLookups.TestReadInfo),
            new("ups.beeper.status", 0, "UPS.PowerSummary.AudibleAlarmControl", "%s", SemiStatic, CommonLookups.BeeperInfo),
            new("output.voltage", 0, "UPS.PowerConverter.Output.Voltage", "%.1f", None, null),
            new("output.voltage.nominal", 0, "UPS.Flow.[4].ConfigVoltage", "%.0f", SemiStatic, null),
            new("output.frequency", 0, "UPS.PowerConverter.Output.Frequency", "%.2f", None, null),
            new("output.frequency.nominal", 0, "UPS.Flow.[4].ConfigFrequency", "%.0f", SemiStatic, null),
            new("output.transfer.high", 0, "UPS.PowerConverter.Output.HighVoltageTransfer", "%.1f", SemiStatic, null),
            new("output.transfer.low", 0, "UPS.PowerConverter.Output.LowVoltageTransfer", "%.1f", SemiStatic, null),
            new("input.voltage", 0, "UPS.PowerConverter.Input.[1].Voltage", "%.1f", None, null),
            new("input.frequency", 0, "UPS.PowerConverter.Input.[1].Frequency", "%.2f", None, null),
            new("input.transfer.low", 0, "UPS.PowerConverter.Output.ffff0057", "%.0f", SemiStatic, null),
            new("input.transfer.high", 0, "UPS.PowerConverter.Output.ffff0058", "%.0f", SemiStatic, null),
            new("input.frequency.transfer.low", 0, "UPS.PowerConverter.Output.ffff00f9", "%.0f", SemiStatic, null),
            new("input.frequency.transfer.high", 0, "UPS.PowerConverter.Output.ffff00f8", "%.0f", SemiStatic, null),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ACPresent", "%.0f", QuickPoll, CommonLookups.OnlineInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.BelowRemainingCapacityLimit", "%.0f", QuickPoll, CommonLookups.LowbattInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Charging", "%.0f", QuickPoll, CommonLookups.ChargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Discharging", "%.0f", QuickPoll, CommonLookups.DischargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Overload", "%.0f", QuickPoll, CommonLookups.OverloadInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Good", null, QuickPoll, CommonLookups.OffInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.InternalFailure", null, QuickPoll, CommonLookups.CommfaultInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ShutdownImminent", "%.0f", QuickPoll, CommonLookups.ShutdownimmInfo),
            new("BOOL", 0, "UPS.PowerConverter.Input.[1].PresentStatus.Buck", null, None, CommonLookups.TrimInfo),
            new("BOOL", 0, "UPS.PowerConverter.Input.[1].PresentStatus.Boost", null, None, CommonLookups.BoostInfo),
            new("ups.delay.start", 10, "UPS.PowerSummary.DelayBeforeStartup", "30", Rw | Str | Absent, null),
            new("ups.delay.shutdown", 10, "UPS.PowerSummary.DelayBeforeShutdown", "20", Rw | Str | Absent, null),
            new("test.battery.start", 0, "UPS.BatterySystem.Battery.Test", "1", Cmd, null),
            new("load.off.delay", 0, "UPS.PowerSummary.DelayBeforeShutdown", "20", Cmd, null),
            new("load.on.delay", 0, "UPS.PowerSummary.DelayBeforeStartup", "30", Cmd, null),
            new("shutdown.stop", 0, "UPS.PowerSummary.DelayBeforeShutdown", "-1", Cmd, null),
            new("beeper.toggle", 0, "UPS.PowerSummary.AudibleAlarmControl", "1", Cmd, null),
        ];
    }
}
