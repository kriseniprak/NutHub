using NutHub.Drivers.Hid.Descriptors;

namespace NutHub.Drivers.Hid.Tests.Descriptors;

public sealed class HidValueCodecTests
{
    [Fact]
    public void Reads_little_endian_values_at_any_bit_offset()
    {
        // Report id, then 3 bits of flags, then a 16-bit value 0x1234 starting at bit 3.
        byte[] report = [0x05, 0x1234 << 3 & 0xFF, (0x1234 << 3 >> 8) & 0xFF, (0x1234 << 3 >> 16) & 0xFF];

        Assert.Equal(0x1234, HidValueCodec.GetLogical(report, Field(offset: 3, size: 16, min: 0, max: 65535)));
    }

    [Fact]
    public void Negative_minimums_make_the_field_signed()
    {
        HidField delay = Field(offset: 0, size: 16, min: -1, max: 32767);

        Assert.Equal(-1, HidValueCodec.GetLogical([0x06, 0xFF, 0xFF], delay));
        Assert.Equal(30, HidValueCodec.GetLogical([0x06, 0x1E, 0x00], delay));
    }

    [Fact]
    public void Garbage_above_the_logical_range_is_masked_away()
    {
        // APC Back-UPS BF500 (NUT hidparser.c): LogMax 0xFFFF in a 32-bit field holding 0x80080a00 means 0x0a00.
        HidField field = Field(offset: 0, size: 32, min: 0, max: 0xFFFF);

        Assert.Equal(0x0A00, HidValueCodec.GetLogical([0x01, 0x00, 0x0A, 0x08, 0x80], field));
    }

    [Fact]
    public void Values_outside_the_logical_range_are_clamped()
    {
        HidField percent = Field(offset: 0, size: 8, min: 0, max: 100);

        Assert.Equal(100, HidValueCodec.GetLogical([0x01, 120], percent));
    }

    [Fact]
    public void Bits_past_the_end_of_a_short_report_read_as_zero()
    {
        Assert.Equal(0x12, HidValueCodec.GetLogical([0x01, 0x12], Field(offset: 0, size: 16, min: 0, max: 65535)));
    }

    [Fact]
    public void Physical_limits_scale_the_value()
    {
        HidField field = Field(offset: 0, size: 8, min: 0, max: 255);
        field.PhysicalMinimum = 0;
        field.PhysicalMaximum = 1000;
        field.HasPhysicalMinimum = field.HasPhysicalMaximum = true;

        Assert.Equal(1000, HidValueCodec.GetValue([0x01, 255], field));
        Assert.Equal(51, HidValueCodec.ToLogical(field, 200));
    }

    [Theory]
    [InlineData(0x00F0D121u, 7, 230, 230)] // volt, declared with the spec's implicit exponent 7
    [InlineData(0x00F0D121u, 5, 2301, 23.01)]
    [InlineData(0x0000D121u, 7, 700, 700)] // watt / VA
    [InlineData(0u, -1, 500, 50)] // no unit: the exponent applies as is
    [InlineData(0u, -2, 1234, 12.34)]
    public void The_unit_exponent_scales_the_value(uint unit, int exponent, int raw, double expected)
    {
        HidField field = Field(offset: 0, size: 16, min: 0, max: 65535);
        field.Unit = unit;
        field.UnitExponent = (sbyte)exponent;

        Assert.Equal(expected, HidValueCodec.GetValue([0x01, (byte)raw, (byte)(raw >> 8)], field), 6);
        Assert.Equal(raw, HidValueCodec.ToLogical(field, expected + 1e-9));
    }

    [Fact]
    public void Writing_a_field_keeps_the_other_bits_of_the_report()
    {
        byte[] report = [0x05, 0xFF, 0xFF, 0xFF];
        HidValueCodec.SetLogical(report, Field(offset: 4, size: 8, min: 0, max: 255), 0x00);

        Assert.Equal([0x05, 0x0F, 0xF0, 0xFF], report);
    }

    [Fact]
    public void Negative_values_are_written_in_twos_complement()
    {
        byte[] report = [0x06, 0x00, 0x00];
        HidValueCodec.SetLogical(report, Field(offset: 0, size: 16, min: -1, max: 32767), -1);

        Assert.Equal([0x06, 0xFF, 0xFF], report);
    }

    [Fact]
    public void Writing_past_the_buffer_is_refused()
    {
        Assert.Throws<ArgumentException>(() => HidValueCodec.SetLogical(new byte[2], Field(offset: 4, size: 8, min: 0, max: 255), 1));
    }

    private static HidField Field(int offset, int size, long min, long max) => new()
    {
        Path = new HidPath(0x00840004, 0x00840030),
        ReportId = 1,
        Kind = HidReportKind.Feature,
        BitOffset = offset,
        BitSize = size,
        LogicalMinimum = min,
        LogicalMaximum = max,
    };
}
