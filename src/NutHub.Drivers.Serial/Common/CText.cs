using System.Globalization;
using System.Text;

namespace NutHub.Drivers.Serial.Common;

/// <summary>
/// The C string conversions the NUT drivers rely on (strtod, strtol, strspn), reproduced so the ported parsers accept
/// and reject exactly the same device replies: "014" is 14, "27.6\r" is 27.6, " 750" is 750, "@@@.@" is rejected by
/// the character check before it is ever converted.
/// </summary>
internal static class CText
{
    /// <summary>Bytes to text one to one (ISO-8859-1), so binary replies survive the round trip.</summary>
    public static Encoding Latin1 => Encoding.Latin1;

    /// <summary>True when every character is one of <paramref name="allowed"/> (C: strspn(s, allowed) == strlen(s)).</summary>
    public static bool OnlyChars(string text, string allowed)
    {
        foreach (char c in text)
        {
            if (allowed.IndexOf(c) < 0)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The characters NUT accepts in a numeric field before converting it.</summary>
    public static bool IsNumericField(string text) => OnlyChars(text, "0123456789 .");

    /// <summary>
    /// C strtod: skips leading white space, reads the longest valid decimal prefix, returns 0 when there is none.
    /// </summary>
    public static double StrToD(string text) => TryStrToD(text, out double value) ? value : 0;

    /// <summary>Like <see cref="StrToD"/>, but reports whether a number was found at all.</summary>
    public static bool TryStrToD(string? text, out double value)
    {
        value = 0;
        if (text is null)
        {
            return false;
        }

        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        int start = i;
        if (i < text.Length && (text[i] == '+' || text[i] == '-'))
        {
            i++;
        }

        int digits = 0;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
            digits++;
        }

        if (i < text.Length && text[i] == '.')
        {
            i++;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
                digits++;
            }
        }

        if (digits == 0)
        {
            return false;
        }

        return double.TryParse(text.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>C strtol in base 10 or 16: leading white space, optional sign, longest valid prefix, 0 when none.</summary>
    public static long StrToL(string text, int radix = 10) => TryStrToL(text, radix, out long value, out _) ? value : 0;

    /// <summary>strtol that also returns the index after the number (C's endptr).</summary>
    public static bool TryStrToL(string text, int radix, out long value, out int end)
    {
        value = 0;
        int i = 0;
        while (i < text.Length && char.IsWhiteSpace(text[i]))
        {
            i++;
        }

        bool negative = false;
        if (i < text.Length && (text[i] == '+' || text[i] == '-'))
        {
            negative = text[i] == '-';
            i++;
        }

        if (radix == 16 && i + 1 < text.Length && text[i] == '0' && (text[i + 1] == 'x' || text[i + 1] == 'X') &&
            i + 2 < text.Length && Uri.IsHexDigit(text[i + 2]))
        {
            i += 2;
        }

        int digitsStart = i;
        long result = 0;
        while (i < text.Length)
        {
            int digit = DigitValue(text[i]);
            if (digit < 0 || digit >= radix)
            {
                break;
            }

            result = unchecked(result * radix + digit);
            i++;
        }

        if (i == digitsStart)
        {
            end = 0;
            return false;
        }

        value = negative ? -result : result;
        end = i;
        return true;
    }

    /// <summary>Removes the given characters from both ends (NUT str_trim_m).</summary>
    public static string TrimChars(string text, string characters) => text.Trim(characters.ToCharArray());

    /// <summary>Printable form of a command or reply for logs: control characters as \r, \n or \xNN.</summary>
    public static string Printable(string text)
    {
        var sb = new StringBuilder(text.Length + 8);
        foreach (char c in text)
        {
            sb.Append(c switch
            {
                '\r' => @"\r",
                '\n' => @"\n",
                _ when c < ' ' || c > '~' => $"\\x{(int)c:X2}",
                _ => c.ToString(),
            });
        }

        return sb.ToString();
    }

    private static int DigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };
}
