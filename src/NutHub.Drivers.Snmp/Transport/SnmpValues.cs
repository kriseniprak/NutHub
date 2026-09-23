using System.Globalization;
using System.Text;
using Lextm.SharpSnmpLib;

namespace NutHub.Drivers.Snmp.Transport;

/// <summary>
/// Conversions of SNMP values the way NUT's snmp-ups reads them (nut_snmp_get_int / nut_snmp_get_str): TimeTicks
/// become seconds, an OBJECT IDENTIFIER value stands for its last arc, numbers sent as text are parsed.
/// </summary>
internal static class SnmpValues
{
    /// <summary>Whether the value says the object does not exist (v2c exceptions, or a NULL from a v1 agent).</summary>
    public static bool IsMissing(ISnmpData? data) =>
        data is null ||
        data.TypeCode is SnmpType.NoSuchObject or SnmpType.NoSuchInstance or SnmpType.EndOfMibView or SnmpType.Null;

    /// <summary>A numeric reading: integers, counters, gauges, TimeTicks in seconds, numeric text, OID last arc.</summary>
    public static bool TryGetNumber(ISnmpData data, out double value)
    {
        switch (data)
        {
            case Integer32 i:
                value = i.ToInt32();
                return true;
            case Counter32 c:
                value = c.ToUInt32();
                return true;
            case Gauge32 g:
                value = g.ToUInt32();
                return true;
            case Counter64 c64:
                value = c64.ToUInt64();
                return true;
            case TimeTicks t:
                value = t.ToUInt32() / 100; // whole seconds, as nut_snmp_get_int() returns them
                return true;
            case ObjectIdentifier oid:
                uint[] arcs = oid.ToNumerical();
                value = arcs.Length > 0 ? arcs[^1] : 0;
                return arcs.Length > 0;
            case OctetString s:
                return double.TryParse(CleanText(s.GetRaw()), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                       double.IsFinite(value);
            default:
                value = 0;
                return false;
        }
    }

    /// <summary>An integer for lookups: as <see cref="TryGetNumber"/>, truncated toward zero.</summary>
    public static bool TryGetInteger(ISnmpData data, out long value)
    {
        if (data is TimeTicks ticks)
        {
            value = ticks.ToUInt32() / 100;
            return true;
        }

        if (TryGetNumber(data, out double number) && number is >= long.MinValue and <= long.MaxValue)
        {
            value = (long)Math.Truncate(number);
            return true;
        }

        value = 0;
        return false;
    }

    /// <summary>Whether the value is a number on the wire (as opposed to text).</summary>
    public static bool IsNumeric(ISnmpData data) =>
        data is Integer32 or Counter32 or Gauge32 or Counter64 or TimeTicks or ObjectIdentifier;

    /// <summary>
    /// Text for a NUT string variable: printable text as is (trailing NULs and spaces removed), binary octets as
    /// hexadecimal, numbers in decimal, TimeTicks in seconds.
    /// </summary>
    public static string? ToText(ISnmpData data) => data switch
    {
        OctetString s => DecodeOctets(s.GetRaw()),
        TimeTicks t => (t.ToUInt32() / 100).ToString(CultureInfo.InvariantCulture),
        Integer32 i => i.ToInt32().ToString(CultureInfo.InvariantCulture),
        Counter32 c => c.ToUInt32().ToString(CultureInfo.InvariantCulture),
        Gauge32 g => g.ToUInt32().ToString(CultureInfo.InvariantCulture),
        Counter64 c64 => c64.ToUInt64().ToString(CultureInfo.InvariantCulture),
        ObjectIdentifier oid => oid.ToString(),
        IP ip => ip.ToString(),
        Opaque opaque => opaque.ToString(),
        _ => null,
    };

    /// <summary>The dotted form without a leading dot, as used for dictionary keys.</summary>
    public static string Key(ObjectIdentifier oid) => oid.ToString().TrimStart('.');

    private static string CleanText(byte[] raw) => Encoding.UTF8.GetString(raw).Trim('\0', ' ', '\t', '\r', '\n');

    private static string DecodeOctets(byte[] raw)
    {
        int length = raw.Length;
        while (length > 0 && raw[length - 1] == 0)
        {
            length--;
        }

        ReadOnlySpan<byte> bytes = raw.AsSpan(0, length);
        bool printable = true;
        foreach (byte b in bytes)
        {
            if (b < 0x20 && b != 0x09 && b != 0x0A && b != 0x0D)
            {
                printable = false;
                break;
            }
        }

        if (printable)
        {
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes).Trim();
            }
            catch (DecoderFallbackException)
            {
                // Latin-1 text from older cards: every byte is a character.
                return Encoding.Latin1.GetString(bytes).Trim();
            }
        }

        // Binary (a MAC address, a packed date...): hexadecimal, as net-snmp shows it.
        return string.Join(' ', bytes.ToArray().Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
    }
}
