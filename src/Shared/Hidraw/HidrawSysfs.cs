using System.Globalization;

namespace NutHub.Hidraw;

/// <summary>What sysfs says about a hidraw node: the HID device's uevent and, for USB devices, the USB device above it.</summary>
/// <param name="BusType">From HID_ID ("0003:00000463:0000FFFF"), 0 when missing.</param>
/// <param name="HidName">HID_NAME: manufacturer and product strings joined ("EATON Ellipse PRO").</param>
/// <param name="HidUniq">HID_UNIQ: the USB serial number of USB devices.</param>
/// <param name="Manufacturer">The USB device's manufacturer string, null for other buses.</param>
/// <param name="ReleaseNumberBcd">bcdDevice of the USB device.</param>
internal sealed record HidrawSysfsInfo(
    int BusType,
    int VendorId,
    int ProductId,
    string? HidName,
    string? HidUniq,
    string? Manufacturer,
    string? Product,
    string? Serial,
    int ReleaseNumberBcd);

/// <summary>
/// Reads sysfs for a hidraw node, which it never requires: /sys/class/hidraw/hidrawN links to
/// .../usbB/B-P/B-P:1.0/0003:VVVV:PPPP.NNNN/hidraw/hidrawN, where 0003:VVVV:PPPP.NNNN is the HID device (uevent,
/// report_descriptor) and B-P the USB device (idVendor, idProduct, manufacturer, product, serial, bcdDevice).
/// </summary>
internal static class HidrawSysfs
{
    /// <summary>What sysfs publishes about the node, null when sysfs is not mounted or does not know it.</summary>
    public static HidrawSysfsInfo? Read(IHidrawSystem system, string nodePath)
    {
        string? hid = FindHidDirectory(system, nodePath);
        if (hid is null)
        {
            return null;
        }

        int bus = 0, vendor = 0, product = 0;
        string? name = null, uniq = null;
        foreach (string line in ReadLines(Path.Combine(hid, "uevent")))
        {
            int equals = line.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                continue;
            }

            string value = line[(equals + 1)..].Trim();
            switch (line[..equals])
            {
                case "HID_ID":
                    _ = TryParseHidId(value, out bus, out vendor, out product);
                    break;
                case "HID_NAME":
                    name = NullIfEmpty(value);
                    break;
                case "HID_UNIQ":
                    uniq = NullIfEmpty(value);
                    break;
            }
        }

        string? usb = FindUsbDeviceDirectory(hid);
        return new HidrawSysfsInfo(
            bus,
            vendor,
            product,
            name,
            uniq,
            usb is null ? null : ReadAttribute(usb, "manufacturer"),
            usb is null ? null : ReadAttribute(usb, "product"),
            usb is null ? null : ReadAttribute(usb, "serial"),
            usb is not null && int.TryParse(ReadAttribute(usb, "bcdDevice"), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out int bcd) ? bcd : 0);
    }

    /// <summary>
    /// Whether a sysfs entry describes the device with these ids. A node renamed on its way into a container
    /// ("/dev/hidraw3:/dev/hidraw0") meets the sysfs entry of the host's hidraw0: another device. An entry without
    /// HID_ID cannot be checked and is accepted.
    /// </summary>
    public static bool Describes(HidrawSysfsInfo sysfs, int bus, int vendor, int product) =>
        sysfs.BusType == 0 || (sysfs.BusType == bus && sysfs.VendorId == vendor && sysfs.ProductId == product);

    /// <summary>
    /// Whether what sysfs publishes under an open node's name belongs to the node's own device (HIDIOCGRAWINFO):
    /// false when sysfs has no entry for it or describes another device, true when the node cannot tell.
    /// </summary>
    public static bool DescribesNode(IHidrawSystem system, HidrawHandle node)
    {
        ArgumentNullException.ThrowIfNull(node);
        if (Read(system, node.Path) is not { } sysfs)
        {
            return false;
        }

        return !node.TryGetInfo(out HidrawDevInfo info, out _) ||
               Describes(sysfs, (int)info.BusType, (ushort)info.Vendor, (ushort)info.Product);
    }

    /// <summary>
    /// The report descriptor as sysfs publishes it, readable by everyone even when the node is not (null when
    /// unavailable). With <paramref name="open"/> (the node could be opened), only when sysfs describes the node's
    /// own device (<see cref="DescribesNode"/>): the descriptor of another device would drive the wrong reports.
    /// </summary>
    public static byte[]? ReadReportDescriptor(IHidrawSystem system, string nodePath, HidrawHandle? open = null)
    {
        if (open is not null && !DescribesNode(system, open))
        {
            return null;
        }

        string? hid = FindHidDirectory(system, nodePath);
        if (hid is null)
        {
            return null;
        }

        try
        {
            byte[] descriptor = File.ReadAllBytes(Path.Combine(hid, "report_descriptor"));
            return descriptor.Length == 0 ? null : descriptor;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>"0003:00000463:0000FFFF": bus type, vendor id and product id in hexadecimal.</summary>
    public static bool TryParseHidId(string value, out int bus, out int vendor, out int product)
    {
        bus = vendor = product = 0;
        string[] parts = value.Split(':');
        if (parts.Length != 3 ||
            !uint.TryParse(parts[0], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint b) ||
            !uint.TryParse(parts[1], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint v) ||
            !uint.TryParse(parts[2], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint p))
        {
            return false;
        }

        bus = (int)(b & 0xFFFF);
        vendor = (int)(v & 0xFFFF);
        product = (int)(p & 0xFFFF);
        return true;
    }

    /// <summary>
    /// The HID device directory of a node. The class entry is resolved first, not the "device" link below it, whose
    /// relative target only makes sense from the real directory.
    /// </summary>
    /// <summary>
    /// The usbfs node of the USB device behind a hidraw node ("/dev/bus/usb/003/005"), built from busnum and devnum
    /// in sysfs; null when sysfs is not mounted, does not know the node, or the device is not on USB.
    /// </summary>
    public static string? FindUsbfsNode(IHidrawSystem system, string nodePath)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(nodePath);
        if (FindHidDirectory(system, nodePath) is not { } hid || FindUsbDeviceDirectory(hid) is not { } usb)
        {
            return null;
        }

        return int.TryParse(ReadAttribute(usb, "busnum"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int bus) &&
               int.TryParse(ReadAttribute(usb, "devnum"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int device) &&
               bus is > 0 and < 1000 && device is > 0 and < 1000
            ? string.Create(CultureInfo.InvariantCulture, $"/dev/bus/usb/{bus:D3}/{device:D3}")
            : null;
    }

    private static string? FindHidDirectory(IHidrawSystem system, string nodePath)
    {
        string name = Path.GetFileName(nodePath);
        if (!HidrawEnumerator.IsNodeName(name))
        {
            return null;
        }

        string? target = system.ResolveLink(Path.Combine(system.SysfsRoot, "class", "hidraw", name));
        string? classDirectory = target is null ? null : Path.GetDirectoryName(target);
        if (classDirectory is null || Path.GetFileName(classDirectory) != "hidraw")
        {
            return null;
        }

        string? hid = Path.GetDirectoryName(classDirectory);
        return hid is not null && File.Exists(Path.Combine(hid, "uevent")) ? hid : null;
    }

    /// <summary>The USB device: the first directory above the HID device (and its interface) with idVendor and idProduct.</summary>
    private static string? FindUsbDeviceDirectory(string hid)
    {
        string? directory = Path.GetDirectoryName(hid);
        for (int level = 0; directory is not null && level < 3; level++, directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, "idVendor")) && File.Exists(Path.Combine(directory, "idProduct")))
            {
                return directory;
            }
        }

        return null;
    }

    private static string? ReadAttribute(string directory, string name)
    {
        try
        {
            string file = Path.Combine(directory, name);
            return File.Exists(file) ? NullIfEmpty(File.ReadAllText(file).Trim()) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string[] ReadLines(string file)
    {
        try
        {
            return File.ReadAllLines(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;
}
