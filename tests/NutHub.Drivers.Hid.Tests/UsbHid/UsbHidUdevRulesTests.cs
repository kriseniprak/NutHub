using NutHub.Drivers.Hid.UsbHid;

namespace NutHub.Drivers.Hid.Tests.UsbHid;

public sealed class UsbHidUdevRulesTests
{
    [Fact]
    public void Covers_ups_vendors_and_known_models_for_hidraw_and_usb_nodes()
    {
        string rules = UsbHidUdevRules.Generate("nuthub");
        string[] lines = rules.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Contains("SUBSYSTEM==\"hidraw\", ATTRS{idVendor}==\"051d\", MODE=\"0660\", GROUP=\"nuthub\"", lines);
        Assert.Contains("SUBSYSTEM==\"usb\", ENV{DEVTYPE}==\"usb_device\", ATTR{idVendor}==\"051d\", MODE=\"0660\", GROUP=\"nuthub\"", lines);
        Assert.Contains("SUBSYSTEM==\"hidraw\", ATTRS{idVendor}==\"050d\", ATTRS{idProduct}==\"0980\", MODE=\"0660\", GROUP=\"nuthub\"", lines);
        Assert.DoesNotContain(lines, l => l.Contains("ATTRS{idVendor}==\"051d\", ATTRS{idProduct}", StringComparison.Ordinal));
        Assert.Equal(lines.Length, lines.Distinct().Count());
        Assert.StartsWith("#", lines[0], StringComparison.Ordinal);
        Assert.Equal("LABEL=\"nuthub_ups_end\"", lines[^1]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Root Group")]
    [InlineData("a\"b")]
    public void Refuses_invalid_group_names(string group)
    {
        Assert.Throws<ArgumentException>(() => UsbHidUdevRules.Generate(group));
    }
}
