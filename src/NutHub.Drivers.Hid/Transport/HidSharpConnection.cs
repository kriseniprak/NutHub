using HidSharp;
using Microsoft.Extensions.Logging;

namespace NutHub.Drivers.Hid.Transport;

/// <summary>An open HidSharp stream, with the buffer sizes each platform's HID stack insists on.</summary>
internal sealed class HidSharpConnection : IHidConnection
{
    private readonly HidDevice _hid;
    private readonly HidStream _stream;
    private readonly ILogger _logger;
    private readonly int _maxFeatureLength;
    private readonly Dictionary<int, string?> _strings = [];
    private byte[]? _descriptor;
    private bool _disposed;
    private long _presenceCheckedAt;
    private bool _presenceKnown;
    private bool _present = true;

    public HidSharpConnection(HidDeviceInfo device, HidDevice hid, HidStream stream, ILogger logger)
    {
        Device = device;
        _hid = hid;
        _stream = stream;
        _logger = logger;
        _maxFeatureLength = SafeLength(hid.GetMaxFeatureReportLength);
        MaxInputReportLength = SafeLength(hid.GetMaxInputReportLength);
    }

    public HidDeviceInfo Device { get; }

    public int MaxInputReportLength { get; }

    public byte[] GetReportDescriptor()
    {
        if (_descriptor is null)
        {
            try
            {
                _descriptor = _hid.GetRawReportDescriptor();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw Classify(ex, "reading the report descriptor");
            }
        }

        return _descriptor;
    }

    public byte[] GetFeature(byte reportId, int length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Windows rejects buffers shorter than the longest feature report of the collection; hidraw passes the
        // length on as the request's wLength, so it gets exactly what the caller asks for.
        int size = OperatingSystem.IsWindows() ? Math.Max(length, _maxFeatureLength) : length;

        // HidSharp's Linux stream issues HIDIOCGFEATURE(count - 1) on the buffer from byte 1, so the kernel's answer,
        // which starts with the report id, lands one byte further than on Windows: ask for one byte more and drop
        // byte 0, which gives the device the requested length and the caller the layout of the other platforms.
        int shift = OperatingSystem.IsLinux() ? 1 : 0;
        var buffer = new byte[Math.Clamp(size, 2, 4096) + shift];
        buffer[0] = reportId;
        try
        {
            _stream.GetFeature(buffer, 0, buffer.Length);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw Classify(ex, $"reading feature report 0x{reportId:x2}");
        }

        return shift == 0 ? buffer : buffer[shift..];
    }

    public void SetFeature(byte[] report)
    {
        ArgumentNullException.ThrowIfNull(report);
        ObjectDisposedException.ThrowIf(_disposed, this);
        byte[] buffer = report;
        if (OperatingSystem.IsWindows() && report.Length < _maxFeatureLength)
        {
            // HidD_SetFeature wants the full collection length; the HID class sends the report's own size.
            buffer = new byte[_maxFeatureLength];
            report.CopyTo(buffer, 0);
        }

        try
        {
            _stream.SetFeature(buffer, 0, buffer.Length);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
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

        try
        {
            _stream.ReadTimeout = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
            return _stream.Read(buffer, 0, Math.Min(buffer.Length, Math.Max(MaxInputReportLength, 1)));
        }
        catch (TimeoutException)
        {
            return 0;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // A failing interrupt read means the device went away (unplugged, or its handle was invalidated).
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
            if (OperatingSystem.IsWindows())
            {
                value = WindowsHidStrings.GetIndexedString(Device.Path, index);
            }
            else if (OperatingSystem.IsLinux())
            {
                value = LinuxUsbStrings.GetString(Device.Path, index);
            }
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
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _stream.Dispose();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogDebug(ex, "Closing {Path} failed.", Device.Path);
        }
    }

    private Exception Classify(Exception ex, string operation)
    {
        if (ex is ObjectDisposedException || !StillPresent())
        {
            return new HidDeviceLostException($"The UPS was disconnected while {operation}.", ex);
        }

        return ex is IOException or UnauthorizedAccessException
            ? HidSharpDeviceSource.Translate(ex, Device)
            : new IOException($"Error while {operation}: {ex.Message}", ex);
    }

    /// <summary>
    /// Tells a transient error from an unplugged device. Some firmwares refuse a few reports on every poll; the
    /// answer is kept for a second so those failures do not each cost a device enumeration.
    /// </summary>
    private bool StillPresent()
    {
        long now = Environment.TickCount64;
        if (_presenceKnown && now - _presenceCheckedAt < 1000)
        {
            return _present;
        }

        try
        {
            if (OperatingSystem.IsLinux() && Device.Path.StartsWith("/dev/", StringComparison.Ordinal))
            {
                _present = File.Exists(Device.Path);
            }
            else
            {
                _present = DeviceList.Local.GetHidDevices(Device.VendorId, Device.ProductId)
                    .Any(d => string.Equals(d.DevicePath, Device.Path, StringComparison.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _present = true;
        }

        _presenceCheckedAt = now;
        _presenceKnown = true;
        return _present;
    }

    private static int SafeLength(Func<int> getter)
    {
        try
        {
            return Math.Max(0, getter());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return 0;
        }
    }
}
