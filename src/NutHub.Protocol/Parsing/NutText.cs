using System.Text;

namespace NutHub.Protocol.Parsing;

/// <summary>
/// Text helpers with the exact semantics of upsd: <c>pconf_encode</c> for values in answers, and ASCII-only case
/// folding (C <c>strcasecmp</c>) for command words and names.
/// </summary>
internal static class NutText
{
    /// <summary>
    /// Escapes a value for use inside double quotes, like <c>pconf_encode</c>: a backslash before <c>"</c>,
    /// <c>\</c> and <c>#</c> (older parsers took an unescaped <c>#</c> for a comment even inside quotes). Control
    /// characters, which upsd can never hold, become spaces so that an answer always stays on one line.
    /// </summary>
    public static string Escape(string value)
    {
        int i = 0;
        while (i < value.Length && !NeedsWork(value[i]))
        {
            i++;
        }

        if (i == value.Length)
        {
            return value;
        }

        var sb = new StringBuilder(value.Length + 8);
        sb.Append(value, 0, i);
        for (; i < value.Length; i++)
        {
            char c = value[i];
            if (c is '"' or '\\' or '#')
            {
                sb.Append('\\').Append(c);
            }
            else if (c < ' ')
            {
                sb.Append(' ');
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    /// <summary>The value escaped and between double quotes, as upsd sends every value.</summary>
    public static string Quote(string value) => "\"" + Escape(value) + "\"";

    /// <summary>Upper-cases ASCII letters only, like the command matching of upsd (<c>strcasecmp</c>).</summary>
    public static string AsciiUpper(string word)
    {
        foreach (char c in word)
        {
            if (c is >= 'a' and <= 'z')
            {
                return string.Create(word.Length, word, static (span, source) =>
                {
                    for (int i = 0; i < source.Length; i++)
                    {
                        char ch = source[i];
                        span[i] = ch is >= 'a' and <= 'z' ? (char)(ch - 32) : ch;
                    }
                });
            }
        }

        return word;
    }

    /// <summary>Case-insensitive equality on ASCII letters only (<c>strcasecmp(a, b) == 0</c>).</summary>
    public static bool AsciiEqualsIgnoreCase(string a, string b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            if (Lower(a[i]) != Lower(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Whether <paramref name="text"/> starts with <paramref name="prefix"/>, ignoring ASCII case.</summary>
    public static bool AsciiStartsWithIgnoreCase(string text, string prefix)
    {
        if (text.Length < prefix.Length)
        {
            return false;
        }

        for (int i = 0; i < prefix.Length; i++)
        {
            if (Lower(text[i]) != Lower(prefix[i]))
            {
                return false;
            }
        }

        return true;
    }

    internal static char Lower(char c) => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;

    private static bool NeedsWork(char c) => c is '"' or '\\' or '#' || c < ' ';
}

/// <summary>
/// The order upsd lists variables in: its state tree is sorted with <c>strcasecmp</c>, which compares
/// lower-cased ASCII bytes (so "_" sorts before letters, unlike <see cref="StringComparer.OrdinalIgnoreCase"/>).
/// </summary>
internal sealed class NutNameComparer : IComparer<string>
{
    public static readonly NutNameComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int length = Math.Min(x.Length, y.Length);
        for (int i = 0; i < length; i++)
        {
            int diff = NutText.Lower(x[i]) - NutText.Lower(y[i]);
            if (diff != 0)
            {
                return diff;
            }
        }

        return x.Length - y.Length;
    }
}
