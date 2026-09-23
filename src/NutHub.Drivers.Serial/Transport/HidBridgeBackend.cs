using NutHub.Hidraw;

namespace NutHub.Drivers.Serial.Transport;

/// <summary>A HID device as the USB bridge transport sees it.</summary>
/// <param name="Path">The platform device path, for messages.</param>
internal sealed record HidBridgeDevice(string Path, int VendorId, int ProductId)
{
    /// <summary>The backend's own object for the device (the HidSharp device, the hidraw node).</summary>
    public object? Native { get; init; }

    /// <summary>"0665:5161".</summary>
    public string UsbId => $"{VendorId:x4}:{ProductId:x4}";
}

/// <summary>
/// The HID layer under <see cref="HidBridgeTransport"/>: hidraw used directly on Linux, HidSharp elsewhere or when
/// NUTHUB_HID_BACKEND=hidsharp (see <see cref="HidBackend"/>). Calls may block; none of them opens a serial link.
/// </summary>
internal interface IHidBridgeBackend
{
    /// <summary>
    /// The HID devices with these USB ids (null matches any). Throws for failures of the HID layer itself (see
    /// <see cref="HidBridgeTransport.IsHidPlatformFailure"/>).
    /// </summary>
    IReadOnlyList<HidBridgeDevice> GetDevices(int? vendorId, int? productId);

    /// <summary>The USB serial number, null when the device has none or it cannot be read. Never throws.</summary>
    string? GetSerialNumber(HidBridgeDevice device);

    /// <summary>
    /// The longest input and output reports the device declares, report id byte included (0 when it has none or the
    /// descriptor cannot be read). Never throws.
    /// </summary>
    (int Input, int Output) GetReportLengths(HidBridgeDevice device);

    /// <summary>Opens the device for reports; throws <see cref="TransportException"/> saying why it cannot.</summary>
    IHidBridgePort Open(HidBridgeDevice device);
}

/// <summary>An open bridge. Reads come from the transport's reader thread, writes and the disposal from others.</summary>
internal interface IHidBridgePort : IDisposable
{
    /// <summary>
    /// Waits up to <paramref name="timeout"/> for an input report and copies it, report id first: its length, 0 on
    /// timeout. Throws <see cref="IOException"/> or <see cref="ObjectDisposedException"/> once the device is gone or
    /// the port is closed.
    /// </summary>
    int Read(byte[] buffer, TimeSpan timeout);

    /// <summary>Sends an output report, report id (0 for devices without report ids) first.</summary>
    void Write(byte[] report);
}

internal static class HidBridgeBackend
{
    /// <summary>The backend for this process (see <see cref="HidBackend"/>).</summary>
    public static IHidBridgeBackend Create() =>
        HidBackend.Current == HidBackendKind.Hidraw && OperatingSystem.IsLinux()
            ? new HidrawBridgeBackend(LibcHidrawSystem.Instance)
            : new HidSharpBridgeBackend();
}
