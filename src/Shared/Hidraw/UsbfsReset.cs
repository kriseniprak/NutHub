namespace NutHub.Hidraw;

/// <summary>
/// Resets a USB device through usbfs, the way usbreset(8) does: the kernel re-enumerates the device, which is the
/// way back from a UPS that stays plugged in but has stopped answering (several APC Back-UPS and Eaton models do
/// this after weeks of uptime, and some USB controllers suspend the port under them). The node is
/// /dev/bus/usb/BBB/DDD, found from the hidraw node through sysfs; a container needs the usb devices (major 189)
/// for it, not only the hidraw node.
/// </summary>
internal static class UsbfsReset
{
    /// <summary>USBDEVFS_RESET: _IO('U', 20), the same number on every architecture.</summary>
    internal const uint Request = 0x5514;

    /// <summary>
    /// Resets the USB device behind a hidraw node. False with a reason for the log when sysfs does not describe the
    /// node, when the usbfs node cannot be opened (a container without the usb devices) or when the kernel refuses.
    /// </summary>
    public static bool TryReset(IHidrawSystem system, string hidrawPath, out string detail)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(hidrawPath);
        if (HidrawSysfs.FindUsbfsNode(system, hidrawPath) is not { } node)
        {
            detail = $"there is no /dev/bus/usb node for {hidrawPath}: sysfs does not describe it";
            return false;
        }

        int fd = system.Open(node, out int errno);
        if (fd < 0)
        {
            detail = Errno.IsAccessDenied(errno)
                ? $"{node} may not be opened ({Errno.Describe(errno)}): in a container give it the usb devices, " +
                  "for example \"device_cgroup_rules: ['c 189:* rmw']\" with /dev mounted"
                : $"{node} could not be opened: {Errno.Describe(errno)}";
            return false;
        }

        try
        {
            if (system.Ioctl(fd, Request, Span<byte>.Empty, out errno) < 0)
            {
                detail = $"the kernel refused to reset {node}: {Errno.Describe(errno)}";
                return false;
            }
        }
        finally
        {
            system.Close(fd);
        }

        detail = node;
        return true;
    }
}
