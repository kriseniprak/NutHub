namespace NutHub.Drivers.Hid.WinBattery;

/// <summary>
/// What Windows tells about one battery (IOCTL_BATTERY_QUERY_INFORMATION). UPSes appear here through the HidBatt
/// driver, laptop batteries through ACPI.
/// </summary>
/// <param name="Path">The device interface path, "\\?\hid#vid_051d&amp;pid_0002#...#{72631e54-...}" for a USB UPS.</param>
/// <param name="Capabilities">BATTERY_INFORMATION.Capabilities flags.</param>
/// <param name="Chemistry">Four characters such as "PbAc" or "LION", null when unreported.</param>
/// <param name="FullChargedCapacity">mWh, or the full scale (usually 100) of a relative battery.</param>
internal sealed record BatteryDevice(
    string Path,
    string? Name,
    string? Manufacturer,
    string? Serial,
    string? UniqueId,
    uint Capabilities,
    string? Chemistry,
    uint DesignedCapacity,
    uint FullChargedCapacity,
    DateOnly? ManufactureDate)
{
    /// <summary>BATTERY_SYSTEM_BATTERY: the battery can power the machine.</summary>
    public const uint SystemBatteryFlag = 0x80000000;

    /// <summary>BATTERY_CAPACITY_RELATIVE: capacities are percentages, not mWh.</summary>
    public const uint RelativeCapacityFlag = 0x40000000;

    /// <summary>BATTERY_IS_SHORT_TERM: the battery lasts minutes, which is what a UPS reports.</summary>
    public const uint ShortTermFlag = 0x20000000;

    public bool IsShortTerm => (Capabilities & ShortTermFlag) != 0;

    public bool IsRelative => (Capabilities & RelativeCapacityFlag) != 0;

    /// <summary>"APC Back-UPS ES 700" or the unique id, for messages.</summary>
    public string Description => string.Join(' ', new[] { Manufacturer, Name }.Where(s => !string.IsNullOrWhiteSpace(s)))
        is { Length: > 0 } text ? text : UniqueId ?? Path;
}

/// <summary>One reading of a battery (IOCTL_BATTERY_QUERY_STATUS plus the estimated time).</summary>
/// <param name="PowerState">BATTERY_POWER_ON_LINE (1), BATTERY_DISCHARGING (2), BATTERY_CHARGING (4), BATTERY_CRITICAL (8).</param>
/// <param name="Capacity">mWh or percent (relative batteries); <see cref="Unknown"/> when unknown.</param>
/// <param name="Voltage">mV; <see cref="Unknown"/> when unknown.</param>
/// <param name="EstimatedSeconds">Runtime at the present load; <see cref="Unknown"/> when unknown.</param>
internal readonly record struct BatteryReading(uint PowerState, uint Capacity, uint Voltage, int Rate, uint EstimatedSeconds)
{
    /// <summary>BATTERY_UNKNOWN_CAPACITY, BATTERY_UNKNOWN_VOLTAGE and BATTERY_UNKNOWN_TIME.</summary>
    public const uint Unknown = 0xFFFFFFFF;

    public const uint OnLine = 0x1;
    public const uint Discharging = 0x2;
    public const uint Charging = 0x4;
    public const uint Critical = 0x8;
}

/// <summary>
/// Access to the batteries of the machine. The production implementation uses SetupAPI and the battery IOCTLs;
/// tests provide fakes. Calls block briefly and run on the driver's device thread.
/// </summary>
internal interface IBatterySource
{
    /// <summary>The batteries present now; entries that cannot be queried are left out. Never throws for none.</summary>
    IReadOnlyList<BatteryDevice> GetBatteries();

    /// <summary>Opens a battery for readings. Throws <see cref="IOException"/> when it is gone or cannot be opened.</summary>
    IBatteryHandle Open(BatteryDevice battery);
}

/// <summary>An open battery.</summary>
internal interface IBatteryHandle : IDisposable
{
    /// <summary>Reads the status. Throws <see cref="BatteryLostException"/> when the battery was removed.</summary>
    BatteryReading Read();
}

/// <summary>The battery (the UPS) disappeared: unplugged, switched off, or its driver was reloaded.</summary>
internal sealed class BatteryLostException(string message, Exception? inner = null) : IOException(message, inner);
