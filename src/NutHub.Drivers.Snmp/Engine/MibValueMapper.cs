using System.Globalization;
using System.Text.RegularExpressions;
using Lextm.SharpSnmpLib;
using NutHub.Core.Model;
using NutHub.Drivers.Snmp.Mibs;
using NutHub.Drivers.Snmp.Transport;

namespace NutHub.Drivers.Snmp.Engine;

/// <summary>
/// Turns the SNMP value of a mapping entry into its NUT form, following snmp-ups' su_ups_get(): multipliers,
/// lookups, the "invalid value" flags and the few conversions NUT hard-codes (APC IEM temperature unit, US dates).
/// </summary>
internal static partial class MibValueMapper
{
    /// <summary>The value 2 of the APC IEM probe unit object means Fahrenheit (NUT APCC_IEM_FAHRENHEIT).</summary>
    private const long Fahrenheit = 2;

    /// <summary>
    /// The NUT value of a Number or Text entry, or null when the device's value is unusable or flagged invalid.
    /// </summary>
    /// <param name="entry">The mapping entry.</param>
    /// <param name="data">The value read.</param>
    /// <param name="fahrenheitUnit">
    /// For entries with <see cref="MibEntry.FahrenheitUnitOid"/>: the unit object's value, or null when the agent did
    /// not answer it (NUT then assumes Fahrenheit).
    /// </param>
    public static string? ToNutValue(MibEntry entry, ISnmpData data, ISnmpData? fahrenheitUnit = null)
    {
        if (SnmpValues.IsMissing(data))
        {
            return null;
        }

        return entry.Kind switch
        {
            MibEntryKind.Number => NumberValue(entry, data, fahrenheitUnit),
            MibEntryKind.Text => TextValue(entry, data),
            _ => null,
        };
    }

    /// <summary>The status tokens a Status entry contributes ("OL", "OB LB"...); empty when none.</summary>
    public static IEnumerable<string> StatusTokens(MibEntry entry, ISnmpData data)
    {
        string? text = LookupText(entry, data);
        return string.IsNullOrWhiteSpace(text)
            ? []
            : text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>The message an Alarm entry contributes, or null when its value means "no alarm".</summary>
    public static string? AlarmMessage(MibEntry entry, ISnmpData data)
    {
        string? text = LookupText(entry, data);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static string? LookupText(MibEntry entry, ISnmpData data)
    {
        if (entry.Lookup is null || SnmpValues.IsMissing(data) || !SnmpValues.TryGetInteger(data, out long value))
        {
            return null;
        }

        return entry.Lookup.Find(value);
    }

    private static string? NumberValue(MibEntry entry, ISnmpData data, ISnmpData? fahrenheitUnit)
    {
        if (entry.Has(MibFlags.NotAvailableInvalid) && data is OctetString s && IsNotAvailable(s))
        {
            return null;
        }

        if (!SnmpValues.TryGetNumber(data, out double raw))
        {
            return null;
        }

        if ((entry.Has(MibFlags.NegativeInvalid) && raw < 0) || (entry.Has(MibFlags.ZeroInvalid) && raw == 0))
        {
            return null;
        }

        if (entry.Lookup is not null && SnmpValues.TryGetInteger(data, out long code) && entry.Lookup.Find(code) is { } text)
        {
            return text;
        }

        if (entry.FahrenheitUnitOid is not null)
        {
            bool fahrenheit = fahrenheitUnit is null || SnmpValues.IsMissing(fahrenheitUnit) ||
                              (SnmpValues.TryGetInteger(fahrenheitUnit, out long unit) && unit == Fahrenheit);
            double celsius = fahrenheit ? (raw - 32) / 1.8 : raw * entry.Multiplier;
            return NutFormat.Fixed(celsius, 1);
        }

        double value = raw * entry.Multiplier;

        // snmp-ups prints every ambient.temperature with one decimal.
        return entry.Name == "ambient.temperature" ? NutFormat.Fixed(value, 1) : NutFormat.Number(value, 2);
    }

    private static string? TextValue(MibEntry entry, ISnmpData data)
    {
        if (entry.Lookup is not null && SnmpValues.TryGetInteger(data, out long code) &&
            (SnmpValues.IsNumeric(data) || data is OctetString))
        {
            if (entry.Lookup.Find(code) is { } looked)
            {
                return looked;
            }
        }

        string? text = SnmpValues.ToText(data);
        if (text is null)
        {
            return null;
        }

        if (entry.Has(MibFlags.NotAvailableInvalid) &&
            string.Equals(text.Trim(), "N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (entry.Multiplier != 1 && SnmpValues.IsNumeric(data) && SnmpValues.TryGetNumber(data, out double number))
        {
            text = NutFormat.Number(number * entry.Multiplier, 2);
        }

        return entry.Converter switch
        {
            MibConverter.UsDateToIso => UsDateToIso(text),
            _ => text,
        };
    }

    /// <summary>"MM/DD/YYYY" (or "MM/DD/YY") to "YYYY-MM-DD"; anything else is returned unchanged.</summary>
    public static string UsDateToIso(string text)
    {
        Match m = UsDateRegex().Match(text.Trim());
        if (!m.Success)
        {
            return text;
        }

        int month = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        int day = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        int year = int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        if (m.Groups[3].Value.Length == 2)
        {
            // Two-digit years of UPS firmware are battery replacement dates: 1990-2089.
            year += year < 90 ? 2000 : 1900;
        }

        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return text;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{year:D4}-{month:D2}-{day:D2}");
    }

    /// <summary>"YYYY-MM-DD" back to "MM/DD/YYYY" for writing a date the device keeps in US format.</summary>
    public static string IsoDateToUs(string text)
    {
        Match m = IsoDateRegex().Match(text.Trim());
        return m.Success ? $"{m.Groups[2].Value}/{m.Groups[3].Value}/{m.Groups[1].Value}" : text;
    }

    private static bool IsNotAvailable(OctetString s) =>
        string.Equals(SnmpValues.ToText(s)?.Trim(), "N/A", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^(\d{1,2})/(\d{1,2})/(\d{4}|\d{2})$")]
    private static partial Regex UsDateRegex();

    [GeneratedRegex(@"^(\d{4})-(\d{2})-(\d{2})$")]
    private static partial Regex IsoDateRegex();
}
