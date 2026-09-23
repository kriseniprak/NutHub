using System.IO.Ports;

namespace NutHub.Drivers.Serial.Transport;

/// <summary>Where and how to reach the UPS; parsed and validated from the driver options.</summary>
internal abstract record TransportSettings;

/// <summary>A local serial port ("COM3", "/dev/ttyUSB0", "/dev/serial/by-id/usb-...").</summary>
/// <param name="Dtr">State of the DTR line; some UPS cables draw their power from DTR/RTS.</param>
/// <param name="Rts">State of the RTS line.</param>
internal sealed record SerialPortSettings(
    string PortName,
    int BaudRate,
    bool Dtr,
    bool Rts,
    int DataBits = 8,
    Parity Parity = Parity.None,
    StopBits StopBits = StopBits.One) : TransportSettings;

/// <summary>A raw TCP connection to a serial device server (ser2net in raw mode, Moxa NPort in TCP server mode...).</summary>
internal sealed record TcpSettings(string Host, int Port) : TransportSettings;

/// <summary>A USB-to-serial bridge that shows up as a HID device (the nutdrv_qx / blazer_usb USB "subdrivers").</summary>
/// <param name="VendorId">USB vendor id; null to take the first known bridge found.</param>
/// <param name="ProductId">USB product id; null to accept any product of the vendor.</param>
/// <param name="SerialNumber">USB serial number, to tell identical bridges apart.</param>
/// <param name="Kind">How to talk to the bridge; null to pick it from the vendor/product id.</param>
internal sealed record UsbBridgeSettings(
    int? VendorId,
    int? ProductId,
    string? SerialNumber,
    UsbBridgeKind? Kind) : TransportSettings;
