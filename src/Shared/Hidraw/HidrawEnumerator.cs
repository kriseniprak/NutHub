using System.Globalization;

namespace NutHub.Hidraw;

/// <summary>A hidraw node and what the system tells about its device.</summary>
/// <param name="Path">"/dev/hidraw0".</param>
/// <param name="BusType">BUS_USB (3), BUS_BLUETOOTH (5)...; 0 when unknown (the node cannot be opened and sysfs is missing).</param>
/// <param name="Name">HIDIOCGRAWNAME or HID_NAME: manufacturer and product joined, "EATON Ellipse PRO".</param>
/// <param name="Phys">HIDIOCGRAWPHYS: the USB port path, "usb-0000:00:14.0-2/input0".</param>
/// <param name="Uniq">HIDIOCGRAWUNIQ or HID_UNIQ: the serial number of USB devices.</param>
/// <param name="Manufacturer">The USB manufacturer string (sysfs only).</param>
/// <param name="Product">The USB product string (sysfs only).</param>
/// <param name="Serial">The USB serial number: sysfs, else the HID uniq.</param>
/// <param name="ReleaseNumberBcd">bcdDevice (sysfs only), 0 when unknown.</param>
/// <param name="OpenErrno">Why the node could not be opened, 0 when it could.</param>
internal sealed record HidrawNode(
    string Path,
    int BusType,
    int VendorId,
    int ProductId,
    string? Name,
    string? Phys,
    string? Uniq,
    string? Manufacturer,
    string? Product,
    string? Serial,
    int ReleaseNumberBcd,
    int OpenErrno)
{
    public bool IsUsb => BusType == HidrawIoctl.BusUsb;

    /// <summary>The process may not open the node (file mode or device cgroup).</summary>
    public bool AccessDenied => Errno.IsAccessDenied(OpenErrno);
}

/// <summary>
/// Lists the hidraw nodes the way hidapi's hidraw backend identifies devices, but without libudev: every /dev/hidraw*
/// node is opened and asked for its bus and ids (HIDIOCGRAWINFO), name, physical path and unique id; sysfs, when
/// mounted, adds the USB strings and identifies the nodes the process may not open. Works in a container that only
/// received the device node, with or without /sys and /run/udev.
/// </summary>
internal static class HidrawEnumerator
{
    /// <summary>The nodes present now, by number; nodes that vanish while being listed are left out.</summary>
    public static IReadOnlyList<HidrawNode> List(IHidrawSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);
        var nodes = new List<HidrawNode>();
        foreach (string path in system.ListNodes()
                                      .Where(p => IsNodeName(Path.GetFileName(p)))
                                      .OrderBy(p => NodeNumber(Path.GetFileName(p))))
        {
            if (Query(system, path) is { } node)
            {
                nodes.Add(node);
            }
        }

        return nodes;
    }

    /// <summary>What the node and sysfs tell about one node; null when it no longer exists.</summary>
    public static HidrawNode? Query(IHidrawSystem system, string path)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(path);
        int bus = 0, vendor = 0, product = 0;
        string? name = null, phys = null, uniq = null;
        using (HidrawHandle? handle = HidrawHandle.TryOpen(system, path, out int openErrno))
        {
            if (handle is null)
            {
                // Removed between the listing and the open, or no driver behind the number any more.
                if (openErrno is Errno.ENoEnt or Errno.ENoDev or Errno.ENxIo)
                {
                    return null;
                }

                return Complete(system, path, 0, 0, 0, null, null, null, openErrno);
            }

            if (handle.TryGetInfo(out HidrawDevInfo info, out int infoErrno))
            {
                bus = (int)info.BusType;
                vendor = (ushort)info.Vendor;
                product = (ushort)info.Product;
            }
            else if (infoErrno == Errno.ENoDev)
            {
                return null;
            }

            HidrawIoctl ioctl = HidrawIoctl.Current;
            name = handle.GetString(ioctl.GetRawName(HidrawIoctl.StringLength));
            phys = handle.GetString(ioctl.GetRawPhys(HidrawIoctl.StringLength));
            uniq = handle.GetString(ioctl.GetRawUniq(HidrawIoctl.StringLength));
        }

        return Complete(system, path, bus, vendor, product, name, phys, uniq, 0);
    }

    /// <summary>"hidraw0"..."hidraw255".</summary>
    public static bool IsNodeName(string name) =>
        name.Length > 6 && name.StartsWith("hidraw", StringComparison.Ordinal) && !name.AsSpan(6).ContainsAnyExceptInRange('0', '9');

    private static int NodeNumber(string name) =>
        int.TryParse(name.AsSpan(6), NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : int.MaxValue;

    /// <summary>Adds what sysfs knows. The node's own answers win; sysfs is dropped when it describes another device.</summary>
    private static HidrawNode Complete(IHidrawSystem system, string path, int bus, int vendor, int product, string? name,
                                       string? phys, string? uniq, int openErrno)
    {
        HidrawSysfsInfo? sysfs = HidrawSysfs.Read(system, path);
        if (sysfs is not null && bus != 0 && !HidrawSysfs.Describes(sysfs, bus, vendor, product))
        {
            // A node renamed on its way into a container ("/dev/hidraw3:/dev/hidraw0") meets the sysfs entry of the
            // host's hidraw0: another device.
            sysfs = null;
        }

        if (bus == 0 && sysfs is not null)
        {
            bus = sysfs.BusType;
            vendor = sysfs.VendorId;
            product = sysfs.ProductId;
        }

        return new HidrawNode(
            path,
            bus,
            vendor,
            product,
            name ?? sysfs?.HidName,
            phys,
            uniq ?? sysfs?.HidUniq,
            sysfs?.Manufacturer,
            sysfs?.Product,
            sysfs?.Serial ?? uniq ?? sysfs?.HidUniq,
            sysfs?.ReleaseNumberBcd ?? 0,
            openErrno);
    }
}
