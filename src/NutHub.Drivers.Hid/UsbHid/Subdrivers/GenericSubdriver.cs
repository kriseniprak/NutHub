using NutHub.Drivers.Hid.Transport;
using static NutHub.Drivers.Hid.UsbHid.HidMapFlags;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

/// <summary>
/// For UPSes of vendors no other subdriver knows: only the standard usages of the HID Power Device class
/// ("Universal Serial Bus Usage Tables for HID Power Devices" 1.0), in the paths the NUT subdrivers have seen
/// most often. NUT has no such subdriver and refuses unknown vendors; a UPS that follows the specification is
/// better served by this table than by nothing.
/// </summary>
internal sealed class GenericSubdriver : UsbHidSubdriver
{
    public override string Id => "generic";

    public override string DisplayName => "Generic HID Power Device";

    public override string Version => "Generic HID 1.0";

    public override IReadOnlyList<UsbDeviceId> SupportedDevices => [];

    /// <summary>Claimed only as the fallback, when the descriptor shows a Power Device application.</summary>
    public override bool Claim(HidDeviceInfo device, bool productIdGiven) => false;

    public override string? FormatManufacturer(HidDeviceInfo device, IHidConversionContext context) =>
        device.Manufacturer ?? context.ReadItemString("UPS.PowerSummary.iManufacturer");

    public override string? FormatModel(HidDeviceInfo device, IHidConversionContext context) =>
        device.Product ?? context.ReadItemString("UPS.PowerSummary.iProduct");

    public override string? FormatSerial(HidDeviceInfo device, IHidConversionContext context) =>
        device.Serial ?? context.ReadItemString("UPS.PowerSummary.iSerialNumber");

    protected override HidMapping[] CreateMappings() =>
    [
        // Battery
        new("battery.charge", 0, "UPS.PowerSummary.RemainingCapacity", "%.0f", None, null),
        new("battery.charge", 0, "UPS.BatterySystem.Battery.RemainingCapacity", "%.0f", None, null),
        new("battery.charge", 0, "UPS.Battery.RemainingCapacity", "%.0f", None, null),
        new("battery.charge.low", 10, "UPS.PowerSummary.RemainingCapacityLimit", "%.0f", Rw | Str | SemiStatic, null),
        new("battery.charge.low", 10, "UPS.Battery.RemainingCapacityLimit", "%.0f", Rw | Str | SemiStatic, null),
        new("battery.charge.warning", 0, "UPS.PowerSummary.WarningCapacityLimit", "%.0f", None, null),
        new("battery.runtime", 0, "UPS.PowerSummary.RunTimeToEmpty", "%.0f", None, null),
        new("battery.runtime", 0, "UPS.Battery.RunTimeToEmpty", "%.0f", None, null),
        new("battery.runtime.low", 10, "UPS.PowerSummary.RemainingTimeLimit", "%.0f", Rw | Str | SemiStatic, null),
        new("battery.runtime.low", 10, "UPS.Battery.RemainingTimeLimit", "%.0f", Rw | Str | SemiStatic, null),
        new("battery.voltage", 0, "UPS.PowerSummary.Voltage", "%.1f", None, null),
        new("battery.voltage", 0, "UPS.BatterySystem.Battery.Voltage", "%.1f", None, null),
        new("battery.voltage", 0, "UPS.Battery.Voltage", "%.1f", None, null),
        new("battery.voltage.nominal", 0, "UPS.PowerSummary.ConfigVoltage", "%.1f", Static, null),
        new("battery.voltage.nominal", 0, "UPS.Battery.ConfigVoltage", "%.1f", Static, null),
        new("battery.temperature", 0, "UPS.Battery.Temperature", "%s", None, CommonLookups.KelvinCelsiusConversion),
        new("battery.temperature", 0, "UPS.BatterySystem.Battery.Temperature", "%s", None, CommonLookups.KelvinCelsiusConversion),
        new("battery.type", 0, "UPS.PowerSummary.iDeviceChemistry", "%s", Static, CommonLookups.StringidConversion),
        new("battery.mfr.date", 0, "UPS.PowerSummary.ManufacturerDate", "%s", Static, CommonLookups.DateConversion),
        new("battery.mfr.date", 0, "UPS.Battery.ManufacturerDate", "%s", Static, CommonLookups.DateConversion),

        // UPS
        new("ups.load", 0, "UPS.PowerSummary.PercentLoad", "%.0f", None, null),
        new("ups.load", 0, "UPS.Output.PercentLoad", "%.0f", None, null),
        new("ups.load", 0, "UPS.PowerConverter.PercentLoad", "%.0f", None, null),
        new("ups.temperature", 0, "UPS.PowerSummary.Temperature", "%s", None, CommonLookups.KelvinCelsiusConversion),
        new("ups.power", 0, "UPS.Output.ApparentPower", "%.0f", None, null),
        new("ups.power.nominal", 0, "UPS.Output.ConfigApparentPower", "%.0f", Static, null),
        new("ups.power.nominal", 0, "UPS.Flow.ConfigApparentPower", "%.0f", Static, null),
        new("ups.realpower", 0, "UPS.Output.ActivePower", "%.0f", None, null),
        new("ups.realpower.nominal", 0, "UPS.Output.ConfigActivePower", "%.0f", Static, null),
        new("ups.realpower.nominal", 0, "UPS.PowerConverter.ConfigActivePower", "%.0f", Static, null),
        new("ups.beeper.status", 0, "UPS.PowerSummary.AudibleAlarmControl", "%s", None, CommonLookups.BeeperInfo),
        new("ups.test.result", 0, "UPS.BatterySystem.Battery.Test", "%s", None, CommonLookups.TestReadInfo),
        new("ups.test.result", 0, "UPS.Battery.Test", "%s", None, CommonLookups.TestReadInfo),
        new("ups.mfr.date", 0, "UPS.PowerSummary.ManufacturerDate", "%s", Static, CommonLookups.DateConversion),
        new("ups.delay.start", 10, "UPS.PowerSummary.DelayBeforeStartup", "30", Rw | Str | Absent, null),
        new("ups.delay.shutdown", 10, "UPS.PowerSummary.DelayBeforeShutdown", "20", Rw | Str | Absent, null),
        new("ups.delay.start", 10, "UPS.Output.DelayBeforeStartup", "30", Rw | Str | Absent, null),
        new("ups.delay.shutdown", 10, "UPS.Output.DelayBeforeShutdown", "20", Rw | Str | Absent, null),
        new("ups.timer.start", 0, "UPS.PowerSummary.DelayBeforeStartup", "%.0f", QuickPoll, null),
        new("ups.timer.shutdown", 0, "UPS.PowerSummary.DelayBeforeShutdown", "%.0f", QuickPoll, null),
        new("ups.timer.reboot", 0, "UPS.PowerSummary.DelayBeforeReboot", "%.0f", QuickPoll, null),
        new("ups.timer.start", 0, "UPS.Output.DelayBeforeStartup", "%.0f", QuickPoll, null),
        new("ups.timer.shutdown", 0, "UPS.Output.DelayBeforeShutdown", "%.0f", QuickPoll, null),
        new("ups.timer.reboot", 0, "UPS.Output.DelayBeforeReboot", "%.0f", QuickPoll, null),

        // Status
        new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ACPresent", null, QuickPoll, CommonLookups.OnlineInfo),
        new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Discharging", null, QuickPoll, CommonLookups.DischargingInfo),
        new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Charging", null, QuickPoll, CommonLookups.ChargingInfo),
        new("BOOL", 0, "UPS.PowerSummary.PresentStatus.BelowRemainingCapacityLimit", null, QuickPoll, CommonLookups.LowbattInfo),
        new("BOOL", 0, "UPS.PowerSummary.PresentStatus.RemainingTimeLimitExpired", null, QuickPoll, CommonLookups.TimelimitexpiredInfo),
        new("BOOL", 0, "UPS.PowerSummary.PresentStatus.ShutdownImminent", null, QuickPoll, CommonLookups.ShutdownimmInfo),
        new("BOOL", 0, "UPS.PowerSummary.PresentStatus.FullyCharged", null, QuickPoll, CommonLookups.FullychargedInfo),
        new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Overload", null, QuickPoll, CommonLookups.OverloadInfo),
        new("BOOL", 0, "UPS.PowerSummary.PresentStatus.NeedReplacement", null, QuickPoll, CommonLookups.ReplacebattInfo),
        new("BOOL", 0, "UPS.PowerSummary.PresentStatus.BatteryPresent", null, QuickPoll, CommonLookups.NobatteryInfo),
        new("BOOL", 0, "UPS.PowerSummary.PresentStatus.CommunicationLost", null, QuickPoll, CommonLookups.CommfaultInfo),
        new("BOOL", 0, "UPS.PowerSummary.PresentStatus.InternalFailure", null, QuickPoll, CommonLookups.CommfaultInfo),
        new("BOOL", 0, "UPS.PowerSummary.PresentStatus.OverTemperature", null, QuickPoll, CommonLookups.OverheatInfo),
        new("BOOL", 0, "UPS.PowerSummary.PresentStatus.AwaitingPower", null, QuickPoll, CommonLookups.AwaitingpowerInfo),
        new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Boost", null, QuickPoll, CommonLookups.BoostInfo),
        new("BOOL", 0, "UPS.PowerSummary.PresentStatus.Buck", null, QuickPoll, CommonLookups.TrimInfo),
        new("BOOL", 0, "UPS.PowerSummary.PresentStatus.VoltageOutOfRange", null, QuickPoll, CommonLookups.VrangeInfo),
        new("BOOL", 0, "UPS.PowerSummary.PresentStatus.FrequencyOutOfRange", null, QuickPoll, CommonLookups.FrangeInfo),
        new("BOOL", 0, "UPS.PowerSummary.ACPresent", null, QuickPoll, CommonLookups.OnlineInfo),
        new("BOOL", 0, "UPS.PowerSummary.Discharging", null, QuickPoll, CommonLookups.DischargingInfo),
        new("BOOL", 0, "UPS.PowerSummary.Charging", null, QuickPoll, CommonLookups.ChargingInfo),
        new("BOOL", 0, "UPS.PowerSummary.BelowRemainingCapacityLimit", null, QuickPoll, CommonLookups.LowbattInfo),
        new("BOOL", 0, "UPS.PowerSummary.ShutdownImminent", null, QuickPoll, CommonLookups.ShutdownimmInfo),
        new("BOOL", 0, "UPS.PresentStatus.ACPresent", null, QuickPoll, CommonLookups.OnlineInfo),
        new("BOOL", 0, "UPS.PresentStatus.Discharging", null, QuickPoll, CommonLookups.DischargingInfo),
        new("BOOL", 0, "UPS.PresentStatus.Charging", null, QuickPoll, CommonLookups.ChargingInfo),
        new("BOOL", 0, "UPS.PresentStatus.BelowRemainingCapacityLimit", null, QuickPoll, CommonLookups.LowbattInfo),
        new("BOOL", 0, "UPS.PresentStatus.ShutdownImminent", null, QuickPoll, CommonLookups.ShutdownimmInfo),
        new("BOOL", 0, "UPS.PresentStatus.Overload", null, QuickPoll, CommonLookups.OverloadInfo),
        new("BOOL", 0, "UPS.PresentStatus.NeedReplacement", null, QuickPoll, CommonLookups.ReplacebattInfo),
        new("BOOL", 0, "UPS.PowerConverter.PresentStatus.Boost", null, QuickPoll, CommonLookups.BoostInfo),
        new("BOOL", 0, "UPS.PowerConverter.PresentStatus.Buck", null, QuickPoll, CommonLookups.TrimInfo),

        // Input and output
        new("input.voltage", 0, "UPS.Input.Voltage", "%.1f", None, null),
        new("input.voltage", 0, "UPS.PowerConverter.Input.Voltage", "%.1f", None, null),
        new("input.voltage.nominal", 0, "UPS.Input.ConfigVoltage", "%.0f", Static, null),
        new("input.frequency", 0, "UPS.Input.Frequency", "%.1f", None, null),
        new("input.frequency", 0, "UPS.PowerConverter.Input.Frequency", "%.1f", None, null),
        new("input.transfer.low", 10, "UPS.Input.LowVoltageTransfer", "%.0f", Rw | Str | SemiStatic, null),
        new("input.transfer.high", 10, "UPS.Input.HighVoltageTransfer", "%.0f", Rw | Str | SemiStatic, null),
        new("input.transfer.low", 10, "UPS.Output.LowVoltageTransfer", "%.0f", Rw | Str | SemiStatic, null),
        new("input.transfer.high", 10, "UPS.Output.HighVoltageTransfer", "%.0f", Rw | Str | SemiStatic, null),
        new("output.voltage", 0, "UPS.Output.Voltage", "%.1f", None, null),
        new("output.voltage", 0, "UPS.PowerConverter.Output.Voltage", "%.1f", None, null),
        new("output.voltage.nominal", 0, "UPS.Output.ConfigVoltage", "%.0f", Static, null),
        new("output.frequency", 0, "UPS.Output.Frequency", "%.1f", None, null),
        new("output.frequency", 0, "UPS.PowerConverter.Output.Frequency", "%.1f", None, null),
        new("output.frequency.nominal", 0, "UPS.Output.ConfigFrequency", "%.0f", Static, null),
        new("output.current", 0, "UPS.Output.Current", "%.2f", None, null),

        // Instant commands
        new("test.battery.start.quick", 0, "UPS.BatterySystem.Battery.Test", "1", Cmd, null),
        new("test.battery.start.quick", 0, "UPS.Battery.Test", "1", Cmd, null),
        new("test.battery.start.deep", 0, "UPS.BatterySystem.Battery.Test", "2", Cmd, null),
        new("test.battery.start.deep", 0, "UPS.Battery.Test", "2", Cmd, null),
        new("test.battery.stop", 0, "UPS.BatterySystem.Battery.Test", "3", Cmd, null),
        new("test.battery.stop", 0, "UPS.Battery.Test", "3", Cmd, null),
        new("load.off.delay", 0, "UPS.PowerSummary.DelayBeforeShutdown", "20", Cmd, null),
        new("load.on.delay", 0, "UPS.PowerSummary.DelayBeforeStartup", "30", Cmd, null),
        new("shutdown.stop", 0, "UPS.PowerSummary.DelayBeforeShutdown", "-1", Cmd, null),
        new("shutdown.reboot", 0, "UPS.PowerSummary.DelayBeforeReboot", "10", Cmd, null),
        new("load.off.delay", 0, "UPS.Output.DelayBeforeShutdown", "20", Cmd, null),
        new("load.on.delay", 0, "UPS.Output.DelayBeforeStartup", "30", Cmd, null),
        new("shutdown.stop", 0, "UPS.Output.DelayBeforeShutdown", "-1", Cmd, null),
        new("shutdown.reboot", 0, "UPS.Output.DelayBeforeReboot", "10", Cmd, null),
        new("beeper.enable", 0, "UPS.PowerSummary.AudibleAlarmControl", "2", Cmd, null),
        new("beeper.disable", 0, "UPS.PowerSummary.AudibleAlarmControl", "1", Cmd, null),
        new("beeper.mute", 0, "UPS.PowerSummary.AudibleAlarmControl", "3", Cmd, null),
    ];
}
