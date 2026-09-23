using System.Globalization;
using System.Text;

namespace NutHub.Drivers.Hid.UsbHid;

/// <summary>
/// Formats one number with the printf format of a mapping entry ("%.0f", "%.1f", "%0.1f"...), always with a dot
/// as decimal separator. Like NUT's dstate_setinfo_dynamic, a format that is not a single floating-point
/// conversion ("%s", "%d") yields no value rather than garbage.
/// </summary>
internal static class CFormat
{
    public static string? FormatDouble(string? format, double value)
    {
        if (format is null || double.IsNaN(value) || double.IsInfinity(value))
        {
            return null;
        }

        var result = new StringBuilder();
        bool converted = false;
        int i = 0;
        while (i < format.Length)
        {
            char c = format[i++];
            if (c != '%')
            {
                result.Append(c);
                continue;
            }

            if (i < format.Length && format[i] == '%')
            {
                result.Append('%');
                i++;
                continue;
            }

            if (converted)
            {
                return null;
            }

            bool leftAlign = false, plus = false, space = false, zero = false;
            for (; i < format.Length && "-+ 0#".Contains(format[i]); i++)
            {
                switch (format[i])
                {
                    case '-': leftAlign = true; break;
                    case '+': plus = true; break;
                    case ' ': space = true; break;
                    case '0': zero = true; break;
                }
            }

            int width = ReadNumber(format, ref i) ?? 0;
            int precision = 6;
            if (i < format.Length && format[i] == '.')
            {
                i++;
                precision = ReadNumber(format, ref i) ?? 0;
            }

            while (i < format.Length && format[i] is 'l' or 'L')
            {
                i++;
            }

            if (i >= format.Length || width > 64 || precision > 30)
            {
                return null;
            }

            char conversion = format[i++];
            string? body = conversion switch
            {
                'f' or 'F' => Math.Abs(value).ToString("F" + precision, CultureInfo.InvariantCulture),
                'e' or 'E' => Exponential(Math.Abs(value), precision, conversion),
                'g' or 'G' => General(Math.Abs(value), precision, conversion),
                _ => null,
            };
            if (body is null)
            {
                return null;
            }

            bool negative = value < 0 && body.Any(ch => ch is >= '1' and <= '9');
            string sign = negative ? "-" : plus ? "+" : space ? " " : "";
            int pad = width - sign.Length - body.Length;
            if (pad > 0 && leftAlign)
            {
                result.Append(sign).Append(body).Append(' ', pad);
            }
            else if (pad > 0 && zero)
            {
                result.Append(sign).Append('0', pad).Append(body);
            }
            else if (pad > 0)
            {
                result.Append(' ', pad).Append(sign).Append(body);
            }
            else
            {
                result.Append(sign).Append(body);
            }

            converted = true;
        }

        return converted ? result.ToString() : null;
    }

    private static int? ReadNumber(string format, ref int i)
    {
        int start = i;
        while (i < format.Length && char.IsAsciiDigit(format[i]))
        {
            i++;
        }

        return i == start ? null : int.Parse(format.AsSpan(start, i - start), CultureInfo.InvariantCulture);
    }

    /// <summary>C style: at least two exponent digits ("1.5e+02").</summary>
    private static string Exponential(double value, int precision, char conversion)
    {
        string text = value.ToString((conversion == 'E' ? "E" : "e") + precision, CultureInfo.InvariantCulture);
        int e = text.IndexOfAny(['e', 'E']);
        string mantissa = text[..e];
        int exponent = int.Parse(text.AsSpan(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        return $"{mantissa}{text[e]}{(exponent < 0 ? '-' : '+')}{Math.Abs(exponent):00}";
    }

    private static string General(double value, int precision, char conversion)
    {
        int p = precision == 0 ? 1 : precision;
        if (value == 0)
        {
            return "0";
        }

        int exponent = (int)Math.Floor(Math.Log10(value));
        if (exponent < -4 || exponent >= p)
        {
            string text = Exponential(value, p - 1, conversion == 'G' ? 'E' : 'e');
            int e = text.IndexOfAny(['e', 'E']);
            return TrimZeros(text[..e]) + text[e..];
        }

        return TrimZeros(value.ToString("F" + Math.Max(0, p - 1 - exponent), CultureInfo.InvariantCulture));
    }

    private static string TrimZeros(string text) =>
        text.Contains('.') ? text.TrimEnd('0').TrimEnd('.') : text;
}
