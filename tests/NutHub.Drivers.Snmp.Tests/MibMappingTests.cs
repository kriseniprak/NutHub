using Lextm.SharpSnmpLib;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Drivers.Snmp.Engine;
using NutHub.Drivers.Snmp.Tests.Fakes;
using static NutHub.Drivers.Snmp.Tests.Fakes.Profiles;

namespace NutHub.Drivers.Snmp.Tests;

public sealed class MibMappingTests
{
    [Fact]
    public async Task Ietf_values_follow_the_nut_multipliers()
    {
        DriverUpdate u = await PollOnceAsync(IetfUps());

        Assert.Equal("ACME", u.Var("ups.mfr"));
        Assert.Equal("ACME 3000", u.Var("ups.model"));
        Assert.Equal("FW 1.2", u.Var("ups.firmware"));
        Assert.Equal("1500", u.Var("battery.runtime")); // minutes x 60
        Assert.Equal("100", u.Var("battery.charge"));
        Assert.Equal("13.6", u.Var("battery.voltage")); // 0.1 V
        Assert.Null(u.Var("battery.current")); // -1 flagged invalid
        Assert.Equal("49.9", u.Var("input.frequency")); // 0.1 Hz
        Assert.Equal("231", u.Var("input.voltage"));
        Assert.Equal("1.2", u.Var("output.current"));
        Assert.Equal("35", u.Var("ups.load"));
        Assert.Equal("120", u.Var("battery.runtime.low"));
        Assert.Equal("enabled", u.Var("ups.beeper.status"));
        Assert.Equal("no test initiated", u.Var("ups.test.result"));
        Assert.Equal("Server room", u.Var("device.location"));
        Assert.Equal("ietf MIB 1.55", u.Var("driver.version.data"));
        Assert.Equal("20", u.Var("ups.delay.shutdown")); // driver-side setting
        Assert.Equal(["OL"], u.Status());
        Assert.Null(u.Var("input.L1-N.voltage"));
    }

    [Fact]
    public async Task Ietf_three_phase_unit_uses_per_line_names()
    {
        AgentStore s = IetfUps();
        s.Set(Ietf + "3.2.0", 3);
        s.Set(Ietf + "3.3.1.3.2", 232);
        s.Set(Ietf + "3.3.1.3.3", 233);
        s.Set(Ietf + "3.3.1.2.2", 500);
        s.Set(Ietf + "3.3.1.2.3", 501);
        s.Set(Ietf + "3.3.1.4.1", 51);
        s.Set(Ietf + "4.3.0", 3);
        s.Set(Ietf + "4.4.1.2.2", 229);
        s.Set(Ietf + "4.4.1.2.3", 228);
        s.Set(Ietf + "4.4.1.3.2", 22);
        s.Set(Ietf + "4.4.1.3.3", 33);
        s.Set(Ietf + "4.4.1.5.2", 40);
        s.Set(Ietf + "4.4.1.5.3", 45);
        s.Set(Ietf + "5.2.0", 3);
        s.Set(Ietf + "5.1.0", 500);
        s.Set(Ietf + "5.3.1.2.1", 230);
        s.Set(Ietf + "5.3.1.2.2", 231);
        s.Set(Ietf + "5.3.1.2.3", 232);

        MibSession session = await OpenAsync(s);
        DriverUpdate u = await session.PollAsync(CancellationToken.None);

        Assert.Equal((3, 3, 3), session.Phases);
        Assert.Equal("3", u.Var("input.phases"));
        Assert.Equal("231", u.Var("input.L1-N.voltage"));
        Assert.Equal("232", u.Var("input.L2-N.voltage"));
        Assert.Equal("233", u.Var("input.L3-N.voltage"));
        Assert.Equal("50.1", u.Var("input.L3.frequency"));
        Assert.Equal("5.1", u.Var("input.L1.current"));
        Assert.Null(u.Var("input.voltage"));
        Assert.Null(u.Var("input.frequency"));
        Assert.Equal("228", u.Var("output.L3-N.voltage"));
        Assert.Equal("3.3", u.Var("output.L3.current"));
        Assert.Equal("40", u.Var("output.L2.power.percent"));
        Assert.Null(u.Var("output.voltage"));
        Assert.Null(u.Var("ups.load"));
        Assert.Equal("50", u.Var("input.bypass.frequency"));
        Assert.Equal("232", u.Var("input.bypass.L3-N.voltage"));
        Assert.Null(u.Var("input.bypass.voltage"));
    }

    [Fact]
    public async Task Apc_prefers_high_precision_objects_and_falls_back_when_invalid()
    {
        AgentStore s = ApcUps();
        MibSession session = await OpenAsync(s);
        DriverUpdate u = await session.PollAsync(CancellationToken.None);

        Assert.Equal("apcc", session.Mib.Name);
        Assert.Equal("APC", u.Var("ups.mfr"));
        Assert.Equal("Smart-UPS 1500", u.Var("ups.model"));
        Assert.Equal("AS1234567890", u.Var("ups.serial"));
        Assert.Equal("UPS 09.3", u.Var("ups.firmware"));
        Assert.Equal("100", u.Var("battery.charge"));
        Assert.Equal("229.5", u.Var("input.voltage"));
        Assert.Equal("27.2", u.Var("battery.voltage"));
        Assert.Equal("30.1", u.Var("ups.temperature"));
        Assert.Equal("1800", u.Var("battery.runtime")); // TimeTicks to seconds
        Assert.Equal("120", u.Var("battery.runtime.low"));
        Assert.Equal("90", u.Var("ups.delay.shutdown"));
        Assert.Equal("high", u.Var("input.sensitivity"));
        Assert.Equal("noTransfer", u.Var("input.transfer.reason"));
        Assert.Equal("apcc MIB 1.62", u.Var("driver.version.data"));

        s.Set(Apc + "2.3.1.0", -1); // upsHighPrecBatteryCapacity no longer valid
        s.Remove(Apc + "3.3.1.0"); // upsHighPrecInputLineVoltage gone
        u = await session.PollAsync(CancellationToken.None);
        Assert.Equal("99", u.Var("battery.charge"));
        Assert.Equal("229", u.Var("input.voltage"));
    }

    [Fact]
    public async Task Writable_variables_are_described()
    {
        DriverUpdate apc = await PollOnceAsync(ApcUps());
        Assert.NotNull(apc.VariableInfo);
        Assert.Equal(["auto", "low", "medium", "high"], apc.VariableInfo!["input.sensitivity"].EnumValues);
        Assert.True(apc.VariableInfo["input.transfer.low"].Writable);
        Assert.Equal(VariableType.String, apc.VariableInfo["ups.id"].Type);
        Assert.Equal(8, apc.VariableInfo["ups.id"].MaxLength);
        Assert.False(apc.VariableInfo.ContainsKey("ups.model"));

        DriverUpdate ietf = await PollOnceAsync(IetfUps());
        Assert.Equal(["yes", "no"], ietf.VariableInfo!["ups.start.auto"].EnumValues);
        Assert.Equal(VariableType.Number, ietf.VariableInfo["ups.delay.shutdown"].Type);
        Assert.True(ietf.VariableInfo["ups.delay.shutdown"].Writable);
    }

    [Fact]
    public async Task Static_objects_are_read_once_and_missing_ones_are_retried_later()
    {
        AgentStore s = IetfUps();
        s.Remove(Ietf + "2.7.0"); // no battery temperature yet
        MibSession session = await OpenAsync(s, settings: Settings() with { SemiStaticEvery = 3 });
        await session.PollAsync(CancellationToken.None);
        s.ClearHistory();

        s.Set(Ietf + "1.2.0", "Changed model");
        s.Set(Ietf + "2.7.0", 31);
        DriverUpdate u = await session.PollAsync(CancellationToken.None); // poll 1
        Assert.Equal("ACME 3000", u.Var("ups.model"));
        Assert.Null(u.Var("battery.temperature"));
        await session.PollAsync(CancellationToken.None); // poll 2
        u = await session.PollAsync(CancellationToken.None); // poll 3: full round
        Assert.Equal("31", u.Var("battery.temperature"));
        Assert.Equal("ACME 3000", u.Var("ups.model"));
    }

    [Fact]
    public async Task Apc_iem_temperature_is_converted_from_fahrenheit()
    {
        AgentStore s = ApcUps();
        s.Set("1.3.6.1.4.1.318.1.1.10.2.3.2.1.4.1", 77);
        s.Set("1.3.6.1.4.1.318.1.1.10.2.3.2.1.5.1", 2); // Fahrenheit
        Assert.Equal("25.0", (await PollOnceAsync(s)).Var("ambient.temperature"));

        s.Set("1.3.6.1.4.1.318.1.1.10.2.3.2.1.5.1", 1); // Celsius
        Assert.Equal("77.0", (await PollOnceAsync(s)).Var("ambient.temperature"));
    }

    [Theory]
    [InlineData("12/31/2023", "2023-12-31")]
    [InlineData("1/2/24", "2024-01-02")]
    [InlineData("13/01/2023", "13/01/2023")]
    [InlineData("unknown", "unknown")]
    public void Us_dates_become_iso_dates(string input, string expected) =>
        Assert.Equal(expected, MibValueMapper.UsDateToIso(input));

    [Fact]
    public void Values_follow_the_invalid_flags_and_lookups()
    {
        var entry = Mibs.Mib.Num("x", "1.2.3", 0.1, Mibs.MibFlags.ZeroInvalid);
        Assert.Null(MibValueMapper.ToNutValue(entry, new Integer32(0)));
        Assert.Equal("1.5", MibValueMapper.ToNutValue(entry, new Integer32(15)));
        Assert.Equal("1.5", MibValueMapper.ToNutValue(entry, new OctetString("15"))); // numbers sent as text
        Assert.Null(MibValueMapper.ToNutValue(entry, new OctetString("garbage")));
        Assert.Null(MibValueMapper.ToNutValue(entry, new NoSuchInstance()));

        var na = Mibs.Mib.Text("y", "1.2.4", Mibs.MibFlags.NotAvailableInvalid);
        Assert.Null(MibValueMapper.ToNutValue(na, new OctetString("N/A")));
        Assert.Equal("00 1A FF", MibValueMapper.ToNutValue(na, new OctetString([0x00, 0x1a, 0xff, 0x00])));
    }
}
