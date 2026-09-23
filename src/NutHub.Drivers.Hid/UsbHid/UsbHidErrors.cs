using NutHub.Drivers.Hid.Transport;
using NutHub.Hidraw;

namespace NutHub.Drivers.Hid.UsbHid;

/// <summary>
/// The messages shown when the driver cannot reach a UPS. They end up in the web panel and in the log, so they
/// say what to do, not only what failed: on Linux the usual cause is the missing udev rule, on Windows another
/// program (or the battery driver on some models) holding the device.
/// </summary>
internal static class UsbHidErrors
{
    /// <summary>Where the packages install the udev rule that grants the NutHub service access to UPS hidraw nodes.</summary>
    public const string UdevRuleName = "99-nuthub-ups.rules";

    public static string AccessDenied(HidDeviceInfo device, Exception? error = null) => AccessDenied(device, error?.Message);

    /// <param name="reason">What the system said, e.g. <see cref="HidDeviceInfo.AccessDenied"/>.</param>
    public static string AccessDenied(HidDeviceInfo device, string? reason)
    {
        ArgumentNullException.ThrowIfNull(device);
        string detail = reason is null ? "" : $" ({reason.TrimEnd('.')})";
        if (OperatingSystem.IsLinux())
        {
            return LinuxAccessDenied(device, reason, HidrawAccess.InContainer);
        }

        if (OperatingSystem.IsWindows())
        {
            return $"Windows denied access to the UPS (USB {device.UsbId}){detail}: another program or driver is " +
                   "using it exclusively. Close other UPS software (PowerChute, PowerPanel, UPSilon...) or use the " +
                   "'winbattery' driver, which reads the UPS through the Windows battery driver.";
        }

        return $"Access to the UPS {device.Path} (USB {device.UsbId}) was denied{detail}. Run NutHub with an " +
               "account that may open HID devices.";
    }

    /// <summary>
    /// The Linux message: EPERM (a device cgroup refused the node) needs the device passed or allowed, EACCES (the
    /// file mode) the udev rule on the host, and inside a container the container's user or group.
    /// </summary>
    internal static string LinuxAccessDenied(HidDeviceInfo device, string? reason, bool inContainer)
    {
        ArgumentNullException.ThrowIfNull(device);
        string detail = reason is null ? "" : $" ({reason.TrimEnd('.')})";
        string ids = device is { VendorId: 0, ProductId: 0 } ? "" : $" (USB {device.UsbId})";
        string denied = $"Permission denied on {device.Path}{ids}{detail}.";
        if (HidrawAccess.IsDeviceCgroupDenial(reason))
        {
            return $"{denied} {HidrawAccess.DeviceCgroupAdvice(device.Path)}";
        }

        if (inContainer)
        {
            return $"{denied} {HidrawAccess.ContainerPermissionAdvice(device.Path, $"packaging/linux/{UdevRuleName}")}";
        }

        return $"{denied} Install the udev rule shipped with NutHub (copy packaging/linux/{UdevRuleName} to " +
               "/etc/udev/rules.d/, run 'udevadm control --reload-rules && udevadm trigger' and reconnect the UPS), or " +
               "run NutHub as root.";
    }

    /// <param name="unreadable">HID devices whose descriptor could not be read, one of which may be the UPS.</param>
    public static string NotFound(UsbHidSettings settings, int hidDeviceCount, int unreadable = 0)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string criteria = DescribeCriteria(settings);
        string what = criteria.Length == 0 ? "No USB UPS was found" : $"No USB UPS matching {criteria} was found";
        string hint = hidDeviceCount == 0 && OperatingSystem.IsLinux()
            ? " No HID device is visible at all: check the cable and that the hidraw and usbhid kernel modules are loaded."
            : " Check the USB cable and that the UPS is switched on.";
        if (unreadable > 0)
        {
            hint += OperatingSystem.IsLinux()
                ? $" {unreadable} HID device(s) could not be read for lack of permission: if the UPS is one of them, " +
                  $"install the udev rule shipped with NutHub (packaging/linux/{UdevRuleName}) or set its vendor id."
                : $" {unreadable} HID device(s) could not be read for lack of permission; set the vendor id of the UPS " +
                  "to get a precise error.";
        }

        return what + "." + hint;
    }

    public static string NotAUps(HidDeviceInfo device) =>
        $"The USB device {device.UsbId} ({device.Product ?? device.Path}) does not look like a UPS: its report " +
        "descriptor has no Power Device collection and no subdriver knows it. Set the 'subdriver' option to force one.";

    public static string DescribeCriteria(UsbHidSettings settings)
    {
        var parts = new List<string>();
        if (settings.VendorId is int vid)
        {
            parts.Add($"vendor id {vid:x4}");
        }

        if (settings.ProductId is int pid)
        {
            parts.Add($"product id {pid:x4}");
        }

        if (settings.Serial is { } serial)
        {
            parts.Add($"serial number '{serial}'");
        }

        if (settings.Product is { } product)
        {
            parts.Add($"product name containing '{product}'");
        }

        if (settings.DevicePath is { } path)
        {
            parts.Add($"device path '{path}'");
        }

        return string.Join(", ", parts);
    }
}
