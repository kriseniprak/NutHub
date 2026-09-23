using System.Globalization;
using System.Text.RegularExpressions;
using NutHub.Drivers.Hid.Descriptors;

namespace NutHub.Drivers.Hid.Tests.Support;

/// <summary>
/// A report descriptor and feature reports of a real UPS, taken from the debug output NUT users attached to
/// issues in the Network UPS Tools repository (each file names its source). Each "value" line is one field as NUT
/// parsed it, with the value NUT computed and the report bytes it computed it from, so the parser and the codec
/// can be checked against NUT on real firmware.
/// </summary>
internal sealed class RealDeviceDump
{
    public const string ApcBackUpsXs1400U = "apc-back-ups-xs-1400u";
    public const string Eaton5Sc750 = "eaton-5sc-750";
    public const string Eaton3S700 = "eaton-3s-700";
    public const string CyberPowerPr3000 = "cyberpower-pr3000elcdsl";

    public static readonly string[] All = [ApcBackUpsXs1400U, Eaton5Sc750, Eaton3S700, CyberPowerPr3000];

    public required string Name { get; init; }

    public required string Source { get; init; }

    /// <summary>The NUT version that produced the dump.</summary>
    public required Version NutVersion { get; init; }

    public required int VendorId { get; init; }

    public required int ProductId { get; init; }

    public required byte[] Descriptor { get; init; }

    /// <summary>The first answer of the device to each feature report request, report id first.</summary>
    public required IReadOnlyDictionary<byte, byte[]> Reports { get; init; }

    public required IReadOnlyList<NutValue> Values { get; init; }

    /// <summary>One field as NUT saw it: "Path: ..., Type: ..., ReportID: ..., Offset: ..., Size: ..., Value: ...".</summary>
    public sealed record NutValue(string Path, HidReportKind Kind, byte ReportId, int Offset, int Size, double Value, byte[] Report);

    public static RealDeviceDump Load(string name)
    {
        string file = Path.Combine(AppContext.BaseDirectory, "TestData", name + ".txt");
        string? source = null;
        Version? version = null;
        int vendor = 0, product = 0;
        byte[] descriptor = [];
        var reports = new Dictionary<byte, byte[]>();
        var values = new List<NutValue>();
        foreach (string line in File.ReadLines(file))
        {
            if (line.StartsWith("# Source: ", StringComparison.Ordinal))
            {
                source = line["# Source: ".Length..];
                continue;
            }

            if (line.StartsWith('#') && Regex.Match(line, @"NUT (\d+\.\d+\.\d+)") is { Success: true } match)
            {
                version = Version.Parse(match.Groups[1].Value);
            }

            int space = line.IndexOf(' ');
            if (line.StartsWith('#') || space < 0)
            {
                continue;
            }

            string rest = line[(space + 1)..];
            switch (line[..space])
            {
                case "vendor":
                    vendor = int.Parse(rest, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    break;
                case "product":
                    product = int.Parse(rest, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    break;
                case "descriptor":
                    descriptor = Hex(rest);
                    break;
                case "report":
                    byte[] report = Hex(rest);
                    reports[report[0]] = report;
                    break;
                case "value":
                    string[] f = rest.Split('|');
                    values.Add(new NutValue(
                        f[0],
                        Enum.Parse<HidReportKind>(f[1]),
                        byte.Parse(f[2], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        int.Parse(f[3], CultureInfo.InvariantCulture),
                        int.Parse(f[4], CultureInfo.InvariantCulture),
                        double.Parse(f[5], NumberStyles.Float, CultureInfo.InvariantCulture),
                        Hex(f[6])));
                    break;
            }
        }

        return new RealDeviceDump
        {
            Name = name,
            Source = source ?? throw new InvalidDataException($"{file} does not name its source."),
            NutVersion = version ?? throw new InvalidDataException($"{file} does not name the NUT version."),
            VendorId = vendor,
            ProductId = product,
            Descriptor = descriptor,
            Reports = reports,
            Values = values,
        };
    }

    private static byte[] Hex(string text) => Convert.FromHexString(text.Replace(" ", "", StringComparison.Ordinal));
}
