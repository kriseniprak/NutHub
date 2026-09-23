namespace NutHub.Drivers.Hid.Descriptors;

/// <summary>
/// Reads and writes field values in report buffers, and converts between logical (raw) and physical values
/// with the unit exponent. A port of GetValue / SetValue (NUT drivers/hidparser.c) and logical_to_physical /
/// physical_to_logical / get_unit_expo (NUT drivers/libhid.c), whose quirks the subdriver tables rely on.
/// Report buffers always start with the report id byte, even for devices without report ids.
/// </summary>
internal static class HidValueCodec
{
    /// <summary>
    /// Units whose values the HID spec expresses in scaled SI units: volts and watts/VA are defined with an
    /// exponent of 7, so NUT subtracts 7 from the declared exponent (NUT libhid.c HIDUnits).
    /// </summary>
    private static readonly (uint Unit, int Exponent)[] UnitExponents =
    [
        (0x00F0D121, 7), // volt
        (0x0000D121, 7), // VA and watt
    ];

    /// <summary>
    /// Extracts the logical value of <paramref name="field"/> from <paramref name="report"/>: masks away bits
    /// beyond what the logical limits need (some APC firmwares put garbage there), sign-extends when the
    /// minimum is negative, and clamps to the logical range. Bits past the end of the buffer read as 0.
    /// </summary>
    public static long GetLogical(ReadOnlySpan<byte> report, HidField field)
    {
        ArgumentNullException.ThrowIfNull(field);
        int size = Math.Min(field.BitSize, 64);
        ulong raw = 0;
        int bit = field.BitOffset + 8;
        for (int weight = 0; weight < size; weight++, bit++)
        {
            int index = bit >> 3;
            if (index < report.Length && (report[index] & (1 << (bit & 7))) != 0)
            {
                raw |= 1UL << weight;
            }
        }

        long min = field.LogicalMinimum;
        long max = field.LogicalMaximum;
        ulong magMax = max >= 0 ? (ulong)max : (ulong)(-(max + 1));
        ulong magMin = min >= 0 ? (ulong)min : (ulong)(-(min + 1));
        int high = HighBit(Math.Max(magMax, magMin));
        long value;
        if (high >= 63)
        {
            value = unchecked((long)raw);
        }
        else
        {
            ulong signBit = 1UL << high;
            ulong mask = (signBit - 1) | (min < 0 ? signBit : 0);
            ulong masked = raw & mask;
            if (min < 0 && (masked & signBit) != 0)
            {
                masked |= ~mask;
            }

            value = unchecked((long)masked);
        }

        return Math.Clamp(value, Math.Min(min, max), Math.Max(min, max));
    }

    /// <summary>Writes the low <see cref="HidField.BitSize"/> bits of <paramref name="logical"/> into the report.</summary>
    public static void SetLogical(Span<byte> report, HidField field, long logical)
    {
        ArgumentNullException.ThrowIfNull(field);
        int bit = field.BitOffset + 8;
        for (int weight = 0; weight < field.BitSize; weight++, bit++)
        {
            int index = bit >> 3;
            if (index >= report.Length)
            {
                throw new ArgumentException(
                    $"The report buffer ({report.Length} bytes) is too short for {field}.", nameof(report));
            }

            bool set = weight < 64 && ((logical >> weight) & 1) != 0;
            if (set)
            {
                report[index] |= (byte)(1 << (bit & 7));
            }
            else
            {
                report[index] &= (byte)~(1 << (bit & 7));
            }
        }
    }

    /// <summary>The physical value: logical scaled to the physical range, then multiplied by 10^exponent.</summary>
    public static double GetValue(ReadOnlySpan<byte> report, HidField field) =>
        LogicalToPhysical(field, GetLogical(report, field)) * Math.Pow(10, EffectiveExponent(field));

    /// <summary>The logical value to write for a physical value (the inverse of <see cref="GetValue"/>).</summary>
    public static long ToLogical(HidField field, double physical) =>
        PhysicalToLogical(field, physical / Math.Pow(10, EffectiveExponent(field)));

    public static double LogicalToPhysical(HidField field, long logical)
    {
        // HID spec: undefined physical limits, or both zero, mean physical = logical.
        if (!field.HasPhysicalMaximum || !field.HasPhysicalMinimum ||
            (field.PhysicalMaximum == 0 && field.PhysicalMinimum == 0))
        {
            return logical;
        }

        if (field.PhysicalMaximum <= field.PhysicalMinimum || field.LogicalMaximum <= field.LogicalMinimum)
        {
            return logical;
        }

        double factor = (double)(field.PhysicalMaximum - field.PhysicalMinimum) /
                        (field.LogicalMaximum - field.LogicalMinimum);
        double physical = (logical - field.LogicalMinimum) * factor + field.PhysicalMinimum;
        return Math.Clamp(physical, field.PhysicalMinimum, field.PhysicalMaximum);
    }

    public static long PhysicalToLogical(HidField field, double physical)
    {
        if (!field.HasPhysicalMaximum || !field.HasPhysicalMinimum ||
            (field.PhysicalMaximum == 0 && field.PhysicalMinimum == 0) ||
            field.PhysicalMaximum <= field.PhysicalMinimum || field.LogicalMaximum <= field.LogicalMinimum)
        {
            return Truncate(physical);
        }

        double factor = (double)(field.LogicalMaximum - field.LogicalMinimum) /
                        (field.PhysicalMaximum - field.PhysicalMinimum);
        long logical = Truncate((physical - field.PhysicalMinimum) * factor) + field.LogicalMinimum;
        return Math.Clamp(logical, field.LogicalMinimum, field.LogicalMaximum);
    }

    /// <summary>The declared unit exponent corrected for the units the spec already scales (NUT get_unit_expo).</summary>
    public static int EffectiveExponent(HidField field)
    {
        int exponent = field.UnitExponent;
        foreach (var (unit, unitExponent) in UnitExponents)
        {
            if (unit == field.Unit)
            {
                return exponent - unitExponent;
            }
        }

        return exponent;
    }

    /// <summary>C's (long) conversion of a double: truncation toward zero, saturated instead of undefined.</summary>
    public static long Truncate(double value)
    {
        if (double.IsNaN(value))
        {
            return 0;
        }

        if (value >= long.MaxValue)
        {
            return long.MaxValue;
        }

        return value <= long.MinValue ? long.MinValue : (long)value;
    }

    /// <summary>1 + the position of the highest set bit, 0 for 0.</summary>
    private static int HighBit(ulong x) => x == 0 ? 0 : 64 - System.Numerics.BitOperations.LeadingZeroCount(x);
}
