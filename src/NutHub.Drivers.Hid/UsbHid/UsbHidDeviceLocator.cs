using System.Text;
using Microsoft.Extensions.Logging;
using NutHub.Core.Drivers;
using NutHub.Drivers.Hid.Descriptors;
using NutHub.Drivers.Hid.Transport;
using NutHub.Drivers.Hid.UsbHid.Subdrivers;

namespace NutHub.Drivers.Hid.UsbHid;

/// <summary>A device the driver can serve: what the system says about it, its parsed descriptor and its subdriver.</summary>
internal sealed record UsbHidCandidate(HidDeviceInfo Device, HidReportDescriptor Descriptor, UsbHidSubdriver Subdriver);

/// <summary>The outcome of a search: a candidate, or the reason there is none (already phrased for the user).</summary>
internal sealed record UsbHidLocateResult(UsbHidCandidate? Candidate, string? Problem)
{
    public static UsbHidLocateResult Found(UsbHidCandidate candidate) => new(candidate, null);

    public static UsbHidLocateResult Failed(string problem) => new(null, problem);
}

/// <summary>
/// Chooses the device a usbhid UPS drives (NUT's USB matcher plus the subdriver claim) and lists UPS-like devices
/// for discovery. Calls into <see cref="IHidDeviceSource"/> block, so run these on a device thread.
/// </summary>
internal static class UsbHidDeviceLocator
{
    /// <summary>
    /// Vendors that make nothing but UPSes and power devices: their devices are offered by discovery even when the
    /// descriptor cannot be read (a permission problem is then reported instead of hiding the UPS).
    /// </summary>
    private static readonly Dictionary<int, string> UpsVendors = new()
    {
        [0x051d] = "APC",
        [0x0463] = "Eaton",
        [0x0764] = "CyberPower",
        [0x09ae] = "Tripp Lite",
        [0x10af] = "Liebert",
        [0x0d9f] = "PowerCOM",
    };

    /// <summary>Whether a device satisfies every criterion the options set (none set matches everything).</summary>
    public static bool Matches(HidDeviceInfo device, UsbHidSettings settings)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.DevicePath is { } path && !SamePath(path, device.Path))
        {
            return false;
        }

        if (settings.VendorId is int vid && vid != device.VendorId)
        {
            return false;
        }

        if (settings.ProductId is int pid && pid != device.ProductId)
        {
            return false;
        }

        if (settings.Serial is { } serial &&
            !string.Equals(serial.Trim(), device.Serial?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return settings.Product is not { } product ||
               (device.Product?.Contains(product, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    /// <summary>
    /// Finds the device to drive: among the devices matching the options, the first whose descriptor declares a
    /// Power Device or Battery System application and that a subdriver accepts; failing that, the first device a
    /// subdriver claims by its USB ids (some firmwares declare only vendor collections). Devices are tried in path
    /// order so the choice is stable between restarts.
    /// </summary>
    public static UsbHidLocateResult Locate(IHidDeviceSource source, UsbHidSettings settings, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(logger);
        IReadOnlyList<HidDeviceInfo> all = source.GetDevices();
        List<HidDeviceInfo> matching = all
            .Where(d => Matches(d, settings))
            .OrderBy(d => d.Path, StringComparer.Ordinal)
            .ToList();
        if (matching.Count == 0)
        {
            return UsbHidLocateResult.Failed(UsbHidErrors.NotFound(settings, all.Count));
        }

        bool auto = string.Equals(settings.Subdriver, "auto", StringComparison.OrdinalIgnoreCase);
        UsbHidCandidate? fallback = null;
        string? denied = null;
        int unreadable = 0;
        HidDeviceInfo? rejected = null;
        foreach (HidDeviceInfo device in matching)
        {
            HidReportDescriptor descriptor;
            try
            {
                descriptor = HidReportDescriptorParser.Parse(source.GetReportDescriptor(device));
            }
            catch (UnauthorizedAccessException ex)
            {
                // Only worth telling the user when the device could be the UPS.
                if (settings.HasDeviceCriteria || LooksLikeUps(device))
                {
                    denied ??= UsbHidErrors.AccessDenied(device, ex);
                }
                else
                {
                    unreadable++;
                }

                continue;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                logger.LogDebug("Skipping HID device {Path}: {Message}", device.Path, ex.Message);
                continue;
            }

            bool power = descriptor.IsPowerDevice;
            UsbHidSubdriver? subdriver = SubdriverCatalog.Select(device, power, settings.Subdriver, settings.ProductId is not null);
            if (subdriver is null || descriptor.Fields.Count == 0)
            {
                rejected ??= device;
                continue;
            }

            // A forced subdriver claims anything; without criteria that could be a keyboard.
            if (!auto && !power && !settings.HasDeviceCriteria && !LooksLikeUps(device))
            {
                rejected ??= device;
                continue;
            }

            var candidate = new UsbHidCandidate(device, descriptor, subdriver);
            if (power)
            {
                return UsbHidLocateResult.Found(candidate);
            }

            fallback ??= candidate;
        }

        if (fallback is not null)
        {
            return UsbHidLocateResult.Found(fallback);
        }

        if (denied is not null)
        {
            return UsbHidLocateResult.Failed(denied);
        }

        return UsbHidLocateResult.Failed(settings.HasDeviceCriteria && rejected is not null
            ? UsbHidErrors.NotAUps(rejected)
            : UsbHidErrors.NotFound(settings, all.Count, unreadable));
    }

    /// <summary>
    /// Lists the UPSes attached to the machine: devices with a Power Device / Battery System collection, and
    /// devices of known UPS models or vendors even when their descriptor is unreadable. Windows exposes one entry
    /// per top-level collection, so entries of the same physical device are merged.
    /// </summary>
    public static IReadOnlyList<DiscoveredDevice> Discover(IHidDeviceSource source, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(logger);
        var found = new List<(HidDeviceInfo Device, bool Power, string? Problem)>();
        foreach (HidDeviceInfo device in source.GetDevices().OrderBy(d => d.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool power = false;
            string? problem = null;
            try
            {
                power = HidReportDescriptorParser.Parse(source.GetReportDescriptor(device)).IsPowerDevice;
            }
            catch (UnauthorizedAccessException ex)
            {
                problem = UsbHidErrors.AccessDenied(device, ex);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                logger.LogDebug("Discovery skipped the descriptor of {Path}: {Message}", device.Path, ex.Message);
            }

            // Linux publishes descriptors in sysfs for everyone: a readable descriptor does not mean an openable node.
            if (problem is null && device.AccessDenied is { } reason)
            {
                problem = UsbHidErrors.AccessDenied(device, reason);
            }

            // A node nothing could identify (no access, no sysfs) may well be the UPS: the permission is the news.
            if (power || LooksLikeUps(device) || (problem is not null && IsUnidentified(device)))
            {
                found.Add((device, power, problem));
            }
        }

        // One entry per physical device, preferring its Power Device collection.
        var merged = found
            .GroupBy(f => (f.Device.VendorId, f.Device.ProductId, Key: f.Device.Serial ?? f.Device.Path))
            .Select(g => g.OrderByDescending(f => f.Power).First())
            .ToList();

        var result = new List<DiscoveredDevice>(merged.Count);
        foreach (var (device, _, problem) in merged)
        {
            bool ambiguous = merged.Count(m => m.Device.VendorId == device.VendorId && m.Device.ProductId == device.ProductId &&
                                               string.Equals(m.Device.Serial, device.Serial, StringComparison.Ordinal)) > 1;
            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (IsUnidentified(device))
            {
                options["devicePath"] = device.Path;
            }
            else
            {
                options["vendorId"] = device.VendorId.ToString("x4", System.Globalization.CultureInfo.InvariantCulture);
                options["productId"] = device.ProductId.ToString("x4", System.Globalization.CultureInfo.InvariantCulture);
            }

            if (device.Serial is { } serial)
            {
                options["serial"] = serial;
            }

            if (ambiguous)
            {
                // Identical UPSes without serial numbers can only be told apart by their port.
                options["devicePath"] = device.Path;
            }

            string detail = device.Serial is null ? device.Path : $"Serial number {device.Serial} · {device.Path}";
            if (problem is not null)
            {
                detail += " · " + problem;
            }

            result.Add(new DiscoveredDevice(Title(device), detail, options, SuggestName(device)));
        }

        return result;
    }

    /// <summary>"APC Back-UPS ES 700 (USB 051d:0002)": manufacturer and product, without firmware suffixes.</summary>
    public static string Title(HidDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (IsUnidentified(device))
        {
            return $"Unidentified HID device ({device.Path})";
        }

        string vendor = device.Manufacturer ?? UpsVendors.GetValueOrDefault(device.VendorId) ?? "USB";
        string product = ModelName(device.Product) ?? "UPS";
        string name = product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase) ? product : $"{vendor} {product}";
        return $"{name} (USB {device.UsbId})";
    }

    /// <summary>A UPS name from the product: "Back-UPS XS 1400U" gives "back-ups-xs-1400u".</summary>
    public static string SuggestName(HidDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        string source = ModelName(device.Product) ?? UpsVendors.GetValueOrDefault(device.VendorId) ?? "ups";
        var sb = new StringBuilder(source.Length);
        foreach (char c in source.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(c))
            {
                sb.Append(c);
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
        }

        string name = sb.ToString().Trim('-');
        if (name.Length > 32)
        {
            name = name[..32].TrimEnd('-');
        }

        return name.Length == 0 ? "ups" : name;
    }

    /// <summary>
    /// Whether the USB ids alone say "UPS": a model some subdriver knows exactly, or a vendor that makes only
    /// power devices. Vendor ids of generic chip makers (Cypress, Microchip...) are deliberately not enough.
    /// </summary>
    public static bool LooksLikeUps(HidDeviceInfo device) =>
        UpsVendors.ContainsKey(device.VendorId) || SubdriverCatalog.IsKnownDevice(device);

    /// <summary>
    /// Whether nothing is known about the device: a hidraw node the process may not open, on a system without sysfs
    /// to tell its ids.
    /// </summary>
    public static bool IsUnidentified(HidDeviceInfo device) => device.VendorId == 0 && device.ProductId == 0;

    /// <summary>
    /// Compares a configured device path with a device's. On Linux HidSharp names a device by its sysfs path
    /// (/sys/devices/.../hidraw/hidraw0) and the hidraw layer by its node (/dev/hidraw0): a path saved under one
    /// layer still matches under the other.
    /// </summary>
    internal static bool SamePath(string configured, string actual)
    {
        if (string.Equals(configured, actual, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            return true;
        }

        string name = Path.GetFileName(actual);
        return name.StartsWith("hidraw", StringComparison.Ordinal) &&
               string.Equals(Path.GetFileName(configured), name, StringComparison.Ordinal) &&
               IsHidrawPath(configured) && IsHidrawPath(actual);

        static bool IsHidrawPath(string path) =>
            path.StartsWith("/dev/", StringComparison.Ordinal) ||
            (path.StartsWith("/sys/", StringComparison.Ordinal) && path.Contains("/hidraw/", StringComparison.Ordinal));
    }

    /// <summary>APC and others append the firmware to the product string: "Back-UPS XS 1400U  FW:926.T1 .I".</summary>
    private static string? ModelName(string? product)
    {
        if (string.IsNullOrWhiteSpace(product))
        {
            return null;
        }

        int fw = product.IndexOf("FW:", StringComparison.Ordinal);
        string model = (fw > 0 ? product[..fw] : product).Trim();
        return model.Length == 0 ? null : string.Join(' ', model.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
