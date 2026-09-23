using NutHub.Drivers.Serial.Megatec;
using NutHub.Drivers.Serial.Tests.Fakes;

namespace NutHub.Drivers.Serial.Tests.Megatec;

/// <summary>
/// Identification and polling of every Q* dialect with the example replies documented in the NUT subdriver sources
/// (drivers/nutdrv_qx_*.c).
/// </summary>
public sealed class QxProtocolTests
{
    private const string Q1 = FakeMegatecUps.OnLineStatus;
    private const string NutVendor = "#-------------   ------     VT12046Q  \r";

    internal static async Task<IReadOnlyDictionary<string, string>> ReadAsync(string protocol, TableQxLink link,
                                                                               MegatecSettings? settings = null)
    {
        QxEngine engine = TestSettings.Engine(protocol, link, settings);
        Assert.True(await engine.ClaimAsync(CancellationToken.None), $"{protocol} did not claim the UPS.");
        Assert.Null(await engine.InitializeAsync(CancellationToken.None));
        var poll = await engine.PollAsync(CancellationToken.None);
        Assert.Null(poll.Error);
        return poll.Update!.Variables;
    }

    [Fact]
    public async Task Megatec_reads_status_ratings_and_vendor()
    {
        var link = new TableQxLink().Reply("Q1", Q1).Reply("F", FakeMegatecUps.Rating).Reply("I", FakeMegatecUps.Vendor);

        var v = await ReadAsync("megatec", link);

        Assert.Equal("OL", v["ups.status"]);
        Assert.Equal("226.0", v["input.voltage"]);
        Assert.Equal("195.0", v["input.voltage.fault"]);
        Assert.Equal("226.0", v["output.voltage"]);
        Assert.Equal("14", v["ups.load"]);
        Assert.Equal("49.0", v["input.frequency"]);
        Assert.Equal("27.5", v["battery.voltage"]);
        Assert.Equal("30.0", v["ups.temperature"]);
        Assert.Equal("offline / line interactive", v["ups.type"]);
        Assert.Equal("disabled", v["ups.beeper.status"]);
        Assert.Equal("220", v["input.voltage.nominal"]);
        Assert.Equal("24.0", v["battery.voltage.nominal"]);
        Assert.Equal("50", v["input.frequency.nominal"]);
        Assert.Equal("MegaTec", v["ups.mfr"]);
        Assert.Equal("UPS-1000", v["ups.model"]);
        Assert.Equal("V1.0", v["ups.firmware"]);
        Assert.Equal("180", v["ups.delay.start"]);
        Assert.Equal("30", v["ups.delay.shutdown"]);
        Assert.Equal("Megatec 0.10", v["driver.version.data"]);
        Assert.False(v.ContainsKey("ups.alarm"));
        Assert.False(v.ContainsKey("device.mfr"));

        // Charge guessed from the nominal voltage: 10.4-13.0 V per 12 V, i.e. 20.8-26.0 V here; 27.5 V is full.
        Assert.Equal("100", v["battery.charge"]);
        Assert.Equal("20.80", v["battery.voltage.low"]);
        Assert.Equal("26.00", v["battery.voltage.high"]);
    }

    [Fact]
    public async Task Megatec_on_battery_with_low_battery()
    {
        var link = new TableQxLink()
            .Reply("Q1", "(000.0 000.0 230.0 020 50.0 22.0 30.0 11000000\r")
            .Reply("F", FakeMegatecUps.Rating)
            .Reply("I", FakeMegatecUps.Vendor);

        var v = await ReadAsync("megatec", link);

        Assert.Equal("OB LB", v["ups.status"]);
        Assert.Equal("23", v["battery.charge"]); // (22.0 - 20.8) / (26.0 - 20.8)
    }

    [Theory]
    [InlineData(210, "OL TRIM")]
    [InlineData(232, "OL BYPASS")]
    [InlineData(250, "OL BOOST")]
    [InlineData(400, "OL")] // Output far from the input: NUT fails the reply, NutHub ignores the bit.
    public async Task Bypass_boost_or_buck_bit_is_told_apart_by_the_voltage_ratio(double output, string status)
    {
        var link = new TableQxLink()
            .Reply("Q1", FakeMegatecUps.Status(230, 230, output, 20, 50, 27.0, 30, "00101000"))
            .Reply("F", FakeMegatecUps.Rating)
            .Reply("I", FakeMegatecUps.Vendor);

        var v = await ReadAsync("megatec", link);

        Assert.Equal(status, v["ups.status"]);
    }

    [Fact]
    public async Task Test_in_progress_shutdown_active_failure_and_beeper_bits()
    {
        var link = new TableQxLink()
            .Reply("Q1", FakeMegatecUps.Status(226, 195, 226, 14, 49, 27.5, 30, "00010111"))
            .Reply("F", FakeMegatecUps.Rating)
            .Reply("I", FakeMegatecUps.Vendor);

        var v = await ReadAsync("megatec", link);

        Assert.Equal("ALARM OL CAL FSD", v["ups.status"]);
        Assert.Equal("UPS selftest failed! Shutdown imminent!", v["ups.alarm"]);
        Assert.Equal("enabled", v["ups.beeper.status"]);
        Assert.Equal("online", v["ups.type"]);
    }

    [Fact]
    public async Task IgnoreSab_drops_the_shutdown_active_bit()
    {
        var link = new TableQxLink()
            .Reply("Q1", FakeMegatecUps.Status(226, 195, 226, 14, 49, 27.5, 30, "00001010"))
            .Reply("F", FakeMegatecUps.Rating)
            .Reply("I", FakeMegatecUps.Vendor);
        QxEngine engine = new(
            QxProtocols.Create("megatec", new QxProtocolOptions(IgnoreShutdownActive: true)), link,
            TestSettings.Megatec(), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.True(await engine.ClaimAsync(CancellationToken.None));
        Assert.Null(await engine.InitializeAsync(CancellationToken.None));
        var poll = await engine.PollAsync(CancellationToken.None);

        Assert.Equal("OL", poll.Update!.Variables["ups.status"]);
    }

    [Theory]
    [InlineData("(226.0 195.0 226.0 014 49.0 27.5\r")] // Too short.
    [InlineData("#226.0 195.0 226.0 014 49.0 27.5 30.0 00001000\r")] // Wrong first character.
    [InlineData("(226.0 195.0 226.0 014 49.0 27.5 30.0 X0001000\r")] // Not a bit.
    [InlineData("Q1\r")] // Echo of an unknown command.
    [InlineData("")] // Silence.
    public async Task Invalid_status_replies_fail_the_poll(string reply)
    {
        var link = new TableQxLink().Reply("Q1", Q1).Reply("F", FakeMegatecUps.Rating).Reply("I", FakeMegatecUps.Vendor);
        QxEngine engine = TestSettings.Engine("megatec", link);
        Assert.True(await engine.ClaimAsync(CancellationToken.None));
        Assert.Null(await engine.InitializeAsync(CancellationToken.None));

        link.Reply("Q1", reply);
        var poll = await engine.PollAsync(CancellationToken.None);

        Assert.Null(poll.Update);
        Assert.Contains("Q1", poll.Error);
    }

    [Fact]
    public async Task A_non_numeric_value_keeps_the_previous_reading()
    {
        var link = new TableQxLink().Reply("Q1", Q1).Reply("F", FakeMegatecUps.Rating).Reply("I", FakeMegatecUps.Vendor);
        QxEngine engine = TestSettings.Engine("megatec", link);
        Assert.True(await engine.ClaimAsync(CancellationToken.None));
        Assert.Null(await engine.InitializeAsync(CancellationToken.None));

        link.Reply("Q1", "(2x6.0 195.0 226.0 014 49.0 27.5 30.0 00001000\r");
        var poll = await engine.PollAsync(CancellationToken.None);

        Assert.Equal("226.0", poll.Update!.Variables["input.voltage"]);
        Assert.Equal("OL", poll.Update.Variables["ups.status"]);
    }

    [Fact]
    public async Task Megatec_is_not_claimed_without_the_vendor_reply()
    {
        var link = new TableQxLink().Reply("Q1", Q1).Reply("F", FakeMegatecUps.Rating).Reply("I", "I\r");

        Assert.False(await TestSettings.Engine("megatec", link).ClaimAsync(CancellationToken.None));
        Assert.True(await TestSettings.Engine("q1", link).ClaimAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("megatec/old", "D", "Megatec/old 0.08")]
    [InlineData("mustek", "QS", "Mustek 0.08")]
    public async Task Status_query_variants(string protocol, string statusCommand, string version)
    {
        var link = new TableQxLink().Reply(statusCommand, Q1).Reply("F", FakeMegatecUps.Rating).Reply("I", NutVendor);

        var v = await ReadAsync(protocol, link);

        Assert.Equal("OL", v["ups.status"]);
        Assert.Equal("226.0", v["input.voltage"]);
        Assert.Equal("-------------", v["ups.mfr"]);
        Assert.Equal("------", v["ups.model"]);
        Assert.Equal("VT12046Q", v["ups.firmware"]);
        Assert.Equal(version, v["driver.version.data"]);
        Assert.DoesNotContain("Q1\r", link.Sent);
    }

    [Fact]
    public async Task Zinto_takes_the_vendor_information_from_FW()
    {
        var link = new TableQxLink().Reply("Q1", Q1).Reply("F", FakeMegatecUps.Rating).Reply("FW?", NutVendor);

        var v = await ReadAsync("zinto", link);

        Assert.Equal("VT12046Q", v["ups.firmware"]);
        Assert.Equal("Zinto 0.07", v["driver.version.data"]);
        Assert.DoesNotContain("I\r", link.Sent);
    }

    [Fact]
    public async Task NoRating_and_noVendor_skip_F_and_I()
    {
        var link = new TableQxLink().Reply("Q1", Q1).Reply("F", FakeMegatecUps.Rating).Reply("I", FakeMegatecUps.Vendor);
        QxEngine engine = new(
            QxProtocols.Create("megatec", new QxProtocolOptions(NoRating: true, NoVendor: true)), link,
            TestSettings.Megatec(), TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.True(await engine.ClaimAsync(CancellationToken.None));
        Assert.Null(await engine.InitializeAsync(CancellationToken.None));
        await engine.PollAsync(CancellationToken.None);

        Assert.DoesNotContain("F\r", link.Sent);
        Assert.DoesNotContain("I\r", link.Sent);
    }

    [Fact]
    public async Task BestUps_reads_the_ID_and_transfer_tables()
    {
        var link = new TableQxLink()
            .Reply("Q1", Q1)
            .Reply("ID", "FOR,750,120,120,20.0,27.6\r")
            .Reply("RT", "025\r")
            .Reply("M", "0\r");

        var v = await ReadAsync("bestups", link);

        Assert.Equal("OL", v["ups.status"]);
        Assert.Equal("Best Power", v["ups.mfr"]);
        Assert.Equal("Fortress", v["ups.model"]);
        Assert.Equal("750", v["ups.power.nominal"]);
        Assert.Equal("120", v["input.voltage.nominal"]);
        Assert.Equal("20.0", v["battery.voltage.low"]);
        Assert.Equal("27.6", v["battery.voltage.high"]);
        Assert.Equal("1500", v["battery.runtime"]);
        Assert.Equal("96", v["input.transfer.low"]);
        Assert.Equal("146", v["input.transfer.high"]);
        Assert.False(v.ContainsKey("ups.beeper.status"));
    }

    [Theory]
    [InlineData("FOR,750,120,120,20.0,27.6\r", "FOR, 750,120,120,20.0, 27.6\r")]
    [InlineData("FOR,1500,120,120,20.0,27.6\r", "FOR,1500,120,120,20.0, 27.6\r")]
    [InlineData("FOR,3000,120,120,20.0,100.6\r", "FOR,3000,120,120,20.0,100.6\r")]
    public void BestUps_ID_is_normalised_to_fixed_positions(string raw, string expected)
    {
        Assert.Equal(expected, NutHub.Drivers.Serial.Megatec.Protocols.BestUpsProtocol.NormaliseId(raw));
    }

    [Fact]
    public async Task VoltronicQs_reads_QS_F_and_QI()
    {
        var link = new TableQxLink()
            .Reply("M", "V\r")
            .Reply("QS", Q1)
            .Reply("F", "#220.0 003 12.00 50.0\r")
            .Reply("QI", "(100 00979 50.0 000.3 177 290 0 0000010000112000\r");

        var v = await ReadAsync("voltronic-qs", link);

        Assert.Equal("PM-V", v["ups.firmware.aux"]);
        Assert.Equal("OL", v["ups.status"]);
        Assert.Equal("49.0", v["output.frequency"]);
        Assert.Equal("50.0", v["input.frequency"]);
        Assert.Equal("220", v["output.voltage.nominal"]);
        Assert.Equal("12.0", v["battery.voltage.nominal"]);
        Assert.Equal("100", v["battery.charge"]);
        Assert.Equal("979", v["battery.runtime"]);
        Assert.Equal("0.3", v["output.current"]);
        Assert.Equal("177", v["input.transfer.low"]);
        Assert.Equal("290", v["input.transfer.high"]);
    }

    [Fact]
    public async Task VoltronicQs_is_not_claimed_by_an_echoing_Megatec_UPS()
    {
        var link = new TableQxLink().Reply("M", "M\r").Reply("QS", "QS\r");

        Assert.False(await TestSettings.Engine("voltronic-qs", link).ClaimAsync(CancellationToken.None));
        Assert.False(await TestSettings.Engine("voltronic-qs-hex", link).ClaimAsync(CancellationToken.None));
    }

    [Fact]
    public async Task VoltronicQsHex_decodes_the_binary_status()
    {
        // "#6C01 35 6C01 35 03 519A 1312D0 E6 1E 00001001" once decoded (NUT drivers/nutdrv_qx_voltronic-qs-hex.c).
        string raw = "#" + Bytes(0x6C, 0x01) + " " + Bytes(0x35) + " " + Bytes(0x6C, 0x01) + " " + Bytes(0x35) + " " +
                     Bytes(0x03) + " " + Bytes(0x51, 0x9A) + " " + Bytes(0x13, 0x12, 0xD0) + " " + Bytes(0xE6) + " " +
                     Bytes(0x1E) + " " + Bytes(0x09) + "\r";
        var link = new TableQxLink().Reply("M", "P\r").Reply("QS", raw);

        var v = await ReadAsync("voltronic-qs-hex", link);

        Assert.Equal("112.2", v["input.voltage"]);
        Assert.Equal("112.2", v["output.voltage"]);
        Assert.Equal("3", v["ups.load"]);
        Assert.Equal("59.8", v["output.frequency"]);
        Assert.Equal("13.53", v["battery.voltage"]);
        Assert.Equal("OL", v["ups.status"]);
        Assert.Equal("enabled", v["ups.beeper.status"]);
    }

    [Fact]
    public void VoltronicQsHex_restores_escaped_bytes()
    {
        // 0x28 0x00 stands for a CR inside the binary data.
        string raw = "#" + Bytes(0x6C, 0x28, 0x00) + " " + Bytes(0x35) + " " + Bytes(0x6C, 0x01) + " " + Bytes(0x35) + " " +
                     Bytes(0x03) + " " + Bytes(0x51, 0x9A) + " " + Bytes(0x13, 0x12, 0xD0) + " " + Bytes(0xE6) + " " +
                     Bytes(0x1E) + " " + Bytes(0x09) + "\r";

        string? decoded = NutHub.Drivers.Serial.Megatec.Protocols.VoltronicQsHexProtocol.DecodeQs(raw);

        Assert.NotNull(decoded);
        Assert.StartsWith("#6c0d ", decoded);
        Assert.Null(NutHub.Drivers.Serial.Megatec.Protocols.VoltronicQsHexProtocol.DecodeQs("#12 34\r"));
    }

    [Fact]
    public async Task Voltronic_reads_the_P_protocol_queries()
    {
        var link = new TableQxLink()
            .Reply("QPI", "(PI00\r")
            .Reply("QRI", "(230.0 004 024.0 50.0\r")
            .Reply("QMD", "(#######OLHVT1K0 ###1000 80 1/1 230 230 02 12.0\r")
            .Reply("QMF", "(#######BOH\r")
            .Reply("QVFW", "(VERFW:00322.02\r")
            .Reply("QID", "(12345679012345\r")
            .Reply("QGS", "(234.9 50.0 229.8 50.0 000.0 000 369.1 ---.- 026.5 ---.- 018.8 100000000001\r")
            .Reply("QMOD", "(L\r")
            .Reply("QWS", "(" + new string('0', 64) + "\r")
            .Reply("QBV", "(026.5 02 01 068 255\r");

        var v = await ReadAsync("voltronic", link);

        Assert.Equal("P00", v["ups.firmware.aux"]);
        Assert.Equal("OL", v["ups.status"]);
        Assert.Equal("234.9", v["input.voltage"]);
        Assert.Equal("229.8", v["output.voltage"]);
        Assert.Equal("18.8", v["ups.temperature"]);
        Assert.Equal("online", v["ups.type"]);
        Assert.Equal("enabled", v["ups.beeper.status"]);
        Assert.Equal("68", v["battery.charge"]);
        Assert.Equal("15300", v["battery.runtime"]);
        Assert.Equal("OLHVT1K0", v["ups.model"]);
        Assert.Equal("BOH", v["ups.mfr"]);
        Assert.Equal("00322.02", v["ups.firmware"]);
        Assert.Equal("12345679012345", v["ups.serial"]);
        Assert.Equal("0.8", v["output.powerfactor"]);
        Assert.Equal("Voltronic 0.15", v["driver.version.data"]);
    }

    [Fact]
    public async Task Voltronic_warnings_and_battery_mode()
    {
        var link = new TableQxLink()
            .Reply("QPI", "(PI00\r")
            .Reply("QGS", "(234.9 50.0 229.8 50.0 000.0 000 369.1 ---.- 026.5 ---.- 018.8 100000000001\r")
            .Reply("QMOD", "(B\r")
            .Reply("QWS", "(0000000100000000000000000000000000000000000000000000000000000000\r");

        var v = await ReadAsync("voltronic", link);

        Assert.StartsWith("ALARM OB", v["ups.status"]);
        Assert.Contains("LB", v["ups.status"].Split(' '));
        Assert.False(string.IsNullOrEmpty(v["ups.alarm"]));
    }

    [Fact]
    public async Task Voltronic_refuses_an_unknown_protocol_number()
    {
        var link = new TableQxLink()
            .Reply("QPI", "(PI55\r")
            .Reply("QGS", "(234.9 50.0 229.8 50.0 000.0 000 369.1 ---.- 026.5 ---.- 018.8 100000000001\r");

        Assert.False(await TestSettings.Engine("voltronic", link).ClaimAsync(CancellationToken.None));
    }

    private static string Bytes(params int[] values) => new(values.Select(v => (char)v).ToArray());
}
