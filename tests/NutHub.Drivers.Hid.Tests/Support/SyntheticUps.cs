using NutHub.Drivers.Hid.Transport;
using static NutHub.Drivers.Hid.Tests.Support.DescriptorBuilder;

namespace NutHub.Drivers.Hid.Tests.Support;

/// <summary>
/// A small standard-conforming UPS of an unknown vendor, served by the generic subdriver:
/// <list type="bullet">
/// <item>0x01 RemainingCapacity (8 bits), 0x02 RunTimeToEmpty (16 bits);</item>
/// <item>0x03 PresentStatus: ACPresent, Discharging, Charging, BelowRemainingCapacityLimit, ShutdownImminent,
/// NeedReplacement, Overload (one bit each, then padding), as feature and as input report;</item>
/// <item>0x05 RemainingCapacityLimit (8 bits) and RemainingTimeLimit (16 bits), writable;</item>
/// <item>0x06 DelayBeforeShutdown, 0x07 DelayBeforeStartup (16 bits, -1 = none), 0x08 AudibleAlarmControl;</item>
/// <item>0x09 Input.Voltage (volt, exponent 7) and Input.Frequency (exponent -1);</item>
/// <item>0x0A Battery.Test.</item>
/// </list>
/// </summary>
internal static class SyntheticUps
{
    public const int VendorId = 0x1234;
    public const int ProductId = 0x5678;

    /// <summary>ACPresent and Charging.</summary>
    public const byte OnLineCharging = 0b0000_0101;

    /// <summary>Discharging only.</summary>
    public const byte OnBattery = 0b0000_0010;

    /// <summary>Discharging and BelowRemainingCapacityLimit.</summary>
    public const byte OnBatteryLow = 0b0000_1010;

    public static readonly byte[] Descriptor = BuildDescriptor();

    public static HidDeviceInfo Info(string path = "/dev/hidraw7", string? serial = "SN0001") =>
        new(path, VendorId, ProductId, "Acme", "Acme UPS 1000", serial);

    public static FakeHidDevice Create(string path = "/dev/hidraw7", string? serial = "SN0001", byte status = OnLineCharging) =>
        new(Info(path, serial), Descriptor,
        [
            [0x01, 85],
            [0x02, 0x08, 0x07], // 1800 s
            [0x03, status],
            [0x05, 10, 0x78, 0x00], // 10 %, 120 s
            [0x06, 0xFF, 0xFF],
            [0x07, 0xFF, 0xFF],
            [0x08, 2], // enabled
            [0x09, 230, 0, 0xF4, 0x01], // 230 V, 50.0 Hz
            [0x0A, 6], // no test initiated
        ], maxInputLength: 2);

    private static byte[] BuildDescriptor()
    {
        var b = new DescriptorBuilder();
        b.UsagePage(0x84).Usage("UPS").Collection(Application);

        b.Collection("PowerSummary", Physical);
        b.ReportId(0x01).Value("RemainingCapacity", 8, 0, 100);
        b.ReportId(0x02).Value("RunTimeToEmpty", 16, 0, 65535);
        b.ReportId(0x05).Value("RemainingCapacityLimit", 8, 0, 100).Value("RemainingTimeLimit", 16, 0, 65535);
        b.ReportId(0x03);
        StatusBits(b, input: false);
        StatusBits(b, input: true);
        b.ReportId(0x06).Value("DelayBeforeShutdown", 16, -1, 32767);
        b.ReportId(0x07).Value("DelayBeforeStartup", 16, -1, 32767);
        b.ReportId(0x08).Value("AudibleAlarmControl", 8, 1, 3);
        b.EndCollection();

        b.Collection("Input", Physical);
        b.ReportId(0x09).Unit(0x00F0D121).UnitExponent(7).Value("Voltage", 16, 0, 65535);
        b.Unit(0).UnitExponent(-1).Value("Frequency", 16, 0, 10000);
        b.UnitExponent(0);
        b.EndCollection();

        b.Collection("Battery", Physical);
        b.ReportId(0x0A).Value("Test", 8, 0, 6);
        b.EndCollection();

        b.EndCollection();
        return b.ToArray();
    }

    private static void StatusBits(DescriptorBuilder b, bool input)
    {
        b.Collection("PresentStatus", Logical);
        b.Usage("ACPresent").Usage("Discharging").Usage("Charging").Usage("BelowRemainingCapacityLimit")
         .Usage("ShutdownImminent").Usage("NeedReplacement").Usage("Overload");
        b.LogicalMinimum(0).LogicalMaximum(1).ReportSize(1).ReportCount(7);
        _ = input ? b.Input() : b.Feature();
        b.ReportCount(1);
        _ = input ? b.Input(Constant) : b.Feature(Constant);
        b.EndCollection();
    }
}
