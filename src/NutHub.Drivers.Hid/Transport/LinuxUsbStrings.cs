using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace NutHub.Drivers.Hid.Transport;

/// <summary>
/// USB string descriptors for hidraw devices, which the hidraw interface does not expose. The manufacturer,
/// product and serial strings come from sysfs; any other index is fetched with a GET_DESCRIPTOR control request
/// on the usbfs node (/dev/bus/usb/BBB/DDD), which works when the udev rule grants access to it.
/// </summary>
[SupportedOSPlatform("linux")]
internal static partial class LinuxUsbStrings
{
    private const int ORdOnly = 0;
    private const int ORdWr = 2;
    private const int OCloExec = 0x80000;
    private const byte UsbDirIn = 0x80;
    private const byte UsbRequestGetDescriptor = 0x06;
    private const ushort UsbDescriptorTypeString = 0x03;

    /// <summary>The report descriptor from sysfs, readable by everyone (null when unavailable).</summary>
    public static byte[]? ReadSysfsReportDescriptor(string devicePath)
    {
        string? name = HidrawName(devicePath);
        if (name is null)
        {
            return null;
        }

        string file = $"/sys/class/hidraw/{name}/device/report_descriptor";
        try
        {
            return File.Exists(file) ? File.ReadAllBytes(file) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? GetString(string devicePath, int index)
    {
        string? usbDir = FindUsbDeviceDirectory(devicePath);
        if (usbDir is null)
        {
            return null;
        }

        // The device descriptor names the indexes of the three strings sysfs publishes.
        byte[] descriptors = File.ReadAllBytes(Path.Combine(usbDir, "descriptors"));
        if (descriptors.Length >= 18 && descriptors[1] == 0x01)
        {
            string? file = index == descriptors[14] ? "manufacturer"
                : index == descriptors[15] ? "product"
                : index == descriptors[16] ? "serial"
                : null;
            if (file is not null && File.Exists(Path.Combine(usbDir, file)))
            {
                return File.ReadAllText(Path.Combine(usbDir, file)).Trim();
            }
        }

        return ReadThroughUsbfs(usbDir, index);
    }

    private static string? HidrawName(string devicePath)
    {
        string name = Path.GetFileName(devicePath);
        return name.StartsWith("hidraw", StringComparison.Ordinal) ? name : null;
    }

    /// <summary>
    /// /sys/class/hidraw/hidrawN links to .../usbB/B-P/B-P:1.0/0003:VVVV:PPPP.NNNN/hidraw/hidrawN; the USB
    /// device is the first directory above it with a "busnum" file.
    /// </summary>
    private static string? FindUsbDeviceDirectory(string devicePath)
    {
        string? name = HidrawName(devicePath);
        if (name is null)
        {
            return null;
        }

        // Resolve the class link itself (relative to /sys/class/hidraw), not the "device" link below it, whose
        // relative target only makes sense from the real directory.
        FileSystemInfo? target = Directory.ResolveLinkTarget($"/sys/class/hidraw/{name}", returnFinalTarget: true);
        string? dir = target is null ? null : Path.GetDirectoryName(target.FullName);
        for (int level = 0; dir is not null && level < 5; level++, dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, "busnum")) && File.Exists(Path.Combine(dir, "descriptors")))
            {
                return dir;
            }
        }

        return null;
    }

    private static unsafe string? ReadThroughUsbfs(string usbDir, int index)
    {
        int bus = int.Parse(File.ReadAllText(Path.Combine(usbDir, "busnum")).Trim(), CultureInfo.InvariantCulture);
        int dev = int.Parse(File.ReadAllText(Path.Combine(usbDir, "devnum")).Trim(), CultureInfo.InvariantCulture);
        string node = $"/dev/bus/usb/{bus:D3}/{dev:D3}";
        int fd = Open(node, ORdWr | OCloExec);
        if (fd < 0)
        {
            fd = Open(node, ORdOnly | OCloExec);
        }

        if (fd < 0)
        {
            return null;
        }

        try
        {
            byte* buffer = stackalloc byte[255];
            // String 0 lists the supported languages; use the first one, US English when it is missing.
            ushort language = 0x0409;
            int got = Control(fd, 0, 0, buffer, 255);
            if (got >= 4 && buffer[1] == UsbDescriptorTypeString)
            {
                language = (ushort)(buffer[2] | (buffer[3] << 8));
            }

            got = Control(fd, (byte)index, language, buffer, 255);
            if (got < 2 || buffer[1] != UsbDescriptorTypeString)
            {
                return null;
            }

            int length = Math.Min(Math.Min(got, (int)buffer[0]), 255);
            return length <= 2 ? null : Encoding.Unicode.GetString(buffer + 2, (length - 2) & ~1).TrimEnd('\0');
        }
        finally
        {
            _ = Close(fd);
        }
    }

    private static unsafe int Control(int fd, byte index, ushort language, byte* buffer, ushort length)
    {
        var transfer = new UsbCtrlTransfer
        {
            RequestType = UsbDirIn,
            Request = UsbRequestGetDescriptor,
            Value = (ushort)((UsbDescriptorTypeString << 8) | index),
            Index = language,
            Length = length,
            Timeout = 1000,
            Data = (nint)buffer,
        };

        // _IOWR('U', 0, struct usbdevfs_ctrltransfer): the size is part of the request number.
        nuint request = (nuint)((3u << 30) | ((uint)sizeof(UsbCtrlTransfer) << 16) | ((uint)'U' << 8));
        return Ioctl(fd, request, &transfer);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UsbCtrlTransfer
    {
        public byte RequestType;
        public byte Request;
        public ushort Value;
        public ushort Index;
        public ushort Length;
        public uint Timeout;
        public nint Data;
    }

    [LibraryImport("libc", EntryPoint = "open", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "close", SetLastError = true)]
    private static partial int Close(int fd);

    [LibraryImport("libc", EntryPoint = "ioctl", SetLastError = true)]
    private static unsafe partial int Ioctl(int fd, nuint request, void* argument);
}
