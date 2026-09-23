using System.Globalization;
using NutHub.Drivers.Hid.Descriptors;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

/// <summary>
/// The lookups every subdriver shares (NUT drivers/usbhid-ups.c): status flag tables (generated part) and the
/// generic conversions below.
/// </summary>
internal static partial class CommonLookups
{
    /// <summary>Battery manufacture dates packed as (year - 1980) * 512 + month * 32 + day (HID PDC 4.2.6).</summary>
    public static readonly NutLookup DateConversion = NutLookup.Function(DateConversionFun, DateConversionReverse);

    public static readonly NutLookup HexConversion = NutLookup.Function(
        (value, _) => ((ulong)HidValueCodec.Truncate(value)).ToString("x8", CultureInfo.InvariantCulture));

    /// <summary>
    /// The value is the index of a USB string descriptor. NUT publishes an empty string when the string cannot be
    /// read; leaving the variable out is more useful to clients.
    /// </summary>
    public static readonly NutLookup StringidConversion = NutLookup.Function(
        (value, context) => context.GetIndexedString((int)Math.Clamp(HidValueCodec.Truncate(value), 0, 255)));

    public static readonly NutLookup DivideBy10Conversion = NutLookup.Function(
        (value, _) => CFormat.FormatDouble("%0.1f", value * 0.1));

    public static readonly NutLookup DivideBy100Conversion = NutLookup.Function(
        (value, _) => CFormat.FormatDouble("%0.1f", value * 0.01));

    /// <summary>
    /// Kelvin to Celsius, unless the value is already outside the plausible Kelvin range (some HP firmwares send
    /// Celsius in a Kelvin field).
    /// </summary>
    public static readonly NutLookup KelvinCelsiusConversion = NutLookup.Function(
        (value, _) => CFormat.FormatDouble("%.1f", value is >= 273 and <= 373 ? value - 273.15 : value));

    private static string? DateConversionFun(double value, IHidConversionContext context)
    {
        long packed = HidValueCodec.Truncate(value);
        if (packed == 0)
        {
            return "not set";
        }

        // Arithmetic shift keeps pre-1980 dates negative, as in NUT.
        long year = 1980 + (packed >> 9);
        long month = (packed >> 5) & 0x0F;
        long day = packed & 0x1F;
        return string.Create(CultureInfo.InvariantCulture, $"{year:0000}/{month:00}/{day:00}");
    }

    private static double? DateConversionReverse(string? text, IHidConversionContext context)
    {
        if (!TryParseDate(text, out int year, out int month, out int day))
        {
            return null;
        }

        if (year - 1980 > 127 || month > 12 || day > 31)
        {
            return 0;
        }

        return ((year - 1980) << 9) + (month << 5) + day;
    }

    /// <summary>Reads "YYYY/MM/DD" like NUT's sscanf("%04d/%02d/%02d").</summary>
    internal static bool TryParseDate(string? text, out int year, out int month, out int day)
    {
        year = month = day = 0;
        if (text is null)
        {
            return false;
        }

        string[] parts = text.Trim().Split('/');
        return parts.Length == 3 &&
               int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out year) &&
               int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out month) &&
               int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out day) &&
               month >= 1 && day >= 1;
    }
}
