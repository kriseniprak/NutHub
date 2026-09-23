using HidSharp;

namespace NutHub.Drivers.Serial.Transport;

/// <summary>USB bridges through HidSharp: the Windows HID API, IOKit on macOS, libudev and hidraw on Linux.</summary>
internal sealed class HidSharpBridgeBackend : IHidBridgeBackend
{
    public IReadOnlyList<HidBridgeDevice> GetDevices(int? vendorId, int? productId) =>
        DeviceList.Local.GetHidDevices(vendorId, productId)
                  .Select(d => new HidBridgeDevice(d.DevicePath, d.VendorID, d.ProductID) { Native = d })
                  .ToList();

    public string? GetSerialNumber(HidBridgeDevice device) => SafeString(Hid(device).GetSerialNumber);

    public (int Input, int Output) GetReportLengths(HidBridgeDevice device)
    {
        HidDevice hid = Hid(device);
        return (SafeLength(hid.GetMaxInputReportLength), SafeLength(hid.GetMaxOutputReportLength));
    }

    public IHidBridgePort Open(HidBridgeDevice device)
    {
        HidDevice hid = Hid(device);
        if (!hid.TryOpen(out HidStream stream))
        {
            throw OpenFailure(hid);
        }

        stream.ReadTimeout = 1000;
        stream.WriteTimeout = 5000;
        return new Port(stream);
    }

    private static HidDevice Hid(HidBridgeDevice device) =>
        device.Native as HidDevice ?? throw new ArgumentException("Not a HidSharp device.", nameof(device));

    private static TransportException OpenFailure(HidDevice device)
    {
        string id = $"{device.VendorID:x4}:{device.ProductID:x4}";
        if (OperatingSystem.IsLinux())
        {
            return HidBridgeTransport.LinuxAccessDenied(id, SafeString(device.GetFileSystemName) ?? "/dev/hidraw*");
        }

        return new TransportException(TransportErrorKind.Busy,
            $"Cannot open the USB device {id}: it is in use by another program (vendor UPS software, NUT) or access " +
            "is denied. Close the other program and NutHub will retry.");
    }

    private static int SafeLength(Func<int> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            return 0;
        }
    }

    private static string? SafeString(Func<string> read)
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>HidSharp only offers blocking reads with a timeout, reported as <see cref="TimeoutException"/>.</summary>
    private sealed class Port(HidStream stream) : IHidBridgePort
    {
        public int Read(byte[] buffer, TimeSpan timeout)
        {
            stream.ReadTimeout = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
            try
            {
                return stream.Read(buffer, 0, buffer.Length);
            }
            catch (TimeoutException)
            {
                return 0;
            }
        }

        public void Write(byte[] report) => stream.Write(report);

        public void Dispose() => stream.Dispose();
    }
}
