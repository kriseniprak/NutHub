// Mapping tables ported from Network UPS Tools, drivers/salicru-hid.c (Salicru HID 0.5),
// GPL-2.0-or-later. Keep them in sync with that file rather than editing entries by hand.
using NutHub.Drivers.Hid.Descriptors;
using static NutHub.Drivers.Hid.UsbHid.HidMapFlags;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

internal sealed partial class SalicruSubdriver
{
    /// <summary>The NUT subdriver this one is ported from, reported as driver.version.data.</summary>
    public override string Version => "Salicru HID 0.5";

    /// <summary>Vendor-specific usage names (NUT salicru_usage_lkp).</summary>
    private static readonly HidUsageTable VendorUsageTable = HidUsageTable.Empty;

    /// <summary>The USB ids this subdriver supports (NUT salicru_usb_device_table).</summary>
    private static readonly UsbDeviceId[] DeviceIds =
    [
        new(0x2e66, 0x0101),
        new(0x2e66, 0x0201),
        new(0x2e66, 0x0202),
        new(0x2e66, 0x0203),
        new(0x2e66, 0x0300),
        new(0x2e66, 0x0302),
    ];

    /// <summary>Builds the HID to NUT mapping table; function lookups bind to this instance's state.</summary>
    private HidMapping[] BuildMappings()
    {
        return
        [
            new("battery.type", 0, "UPS.PowerSummary.iDeviceChemistry", "%s", None, CommonLookups.StringidConversion),
            new("battery.charge", 0, "UPS.PowerSummary.RemainingCapacity", "%.0f", None, null),
            new("battery.charge.warning", 0, "UPS.PowerSummary.WarningCapacityLimit", "%.0f", None, null),
            new("battery.charge.low", 10, "UPS.PowerSummary.RemainingCapacityLimit", "%.0f", Rw | Str | SemiStatic, null),
            new("battery.runtime", 0, "UPS.PowerSummary.RunTimeToEmpty", "%.0f", None, null),
            new("battery.runtime.low", 10, "UPS.PowerSummary.RemainingTimeLimit", "%.0f", Rw | Str | SemiStatic, null),
            new("battery.voltage.nominal", 0, "UPS.PowerSummary.ConfigVoltage", "%.0f", None, null),
            new("battery.voltage", 0, "UPS.PowerSummary.Voltage", "%.2f", None, null),
            new("ups.load", 0, "UPS.Output.PercentLoad", "%.0f", None, null),
            new("ups.beeper.status", 0, "UPS.PowerSummary.AudibleAlarmControl", "%s", None, CommonLookups.BeeperInfo),
            new("ups.test.result", 0, "UPS.Output.Test", "%s", None, CommonLookups.TestReadInfo),
            new("ups.realpower.nominal", 0, "UPS.Output.ConfigActivePower", "%.0f", None, null),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ACPresent", null, QuickPoll, CommonLookups.OnlineInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Charging", null, QuickPoll, CommonLookups.ChargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Discharging", null, QuickPoll, CommonLookups.DischargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.BelowRemainingCapacityLimit", null, QuickPoll, CommonLookups.LowbattInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.FullyCharged", null, None, CommonLookups.FullychargedInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.RemainingTimeLimitExpired", null, None, CommonLookups.TimelimitexpiredInfo),
            new("BOOL", 0, "UPS.Output.CommunicationLost", null, None, CommonLookups.CommfaultInfo),
            new("BOOL", 0, "UPS.Output.Boost", null, None, CommonLookups.BoostInfo),
            new("BOOL", 0, "UPS.Output.Overload", null, None, CommonLookups.OverloadInfo),
            new("input.frequency", 0, "UPS.Input.Frequency", "%.1f", None, null),
            new("input.voltage.nominal", 0, "UPS.Input.ConfigVoltage", "%.0f", None, null),
            new("input.voltage", 0, "UPS.Input.Voltage", "%.1f", None, null),
            new("input.transfer.high", 0, "UPS.PowerConverter.Output.HighVoltageTransfer", "%.0f", Static, null),
            new("input.transfer.low", 0, "UPS.PowerConverter.Output.LowVoltageTransfer", "%.0f", Static, null),
            new("output.frequency", 0, "UPS.Output.Frequency", "%.1f", None, null),
            new("output.voltage", 0, "UPS.Output.Voltage", "%.1f", None, null),
            new("output.voltage.nominal", 0, "UPS.Output.ConfigVoltage", "%.0f", None, null),
            new("ups.load", 0, "UPS.PowerSummary.PercentLoad", "%.0f", None, null),
            new("ups.test.result", 0, "UPS.BatterySystem.Battery.Test", "%s", None, CommonLookups.TestReadInfo),
            new("ups.realpower.nominal", 0, "UPS.Flow.[4].ConfigActivePower", "%.0f", None, null),
            new("ups.realpower", 0, "UPS.PowerConverter.Output.ActivePower", "%.0f", None, null),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Overload", null, None, CommonLookups.OverloadInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.OverTemperature", null, None, CommonLookups.OverheatInfo),
            new("input.frequency", 0, "UPS.PowerConverter.Input.[1].Frequency", "%.1f", None, null),
            new("input.voltage", 0, "UPS.PowerConverter.Input.[1].Voltage", "%.1f", None, null),
            new("output.frequency", 0, "UPS.PowerConverter.Output.Frequency", "%.1f", None, null),
            new("output.voltage", 0, "UPS.PowerConverter.Output.Voltage", "%.1f", None, null),
            new("output.voltage.nominal", 0, "UPS.PowerSummary.ConfigVoltage", "%.0f", None, null),
            new("ups.test.result", 0, "UPS.BatterySystem.Battery.Test", "%s", None, CommonLookups.TestReadInfo),
            new("test.battery.start.quick", 0, "UPS.BatterySystem.Battery.Test", "1", Cmd, null),
            new("test.battery.start.deep", 0, "UPS.BatterySystem.Battery.Test", "2", Cmd, null),
            new("test.battery.stop", 0, "UPS.BatterySystem.Battery.Test", "3", Cmd, null),
        ];
    }
}
