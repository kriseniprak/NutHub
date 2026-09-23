using NutHub.Drivers.Net.Apcupsd;
using NutHub.Drivers.Net.Tests.Fakes;

namespace NutHub.Drivers.Net.Tests.Apcupsd;

public sealed class ApcupsdStatusMapperTests
{
    private static ApcupsdReading Read(string status) =>
        ApcupsdStatusMapper.ToReading(ApcupsdStatusMapper.ParseFields(status.Split('\n')));

    [Fact]
    public void Maps_the_documented_back_ups_status()
    {
        ApcupsdReading reading = Read(FakeApcupsd.BackUpsStatus);
        IReadOnlyDictionary<string, string> v = reading.Variables;

        Assert.False(reading.CommunicationLost);
        Assert.Equal("OL", v["ups.status"]);
        Assert.Equal("100", v["battery.charge"]);
        Assert.Equal("3138", v["battery.runtime"]); // 52.3 minutes
        Assert.Equal("24", v["ups.load"]);
        Assert.Equal("123", v["input.voltage"]);
        Assert.Equal("120", v["input.voltage.nominal"]);
        Assert.Equal("97", v["input.transfer.low"]);
        Assert.Equal("138", v["input.transfer.high"]);
        Assert.Equal("27", v["battery.voltage"]);
        Assert.Equal("24", v["battery.voltage.nominal"]);
        Assert.Equal("5", v["battery.charge.low"]);
        Assert.Equal("180", v["battery.runtime.low"]);
        Assert.Equal("2005-05-04", v["battery.date"]);
        Assert.Equal("No test initiated", v["ups.test.result"]);
        Assert.Equal("Automatic or explicit self test", v["input.transfer.reason"]);
        Assert.Equal("Medium", v["input.sensitivity"]);
        Assert.Equal("JB0520005612", v["ups.serial"]);
        Assert.Equal("Back-UPS RS 1500", v["ups.model"]);
        Assert.Equal("APC", v["ups.mfr"]);
        Assert.Equal("8.g9 .D USB FW:g9", v["ups.firmware"]);
        Assert.Equal("dev", v["ups.id"]);
        Assert.Equal("3.14.6 (16 May 2009) redhat", reading.Version);
        Assert.All(v, kv => Assert.DoesNotContain("Volts", kv.Value));
        Assert.DoesNotContain(v.Keys, k => k.StartsWith("driver.", StringComparison.Ordinal));
    }

    [Fact]
    public void Maps_a_smart_ups_on_battery()
    {
        IReadOnlyDictionary<string, string> v = Read(FakeApcupsd.SmartUpsOnBattery).Variables;

        Assert.Equal("OB LB", v["ups.status"]);
        Assert.Equal("12", v["battery.charge"]);
        Assert.Equal("150", v["battery.runtime"]);
        Assert.Equal("31.2", v["ups.load"]);
        Assert.Equal("0", v["input.voltage"]);
        Assert.Equal("230.4", v["output.voltage"]);
        Assert.Equal("230", v["output.voltage.nominal"]);
        Assert.Equal("29.2", v["ups.temperature"]);
        Assert.Equal("50", v["input.frequency"]);
        Assert.Equal("980", v["ups.realpower.nominal"]);
        Assert.Equal("1500", v["ups.power.nominal"]);
        Assert.Equal("235.2", v["input.voltage.maximum"]);
        Assert.Equal("15", v["battery.charge.restart"]);
        Assert.Equal("90", v["ups.delay.shutdown"]);
        Assert.Equal("300", v["battery.runtime.low"]);
        Assert.Equal("UPS 09.3", v["ups.firmware"]);
        Assert.Equal("ID=18", v["ups.firmware.aux"]);
        Assert.Equal("2016-03-14", v["ups.mfr.date"]);
        Assert.Equal("Done and passed", v["ups.test.result"]);
        Assert.Equal("APC", v["ups.mfr"]);
    }

    [Theory]
    [InlineData("ONLINE", "OL")]
    [InlineData("ONBATT", "OB")]
    [InlineData("ONBATT LOWBATT", "OB LB")]
    [InlineData("ONLINE REPLACEBATT", "OL RB")]
    [InlineData("CAL ONLINE", "CAL OL")]
    [InlineData("TRIM ONLINE", "TRIM OL")]
    [InlineData("BOOST ONLINE", "BOOST OL")]
    [InlineData("ONLINE OVERLOAD", "OL OVER")]
    [InlineData("ONLINE NOBATT", "OL ALARM")]
    public void Status_words_map_to_nut_tokens(string status, string expected)
    {
        ApcupsdReading reading = Read($"STATUS   : {status} \nMODEL    : Smart-UPS 750");

        Assert.Equal(expected, reading.Variables["ups.status"]);
        Assert.Equal(status.Contains("NOBATT"), reading.Variables.ContainsKey("ups.alarm"));
    }

    [Theory]
    [InlineData("STATUS   : COMMLOST")]
    [InlineData("STATUS   : SLAVEDOWN")]
    [InlineData("STATFLAG : 0x07000108")]
    public void Communication_lost_is_detected(string line)
    {
        ApcupsdReading reading = Read($"{line}\nBCHARGE  : 100.0 Percent");

        Assert.True(reading.CommunicationLost);
        Assert.DoesNotContain("ups.status", reading.Variables.Keys);
    }

    [Fact]
    public void Shutting_down_becomes_fsd_with_the_flags_of_statflag()
    {
        ApcupsdReading reading = Read("STATUS   : SHUTTING DOWN\nSTATFLAG : 0x07000250 Status Flag");

        Assert.Equal("FSD OB LB", reading.Variables["ups.status"]);
    }

    [Fact]
    public void Statflag_is_used_when_status_is_missing()
    {
        Assert.Equal("OL", Read("STATFLAG : 0x07000008 Status Flag").Variables["ups.status"]);
    }

    [Fact]
    public void Self_test_in_progress_adds_test()
    {
        Assert.Equal("OL TEST", Read("STATUS : ONLINE\nSELFTEST : IP").Variables["ups.status"]);
    }

    [Theory]
    [InlineData("230.0 Volts", 230.0)]
    [InlineData("  24.0 Percent Load Capacity", 24.0)]
    [InlineData("12.0 Minutes", 12.0)]
    [InlineData("097.0 Volts", 97.0)]
    [InlineData("-5.5 C", -5.5)]
    [InlineData("980 Watts", 980.0)]
    [InlineData(".5 Hz", 0.5)]
    [InlineData("N/A", null)]
    [InlineData("Volts", null)]
    [InlineData("", null)]
    public void Units_are_stripped(string text, double? expected)
    {
        Assert.Equal(expected, ApcupsdStatusMapper.LeadingNumber(text));
    }

    [Fact]
    public void Fahrenheit_temperatures_are_converted()
    {
        Assert.Equal("30", Read("ITEMP    : 86.0 F Internal").Variables["ups.temperature"]);
        Assert.Equal("30.5", Read("ITEMP    : 30.5 C Internal").Variables["ups.temperature"]);
    }

    [Fact]
    public void Unavailable_and_malformed_fields_are_skipped()
    {
        ApcupsdReading reading = Read("LINEV    : N/A\nnot a field\n: no key\nBCHARGE  : lots\nMODEL    : \nSTATUS   : ONLINE");

        Assert.Equal(["ups.status"], reading.Variables.Keys);
    }

    [Theory]
    [InlineData("Back-UPS ES 700", true)]
    [InlineData("Smart-UPS 1500", true)]
    [InlineData("SMT1500I", true)]
    [InlineData("APC UPS", true)]
    [InlineData("Generic 1000VA", false)]
    public void Manufacturer_is_apc_only_for_apc_models(string model, bool apc)
    {
        ApcupsdReading reading = Read($"MODEL : {model}\nSTATUS : ONLINE");

        Assert.Equal(apc, reading.Variables.TryGetValue("ups.mfr", out string? mfr) && mfr == "APC");
    }
}
