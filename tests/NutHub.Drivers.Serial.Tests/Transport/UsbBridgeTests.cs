using NutHub.Drivers.Serial.Transport;

namespace NutHub.Drivers.Serial.Tests.Transport;

/// <summary>
/// Framing of the USB-serial HID bridges (NUT nutdrv_qx cypress/phoenix, ippon and sgs command functions) and the
/// table of known bridges. No USB device is involved: only the conversion between serial bytes and HID reports.
/// </summary>
public sealed class UsbBridgeTests
{
    private static byte[] Ascii(string text) => System.Text.Encoding.Latin1.GetBytes(text);

    [Fact]
    public void Cypress_commands_go_out_in_8_byte_reports_after_the_report_id()
    {
        var reports = HidBridgeTransport.BuildReports(Ascii("S.5R0003\r"), UsbBridgeKind.Cypress, 9).ToList();

        Assert.Equal(2, reports.Count);
        Assert.All(reports, r => Assert.Equal(9, r.Length));
        Assert.Equal(0, reports[0][0]);
        Assert.Equal(Ascii("S.5R0003"), reports[0][1..]);
        Assert.Equal((byte)'\r', reports[1][1]);
        Assert.All(reports[1][2..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Sgs_reports_carry_a_length_byte_and_seven_data_bytes()
    {
        var reports = HidBridgeTransport.BuildReports(Ascii("Q1\r"), UsbBridgeKind.Sgs, 9).ToList();
        var twoReports = HidBridgeTransport.BuildReports(Ascii("S.5R0003\r"), UsbBridgeKind.Sgs, 9).ToList();

        Assert.Single(reports);
        Assert.Equal(3, reports[0][1]);
        Assert.Equal(Ascii("Q1\r"), reports[0][2..5]);
        Assert.Equal(2, twoReports.Count);
        Assert.Equal(7, twoReports[0][1]);
        Assert.Equal(2, twoReports[1][1]);
    }

    [Fact]
    public void Cypress_replies_stop_at_the_carriage_return()
    {
        byte[] middle = HidBridgeTransport.DecodeReport([0, .. Ascii("(226.0 1")], UsbBridgeKind.Cypress);
        byte[] last = HidBridgeTransport.DecodeReport([0, .. Ascii("000\r"), 0, 0, 0, 0], UsbBridgeKind.Phoenix);

        Assert.Equal(Ascii("(226.0 1"), middle);
        Assert.Equal(Ascii("000\r"), last);
    }

    [Fact]
    public void Ippon_replies_come_whole_and_get_their_missing_carriage_return()
    {
        byte[] complete = HidBridgeTransport.DecodeReport([0, .. Ascii("(226.0\r"), 0, 0], UsbBridgeKind.Ippon);
        byte[] truncated = HidBridgeTransport.DecodeReport([0, .. Ascii("#MegaTec"), 0, 0], UsbBridgeKind.Ippon);

        Assert.Equal(Ascii("(226.0\r"), complete);
        Assert.Equal(Ascii("#MegaTec\r"), truncated);
    }

    [Fact]
    public void Sgs_replies_use_the_length_byte()
    {
        byte[] data = HidBridgeTransport.DecodeReport([0, 3, .. Ascii("(22xxxx")], UsbBridgeKind.Sgs);

        Assert.Equal(Ascii("(22"), data);
        Assert.Empty(HidBridgeTransport.DecodeReport([0], UsbBridgeKind.Sgs));
    }

    [Theory]
    [InlineData(0x0665, 0x5161, "Cypress")]
    [InlineData(0x06da, 0x0003, "Ippon")]
    [InlineData(0x06da, 0x0601, "Phoenix")]
    [InlineData(0x0483, 0x0035, "Sgs")]
    public void Known_bridges_have_their_framing(int vendor, int product, string kind)
    {
        UsbBridgeInfo? info = UsbBridgeCatalog.Find(vendor, product);

        Assert.NotNull(info);
        Assert.Equal(kind, info.Kind.ToString());
    }

    [Theory]
    [InlineData(0x0001, 0x0000)]
    [InlineData(0x0925, 0x1234)]
    [InlineData(0xffff, 0x0000)]
    public void Bridges_needing_raw_USB_are_listed_as_unsupported(int vendor, int product)
    {
        UsbBridgeInfo? info = UsbBridgeCatalog.Find(vendor, product);

        Assert.NotNull(info);
        Assert.False(info.IsSupported);
        Assert.False(string.IsNullOrEmpty(info.UnsupportedReason));
    }
}
