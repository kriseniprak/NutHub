using HidSharp;
using Microsoft.Extensions.Logging;

namespace NutHub.Drivers.Hid.Transport;

/// <summary>
/// HID access through HidSharp: the Windows HID API (hid.dll) on Windows; on Linux, libudev and hidraw, only when
/// NUTHUB_HID_BACKEND=hidsharp asks for it (<see cref="NutHub.Hidraw.HidBackend"/>). HidSharp calls block, so callers
/// run them on the driver's dedicated device thread.
/// </summary>
internal sealed class HidSharpDeviceSource(ILogger<HidSharpDeviceSource> logger) : IHidDeviceSource
{
    public IReadOnlyList<HidDeviceInfo> GetDevices()
    {
        var result = new List<HidDeviceInfo>();
        IEnumerable<HidDevice> devices;
        try
        {
            devices = DeviceList.Local.GetHidDevices().ToList();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Enumeration only fails on broken platform stacks; report no devices rather than crash the driver.
            logger.LogWarning(ex, "Listing the HID devices failed.");
            return result;
        }

        foreach (HidDevice device in devices)
        {
            try
            {
                result.Add(new HidDeviceInfo(
                    device.DevicePath,
                    device.VendorID,
                    device.ProductID,
                    TryGet(device.GetManufacturer),
                    TryGet(device.GetProductName),
                    TryGet(device.GetSerialNumber),
                    SafeReleaseNumber(device)));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                logger.LogDebug(ex, "Skipping HID device {Path}.", SafePath(device));
            }
        }

        return result;
    }

    public byte[] GetReportDescriptor(HidDeviceInfo device)
    {
        HidDevice hid = Find(device);
        try
        {
            return hid.GetRawReportDescriptor();
        }
        catch (Exception ex) when (IsAccessDenied(ex) && OperatingSystem.IsLinux())
        {
            // sysfs publishes the descriptor world-readable even when /dev/hidrawN is not accessible.
            return LinuxUsbStrings.ReadSysfsReportDescriptor(device.Path) ?? throw Translate(ex, device);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw Translate(ex, device);
        }
    }

    public IHidConnection Open(HidDeviceInfo device)
    {
        HidDevice hid = Find(device);
        HidStream stream;
        try
        {
            stream = hid.Open();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw Translate(ex, device);
        }

        try
        {
            return new HidSharpConnection(device, hid, stream, logger);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Maps HidSharp and platform errors to the exception types the driver understands.</summary>
    internal static Exception Translate(Exception ex, HidDeviceInfo device) => ex switch
    {
        UnauthorizedAccessException => new UnauthorizedAccessException(ex.Message, ex),
        _ when IsAccessDenied(ex) || LinuxAccessDenied(device.Path) => new UnauthorizedAccessException(
            $"Access to the HID device {device.Path} was denied: {ex.Message}", ex),
        IOException io => io,
        _ => new IOException($"The HID device {device.Path} failed: {ex.Message}", ex),
    };

    /// <summary>EACCES on Linux; ERROR_ACCESS_DENIED or ERROR_SHARING_VIOLATION on Windows.</summary>
    internal static bool IsAccessDenied(Exception ex)
    {
        if (ex is UnauthorizedAccessException)
        {
            return true;
        }

        int code = ex.HResult & 0xFFFF;
        return ex is IOException && OperatingSystem.IsWindows() && code is 5 or 32;
    }

    /// <summary>
    /// HidSharp reports a failed hidraw open without the errno, so a missing permission looks like any I/O error.
    /// Opening the node once more tells them apart; hidraw allows several opens, so the probe disturbs nothing.
    /// </summary>
    private static bool LinuxAccessDenied(string path)
    {
        if (!OperatingSystem.IsLinux() || !path.StartsWith("/dev/", StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var probe = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
        catch (Exception ex) when (ex is IOException)
        {
            return false;
        }
    }

    private static HidDevice Find(HidDeviceInfo device)
    {
        HidDevice? hid = DeviceList.Local.GetHidDevices()
            .FirstOrDefault(d => string.Equals(d.DevicePath, device.Path, StringComparison.OrdinalIgnoreCase));
        return hid ?? throw new HidDeviceLostException($"The HID device {device.Path} is no longer present.");
    }

    private static string? TryGet(Func<string> getter)
    {
        try
        {
            string value = getter();
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static int SafeReleaseNumber(HidDevice device)
    {
        try
        {
            return device.ReleaseNumberBcd;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return 0;
        }
    }

    private static string SafePath(HidDevice device)
    {
        try
        {
            return device.DevicePath;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return "(unknown)";
        }
    }
}
