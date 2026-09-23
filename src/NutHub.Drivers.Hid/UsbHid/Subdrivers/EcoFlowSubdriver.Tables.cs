// Mapping tables ported from Network UPS Tools, drivers/ecoflow-hid.c (EcoFlow HID 0.02),
// GPL-2.0-or-later. Keep them in sync with that file rather than editing entries by hand.
using NutHub.Drivers.Hid.Descriptors;
using static NutHub.Drivers.Hid.UsbHid.HidMapFlags;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

internal sealed partial class EcoFlowSubdriver
{
    /// <summary>The NUT subdriver this one is ported from, reported as driver.version.data.</summary>
    public override string Version => "EcoFlow HID 0.02";

    /// <summary>Vendor-specific usage names (NUT ecoflow_usage_lkp).</summary>
    private static readonly HidUsageTable VendorUsageTable = HidUsageTable.Empty;

    /// <summary>The USB ids this subdriver supports (NUT ecoflow_usb_device_table).</summary>
    private static readonly UsbDeviceId[] DeviceIds =
    [
        new(0x3746, 0xffff),
    ];

    /// <summary>Builds the HID to NUT mapping table; function lookups bind to this instance's state.</summary>
    private HidMapping[] BuildMappings()
    {
        NutLookup ecoflowRuntimeConversionLkp = NutLookup.Create([], EcoflowBatteryRuntimeConversion, null);

        return
        [
            new("ups.beeper.status", 0, "UPS.PowerSummary.AudibleAlarmControl", "%s", QuickPoll, CommonLookups.BeeperInfo),
            new("ups.power.nominal", 0, "UPS.Flow.[4].ConfigActivePower", "%.0f", Static, null),
            new("battery.voltage.nominal", 0, "UPS.PowerSummary.ConfigVoltage", "%.1f", SemiStatic, null),
            new("ups.timer.reboot", 0, "UPS.OutletSystem.Outlet.DelayBeforeReboot", "%.0f", QuickPoll, null),
            new("ups.timer.shutdown", 0, "UPS.OutletSystem.Outlet.DelayBeforeShutdown", "%.0f", QuickPoll, null),
            new("battery.capacity.nominal", 0, "UPS.PowerSummary.DesignCapacity", "%.0f", SemiStatic, null),
            new("battery.capacity", 0, "UPS.PowerSummary.FullChargeCapacity", "%.0f", SemiStatic, null),
            new("battery.type", 0, "UPS.PowerSummary.iDeviceChemistry", "%s", Static, CommonLookups.StringidConversion),
            new("experimental.ups.powersummary.ioeminformation", 0, "UPS.PowerSummary.iOEMInformation", "%s", Static, CommonLookups.StringidConversion),
            new("ups.mfr.date", 0, "UPS.PowerSummary.ManufacturerDate", "%s", None, CommonLookups.DateConversion),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ACPresent", null, QuickPoll, CommonLookups.OnlineInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.BatteryPresent", null, QuickPoll, CommonLookups.NobatteryInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.BelowRemainingCapacityLimit", null, QuickPoll, CommonLookups.LowbattInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.CommunicationLost", null, QuickPoll, CommonLookups.CommfaultInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Charging", null, QuickPoll, CommonLookups.ChargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Discharging", null, QuickPoll, CommonLookups.DischargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.FullyCharged", null, QuickPoll, CommonLookups.FullychargedInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.FullyDischarged", null, QuickPoll, CommonLookups.DepletedInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.NeedReplacement", null, QuickPoll, CommonLookups.ReplacebattInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Overload", null, QuickPoll, CommonLookups.OverloadInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.RemainingTimeLimitExpired", null, QuickPoll, CommonLookups.TimelimitexpiredInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ShutdownImminent", null, QuickPoll, CommonLookups.ShutdownimmInfo),
            new("experimental.ups.powersummary.presentstatus.shutdownrequested", 0, "UPS.PowerSummary.PresentStatus.ShutdownRequested", "%.0f", None, null),
            new("experimental.ups.powersummary.presentstatus.voltagenotregulated", 0, "UPS.PowerSummary.PresentStatus.VoltageNotRegulated", "%.0f", None, null),
            new("experimental.ups.powersummary.rechargeable", 0, "UPS.PowerSummary.Rechargeable", "%.0f", None, null),
            new("battery.charge", 0, "UPS.PowerSummary.RemainingCapacity", "%.0f", QuickPoll, null),
            new("battery.charge.low", 5, "UPS.PowerSummary.RemainingCapacityLimit", "%.0f", Rw | Str | SemiStatic, null),
            new("battery.runtime.low", 0, "UPS.PowerSummary.RemainingTimeLimit", "%.0f", SemiStatic, null),
            new("battery.runtime", 0, "UPS.PowerSummary.RunTimeToEmpty", "%.0f", QuickPoll, ecoflowRuntimeConversionLkp),
            new("battery.voltage", 0, "UPS.PowerSummary.Voltage", "%.1f", QuickPoll, null),
            new("battery.charge.warning", 0, "UPS.PowerSummary.WarningCapacityLimit", "%.0f", SemiStatic, null),
        ];
    }
}
