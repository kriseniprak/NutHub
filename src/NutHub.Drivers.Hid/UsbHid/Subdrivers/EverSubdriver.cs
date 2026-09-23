using System.Globalization;
using System.Text;
using NutHub.Drivers.Hid.Descriptors;
using NutHub.Drivers.Hid.Transport;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

/// <summary>
/// Ever UPSes (port of NUT drivers/ever-hid.c). Several values are whole vendor reports (MAC and IP addresses,
/// packet counters, bit fields) that the conversions decode from the raw report buffer.
/// </summary>
internal sealed partial class EverSubdriver : UsbHidSubdriver
{
    private const byte WorkModeReport = 74;
    private const byte MessagesReport = 75;
    private const byte AlarmsReport = 76;

    public override string Id => "ever";

    public override string DisplayName => "Ever";

    public override IReadOnlyList<UsbDeviceId> SupportedDevices => DeviceIds;

    protected override HidUsageTable VendorUsages => VendorUsageTable;

    protected override HidMapping[] CreateMappings() => BuildMappings();

    public override string? FormatManufacturer(HidDeviceInfo device, IHidConversionContext context) =>
        device.Manufacturer ?? "Ever";

    private static string? EverFormatHardwareFun(double value, IHidConversionContext context)
    {
        long v = HidValueCodec.Truncate(value);
        long revision = (v & 0xFF00) >> 8;
        string letter = revision == 0 ? "0" : revision <= 26 ? ((char)('A' + revision - 1)).ToString() : "?";
        return string.Create(CultureInfo.InvariantCulture, $"rev.{letter}v{v & 0xFF:00}");
    }

    private static string? EverFormatVersionFun(double value, IHidConversionContext context)
    {
        long v = HidValueCodec.Truncate(value);
        return string.Create(CultureInfo.InvariantCulture, $"v{(v & 0xF000) >> 12:X}.{(v & 0xF00) >> 8:X}b{v & 0xFF:00}");
    }

    /// <summary>
    /// NUT walks a fixed list of report ids with a counter; the report of the field being converted is the same
    /// report and does not depend on the order of the table.
    /// </summary>
    private static string? EverMacAddressFun(double value, IHidConversionContext context) =>
        JoinBytes(CurrentReport(context), ":", "x2");

    private static string? EverIpAddressFun(double value, IHidConversionContext context) =>
        JoinBytes(CurrentReport(context), ".", "D");

    private static string? EverPacketsFun(double value, IHidConversionContext context)
    {
        ReadOnlySpan<byte> report = CurrentReport(context);
        if (report.Length < 5)
        {
            return "";
        }

        int packets = report[1] | (report[2] << 8) | (report[3] << 16) | (report[4] << 24);
        return packets.ToString(CultureInfo.InvariantCulture);
    }

    private static string? EverWorkmodeFun(double value, IHidConversionContext context) =>
        WorkMode(context) switch
        {
            2 => "STOP",
            4 => "ONLINE",
            8 => "ONBATTERY",
            16 => "WATCH",
            32 => "WAITING",
            64 => "EMERGENCY",
            _ => "UNKNOWN",
        };

    /// <summary>The output is off unless the work mode is on line (4) or on battery (8).</summary>
    private static string? EverOnOffFun(double value, IHidConversionContext context) =>
        WorkMode(context) is 4 or 8 ? "!off" : "off";

    private static string? EverMessagesFun(double value, IHidConversionContext context)
    {
        int m = Word(context.GetReportBuffer(HidReportKind.Feature, MessagesReport));
        var words = new List<string>();
        void Add(bool condition, string word)
        {
            if (condition)
            {
                words.Add(word);
            }
        }

        Add((m & 0x04) != 0, "BOOST");
        Add((m & 0x08) != 0, "BUCK");
        Add((m & 0x10) != 0, "BOOST_BLOCKED");
        Add((m & 0x20) != 0, "BUCK_BLOCKED");
        Add((m & 0x40) != 0, "CHARGING");
        Add((m & 0x80) != 0, "FAN_ON");
        Add((m & 0x100) != 0, "EPO_BLOCKED");
        Add((m & 0x200) != 0, "NEED_REPLACMENT");
        Add((m & 0xC00) != 0, "OVERHEAT");
        Add((m & 0x1000) != 0, "WAITING_FOR_MIN_CHARGE");
        Add((m & 0x2000) != 0, "MAINS_OUT_OF_RANGE");
        return string.Join(' ', words);
    }

    private static string? EverAlarmsFun(double value, IHidConversionContext context)
    {
        int a = Word(context.GetReportBuffer(HidReportKind.Feature, AlarmsReport));
        var words = new List<string>();
        void Add(bool condition, string word)
        {
            if (condition)
            {
                words.Add(word);
            }
        }

        Add((a & 0x01) != 0, "OVERLOAD");
        Add((a & 0x02) != 0, "SHORT-CIRCUIT");
        Add((a & 0x0C) != 0, "OVERHEAT");
        Add((a & 0x10) != 0, "EPO");
        Add((a & 0x20) != 0, "INERNAL_ERROR");
        Add((a & 0x40) != 0, "REVERSE_POWER_SUPPLY");
        Add((a & 0x80) != 0, "NO_INETERNAL_COMM");
        Add((a & 0x100) != 0, "CRITICAL_BATT_VOLTAGE");
        return string.Join(' ', words);
    }

    private static int WorkMode(IHidConversionContext context)
    {
        ReadOnlySpan<byte> report = context.GetReportBuffer(HidReportKind.Feature, WorkModeReport);
        return report.Length > 1 ? report[1] : -1;
    }

    private static int Word(ReadOnlySpan<byte> report) => report.Length > 2 ? report[1] | (report[2] << 8) : 0;

    private static ReadOnlySpan<byte> CurrentReport(IHidConversionContext context) =>
        context.CurrentField is { } field ? context.GetReportBuffer(field.Kind, field.ReportId) : [];

    private static string JoinBytes(ReadOnlySpan<byte> report, string separator, string format)
    {
        var sb = new StringBuilder();
        for (int i = 1; i < report.Length; i++)
        {
            if (i > 1)
            {
                sb.Append(separator);
            }

            sb.Append(report[i].ToString(format, CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}
