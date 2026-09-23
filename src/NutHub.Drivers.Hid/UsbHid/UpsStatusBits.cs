namespace NutHub.Drivers.Hid.UsbHid;

/// <summary>
/// The raw status information collected from the device, before it is turned into ups.status (NUT usbhid-ups.h
/// status_bit_t). Several HID flags can drive the same NUT token (low battery, shutdown imminent and time limit
/// expired all mean LB), so they are remembered separately and combined afterwards.
/// </summary>
[Flags]
internal enum UpsStatusBits : long
{
    None = 0,
    Online = 1L << 0,
    Offline = 1L << 1,
    Discharging = 1L << 2,
    Charging = 1L << 3,
    LowBattery = 1L << 4,
    Overload = 1L << 5,
    ReplaceBattery = 1L << 6,
    ShutdownImminent = 1L << 7,
    Trim = 1L << 8,
    Boost = 1L << 9,
    BypassAuto = 1L << 10,
    BypassManual = 1L << 11,
    EcoMode = 1L << 12,
    EssMode = 1L << 13,
    Off = 1L << 14,
    Calibrating = 1L << 15,
    Overheat = 1L << 16,
    CommFault = 1L << 17,
    Depleted = 1L << 18,
    TimeLimitExpired = 1L << 19,
    FullyCharged = 1L << 20,
    NotFullyCharged = 1L << 21,
    AwaitingPower = 1L << 22,
    FanFailure = 1L << 23,
    NoBattery = 1L << 24,
    BatteryVoltageLow = 1L << 25,
    BatteryVoltageHigh = 1L << 26,
    ChargerFailure = 1L << 27,
    VoltageOutOfRange = 1L << 28,
    FrequencyOutOfRange = 1L << 29,
}

/// <summary>The internal flag names the lookup tables produce ("online", "!dischrg"...), NUT status_info[].</summary>
internal static class UpsStatusBitNames
{
    private static readonly Dictionary<string, UpsStatusBits> Names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["online"] = UpsStatusBits.Online,
        ["offline"] = UpsStatusBits.Offline,
        ["dischrg"] = UpsStatusBits.Discharging,
        ["chrg"] = UpsStatusBits.Charging,
        ["lowbatt"] = UpsStatusBits.LowBattery,
        ["overload"] = UpsStatusBits.Overload,
        ["replacebatt"] = UpsStatusBits.ReplaceBattery,
        ["shutdownimm"] = UpsStatusBits.ShutdownImminent,
        ["trim"] = UpsStatusBits.Trim,
        ["boost"] = UpsStatusBits.Boost,
        ["bypassauto"] = UpsStatusBits.BypassAuto,
        ["bypassman"] = UpsStatusBits.BypassManual,
        ["ecomode"] = UpsStatusBits.EcoMode,
        ["essmode"] = UpsStatusBits.EssMode,
        ["off"] = UpsStatusBits.Off,
        ["cal"] = UpsStatusBits.Calibrating,
        ["overheat"] = UpsStatusBits.Overheat,
        ["commfault"] = UpsStatusBits.CommFault,
        ["depleted"] = UpsStatusBits.Depleted,
        ["timelimitexp"] = UpsStatusBits.TimeLimitExpired,
        ["fullycharged"] = UpsStatusBits.FullyCharged,
        ["notfullycharged"] = UpsStatusBits.NotFullyCharged,
        ["awaitingpower"] = UpsStatusBits.AwaitingPower,
        ["fanfail"] = UpsStatusBits.FanFailure,
        ["nobattery"] = UpsStatusBits.NoBattery,
        ["battvoltlo"] = UpsStatusBits.BatteryVoltageLow,
        ["battvolthi"] = UpsStatusBits.BatteryVoltageHigh,
        ["chargerfail"] = UpsStatusBits.ChargerFailure,
        ["vrange"] = UpsStatusBits.VoltageOutOfRange,
        ["frange"] = UpsStatusBits.FrequencyOutOfRange,
    };

    /// <summary>
    /// Applies one flag value to the bits (NUT process_boolean_info): "name" sets, "!name" clears. Online and
    /// offline, and fullycharged and notfullycharged, exclude each other. Unknown names ("normal") are ignored.
    /// </summary>
    public static UpsStatusBits Apply(UpsStatusBits bits, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value == "online")
        {
            bits = Apply(bits, "!offline");
        }
        else if (value == "offline")
        {
            bits = Apply(bits, "!online");
        }

        if (value == "fullycharged")
        {
            bits = Apply(bits, "!notfullycharged");
        }
        else if (value == "notfullycharged")
        {
            bits = Apply(bits, "!fullycharged");
        }

        bool clear = value.StartsWith('!');
        if (!Names.TryGetValue(clear ? value[1..] : value, out UpsStatusBits flag))
        {
            return bits;
        }

        return clear ? bits & ~flag : bits | flag;
    }
}
