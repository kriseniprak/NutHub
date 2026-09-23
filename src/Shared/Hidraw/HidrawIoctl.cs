using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NutHub.Hidraw;

/// <summary>
/// The request numbers of the hidraw ioctls (include/uapi/linux/hidraw.h), built with the kernel's _IOC macro so the
/// requests whose size is the buffer length (HIDIOCGFEATURE(len)...) can be made for any length. A request packs the
/// direction, the type 'H', the number and the size of the argument; the bit layout is the generic one
/// (include/uapi/asm-generic/ioctl.h: x86, ARM, ARM64, RISC-V, s390x, LoongArch) except on PowerPC, whose
/// arch/powerpc/include/uapi/asm/ioctl.h has three direction bits, other direction values and a 13-bit size.
/// </summary>
/// <param name="Write">_IOC_WRITE: user space writes, the kernel reads.</param>
/// <param name="Read">_IOC_READ: the kernel writes, user space reads.</param>
/// <param name="SizeBits">_IOC_SIZEBITS; the direction bits follow the size.</param>
internal readonly record struct HidrawIoctl(uint Write, uint Read, int SizeBits)
{
    /// <summary>BUS_USB (include/uapi/linux/input.h), the bus type of USB HID devices in hidraw_devinfo.</summary>
    public const int BusUsb = 0x03;

    /// <summary>HID_MAX_DESCRIPTOR_SIZE (include/uapi/linux/hid.h), the size of hidraw_report_descriptor.value.</summary>
    public const int MaxDescriptorSize = 4096;

    /// <summary>The buffer passed to HIDIOCGRAWNAME, HIDIOCGRAWPHYS and HIDIOCGRAWUNIQ (the kernel strings are shorter).</summary>
    public const int StringLength = 256;

    /// <summary>_IOC_WRITE 1, _IOC_READ 2, _IOC_SIZEBITS 14.</summary>
    public static readonly HidrawIoctl Generic = new(Write: 1, Read: 2, SizeBits: 14);

    /// <summary>_IOC_WRITE 4, _IOC_READ 2, _IOC_SIZEBITS 13.</summary>
    public static readonly HidrawIoctl PowerPc = new(Write: 4, Read: 2, SizeBits: 13);

    /// <summary>The layout of the architecture the process runs on.</summary>
    public static HidrawIoctl Current { get; } =
        RuntimeInformation.ProcessArchitecture == Architecture.Ppc64le ? PowerPc : Generic;

    /// <summary>HIDIOCGRDESCSIZE: _IOR('H', 0x01, int).</summary>
    public uint GetDescriptorSize => Ioc(Read, 0x01, sizeof(int));

    /// <summary>HIDIOCGRDESC: _IOR('H', 0x02, struct hidraw_report_descriptor).</summary>
    public uint GetDescriptor => Ioc(Read, 0x02, Unsafe.SizeOf<HidrawReportDescriptor>());

    /// <summary>HIDIOCGRAWINFO: _IOR('H', 0x03, struct hidraw_devinfo).</summary>
    public uint GetRawInfo => Ioc(Read, 0x03, Unsafe.SizeOf<HidrawDevInfo>());

    /// <summary>HIDIOCGRAWNAME(len): _IOC(_IOC_READ, 'H', 0x04, len).</summary>
    public uint GetRawName(int length) => Ioc(Read, 0x04, length);

    /// <summary>HIDIOCGRAWPHYS(len): _IOC(_IOC_READ, 'H', 0x05, len).</summary>
    public uint GetRawPhys(int length) => Ioc(Read, 0x05, length);

    /// <summary>HIDIOCSFEATURE(len): _IOC(_IOC_WRITE|_IOC_READ, 'H', 0x06, len).</summary>
    public uint SetFeature(int length) => Ioc(Write | Read, 0x06, length);

    /// <summary>HIDIOCGFEATURE(len): _IOC(_IOC_WRITE|_IOC_READ, 'H', 0x07, len).</summary>
    public uint GetFeature(int length) => Ioc(Write | Read, 0x07, length);

    /// <summary>HIDIOCGRAWUNIQ(len): _IOC(_IOC_READ, 'H', 0x08, len); kernels before 5.6 answer EINVAL or ENOTTY.</summary>
    public uint GetRawUniq(int length) => Ioc(Read, 0x08, length);

    /// <summary>_IOC(direction, 'H', number, size).</summary>
    public uint Ioc(uint direction, int number, int size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(size, 1 << SizeBits);
        ArgumentOutOfRangeException.ThrowIfNegative(number);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(number, 0xFF);

        // _IOC_NRSHIFT 0, _IOC_TYPESHIFT 8, _IOC_SIZESHIFT 16, _IOC_DIRSHIFT 16 + _IOC_SIZEBITS.
        return (direction << (16 + SizeBits)) | ((uint)size << 16) | ((uint)'H' << 8) | (uint)number;
    }
}

/// <summary>struct hidraw_devinfo: __u32 bustype, __s16 vendor, __s16 product.</summary>
[StructLayout(LayoutKind.Explicit, Size = 8)]
internal struct HidrawDevInfo
{
    [FieldOffset(0)]
    public uint BusType;

    /// <summary>Signed in the kernel structure: 0x8000 and above come out negative; cast to ushort.</summary>
    [FieldOffset(4)]
    public short Vendor;

    [FieldOffset(6)]
    public short Product;
}

/// <summary>struct hidraw_report_descriptor: __u32 size, __u8 value[HID_MAX_DESCRIPTOR_SIZE].</summary>
[StructLayout(LayoutKind.Explicit, Size = 4 + HidrawIoctl.MaxDescriptorSize)]
internal struct HidrawReportDescriptor
{
    /// <summary>Set by the caller to the size HIDIOCGRDESCSIZE returned.</summary>
    [FieldOffset(0)]
    public uint Size;

    [FieldOffset(4)]
    public DescriptorBytes Value;

    [InlineArray(HidrawIoctl.MaxDescriptorSize)]
    public struct DescriptorBytes
    {
        private byte _element;
    }
}
