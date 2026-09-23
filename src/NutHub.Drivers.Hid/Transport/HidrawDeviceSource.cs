using Microsoft.Extensions.Logging;
using NutHub.Drivers.Hid.Descriptors;
using NutHub.Hidraw;

namespace NutHub.Drivers.Hid.Transport;

/// <summary>
/// HID access on Linux through the kernel's hidraw interface directly, as hidapi's hidraw backend does: the devices
/// are the /dev/hidraw* nodes, identified with the HIDIOCGRAW* ioctls and, when sysfs is mounted, the USB device
/// above them (<see cref="HidrawEnumerator"/>). Unlike HidSharp it needs neither libudev nor the udev database, so it
/// works in a container that only received the device node. Only USB devices are listed, plus nodes the process may
/// not open and nothing identifies, so a permission problem is reported instead of an empty list.
/// </summary>
internal sealed class HidrawDeviceSource : IHidDeviceSource
{
    private readonly IHidrawSystem _system;
    private readonly ILogger _logger;
    private readonly Func<string, int, string?>? _usbStrings;

    /// <param name="usbStrings">
    /// Reads a USB string descriptor by device path and index (<see cref="LinuxUsbStrings.GetString"/>); null when
    /// strings other than those already known cannot be read.
    /// </param>
    public HidrawDeviceSource(IHidrawSystem system, ILogger logger, Func<string, int, string?>? usbStrings = null)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _usbStrings = usbStrings;
    }

    public IReadOnlyList<HidDeviceInfo> GetDevices()
    {
        IReadOnlyList<HidrawNode> nodes;
        try
        {
            nodes = HidrawEnumerator.List(_system);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Enumeration only fails on broken systems; report no devices rather than crash the driver.
            _logger.LogWarning(ex, "Listing the hidraw devices failed.");
            return [];
        }

        var result = new List<HidDeviceInfo>(nodes.Count);
        foreach (HidrawNode node in nodes)
        {
            if (node.OpenErrno != 0)
            {
                _logger.LogDebug("{Path} cannot be opened: {Error}.", node.Path, Errno.Describe(node.OpenErrno));
            }

            if (!node.IsUsb && node.BusType != 0)
            {
                _logger.LogDebug("Skipping {Path}: not a USB device (bus type {Bus}).", node.Path, node.BusType);
                continue;
            }

            result.Add(ToDeviceInfo(node));
        }

        return result;
    }

    public byte[] GetReportDescriptor(HidDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        using HidrawHandle? handle = HidrawHandle.TryOpen(_system, device.Path, out int errno);
        if (handle?.TryGetReportDescriptor(out errno) is { } descriptor)
        {
            return descriptor;
        }

        // sysfs publishes the descriptor world-readable even when /dev/hidrawN is not accessible; for a node that
        // opened, only when sysfs describes its device.
        return HidrawSysfs.ReadReportDescriptor(_system, device.Path, handle)
               ?? throw Translate(errno, device, "Reading the report descriptor of");
    }

    public bool TryReset(HidDeviceInfo device, out string detail)
    {
        ArgumentNullException.ThrowIfNull(device);
        return UsbfsReset.TryReset(_system, device.Path, out detail);
    }

    public IHidConnection Open(HidDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        HidrawHandle handle = HidrawHandle.TryOpen(_system, device.Path, out int errno)
                              ?? throw Translate(errno, device, "Opening");
        try
        {
            // The number may belong to another device by now: a UPS replugged while the list was being used.
            if (device.VendorId != 0 && handle.TryGetInfo(out HidrawDevInfo info, out _) &&
                ((ushort)info.Vendor != device.VendorId || (ushort)info.Product != device.ProductId))
            {
                throw new HidDeviceLostException(
                    $"{device.Path} now belongs to another USB device ({(ushort)info.Vendor:x4}:{(ushort)info.Product:x4}).");
            }

            // What sysfs publishes under the node's name may belong to another device (a node renamed on its way into
            // a container): its descriptor and USB strings are used only when sysfs describes this node's device.
            bool sysfsDescribesNode = HidrawSysfs.DescribesNode(_system, handle);
            byte[] descriptor = handle.TryGetReportDescriptor(out errno)
                                ?? (sysfsDescribesNode ? HidrawSysfs.ReadReportDescriptor(_system, device.Path) : null)
                                ?? throw Translate(errno, device, "Reading the report descriptor of");
            HidReportDescriptor parsed;
            try
            {
                parsed = HidReportDescriptorParser.Parse(descriptor);
            }
            catch (InvalidDataException ex)
            {
                throw new IOException($"The report descriptor of {device.Path} cannot be read: {ex.Message}", ex);
            }

            return new HidrawConnection(device, handle, descriptor, parsed, _logger, sysfsDescribesNode ? _usbStrings : null);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal static HidDeviceInfo ToDeviceInfo(HidrawNode node) =>
        new(node.Path, node.VendorId, node.ProductId, node.Manufacturer, node.Product ?? node.Name, node.Serial,
            node.ReleaseNumberBcd, node.AccessDenied ? OpenDenied(node.Path, node.OpenErrno) : null);

    /// <summary>
    /// Maps an errno to the exception types the driver understands, as <see cref="HidSharpDeviceSource.Translate"/>
    /// does for HidSharp: no permission, a device that is gone (the driver reconnects), any other failure.
    /// </summary>
    internal static Exception Translate(int errno, HidDeviceInfo device, string operation)
    {
        if (Errno.IsAccessDenied(errno))
        {
            return new UnauthorizedAccessException(OpenDenied(device.Path, errno));
        }

        return Errno.IsGone(errno)
            ? new HidDeviceLostException($"The HID device {device.Path} is no longer present ({Errno.Describe(errno)}).")
            : new IOException($"{operation} {device.Path} failed: {Errno.Describe(errno)}.");
    }

    /// <summary>"open() of /dev/hidraw0 returned EACCES": EACCES from the file mode, EPERM from a device cgroup.</summary>
    private static string OpenDenied(string path, int errno) => $"open() of {path} returned {Errno.Name(errno)}";
}
