using System.Globalization;
using NutHub.Core.Drivers;
using NutHub.Drivers.Serial.Transport;

namespace NutHub.Drivers.Serial.Common;

/// <summary>
/// The connection options both serial drivers share ("transport", "port", "baudRate", "host", "tcpPort" and, for the
/// Megatec driver, the USB bridge options), with their parsing and validation.
/// </summary>
internal static class TransportOptions
{
    public const string Serial = "serial";
    public const string Tcp = "tcp";
    public const string Usb = "usb";

    public static DriverOption TransportOption(bool withUsb) => new()
    {
        Key = "transport",
        Label = "Connection",
        Type = DriverOptionType.Choice,
        Default = Serial,
        Choices = withUsb
            ?
            [
                new(Serial, "Serial port (COM / ttyS / ttyUSB)"),
                new(Tcp, "Serial device server over TCP (ser2net, NPort...)"),
                new(Usb, "USB cable with a built-in USB-serial bridge (HID)"),
            ]
            :
            [
                new(Serial, "Serial port (COM / ttyS / ttyUSB)"),
                new(Tcp, "Serial device server over TCP (ser2net, NPort...)"),
            ],
    };

    public static DriverOption PortOption => new()
    {
        Key = "port",
        Label = "Serial port",
        Type = DriverOptionType.SerialPort,
        Help = "COM3 on Windows; on Linux prefer /dev/serial/by-id/... over /dev/ttyUSB0, whose number can change.",
        VisibleWhen = new("transport", [Serial]),
    };

    public static DriverOption BaudRateOption(int defaultBaud) => new()
    {
        Key = "baudRate",
        Label = "Baud rate",
        Type = DriverOptionType.Integer,
        Default = defaultBaud.ToString(CultureInfo.InvariantCulture),
        Min = 300,
        Max = 115200,
        Advanced = true,
        Help = "These UPSes talk at 2400 baud, 8 data bits, no parity, 1 stop bit; change it only if the manual says so.",
        VisibleWhen = new("transport", [Serial]),
    };

    public static DriverOption HostOption => new()
    {
        Key = "host",
        Label = "Serial server host",
        Type = DriverOptionType.Host,
        Help = "Address of the serial device server. It must pass the bytes through unchanged (ser2net 'raw' mode, " +
               "NPort 'TCP server' mode) at 2400 baud 8N1.",
        VisibleWhen = new("transport", [Tcp]),
    };

    public static DriverOption TcpPortOption => new()
    {
        Key = "tcpPort",
        Label = "Serial server TCP port",
        Type = DriverOptionType.Port,
        VisibleWhen = new("transport", [Tcp]),
    };

    /// <summary>Parses the connection options into transport settings; throws <see cref="DriverConfigurationException"/>.</summary>
    /// <param name="dtr">DTR state for a serial port (protocol and cable specific).</param>
    /// <param name="rts">RTS state for a serial port.</param>
    public static TransportSettings Parse(DriverOptionReader read, bool withUsb, int defaultBaud, bool dtr, bool rts)
    {
        string transport = withUsb
            ? read.GetChoice("transport", Serial, Serial, Tcp, Usb)
            : read.GetChoice("transport", Serial, Serial, Tcp);
        switch (transport)
        {
            case Serial:
            {
                string port = read.GetString("port") ?? throw new DriverConfigurationException(
                    "Choose the serial port the UPS is connected to (for example COM3 or /dev/ttyUSB0).", "port");
                int baud = read.GetInt("baudRate", defaultBaud, 300, 115200);
                return new SerialPortSettings(port, baud, dtr, rts);
            }

            case Tcp:
            {
                string host = read.GetString("host") ?? throw new DriverConfigurationException(
                    "Enter the host name or address of the serial device server.", "host");
                if (read.GetString("tcpPort") is null)
                {
                    throw new DriverConfigurationException("Enter the TCP port of the serial device server.", "tcpPort");
                }

                return new TcpSettings(host, read.GetPort("tcpPort", 0));
            }

            default:
            {
                int? vendor = read.GetHex("vendorId");
                int? product = read.GetHex("productId");
                if (vendor is > 0xffff)
                {
                    throw new DriverConfigurationException("The USB vendor id must be 4 hexadecimal digits.", "vendorId");
                }

                if (product is > 0xffff)
                {
                    throw new DriverConfigurationException("The USB product id must be 4 hexadecimal digits.", "productId");
                }

                if (product is not null && vendor is null)
                {
                    throw new DriverConfigurationException("Set the USB vendor id together with the product id.", "vendorId");
                }

                UsbBridgeKind? kind;
                try
                {
                    kind = UsbBridgeCatalog.ParseKind(read.GetString("usbSubdriver"));
                }
                catch (ArgumentException)
                {
                    throw new DriverConfigurationException(
                        "The USB bridge type must be one of: auto, cypress, phoenix, ippon, sgs.", "usbSubdriver");
                }

                if (vendor is not null && product is not null && kind is null &&
                    UsbBridgeCatalog.Find(vendor.Value, product.Value) is { IsSupported: false } unsupported)
                {
                    throw new DriverConfigurationException(
                        $"The USB device {unsupported.Id} ({unsupported.Devices}) cannot be used: {unsupported.UnsupportedReason}.",
                        "vendorId");
                }

                return new UsbBridgeSettings(vendor, product, read.GetString("serial"), kind);
            }
        }
    }

    /// <summary>The serial ports of this machine as discovery results, without opening them.</summary>
    public static IEnumerable<DiscoveredDevice> DiscoverSerialPorts(string driverTitle, string suggestedName)
    {
        foreach (SerialPortInfo port in SerialPortCatalog.ListPorts())
        {
            string detail = port.Description is null
                ? "Serial port (not probed: check that the UPS is connected here)"
                : $"{port.Description} (not probed: check that the UPS is connected here)";
            yield return new DiscoveredDevice(
                $"{driverTitle} on {port.Name}",
                detail,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["transport"] = Serial,
                    ["port"] = port.Name,
                },
                suggestedName);
        }
    }
}
