using NutHub.Core.Catalog;
using NutHub.Core.Model;

namespace NutHub.Core.Tests.Model;

public sealed class VariableInfoTests
{
    [Fact]
    public void Read_only_numbers_are_plain_NUMBER() =>
        Assert.Equal("NUMBER", VariableInfo.ReadOnly.ToNutTypeWords());

    [Fact]
    public void Writable_number() => Assert.Equal("RW NUMBER", VariableInfo.WritableNumber().ToNutTypeWords());

    [Fact]
    public void Writable_string_reports_its_length() =>
        Assert.Equal("RW STRING:32", VariableInfo.WritableString(32).ToNutTypeWords());

    // As documented on ToNutTypeWords and in NUT's net-protocol.txt ("TYPE su700 input.transfer.low ENUM").
    // Note: upsd 2.8 itself appends NUMBER to every non-string type ("RW ENUM NUMBER"); upsrw accepts both.
    [Fact]
    public void Enum_and_range_do_not_add_NUMBER()
    {
        Assert.Equal("RW ENUM", VariableInfo.WritableEnum("1", "2").ToNutTypeWords());
        Assert.Equal("RW RANGE", VariableInfo.WritableRange(0, 600).ToNutTypeWords());
        Assert.Equal("ENUM", new VariableInfo { EnumValues = ["a"] }.ToNutTypeWords());
    }

    [Fact]
    public void Enum_of_strings_reports_both()
    {
        var info = new VariableInfo { Writable = true, Type = VariableType.String, MaxLength = 10, EnumValues = ["on", "off"] };
        Assert.Equal("RW ENUM STRING:10", info.ToNutTypeWords());
    }

    [Fact]
    public void WritableRange_formats_bounds_as_NUT_numbers()
    {
        var info = VariableInfo.WritableRange(0.5, 1500);
        Assert.Equal(new ValueRange("0.5", "1500"), Assert.Single(info.Ranges));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(600, true)]
    [InlineData(300.5, true)]
    [InlineData(-1, false)]
    [InlineData(600.01, false)]
    public void ValueRange_contains_its_bounds(double value, bool inside) =>
        Assert.Equal(inside, new ValueRange("0", "600").Contains(value));

    [Fact]
    public void ValueRange_with_unparsable_bounds_contains_nothing() =>
        Assert.False(new ValueRange("low", "high").Contains(1));

    [Fact]
    public void Equality_compares_the_lists_by_content()
    {
        Assert.Equal(VariableInfo.WritableEnum("a", "b"), VariableInfo.WritableEnum("a", "b"));
        Assert.NotEqual(VariableInfo.WritableEnum("a", "b"), VariableInfo.WritableEnum("b", "a"));
        Assert.Equal(VariableInfo.WritableRange(1, 2).GetHashCode(), VariableInfo.WritableRange(1, 2).GetHashCode());
        Assert.NotEqual(VariableInfo.WritableString(5), VariableInfo.WritableString(6));
    }
}

public sealed class NutCatalogTests
{
    [Fact]
    public void Known_variables_and_commands_have_their_NUT_description()
    {
        Assert.Equal("Battery charge (percent of full)", NutCatalog.DescribeVariable("battery.charge"));
        Assert.Equal("Start a battery test", NutCatalog.DescribeCommand("test.battery.start"));
        Assert.True(NutCatalog.VariableDescriptions.Count > 100);
        Assert.True(NutCatalog.CommandDescriptions.Count > 20);
    }

    [Theory]
    [InlineData("input.L1-N.voltage", "input.voltage")]
    [InlineData("input.L2.current", "input.current")]
    [InlineData("outlet.2.id", "outlet.id")]
    [InlineData("ambient.1.temperature", "ambient.temperature")]
    public void Phase_and_index_variables_fall_back_to_the_generic_description(string name, string generic)
    {
        string? expected = NutCatalog.DescribeVariable(generic);
        Assert.NotNull(expected);
        Assert.Equal(expected, NutCatalog.DescribeVariable(name));
    }

    [Fact]
    public void Unknown_names_have_no_description()
    {
        Assert.Null(NutCatalog.DescribeVariable("vendor.secret.thing"));
        Assert.Null(NutCatalog.DescribeVariable("input.L1-N.nothing"));
        Assert.Null(NutCatalog.DescribeCommand("make.coffee"));
    }

    [Fact]
    public void Variable_lookup_is_case_sensitive_and_commands_are_not()
    {
        Assert.Null(NutCatalog.DescribeVariable("BATTERY.CHARGE"));
        Assert.Equal(NutCatalog.DescribeCommand("test.battery.start"), NutCatalog.DescribeCommand("TEST.BATTERY.START"));
    }
}
