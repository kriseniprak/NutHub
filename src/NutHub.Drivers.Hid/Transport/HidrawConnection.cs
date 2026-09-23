using Microsoft.Extensions.Logging;
using NutHub.Drivers.Hid.Descriptors;
using NutHub.Hidraw;

namespace NutHub.Drivers.Hid.Transport;

/// <summary>
/// An open hidraw node, with the buffer conventions of <see cref="IHidConnection"/> (those of the Windows HID API and
/// hidapi): feature reports carry the report id in byte 0, followed by the report data, and come back at the length
/// asked for; input reports always start with the report id, 0 for devices without report ids (hidraw leaves it out
/// for those, HidSharp puts it back).
/// </summary>
internal sealed class HidrawConnection : IHidConnection
{
    private readonly HidrawHandle _handle;
    private readonly byte[] _descriptor;
    private readonly bool _inputReportsNumbered;
    private readonly ILogger _logger;
    private readonly Func<string, int, string?>? _usbStrings;
    private readonly Dictionary<int, string?> _strings = [];
    private volatile bool _disposed;

    public HidrawConnection(HidDeviceInfo device, HidrawHandle handle, byte[] descriptor, HidReportDescriptor parsed,
                            ILogger logger, Func<string, int, string?>? usbStrings)
    {
        Device = device;
        _handle = handle;
        _descriptor = descriptor;
        _logger = logger;
        _usbStrings = usbStrings;
        // hidraw hands the interrupt data over as it comes from the wire: it starts with the report id only when the
        // device's input reports are numbered, which the kernel decides per report type (report_enum.numbered in
        // drivers/hid/hid-core.c), whatever the feature reports do.
        _inputReportsNumbered = parsed.GetReportIds(HidReportKind.Input).Any(id => id != 0);

        // As HidSharp counts it: the longest input report plus the report id byte, 0 without input reports.
        MaxInputReportLength = parsed.GetReportIds(HidReportKind.Input)
                                     .Select(id => 1 + parsed.GetReportLength(HidReportKind.Input, id))
                                     .DefaultIfEmpty(0)
                                     .Max();
    }

    public HidDeviceInfo Device { get; }

    public int MaxInputReportLength { get; }

    public byte[] GetReportDescriptor() => _descriptor;

    public byte[] GetFeature(byte reportId, int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // hidraw passes the length on as the request's wLength, so the device gets exactly what the caller asks for.
        var buffer = new byte[Math.Clamp(length, 2, 4096)];
        buffer[0] = reportId;
        int received;
        try
        {
            received = _handle.GetFeature(buffer);
        }
        catch (Exception ex) when (ex is HidrawException or ObjectDisposedException)
        {
            throw Classify(ex, $"reading feature report 0x{reportId:x2}");
        }

        // The kernel counts the report id byte, also for devices without report ids. A device that answers with an
        // empty data stage sent no report: zeros in its place would read as real values (a battery at 0 %).
        if (received < 2)
        {
            throw new IOException($"Error while reading feature report 0x{reportId:x2}: the UPS answered with no data.");
        }

        return buffer;
    }

    public void SetFeature(byte[] report)
    {
        ArgumentNullException.ThrowIfNull(report);
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            _handle.SetFeature(report);
        }
        catch (Exception ex) when (ex is HidrawException or ObjectDisposedException)
        {
            throw Classify(ex, $"writing feature report 0x{report[0]:x2}");
        }
    }

    public int ReadInput(byte[] buffer, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (MaxInputReportLength <= 0)
        {
            return 0;
        }

        int length = Math.Min(buffer.Length, Math.Max(MaxInputReportLength, 1));
        int milliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
        try
        {
            if (_inputReportsNumbered)
            {
                return _handle.Read(buffer.AsSpan(0, length), milliseconds);
            }

            // Without report ids hidraw returns the report data alone; put report id 0 in front of it.
            int read = _handle.Read(buffer.AsSpan(1, Math.Max(length - 1, 0)), milliseconds);
            if (read <= 0)
            {
                return 0;
            }

            buffer[0] = 0;
            return read + 1;
        }
        catch (Exception ex) when (ex is HidrawException or ObjectDisposedException)
        {
            // A failing interrupt read means the device went away (unplugged, or its handle was closed).
            throw new HidDeviceLostException($"Reading from the UPS failed: {ex.Message}", ex);
        }
    }

    public string? GetIndexedString(int index)
    {
        if (index <= 0 || index > 255)
        {
            return null;
        }

        if (_strings.TryGetValue(index, out string? cached))
        {
            return cached;
        }

        string? value = null;
        try
        {
            value = _usbStrings?.Invoke(Device.Path, index);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Reading USB string {Index} of {Path} failed.", index, Device.Path);
        }

        value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        _strings[index] = value;
        return value;
    }

    public void Dispose()
    {
        _disposed = true;
        _handle.Dispose();
    }

    /// <summary>
    /// hidraw answers ENODEV once its device is gone; any other errno of a report request comes from the USB transfer
    /// (EIO included: some host controllers use it for a failed transfer), so the node is asked whether its device
    /// still exists before the driver is told to reconnect.
    /// </summary>
    private Exception Classify(Exception ex, string operation)
    {
        if (ex is ObjectDisposedException || (ex is HidrawException { Errno: Errno.ENoDev }) || !StillPresent())
        {
            return new HidDeviceLostException($"The UPS was disconnected while {operation}.", ex);
        }

        int errno = ((HidrawException)ex).Errno;
        return Errno.IsAccessDenied(errno)
            ? new UnauthorizedAccessException($"Access to the HID device {Device.Path} was denied while {operation} ({Errno.Name(errno)}).", ex)
            : new IOException($"Error while {operation}: {Errno.Describe(errno)}.", ex);
    }

    /// <summary>
    /// Tells a transient error (a report the firmware refuses, a control request that timed out) from an unplugged
    /// device: once its device is gone, hidraw answers every ioctl with ENODEV, HIDIOCGRAWINFO included, which costs
    /// no USB traffic.
    /// </summary>
    private bool StillPresent()
    {
        try
        {
            return _handle.TryGetInfo(out _, out int errno) || !Errno.IsGone(errno);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
