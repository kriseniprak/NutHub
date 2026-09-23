using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Drivers.Hid.Transport;
using NutHub.Hidraw;
using NutHub.Hidraw.Testing;
using static NutHub.Drivers.Hid.Tests.Support.DescriptorBuilder;

namespace NutHub.Drivers.Hid.Tests.Support;

/// <summary>Devices for the fake hidraw system, and the hidraw device source on top of it.</summary>
internal static class HidrawFakes
{
    public const string EatonSerial = "G364P10048";

    /// <summary>
    /// The EATON Ellipse PRO of the report that started the native hidraw layer (HID_NAME=EATON Ellipse PRO,
    /// HID_UNIQ=G364P10048, 0463:ffff), answering with the descriptor and reports of a real Eaton UPS.
    /// </summary>
    public static FakeHidrawDevice Eaton(string dumpName = RealDeviceDump.Eaton5Sc750, string product = "Ellipse PRO",
                                         string serial = EatonSerial, int openErrno = 0, bool uniqSupported = true,
                                         bool descriptorIoctlFails = false)
    {
        RealDeviceDump dump = RealDeviceDump.Load(dumpName);
        var device = new FakeHidrawDevice
        {
            VendorId = dump.VendorId,
            ProductId = dump.ProductId,
            Name = "EATON " + product,
            Uniq = serial,
            Descriptor = dump.Descriptor,
            Manufacturer = "EATON",
            Product = product,
            Serial = serial,
            OpenErrno = openErrno,
            UniqSupported = uniqSupported,
            DescriptorIoctlFails = descriptorIoctlFails,
        };
        device.AddFeatures(dump.Reports.Values);
        return device;
    }

    /// <summary>A USB keyboard: not a UPS, and not from a UPS vendor.</summary>
    public static FakeHidrawDevice Keyboard() => new()
    {
        VendorId = 0x046d,
        ProductId = 0xc31c,
        Name = "Logitech USB Keyboard",
        Descriptor = new DescriptorBuilder().UsagePage(0x01).Usage(0x06).Collection(Application)
                                            .ReportSize(8).ReportCount(8).Input(Constant).EndCollection().ToArray(),
        Manufacturer = "Logitech",
        Product = "USB Keyboard",
    };

    /// <summary>A Bluetooth HID device of a UPS vendor: hidraw lists it, the usbhid driver must not.</summary>
    public static FakeHidrawDevice Bluetooth() => new()
    {
        BusType = 0x05,
        VendorId = 0x0463,
        ProductId = 0x1234,
        Name = "Some Bluetooth thing",
        Phys = "00:11:22:33:44:55",
        Descriptor = SyntheticUps.Descriptor,
    };

    /// <summary>
    /// A UPS whose reports carry no report id: one feature and one input byte of RemainingCapacity, so hidraw returns
    /// its input reports without an id byte.
    /// </summary>
    public static FakeHidrawDevice WithoutReportIds()
    {
        byte[] descriptor = new DescriptorBuilder()
            .UsagePage(0x84).Usage("UPS").Collection(Application)
            .Collection("PowerSummary", Physical)
            .Value("RemainingCapacity", 8, 0, 100)
            .Value("RemainingCapacity", 8, 0, 100, input: true)
            .EndCollection()
            .EndCollection()
            .ToArray();
        var device = new FakeHidrawDevice
        {
            VendorId = SyntheticUps.VendorId,
            ProductId = SyntheticUps.ProductId,
            Name = "Acme UPS",
            Descriptor = descriptor,
            Manufacturer = "Acme",
            Product = "UPS",
        };
        device.Features[0] = [0x00, 77];
        return device;
    }

    public static HidrawDeviceSource Source(FakeHidrawSystem system) =>
        new(system, NullLogger.Instance, (_, index) => index == 4 ? "PbAc" : null);

    public static HidDeviceInfo Info(HidrawDeviceSource source, string path) =>
        source.GetDevices().Single(d => d.Path == path);
}
