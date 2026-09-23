namespace NutHub.Drivers.Hid.Descriptors;

/// <summary>Usages the code refers to by value (NUT drivers/hidtypes.h), page in the high half.</summary>
internal static class PowerDeviceUsages
{
    public const uint Ups = 0x00840004;
    public const uint PowerSummary = 0x00840024;
    public const uint Voltage = 0x00840030;
    public const uint ActivePower = 0x00840034;
    public const uint ConfigVoltage = 0x00840040;
    public const uint ConfigActivePower = 0x00840044;
    public const uint HighVoltageTransfer = 0x00840054;
    public const uint RunTimeToEmpty = 0x00850068;

    /// <summary>The HID unit of apparent and active power (watt / VA).</summary>
    public const uint PowerUnit = 0x0000D121;
}
