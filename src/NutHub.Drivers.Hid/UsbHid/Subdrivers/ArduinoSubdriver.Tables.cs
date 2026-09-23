// Mapping tables ported from Network UPS Tools, drivers/arduino-hid.c (Arduino HID 0.23),
// GPL-2.0-or-later. Keep them in sync with that file rather than editing entries by hand.
using NutHub.Drivers.Hid.Descriptors;
using static NutHub.Drivers.Hid.UsbHid.HidMapFlags;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

internal sealed partial class ArduinoSubdriver
{
    /// <summary>The NUT subdriver this one is ported from, reported as driver.version.data.</summary>
    public override string Version => "Arduino HID 0.23";

    /// <summary>Vendor-specific usage names (NUT arduino_usage_lkp).</summary>
    private static readonly HidUsageTable VendorUsageTable = HidUsageTable.Empty;

    /// <summary>The USB ids this subdriver supports (NUT arduino_usb_device_table).</summary>
    private static readonly UsbDeviceId[] DeviceIds =
    [
        new(0x2341, 0x0036),
        new(0x2341, 0x8036),
        new(0x2a03, 0x0036),
        new(0x2a03, 0x8036),
        new(0x2a03, 0x0040),
        new(0x2a03, 0x8040),
    ];

    /// <summary>Builds the HID to NUT mapping table; function lookups bind to this instance's state.</summary>
    private HidMapping[] BuildMappings()
    {
        return
        [
            new("ups.delay.start", 10, "UPS.PowerSummary.DelayBeforeStartup", "30", Rw | Str | Absent, null),
            new("ups.delay.shutdown", 10, "UPS.PowerSummary.DelayBeforeShutdown", "20", Rw | Str | Absent, null),
            new("ups.timer.start", 0, "UPS.PowerSummary.DelayBeforeStartup", "%.0f", QuickPoll, null),
            new("ups.timer.shutdown", 0, "UPS.PowerSummary.DelayBeforeShutdown", "%.0f", QuickPoll, null),
            new("ups.timer.reboot", 0, "UPS.PowerSummary.DelayBeforeReboot", "%.0f", QuickPoll, null),
            new("load.off.delay", 0, "UPS.PowerSummary.DelayBeforeShutdown", "20", Cmd, null),
            new("load.on.delay", 0, "UPS.PowerSummary.DelayBeforeStartup", "30", Cmd, null),
            new("shutdown.stop", 0, "UPS.PowerSummary.DelayBeforeShutdown", "-1", Cmd, null),
            new("shutdown.reboot", 0, "UPS.PowerSummary.DelayBeforeReboot", "10", Cmd, null),
            new("battery.type", 0, "UPS.PowerSummary.iDeviceChemistry", "%s", Static, CommonLookups.StringidConversion),
            new("battery.voltage.nominal", 0, "UPS.PowerSummary.ConfigVoltage", "%.2f", QuickPoll, null),
            new("battery.voltage", 0, "UPS.PowerSummary.Voltage", "%.2f", QuickPoll, null),
            new("battery.runtime", 0, "UPS.PowerSummary.RunTimeToEmpty", "%.0f", QuickPoll, null),
            new("battery.runtime.low", 0, "UPS.PowerSummary.RemainingTimeLimit", "%.0f", SemiStatic, null),
            new("battery.charge", 0, "UPS.PowerSummary.RemainingCapacity", "%.0f", None, null),
            new("battery.charge.low", 0, "UPS.PowerSummary.RemainingCapacityLimit", "%.0f", SemiStatic, null),
            new("battery.charge.warning", 0, "UPS.PowerSummary.WarningCapacityLimit", "%.0f", SemiStatic, null),
            new("ups.load", 0, "UPS.PowerSummary.PercentLoad", "%.0f", None, null),
            new("input.voltage", 0, "UPS.PowerConverter.Input.[1].Voltage", "%.1f", None, null),
            new("output.voltage", 0, "UPS.PowerConverter.Output.Voltage", "%.1f", None, null),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ACPresent", null, QuickPoll, CommonLookups.OnlineInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Discharging", null, QuickPoll, CommonLookups.DischargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Charging", null, QuickPoll, CommonLookups.ChargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.NeedReplacement", null, None, CommonLookups.ReplacebattInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ShutdownImminent", null, None, CommonLookups.ShutdownimmInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.BelowRemainingCapacityLimit", null, QuickPoll, CommonLookups.LowbattInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Overload", null, None, CommonLookups.OverloadInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.RemainingTimeLimitExpired", null, None, CommonLookups.TimelimitexpiredInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.BatteryPresent", null, None, CommonLookups.NobatteryInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.FullyCharged", null, QuickPoll, CommonLookups.FullychargedInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.FullyDischarged", null, QuickPoll, CommonLookups.DepletedInfo),
        ];
    }
}
