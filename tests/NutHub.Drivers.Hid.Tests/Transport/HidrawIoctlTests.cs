using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Drivers.Hid.Transport;
using NutHub.Drivers.Hid.UsbHid;
using NutHub.Hidraw;

namespace NutHub.Drivers.Hid.Tests.Transport;

/// <summary>
/// The hidraw request numbers against the values the kernel headers give (include/uapi/linux/hidraw.h with the
/// _IOC macros of include/uapi/asm-generic/ioctl.h and arch/powerpc/include/uapi/asm/ioctl.h), the layout of the
/// structures passed with them, and the choice between the hidraw layer and HidSharp.
/// </summary>
public sealed class HidrawIoctlTests
{
    [Fact]
    public void Generic_requests_match_the_kernel_headers()
    {
        HidrawIoctl ioctl = HidrawIoctl.Generic;

        Assert.Equal(0x80044801u, ioctl.GetDescriptorSize);   // _IOR('H', 0x01, int)
        Assert.Equal(0x90044802u, ioctl.GetDescriptor);       // _IOR('H', 0x02, struct hidraw_report_descriptor), 4100 bytes
        Assert.Equal(0x80084803u, ioctl.GetRawInfo);          // _IOR('H', 0x03, struct hidraw_devinfo), 8 bytes
        Assert.Equal(0x81004804u, ioctl.GetRawName(256));     // _IOC(_IOC_READ, 'H', 0x04, 256)
        Assert.Equal(0x81004805u, ioctl.GetRawPhys(256));
        Assert.Equal(0x81004808u, ioctl.GetRawUniq(256));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(9)]
    [InlineData(64)]
    [InlineData(255)]
    [InlineData(4096)]
    public void Feature_requests_carry_the_buffer_length(int length)
    {
        HidrawIoctl ioctl = HidrawIoctl.Generic;

        Assert.Equal(0xC0004807u | ((uint)length << 16), ioctl.GetFeature(length));
        Assert.Equal(0xC0004806u | ((uint)length << 16), ioctl.SetFeature(length));
    }

    [Fact]
    public void PowerPc_uses_its_own_direction_bits()
    {
        HidrawIoctl ioctl = HidrawIoctl.PowerPc;

        Assert.Equal(0x40044801u, ioctl.GetDescriptorSize);
        Assert.Equal(0x50044802u, ioctl.GetDescriptor);
        Assert.Equal(0x40084803u, ioctl.GetRawInfo);
        Assert.Equal(0xC0404807u, ioctl.GetFeature(64));
        Assert.Equal(0xC0094806u, ioctl.SetFeature(9));
    }

    [Fact]
    public void Sizes_beyond_the_size_field_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => HidrawIoctl.Generic.GetFeature(1 << 14));
        Assert.Throws<ArgumentOutOfRangeException>(() => HidrawIoctl.PowerPc.GetFeature(1 << 13));
        Assert.Throws<ArgumentOutOfRangeException>(() => HidrawIoctl.Generic.GetFeature(-1));
    }

    [Fact]
    public void The_running_architecture_uses_the_generic_layout_unless_powerpc()
    {
        HidrawIoctl expected = RuntimeInformation.ProcessArchitecture == Architecture.Ppc64le ? HidrawIoctl.PowerPc : HidrawIoctl.Generic;

        Assert.Equal(expected, HidrawIoctl.Current);
    }

    [Fact]
    public void Structures_have_the_kernel_layout()
    {
        Assert.Equal(8, Unsafe.SizeOf<HidrawDevInfo>());
        Assert.Equal(4 + 4096, Unsafe.SizeOf<HidrawReportDescriptor>());
        Assert.Equal(4, (int)Marshal.OffsetOf<HidrawDevInfo>(nameof(HidrawDevInfo.Vendor)));
        Assert.Equal(6, (int)Marshal.OffsetOf<HidrawDevInfo>(nameof(HidrawDevInfo.Product)));

        // __s16 vendor: ids of 0x8000 and above come back negative and must be read unsigned.
        byte[] raw = BitConverter.IsLittleEndian ? [0x03, 0, 0, 0, 0x63, 0x04, 0xFF, 0xFF] : [0, 0, 0, 0x03, 0x04, 0x63, 0xFF, 0xFF];
        HidrawDevInfo info = MemoryMarshal.Read<HidrawDevInfo>(raw);
        Assert.Equal(3u, info.BusType);
        Assert.Equal(0x0463, (ushort)info.Vendor);
        Assert.Equal(0xFFFF, (ushort)info.Product);
    }

    [Theory]
    [InlineData(true, null, "Hidraw")]
    [InlineData(true, "", "Hidraw")]
    [InlineData(true, "hidraw", "Hidraw")]
    [InlineData(true, "hidsharp", "HidSharp")]
    [InlineData(true, " HidSharp ", "HidSharp")]
    [InlineData(true, "libusb", "Hidraw")]
    [InlineData(false, null, "HidSharp")]
    [InlineData(false, "hidraw", "HidSharp")]
    public void Linux_uses_hidraw_unless_told_otherwise(bool linux, string? setting, string expected)
    {
        Assert.Equal(expected, HidBackend.Select(linux, setting).ToString());
    }

    [Fact]
    public void The_factory_builds_the_selected_source_where_it_can_run()
    {
        IHidDeviceSource hidraw = UsbHidDriverFactory.CreateSource(NullLoggerFactory.Instance, HidBackendKind.Hidraw);
        IHidDeviceSource hidSharp = UsbHidDriverFactory.CreateSource(NullLoggerFactory.Instance, HidBackendKind.HidSharp);

        // hidraw only exists on Linux; elsewhere the request falls back to HidSharp.
        Assert.IsType(OperatingSystem.IsLinux() ? typeof(HidrawDeviceSource) : typeof(HidSharpDeviceSource), hidraw);
        Assert.IsType<HidSharpDeviceSource>(hidSharp);
    }
}
