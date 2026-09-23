namespace NutHub.Drivers.Hid.Transport;

/// <summary>What the operating system tells about a HID device without opening it.</summary>
/// <param name="Path">The platform device path: "\\?\hid#vid_051d..." on Windows, "/dev/hidraw0" on Linux.</param>
/// <param name="ReleaseNumberBcd">bcdDevice from the USB device descriptor.</param>
/// <param name="AccessDenied">
/// Why the process may not open the device, when listing already found out ("open() of /dev/hidraw0 returned
/// EACCES"); null when it may, or when the system does not tell.
/// </param>
internal sealed record HidDeviceInfo(
    string Path,
    int VendorId,
    int ProductId,
    string? Manufacturer,
    string? Product,
    string? Serial,
    int ReleaseNumberBcd = 0,
    string? AccessDenied = null)
{
    /// <summary>"051d:0002".</summary>
    public string UsbId => $"{VendorId:x4}:{ProductId:x4}";
}

/// <summary>
/// Access to the HID devices of the machine. The production implementations use hidraw directly on Linux
/// (<see cref="HidrawDeviceSource"/>) and HidSharp elsewhere (<see cref="HidSharpDeviceSource"/>); tests provide fakes
/// so the whole driver runs without hardware. All members may block and must be called off the thread pool.
/// </summary>
internal interface IHidDeviceSource
{
    /// <summary>Lists the HID devices present now. Never throws for an empty list.</summary>
    IReadOnlyList<HidDeviceInfo> GetDevices();

    /// <summary>
    /// Reads the report descriptor of a device. Throws <see cref="UnauthorizedAccessException"/> when the process
    /// may not access it and <see cref="IOException"/> for other failures.
    /// </summary>
    byte[] GetReportDescriptor(HidDeviceInfo device);

    /// <summary>
    /// Opens a device. Throws <see cref="UnauthorizedAccessException"/> for missing permissions (Linux hidraw) or a
    /// device another driver holds exclusively (Windows), <see cref="IOException"/> for other failures.
    /// </summary>
    IHidConnection Open(HidDeviceInfo device);

    /// <summary>
    /// Asks the system to re-enumerate the device, the way usbreset(8) does, and reports what happened in
    /// <paramref name="detail"/>. Only Linux can: everywhere else this answers false and changes nothing.
    /// </summary>
    bool TryReset(HidDeviceInfo device, out string detail)
    {
        ArgumentNullException.ThrowIfNull(device);
        detail = "resetting a USB device is only possible on Linux.";
        return false;
    }
}

/// <summary>
/// An open HID device. Not thread-safe: the driver serialises every call on one dedicated thread. Report buffers
/// start with the report id byte (0 for devices without report ids).
/// </summary>
internal interface IHidConnection : IDisposable
{
    HidDeviceInfo Device { get; }

    byte[] GetReportDescriptor();

    /// <summary>
    /// Reads a feature report. <paramref name="length"/> includes the report id byte; the result may be longer
    /// (Windows always returns the longest feature report of the collection).
    /// </summary>
    byte[] GetFeature(byte reportId, int length);

    /// <summary>Sends a feature report (report id first).</summary>
    void SetFeature(byte[] report);

    /// <summary>The size of the buffer <see cref="ReadInput"/> needs, 0 when the device has no input reports.</summary>
    int MaxInputReportLength { get; }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for an input report (interrupt pipe). Returns the number of bytes
    /// read, 0 on timeout. Throws <see cref="HidDeviceLostException"/> when the device is gone.
    /// </summary>
    int ReadInput(byte[] buffer, TimeSpan timeout);

    /// <summary>A USB string descriptor by index, or null when unavailable.</summary>
    string? GetIndexedString(int index);
}

/// <summary>The device was unplugged or stopped answering altogether; the driver reconnects.</summary>
internal sealed class HidDeviceLostException(string message, Exception? inner = null) : IOException(message, inner);
