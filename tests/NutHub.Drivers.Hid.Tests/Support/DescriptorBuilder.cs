using NutHub.Drivers.Hid.Descriptors;

namespace NutHub.Drivers.Hid.Tests.Support;

/// <summary>Writes HID report descriptors item by item, choosing the shortest encoding like firmware tools do.</summary>
internal sealed class DescriptorBuilder
{
    public const byte Application = 0x01;
    public const byte Physical = 0x00;
    public const byte Logical = 0x02;

    /// <summary>Data, Variable, Absolute: a normal value.</summary>
    public const byte Data = 0x02;

    /// <summary>Constant: padding.</summary>
    public const byte Constant = 0x03;

    private readonly List<byte> _bytes = [];
    private ushort _page;

    public byte[] ToArray() => [.. _bytes];

    public DescriptorBuilder UsagePage(ushort page)
    {
        _page = page;
        return Unsigned(0x04, page);
    }

    /// <summary>A usage of the current page (short form).</summary>
    public DescriptorBuilder Usage(ushort id) => Unsigned(0x08, id);

    /// <summary>A usage by its name in the standard table: switches the usage page when needed.</summary>
    public DescriptorBuilder Usage(string name)
    {
        if (!HidUsageTable.Standard.TryGetCode(name, out uint code))
        {
            throw new ArgumentException($"Unknown usage '{name}'.", nameof(name));
        }

        if ((ushort)(code >> 16) != _page)
        {
            UsagePage((ushort)(code >> 16));
        }

        return Usage((ushort)code);
    }

    /// <summary>A usage in the 4-byte form that carries its page.</summary>
    public DescriptorBuilder ExtendedUsage(uint usage) => Raw(0x0B, (byte)usage, (byte)(usage >> 8), (byte)(usage >> 16), (byte)(usage >> 24));

    public DescriptorBuilder UsageMinimum(ushort id) => Unsigned(0x18, id);

    public DescriptorBuilder UsageMaximum(ushort id) => Unsigned(0x28, id);

    public DescriptorBuilder Collection(byte type) => Raw(0xA1, type);

    public DescriptorBuilder Collection(string usage, byte type) => Usage(usage).Collection(type);

    public DescriptorBuilder EndCollection() => Raw(0xC0);

    public DescriptorBuilder ReportId(byte id) => Raw(0x85, id);

    public DescriptorBuilder ReportSize(int bits) => Unsigned(0x74, (uint)bits);

    public DescriptorBuilder ReportCount(int count) => Unsigned(0x94, (uint)count);

    public DescriptorBuilder LogicalMinimum(long value) => Signed(0x14, value);

    public DescriptorBuilder LogicalMaximum(long value) => Signed(0x24, value);

    public DescriptorBuilder PhysicalMinimum(long value) => Signed(0x34, value);

    public DescriptorBuilder PhysicalMaximum(long value) => Signed(0x44, value);

    public DescriptorBuilder UnitExponent(int exponent) => Raw(0x55, (byte)(exponent & 0x0F));

    public DescriptorBuilder Unit(uint unit) => Unsigned(0x64, unit);

    public DescriptorBuilder Push() => Raw(0xA4);

    public DescriptorBuilder Pop() => Raw(0xB4);

    public DescriptorBuilder Input(byte flags = Data) => Raw(0x81, flags);

    public DescriptorBuilder Output(byte flags = Data) => Raw(0x91, flags);

    public DescriptorBuilder Feature(byte flags = Data) => Raw(0xB1, flags);

    /// <summary>One value: report size and count, limits, then the main item.</summary>
    public DescriptorBuilder Value(string usage, int bits, long min, long max, bool input = false)
    {
        Usage(usage).LogicalMinimum(min).LogicalMaximum(max).ReportSize(bits).ReportCount(1);
        return input ? Input() : Feature();
    }

    public DescriptorBuilder Raw(params byte[] bytes)
    {
        _bytes.AddRange(bytes);
        return this;
    }

    private DescriptorBuilder Unsigned(byte prefix, uint value) => value switch
    {
        <= 0xFF => Raw((byte)(prefix | 1), (byte)value),
        <= 0xFFFF => Raw((byte)(prefix | 2), (byte)value, (byte)(value >> 8)),
        _ => Raw((byte)(prefix | 3), (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)),
    };

    private DescriptorBuilder Signed(byte prefix, long value) => value switch
    {
        >= sbyte.MinValue and <= sbyte.MaxValue => Raw((byte)(prefix | 1), (byte)value),
        >= short.MinValue and <= short.MaxValue => Raw((byte)(prefix | 2), (byte)value, (byte)(value >> 8)),
        _ => Raw((byte)(prefix | 3), (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24)),
    };
}
