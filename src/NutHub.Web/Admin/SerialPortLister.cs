using System.ComponentModel;
using System.IO.Ports;

namespace NutHub.Web.Admin;

/// <summary>
/// The serial ports of this machine for the driver forms. On Linux the stable /dev/serial/by-id links come too:
/// unlike /dev/ttyUSB0 they do not change when adapters are plugged in another order.
/// </summary>
internal static class SerialPortLister
{
    private const string ByIdDirectory = "/dev/serial/by-id";

    public static IReadOnlyList<string> List()
    {
        var ports = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (string port in SerialPort.GetPortNames())
            {
                if (!string.IsNullOrWhiteSpace(port))
                {
                    ports.Add(port.Trim());
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
                                       or PlatformNotSupportedException)
        {
            // No ports can be listed; the form still accepts a typed name.
        }

        if (OperatingSystem.IsLinux())
        {
            try
            {
                if (Directory.Exists(ByIdDirectory))
                {
                    foreach (string link in Directory.EnumerateFileSystemEntries(ByIdDirectory))
                    {
                        ports.Add(link);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return ports.Order(NaturalComparer.Instance).ToList();
    }

    /// <summary>Sorts "COM2" before "COM10".</summary>
    private sealed class NaturalComparer : IComparer<string>
    {
        public static readonly NaturalComparer Instance = new();

        public int Compare(string? x, string? y)
        {
            if (x is null || y is null)
            {
                return string.CompareOrdinal(x, y);
            }

            int i = 0, j = 0;
            while (i < x.Length && j < y.Length)
            {
                if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
                {
                    int si = i, sj = j;
                    while (i < x.Length && char.IsAsciiDigit(x[i]))
                    {
                        i++;
                    }

                    while (j < y.Length && char.IsAsciiDigit(y[j]))
                    {
                        j++;
                    }

                    string a = x[si..i].TrimStart('0'), b = y[sj..j].TrimStart('0');
                    int c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                    if (c != 0)
                    {
                        return c;
                    }
                }
                else
                {
                    int c = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
                    if (c != 0)
                    {
                        return c;
                    }

                    i++;
                    j++;
                }
            }

            return (x.Length - i).CompareTo(y.Length - j);
        }
    }
}
