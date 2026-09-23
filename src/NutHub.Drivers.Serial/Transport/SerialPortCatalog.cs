using System.IO.Ports;
using Microsoft.Win32;

namespace NutHub.Drivers.Serial.Transport;

/// <summary>A serial port found on this machine.</summary>
/// <param name="Name">What to put in the "port" option: "COM3", "/dev/serial/by-id/usb-FTDI_...-port0", "/dev/ttyS0".</param>
/// <param name="Description">A hint for the user: the adapter type on Windows, the kernel device on Linux.</param>
public sealed record SerialPortInfo(string Name, string? Description);

/// <summary>
/// Lists the serial ports of this machine without opening them. On Linux the stable /dev/serial/by-id names come first
/// (and replace the /dev/ttyUSBn they point to, whose number can change between boots); on-board /dev/ttyS ports without
/// a UART behind them are left out.
/// </summary>
public static class SerialPortCatalog
{
    private const string ByIdDirectory = "/dev/serial/by-id";

    /// <summary>The serial ports, in a stable order. Never throws: returns what could be found.</summary>
    public static IReadOnlyList<SerialPortInfo> ListPorts()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return ListWindows();
            }

            if (OperatingSystem.IsLinux())
            {
                return ListLinux();
            }

            return SerialPort.GetPortNames().Order(StringComparer.Ordinal).Select(n => new SerialPortInfo(n, null)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>Just the names, e.g. for GET /api/admin/serial-ports.</summary>
    public static IReadOnlyList<string> ListPortNames() => ListPorts().Select(p => p.Name).ToList();

    private static List<SerialPortInfo> ListWindows()
    {
        Dictionary<string, string> kernelNames = OperatingSystem.IsWindows() ? ReadSerialCommMap() : [];
        return SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n.StartsWith("COM", StringComparison.OrdinalIgnoreCase) && int.TryParse(n.AsSpan(3), out int k) ? k : int.MaxValue)
            .ThenBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Select(n => new SerialPortInfo(n, kernelNames.TryGetValue(n, out string? kernel) ? DescribeWindowsDevice(kernel) : null))
            .ToList();
    }

    /// <summary>
    /// HKLM\HARDWARE\DEVICEMAP\SERIALCOMM maps kernel device names ("\Device\ProlificSerial0") to COM names: a cheap
    /// way to tell the user which adapter a COM port belongs to, without WMI.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Dictionary<string, string> ReadSerialCommMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key is null)
            {
                return map;
            }

            foreach (string valueName in key.GetValueNames())
            {
                if (key.GetValue(valueName) is string com && !string.IsNullOrWhiteSpace(com))
                {
                    map[com.Trim()] = valueName;
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Not worth failing the listing for a hint.
        }

        return map;
    }

    internal static string DescribeWindowsDevice(string kernelName)
    {
        string name = kernelName.Replace(@"\Device\", "", StringComparison.OrdinalIgnoreCase);
        return name switch
        {
            _ when name.StartsWith("ProlificSerial", StringComparison.OrdinalIgnoreCase) => "Prolific USB-serial adapter",
            _ when name.StartsWith("VCP", StringComparison.OrdinalIgnoreCase) => "FTDI USB-serial adapter",
            _ when name.StartsWith("Silabser", StringComparison.OrdinalIgnoreCase) => "Silicon Labs CP210x USB-serial adapter",
            _ when name.StartsWith("CH341", StringComparison.OrdinalIgnoreCase) || name.StartsWith("WCH", StringComparison.OrdinalIgnoreCase)
                => "WCH CH340/CH341 USB-serial adapter",
            _ when name.StartsWith("USBSER", StringComparison.OrdinalIgnoreCase) => "USB serial device (CDC)",
            _ when name.StartsWith("BthModem", StringComparison.OrdinalIgnoreCase) => "Bluetooth serial port",
            _ when name.StartsWith("Serial", StringComparison.OrdinalIgnoreCase) => "Built-in serial port",
            _ => name,
        };
    }

    private static List<SerialPortInfo> ListLinux()
    {
        var result = new List<SerialPortInfo>();
        var covered = new HashSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(ByIdDirectory))
        {
            foreach (string link in Directory.EnumerateFileSystemEntries(ByIdDirectory).Order(StringComparer.Ordinal))
            {
                string? target = ResolveLink(link);
                if (target is not null)
                {
                    covered.Add(target);
                }

                result.Add(new SerialPortInfo(link, target));
            }
        }

        foreach (string pattern in new[] { "ttyUSB*", "ttyACM*", "ttyAMA*", "ttyS*" })
        {
            IEnumerable<string> devices;
            try
            {
                devices = Directory.EnumerateFileSystemEntries("/dev", pattern);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (string device in devices.OrderBy(NaturalKey).ThenBy(d => d, StringComparer.Ordinal))
            {
                if (covered.Contains(device) || (pattern == "ttyS*" && !HasUart(device)))
                {
                    continue;
                }

                result.Add(new SerialPortInfo(device, null));
            }
        }

        return result;
    }

    private static string? ResolveLink(string link)
    {
        try
        {
            FileSystemInfo? target = File.ResolveLinkTarget(link, returnFinalTarget: true);
            return target?.FullName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The kernel creates /dev/ttyS0..31 whether or not a UART exists; /sys/class/tty/ttySn/type is 0 (PORT_UNKNOWN)
    /// for the phantom ones.
    /// </summary>
    private static bool HasUart(string device)
    {
        string typeFile = $"/sys/class/tty/{Path.GetFileName(device)}/type";
        try
        {
            return !File.Exists(typeFile) || File.ReadAllText(typeFile).Trim() != "0";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static int NaturalKey(string device)
    {
        int i = device.Length;
        while (i > 0 && char.IsAsciiDigit(device[i - 1]))
        {
            i--;
        }

        return i < device.Length && int.TryParse(device.AsSpan(i), out int n) ? n : -1;
    }
}
