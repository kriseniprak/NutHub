using System.Globalization;
using NutHub.Core.Model;

namespace NutHub.Core.Tests.Model;

public sealed class NutFormatTests
{
    [Theory]
    [InlineData(230.0, 2, "230")]
    [InlineData(13.6, 2, "13.6")]
    [InlineData(13.456, 2, "13.46")]
    [InlineData(0.005, 2, "0.01")]
    [InlineData(12345678.9, 1, "12345678.9")]
    [InlineData(1e21, 0, "1000000000000000000000")]
    [InlineData(-0.001, 2, "0")]
    [InlineData(-3.5, 0, "-4")]
    [InlineData(49.96, 0, "50")]
    [InlineData(double.NaN, 2, "0")]
    [InlineData(double.PositiveInfinity, 2, "0")]
    public void Number_uses_a_dot_no_exponent_and_rounds_away_from_zero(double value, int decimals, string expected) =>
        Assert.Equal(expected, NutFormat.Number(value, decimals));

    [Fact]
    public void Number_ignores_the_current_culture()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            // A comma-decimal culture (built by hand: the tests run with invariant globalization).
            var comma = (CultureInfo)CultureInfo.InvariantCulture.Clone();
            comma.NumberFormat.NumberDecimalSeparator = ",";
            comma.NumberFormat.NumberGroupSeparator = ".";
            CultureInfo.CurrentCulture = comma;
            Assert.Equal("13.6", NutFormat.Number(13.6));
            Assert.Equal("13.60", NutFormat.Fixed(13.6, 2));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(50.0, 1, "50.0")]
    [InlineData(13.6, 2, "13.60")]
    [InlineData(2.25, 1, "2.3")]
    [InlineData(229.96, 0, "230")]
    public void Fixed_keeps_the_count_of_decimals(double value, int decimals, string expected) =>
        Assert.Equal(expected, NutFormat.Fixed(value, decimals));

    [Theory]
    [InlineData("230", 230.0)]
    [InlineData(" 13.6 ", 13.6)]
    [InlineData("-5", -5.0)]
    [InlineData("1e3", 1000.0)]
    public void TryParseNumber_accepts_dot_decimals(string text, double expected)
    {
        Assert.True(NutFormat.TryParseNumber(text, out double value));
        Assert.Equal(expected, value, 6);
        Assert.Equal(expected, NutFormat.ParseNumberOrNull(text)!.Value, 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("13,6")]
    [InlineData("abc")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void TryParseNumber_rejects_what_is_not_a_NUT_number(string? text)
    {
        Assert.False(NutFormat.TryParseNumber(text, out _));
        Assert.Null(NutFormat.ParseNumberOrNull(text));
    }

    [Theory]
    [InlineData("ups", true)]
    [InlineData("UPS-1_rack.2", true)]
    [InlineData("1ups", true)]
    [InlineData("-ups", false)]
    [InlineData(".ups", false)]
    [InlineData("my ups", false)]
    [InlineData("ups@host", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidUpsName(string? name, bool valid) => Assert.Equal(valid, NutFormat.IsValidUpsName(name));

    [Fact]
    public void Ups_names_are_limited_to_64_characters()
    {
        Assert.True(NutFormat.IsValidUpsName(new string('a', 64)));
        Assert.False(NutFormat.IsValidUpsName(new string('a', 65)));
    }

    [Theory]
    [InlineData("battery.charge", true)]
    [InlineData("input.L1-N.voltage", true)]
    [InlineData("test.battery.start.quick", true)]
    [InlineData("outlet.1.switch", true)]
    [InlineData("battery charge", false)]
    [InlineData("battery.charge\nx", false)]
    [InlineData(".battery", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidVariableName(string? name, bool valid) => Assert.Equal(valid, NutFormat.IsValidVariableName(name));

    // A name ending with a line feed would break the line-based NUT protocol ("VAR ups battery.charge\n ...") and
    // let a configured UPS name inject protocol lines.
    [Fact]
    public void Names_ending_with_a_line_feed_are_invalid()
    {
        Assert.False(NutFormat.IsValidUpsName("ups1\n"));
        Assert.False(NutFormat.IsValidVariableName("battery.charge\n"));
    }

    [Fact]
    public void Variable_names_are_limited_to_128_characters()
    {
        Assert.True(NutFormat.IsValidVariableName(new string('a', 128)));
        Assert.False(NutFormat.IsValidVariableName(new string('a', 129)));
    }
}
