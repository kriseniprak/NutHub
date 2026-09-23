using NutHub.Core.Model;
using NutHub.Drivers.Hid.Descriptors;
using NutHub.Drivers.Hid.UsbHid;
using NutHub.Drivers.Hid.UsbHid.Subdrivers;

namespace NutHub.Drivers.Hid.Tests.UsbHid;

/// <summary>Consistency of the ported mapping tables, for the subdrivers no real dump covers.</summary>
public sealed class SubdriverTableTests
{
    public static TheoryData<string> Subdrivers => new(SubdriverCatalog.Ids);

    [Theory]
    [MemberData(nameof(Subdrivers))]
    public void Every_path_uses_known_usages(string id)
    {
        UsbHidSubdriver subdriver = SubdriverCatalog.Create(id)!;

        Assert.NotEmpty(subdriver.Mappings);
        Assert.False(string.IsNullOrWhiteSpace(subdriver.Version));
        foreach (HidMapping mapping in subdriver.Mappings.Where(m => m.Path is not null))
        {
            Assert.True(HidPath.TryParse(mapping.Path, subdriver.Usages, out _), $"{id}: {mapping.Name} has an unknown usage in {mapping.Path}");
        }
    }

    [Theory]
    [MemberData(nameof(Subdrivers))]
    public void Entries_are_well_formed(string id)
    {
        UsbHidSubdriver subdriver = SubdriverCatalog.Create(id)!;

        foreach (HidMapping mapping in subdriver.Mappings)
        {
            string what = $"{id}: {mapping.Name} ({mapping.Path})";
            if (mapping.IsCommand)
            {
                Assert.True(NutFormat.IsValidVariableName(mapping.Name), $"{what} is not a valid command name");
                continue;
            }

            if (mapping.IsStatusFlag || mapping.IsAlarm)
            {
                Assert.True(mapping.Lookup is not null, $"{what} needs a lookup");
                continue;
            }

            Assert.True(NutFormat.IsValidVariableName(mapping.Name), $"{what} is not a valid variable name");
            if (mapping.Lookup is null && !mapping.IsAbsent)
            {
                // Plain numbers go through the printf format of the entry.
                Assert.True(CFormat.FormatDouble(mapping.Default, 12.5) is not null, $"{what} has an unusable format '{mapping.Default}'");
            }

            if (mapping.Has(HidMapFlags.Str) && mapping.IsWritable && !mapping.Has(HidMapFlags.Enumerated))
            {
                Assert.True(mapping.Length > 0, $"{what} is a writable string without a length");
            }
        }
    }

    [Theory]
    [InlineData("%.0f", 12.5, "12")] // C rounds half to even
    [InlineData("%.0f", 13.5, "14")]
    [InlineData("%.1f", 229.96, "230.0")]
    [InlineData("%.2f", 0.6, "0.60")]
    [InlineData("%05.1f", 7, "007.0")]
    [InlineData("%s", 7, null)] // %s needs a lookup
    public void Formats_numbers_like_printf(string format, double value, string? expected)
    {
        Assert.Equal(expected, CFormat.FormatDouble(format, value));
    }

    [Theory]
    [InlineData(0x2F54, "2003/10/20")] // (23 << 9) | (10 << 5) | 20
    [InlineData(0, "not set")]
    public void Battery_dates_use_the_power_device_encoding(int packed, string expected)
    {
        Assert.Equal(expected, CommonLookups.DateConversion.ToNut(packed, null!));
        if (packed != 0)
        {
            Assert.Equal(packed, CommonLookups.DateConversion.ToHid(expected, null!));
        }
    }

    [Theory]
    [InlineData(298.15, "25.0")]
    [InlineData(31, "31.0")] // already Celsius (some HP and MGE firmwares)
    public void Temperatures_are_converted_from_kelvin(double value, string expected)
    {
        Assert.Equal(expected, CommonLookups.KelvinCelsiusConversion.ToNut(value, null!));
    }
}
