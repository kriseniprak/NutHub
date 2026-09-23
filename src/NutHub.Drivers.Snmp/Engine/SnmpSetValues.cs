using System.Globalization;
using Lextm.SharpSnmpLib;
using NutHub.Core.Model;
using NutHub.Drivers.Snmp.Mibs;

namespace NutHub.Drivers.Snmp.Engine;

/// <summary>
/// Builds the SNMP value written by a variable write or an instant command, following snmp-ups' su_setOID():
/// lookups turn texts back into integers, multipliers are divided out, TimeTicks are sent in hundredths.
/// </summary>
internal static class SnmpSetValues
{
    /// <summary>
    /// The value to write for <paramref name="entry"/>. <paramref name="lastType"/> is the type the agent reported
    /// for this object, which decides the SNMP type of <see cref="SnmpSetType.Auto"/> entries.
    /// </summary>
    /// <exception cref="FormatException">The text cannot be written as this object's type; the message says why.</exception>
    public static ISnmpData Build(MibEntry entry, string text, SnmpType? lastType)
    {
        SnmpSetType type = Resolve(entry, lastType);
        string value = text.Trim();
        switch (type)
        {
            case SnmpSetType.OctetString:
                if (entry.Converter == MibConverter.UsDateToIso)
                {
                    value = MibValueMapper.IsoDateToUs(value);
                }

                return new OctetString(value);

            case SnmpSetType.ObjectIdentifier:
                return new ObjectIdentifier(ParseOid(value));

            case SnmpSetType.TimeTicks:
            {
                long seconds = ParseInteger(entry, value, 0, uint.MaxValue / 100);
                return new TimeTicks((uint)(seconds * 100));
            }

            case SnmpSetType.Gauge:
                return new Gauge32((uint)ParseInteger(entry, value, 0, uint.MaxValue));

            default:
                return new Integer32((int)ParseInteger(entry, value, int.MinValue, int.MaxValue));
        }
    }

    /// <summary>The SNMP type actually used for <paramref name="entry"/>.</summary>
    public static SnmpSetType Resolve(MibEntry entry, SnmpType? lastType)
    {
        if (entry.SetType != SnmpSetType.Auto)
        {
            return entry.SetType;
        }

        return lastType switch
        {
            SnmpType.Integer32 => SnmpSetType.Integer,
            SnmpType.OctetString => SnmpSetType.OctetString,
            SnmpType.TimeTicks => SnmpSetType.TimeTicks,
            SnmpType.Gauge32 or SnmpType.Counter32 => SnmpSetType.Gauge,
            SnmpType.ObjectIdentifier => SnmpSetType.ObjectIdentifier,
            _ => entry.Kind == MibEntryKind.Text && entry.Lookup is null ? SnmpSetType.OctetString : SnmpSetType.Integer,
        };
    }

    private static long ParseInteger(MibEntry entry, string text, long min, long max)
    {
        long raw;
        if (entry.Lookup is not null && entry.Kind != MibEntryKind.Command)
        {
            raw = entry.Lookup.FindValue(text)
                  ?? throw new FormatException($"'{text}' is not one of: {string.Join(", ", entry.Lookup.Texts)}.");
        }
        else
        {
            if (!NutFormat.TryParseNumber(text, out double number))
            {
                throw new FormatException($"'{text}' is not a number.");
            }

            double scaled = entry.Multiplier is 0 or 1 ? number : number / entry.Multiplier;
            raw = (long)Math.Round(scaled, MidpointRounding.AwayFromZero);
        }

        if (raw < min || raw > max)
        {
            throw new FormatException(
                string.Create(CultureInfo.InvariantCulture, $"{text} is out of the range the device accepts ({min} to {max})."));
        }

        return raw;
    }

    private static string ParseOid(string text)
    {
        string oid = text.TrimStart('.');
        if (oid.Length == 0 || !oid.Split('.').All(arc => arc.Length > 0 && arc.All(char.IsAsciiDigit)))
        {
            throw new FormatException($"'{text}' is not an object identifier.");
        }

        return oid;
    }
}
