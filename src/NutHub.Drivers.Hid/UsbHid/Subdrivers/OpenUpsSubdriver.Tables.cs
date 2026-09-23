// Mapping tables ported from Network UPS Tools, drivers/openups-hid.c (openUPS HID 0.51),
// GPL-2.0-or-later. Keep them in sync with that file rather than editing entries by hand.
using NutHub.Drivers.Hid.Descriptors;
using static NutHub.Drivers.Hid.UsbHid.HidMapFlags;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

internal sealed partial class OpenUpsSubdriver
{
    /// <summary>The NUT subdriver this one is ported from, reported as driver.version.data.</summary>
    public override string Version => "openUPS HID 0.51";

    /// <summary>Vendor-specific usage names (NUT openups_usage_lkp).</summary>
    private static readonly HidUsageTable VendorUsageTable = new(
    [
        ("Cell1", 0x00000001),
        ("Cell2", 0x00000002),
        ("Cell3", 0x00000003),
        ("Cell4", 0x00000004),
        ("Cell5", 0x00000005),
        ("Cell6", 0x00000006),
    ]);

    /// <summary>The USB ids this subdriver supports (NUT openups_usb_device_table).</summary>
    private static readonly UsbDeviceId[] DeviceIds =
    [
        new(0x04d8, 0xd004, "get_voltage_multiplier"),
        new(0x04d8, 0xd005, "get_voltage_multiplier"),
    ];

    /// <summary>Builds the HID to NUT mapping table; function lookups bind to this instance's state.</summary>
    private HidMapping[] BuildMappings()
    {
        NutLookup openupsCchargeInfo = NutLookup.Create([], OpenupsScaleCchargeFun, null);
        NutLookup openupsCdischargeInfo = NutLookup.Create([], OpenupsScaleCdischargeFun, null);
        NutLookup openupsChargingInfo = NutLookup.Create([], OpenupsChargingFun, null);
        NutLookup openupsDischargingInfo = NutLookup.Create([], OpenupsDischargingFun, null);
        NutLookup openupsOffInfo = NutLookup.Create([], OpenupsOffFun, null);
        NutLookup openupsOnlineInfo = NutLookup.Create([], OpenupsOnlineFun, null);
        NutLookup openupsTemperatureInfo = NutLookup.Create([], OpenupsTemperatureFun, null);
        NutLookup openupsVinInfo = NutLookup.Create([], OpenupsScaleVinFun, null);
        NutLookup openupsVoutInfo = NutLookup.Create([], OpenupsScaleVoutFun, null);

        return
        [
            new("ups.serial", 0, "UPS.PowerSummary.iSerialNumber", "%s", None, CommonLookups.StringidConversion),
            new("battery.type", 0, "UPS.PowerSummary.iDeviceChemistry", "%s", Static, CommonLookups.StringidConversion),
            new("battery.mfr.date", 0, "UPS.PowerSummary.iOEMInformation", "%s", Static, CommonLookups.StringidConversion),
            new("battery.voltage", 0, "UPS.PowerSummary.Voltage", "%.2f", QuickPoll, null),
            new("battery.current", 0, "UPS.PowerSummary.Current", "%.3f", QuickPoll, null),
            new("battery.capacity", 0, "UPS.PowerSummary.DesignCapacity", "%.0f", Static, null),
            new("battery.charge", 0, "UPS.PowerSummary.RemainingCapacity", "%.0f", QuickPoll, null),
            new("battery.charge.low", 0, "UPS.PowerSummary.RemainingCapacityLimit", "%.0f", QuickPoll, null),
            new("battery.charge.warning", 0, "UPS.PowerSummary.WarningCapacityLimit", "%.0f", None, null),
            new("battery.runtime", 0, "UPS.PowerSummary.RunTimeToEmpty", "%.0f", QuickPoll, null),
            new("battery.temperature", 0, "UPS.PowerSummary.Temperature", null, QuickPoll, openupsTemperatureInfo),
            new("output.voltage", 0, "UPS.PowerSummary.Output.Voltage", null, QuickPoll, openupsVoutInfo),
            new("output.current", 0, "UPS.PowerSummary.Output.Current", null, QuickPoll, openupsCdischargeInfo),
            new("input.voltage", 0, "UPS.PowerSummary.Input.Voltage", null, QuickPoll, openupsVinInfo),
            new("input.current", 0, "UPS.PowerSummary.Input.Current", null, QuickPoll, openupsCchargeInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Good", null, QuickPoll, openupsOffInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.InternalFailure", null, QuickPoll, CommonLookups.CommfaultInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Overload", null, QuickPoll, CommonLookups.OverloadInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.OverTemperature", null, QuickPoll, CommonLookups.OverheatInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ShutdownImminent", null, QuickPoll, CommonLookups.ShutdownimmInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.BelowRemainingCapacityLimit", null, QuickPoll, CommonLookups.LowbattInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.RemainingTimeLimitExpired", null, QuickPoll, CommonLookups.TimelimitexpiredInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Charging", null, QuickPoll, openupsChargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Discharging", null, QuickPoll, openupsDischargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.NeedReplacement", null, None, CommonLookups.ReplacebattInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ACPresent", null, QuickPoll, openupsOnlineInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.BatteryPresent", null, QuickPoll, CommonLookups.NobatteryInfo),
        ];
    }
}
