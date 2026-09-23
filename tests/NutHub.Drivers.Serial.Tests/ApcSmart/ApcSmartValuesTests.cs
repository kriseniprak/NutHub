using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Drivers.Serial.ApcSmart;
using NutHub.Drivers.Serial.Tests.Fakes;

namespace NutHub.Drivers.Serial.Tests.ApcSmart;

/// <summary>Status register, alert characters and value conversions of NUT drivers/apcsmart.c.</summary>
public sealed class ApcSmartValuesTests
{
    private sealed class NullDevice : IFakeDevice
    {
        public void Receive(byte value, Action<byte[]> send)
        {
        }
    }

    [Theory]
    [InlineData(0x00, "OFF")]
    [InlineData(0x08, "OL")]
    [InlineData(0x10, "OB")]
    [InlineData(0x50, "OB LB")]
    [InlineData(0x0A, "TRIM OL")]
    [InlineData(0x0C, "BOOST OL")]
    [InlineData(0x09, "CAL OL")]
    [InlineData(0x28, "OL OVER")]
    [InlineData(0x88, "OL RB")]
    public void Status_register_to_ups_status(int register, string status)
    {
        Assert.Equal(status, ApcSmartValues.FormatStatus(register));
    }

    [Theory]
    [InlineData("08", true, 0x08)]
    [InlineData("50", true, 0x50)]
    [InlineData("8", true, 0x08)]
    [InlineData("ZZ", false, 0)]
    [InlineData("108", false, 0)]
    [InlineData("", false, 0)]
    public void Status_register_parsing(string text, bool valid, int expected)
    {
        Assert.Equal(valid, ApcSmartValues.TryParseStatus(text, out int status));
        Assert.Equal(expected, status);
    }

    [Theory]
    [InlineData(0x08, '!', 0x10)]
    [InlineData(0x10, '$', 0x08)]
    [InlineData(0x10, '%', 0x50)]
    [InlineData(0x50, '+', 0x10)]
    [InlineData(0x08, '#', 0x88)]
    [InlineData(0x08, '?', 0x28)]
    [InlineData(0x28, '=', 0x08)]
    [InlineData(0x08, '&', 0x08)]
    public void Alerts_change_the_status_as_NUT_does(int before, char alert, int after)
    {
        Assert.Equal(after, ApcSmartValues.ApplyAlert(before, alert));
    }

    [Theory]
    [InlineData("Minutes", "0045:", "2700")]
    [InlineData("Hours", "336", "1209600")]
    [InlineData("Volt", "230.4", "230.4")]
    [InlineData("Percent", "100.0", "100")]
    [InlineData("Celsius", "031.5", "31.5")]
    [InlineData("Seconds", "020", "20")]
    [InlineData("Reason", "S", "simulated power failure or UPS test")]
    [InlineData("Hex", "FF", "FF")]
    [InlineData("Volt", "NA", "NA")]
    public void Values_are_converted(string format, string raw, string value)
    {
        Assert.Equal(value, ApcSmartValues.Convert(Enum.Parse<ApcFormat>(format), raw));
    }

    [Fact]
    public void Packed_values_are_spread_over_numbered_variables()
    {
        var variable = new ApcVariable(new ApcVariableDef("ambient.0.humidity", 'H', ApcVarFlags.Pack | ApcVarFlags.Poll, ApcFormat.Percent));
        var values = new Dictionary<string, string>();

        ApcSmartValues.Store(variable, "045.0,,050.5", values);
        Assert.Equal("45", values["ambient.1.humidity"]);
        Assert.Equal("N/A", values["ambient.2.humidity"]);
        Assert.Equal("50.5", values["ambient.3.humidity"]);

        ApcSmartValues.Store(variable, "046.0", values);
        Assert.Equal("46", values["ambient.1.humidity"]);
        Assert.False(values.ContainsKey("ambient.2.humidity"));
    }

    [Fact]
    public async Task Alert_characters_inside_a_reply_are_handled_and_removed()
    {
        var transport = new FakeTransport(new NullDevice());
        await transport.OpenAsync(CancellationToken.None);
        var alerts = new List<char>();
        var link = new ApcSmartLink(transport, TimeProvider.System, TimeSpan.FromSeconds(1), alerts.Add, NullLogger.Instance);

        transport.Inject("10!0.0\r\n");
        ApcLine line = await link.ReadAsync(ApcReadMode.AlertAware, null, CancellationToken.None);

        Assert.True(line.Ok);
        Assert.Equal("100.0", line.Text);
        Assert.Equal(['!'], alerts);
    }

    [Fact]
    public async Task Pending_alerts_are_not_lost_when_the_input_is_flushed()
    {
        var transport = new FakeTransport(new NullDevice());
        await transport.OpenAsync(CancellationToken.None);
        var alerts = new List<char>();
        var link = new ApcSmartLink(transport, TimeProvider.System, TimeSpan.FromSeconds(1), alerts.Add, NullLogger.Instance);

        transport.Inject("!%");
        await link.FlushAsync(alertAware: true, CancellationToken.None);

        Assert.Equal(['!', '%'], alerts);
    }

    [Fact]
    public async Task A_truncated_reply_is_an_error_but_silence_can_be_allowed()
    {
        var transport = new FakeTransport(new NullDevice());
        await transport.OpenAsync(CancellationToken.None);
        var link = new ApcSmartLink(transport, TimeProvider.System, TimeSpan.FromSeconds(1), _ => { }, NullLogger.Instance);

        transport.Inject("23");
        Assert.False((await link.ReadAsync(ApcReadMode.Default, null, CancellationToken.None)).Ok);
        Assert.True((await link.ReadAsync(ApcReadMode.TimeoutAllowed, null, CancellationToken.None)).Ok);
        Assert.False((await link.ReadAsync(ApcReadMode.Default, null, CancellationToken.None)).Ok);
    }
}
