using Microsoft.Extensions.Logging;

namespace NutHub.Drivers.Serial.Transport;

/// <summary>Creates the transport for parsed settings.</summary>
internal static class TransportFactory
{
    public static ISerialTransport Create(TransportSettings settings, TimeProvider time, ILogger logger) => settings switch
    {
        SerialPortSettings serial => new SerialPortTransport(serial, time, logger),
        TcpSettings tcp => new TcpTransport(tcp, time, logger),
        UsbBridgeSettings usb => new HidBridgeTransport(usb, time, logger),
        _ => throw new ArgumentOutOfRangeException(nameof(settings), settings, "Unknown transport settings."),
    };
}
