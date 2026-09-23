using NutHub.Hidraw;

namespace NutHub.Drivers.Serial.Transport;

/// <summary>
/// USB bridges through the Linux hidraw interface directly (<see cref="HidrawEnumerator"/>, <see cref="HidrawHandle"/>),
/// without libudev: works in containers that only received the device node.
/// </summary>
internal sealed class HidrawBridgeBackend(IHidrawSystem system) : IHidBridgeBackend
{
    public IReadOnlyList<HidBridgeDevice> GetDevices(int? vendorId, int? productId) =>
        HidrawEnumerator.List(system)
                        .Where(n => n.IsUsb && (vendorId is null || n.VendorId == vendorId) && (productId is null || n.ProductId == productId))
                        .Select(n => new HidBridgeDevice(n.Path, n.VendorId, n.ProductId) { Native = n })
                        .ToList();

    public string? GetSerialNumber(HidBridgeDevice device) => (device.Native as HidrawNode)?.Serial;

    public (int Input, int Output) GetReportLengths(HidBridgeDevice device)
    {
        HidReportLengths lengths = HidReportLengths.Measure(ReadDescriptor(device.Path, null));
        return (lengths.MaxInput, lengths.MaxOutput);
    }

    public IHidBridgePort Open(HidBridgeDevice device)
    {
        HidrawHandle handle = HidrawHandle.TryOpen(system, device.Path, out int errno) ?? throw OpenFailure(device, errno);
        try
        {
            return new Port(handle, HidReportLengths.Measure(ReadDescriptor(device.Path, handle)).InputReportsNumbered);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The report descriptor from the node, else from sysfs (for a node that opened, only when sysfs describes its
    /// device); empty when neither can be read.
    /// </summary>
    private byte[] ReadDescriptor(string path, HidrawHandle? open)
    {
        using HidrawHandle? handle = open is null ? HidrawHandle.TryOpen(system, path, out _) : null;
        HidrawHandle? node = open ?? handle;
        return node?.TryGetReportDescriptor(out _) ?? HidrawSysfs.ReadReportDescriptor(system, path, node) ?? [];
    }

    private static TransportException OpenFailure(HidBridgeDevice device, int errno)
    {
        if (Errno.IsAccessDenied(errno))
        {
            return HidBridgeTransport.LinuxAccessDenied(device.UsbId, device.Path, errno);
        }

        return Errno.IsGone(errno)
            ? new TransportException(TransportErrorKind.NotFound,
                $"The USB device {device.UsbId} ({device.Path}) is no longer connected ({Errno.Describe(errno)}).")
            : new TransportException(TransportErrorKind.IoError,
                $"Cannot open the USB device {device.UsbId} ({device.Path}): {Errno.Describe(errno)}.");
    }

    /// <summary>
    /// hidraw returns the input reports of devices without report ids without an id byte; the transport expects one,
    /// as HidSharp gives it, so report id 0 is put in front.
    /// </summary>
    private sealed class Port(HidrawHandle handle, bool inputReportsNumbered) : IHidBridgePort
    {
        public int Read(byte[] buffer, TimeSpan timeout)
        {
            int milliseconds = (int)Math.Clamp(timeout.TotalMilliseconds, 1, int.MaxValue);
            if (inputReportsNumbered)
            {
                return handle.Read(buffer, milliseconds);
            }

            int read = handle.Read(buffer.AsSpan(1), milliseconds);
            if (read <= 0)
            {
                return 0;
            }

            buffer[0] = 0;
            return read + 1;
        }

        public void Write(byte[] report) => handle.Write(report);

        public void Dispose() => handle.Dispose();
    }
}
