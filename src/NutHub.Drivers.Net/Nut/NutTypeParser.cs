using System.Globalization;
using NutHub.Core.Model;

namespace NutHub.Drivers.Net.Nut;

/// <summary>What "GET TYPE" says about a variable, before its enum values and ranges are fetched.</summary>
internal readonly record struct NutTypeDescription(bool Writable, bool IsEnum, bool IsRange, bool IsString, int MaxLength)
{
    /// <summary>
    /// When the server cannot describe a writable variable (very old upsd), treat it as unconstrained text: the
    /// upstream server still validates the value, and a number check here could reject a valid string.
    /// </summary>
    public static readonly NutTypeDescription UnknownWritable = new(true, false, false, true, 0);

    /// <remarks>
    /// Enumerated values are often words ("enabled", "disabled") even when the server does not say STRING; an enum
    /// whose list came back empty is therefore treated as text rather than as a number.
    /// </remarks>
    public VariableInfo ToVariableInfo(IReadOnlyList<string> enumValues, IReadOnlyList<ValueRange> ranges) => new()
    {
        Writable = Writable,
        Type = IsString || (IsEnum && enumValues.Count == 0) ? VariableType.String : VariableType.Number,
        MaxLength = IsString ? MaxLength : 0,
        EnumValues = IsEnum ? enumValues : [],
        Ranges = IsRange ? ranges : [],
    };
}

/// <summary>Parses the answers that describe variables: TYPE, ENUM and RANGE items.</summary>
internal static class NutTypeParser
{
    /// <summary>
    /// Parses the words after "TYPE ups var": any combination of RW, ENUM, RANGE, STRING:n and NUMBER
    /// (docs/net-protocol.txt, GET TYPE). Unknown words are ignored so a newer server does not break the driver.
    /// </summary>
    public static NutTypeDescription ParseTypeWords(IEnumerable<string> words)
    {
        bool writable = false, isEnum = false, isRange = false, isString = false;
        int maxLength = 0;
        foreach (string word in words)
        {
            string w = word.ToUpperInvariant();
            if (w == "RW")
            {
                writable = true;
            }
            else if (w == "ENUM")
            {
                isEnum = true;
            }
            else if (w == "RANGE")
            {
                isRange = true;
            }
            else if (w == "STRING" || w.StartsWith("STRING:", StringComparison.Ordinal))
            {
                isString = true;
                if (w.Length > 7 &&
                    int.TryParse(w.AsSpan(7), NumberStyles.None, CultureInfo.InvariantCulture, out int length))
                {
                    maxLength = Math.Max(0, length);
                }
            }
        }

        return new NutTypeDescription(writable, isEnum, isRange, isString, maxLength);
    }

    /// <summary>Reads "RANGE ups var min max" items (already without the RANGE word); malformed items are skipped.</summary>
    public static List<ValueRange> ParseRanges(IEnumerable<List<string>> items)
    {
        var ranges = new List<ValueRange>();
        foreach (List<string> item in items)
        {
            if (item.Count >= 4 && NutFormat.TryParseNumber(item[2], out _) && NutFormat.TryParseNumber(item[3], out _))
            {
                ranges.Add(new ValueRange(item[2].Trim(), item[3].Trim()));
            }
        }

        return ranges;
    }

    /// <summary>Reads "ENUM ups var value" items (already without the ENUM word), keeping the server's order.</summary>
    public static List<string> ParseEnumValues(IEnumerable<List<string>> items)
    {
        var values = new List<string>();
        foreach (List<string> item in items)
        {
            if (item.Count >= 3 && !values.Contains(item[2], StringComparer.Ordinal))
            {
                values.Add(item[2]);
            }
        }

        return values;
    }
}
