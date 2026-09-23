using System.Globalization;
using System.Text.RegularExpressions;

namespace NutHub.Core.Model;

/// <summary>
/// Number formatting and name validation following the NUT conventions: numbers use a dot as decimal separator and
/// never an exponent; UPS and variable names use a restricted character set.
/// </summary>
public static partial class NutFormat
{
    /// <summary>Formats a number the way NUT clients expect it ("230", "13.6", never "1.2E4" or "13,6").</summary>
    public static string Number(double value, int maxDecimals = 2)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return "0";
        }

        double rounded = Math.Round(value, maxDecimals, MidpointRounding.AwayFromZero);
        string format = maxDecimals <= 0 ? "0" : "0." + new string('#', maxDecimals);
        string text = rounded.ToString(format, CultureInfo.InvariantCulture);
        return text == "-0" ? "0" : text;
    }

    /// <summary>Formats a number with a fixed count of decimals ("50.0", "13.60").</summary>
    public static string Fixed(double value, int decimals) =>
        Math.Round(value, decimals, MidpointRounding.AwayFromZero).ToString("F" + decimals, CultureInfo.InvariantCulture);

    /// <summary>Parses a NUT number; accepts integers and dot-decimal floats with optional surrounding spaces.</summary>
    public static bool TryParseNumber(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
               !double.IsNaN(value) && !double.IsInfinity(value);
    }

    public static double? ParseNumberOrNull(string? text) => TryParseNumber(text, out double v) ? v : null;

    /// <summary>
    /// A valid UPS name: 1 to 64 characters among letters, digits, '.', '_' and '-', starting with a letter or
    /// digit. Stricter than upsd, so names are safe in URLs, file names and shell scripts.
    /// </summary>
    public static bool IsValidUpsName(string? name) => name is not null && UpsNameRegex().IsMatch(name);

    /// <summary>
    /// A valid variable or command name, e.g. "battery.charge", "input.L1-N.voltage", "test.battery.start.quick".
    /// </summary>
    public static bool IsValidVariableName(string? name) => name is not null && VariableNameRegex().IsMatch(name);

    // \z, not $: $ also matches before a final line feed, which would let a name inject a protocol line.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}\z")]
    private static partial Regex UpsNameRegex();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z")]
    private static partial Regex VariableNameRegex();
}
