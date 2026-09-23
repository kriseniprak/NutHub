// Mapping tables ported from Network UPS Tools, drivers/powervar-hid.c (Powervar HID 0.22),
// GPL-2.0-or-later. Keep them in sync with that file rather than editing entries by hand.
using NutHub.Drivers.Hid.Descriptors;
using static NutHub.Drivers.Hid.UsbHid.HidMapFlags;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

internal sealed partial class PowervarSubdriver
{
    /// <summary>The NUT subdriver this one is ported from, reported as driver.version.data.</summary>
    public override string Version => "Powervar HID 0.22";

    /// <summary>Vendor-specific usage names (NUT powervar_usage_lkp).</summary>
    private static readonly HidUsageTable VendorUsageTable = new(
    [
        ("POWERVAR1", 0xff000001),
    ]);

    /// <summary>The USB ids this subdriver supports (NUT powervar_usb_device_table).</summary>
    private static readonly UsbDeviceId[] DeviceIds =
    [
        new(0x4234, 0x0002),
    ];

    /// <summary>Builds the HID to NUT mapping table; function lookups bind to this instance's state.</summary>
    private HidMapping[] BuildMappings()
    {
        return
        [
            new("battery.type", 0, "UPS.PowerSummary.iDeviceChemistry", "%s", None, CommonLookups.StringidConversion),
            new("battery.charge", 0, "UPS.PowerSummary.RemainingCapacity", "%.0f", None, null),
            new("battery.capacity", 0, "UPS.PowerSummary.FullChargeCapacity", "%.0f", None, null),
            new("battery.runtime", 0, "UPS.PowerSummary.RuntimeToEmpty", "%.0f", None, null),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ACPresent", null, QuickPoll, CommonLookups.OnlineInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Discharging", null, QuickPoll, CommonLookups.DischargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Charging", null, QuickPoll, CommonLookups.ChargingInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ShutdownImminent", null, None, CommonLookups.ShutdownimmInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.BelowRemainingCapacityLimit", null, None, CommonLookups.LowbattInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Overload", null, None, CommonLookups.OverloadInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.NeedReplacement", null, None, CommonLookups.ReplacebattInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.RemainingTimeLimitExpired", null, None, CommonLookups.TimelimitexpiredInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.InternalFailure", null, None, CommonLookups.YesNoInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Good", null, None, CommonLookups.YesNoInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.OverTemperature", null, None, CommonLookups.OverheatInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.FullyCharged", null, None, CommonLookups.FullychargedInfo),
            new("BOOL", 0, "UPS.PowerSummary.PresentStatus.FullyDischarged", null, None, CommonLookups.DepletedInfo),
        ];
    }
}
