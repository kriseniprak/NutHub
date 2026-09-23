using Microsoft.Extensions.Time.Testing;
using NutHub.Drivers.Serial.Megatec;
using NutHub.Drivers.Serial.Tests.Fakes;

namespace NutHub.Drivers.Serial.Tests.Megatec;

/// <summary>battery.charge and battery.runtime estimated as nutdrv_qx does (its "BATTERY CHARGE GUESSTIMATION").</summary>
public sealed class BatteryEstimatorTests
{
    private readonly FakeTimeProvider _time = new();

    private async Task<(QxEngine Engine, TableQxLink Link)> StartAsync(MegatecSettings settings, string status)
    {
        var link = new TableQxLink().Reply("Q1", status).Reply("F", FakeMegatecUps.Rating).Reply("I", FakeMegatecUps.Vendor);
        QxEngine engine = TestSettings.Engine("megatec", link, settings, _time);
        Assert.True(await engine.ClaimAsync(CancellationToken.None));
        Assert.Null(await engine.InitializeAsync(CancellationToken.None));
        return (engine, link);
    }

    private static async Task<IReadOnlyDictionary<string, string>> PollAsync(QxEngine engine)
    {
        var result = await engine.PollAsync(CancellationToken.None);
        Assert.Null(result.Error);
        return result.Update!.Variables;
    }

    [Fact]
    public async Task Runtime_follows_the_calibration_the_load_and_the_time_on_battery()
    {
        MegatecSettings settings = TestSettings.Megatec() with { RuntimeCalibration = RuntimeCalibration.Parse("240,100,720,50") };
        var (engine, link) = await StartAsync(settings, FakeMegatecUps.Status(230, 230, 230, 50, 50, 27.5, 30, "00001000"));

        var online = await PollAsync(engine);
        Assert.Equal("100", online["battery.charge"]);
        Assert.Equal("720", online["battery.runtime"]); // 240 s at 100 %, 720 s at 50 %.

        link.Reply("Q1", FakeMegatecUps.Status(0, 0, 230, 50, 50, 27.0, 30, "10000000"));
        _time.Advance(TimeSpan.FromSeconds(60));
        var onBattery = await PollAsync(engine);

        // 60 s at 50 % load use 60 × (0.5 ^ 1.585) = 20 s of the 240 s of full-load runtime.
        Assert.Equal("OB", onBattery["ups.status"]);
        Assert.Equal("92", onBattery["battery.charge"]);
        Assert.Equal("660", onBattery["battery.runtime"]);

        link.Reply("Q1", FakeMegatecUps.Status(230, 230, 230, 50, 50, 27.5, 30, "00001000"));
        _time.Advance(TimeSpan.FromSeconds(43200 / 2));
        var recharged = await PollAsync(engine);
        Assert.Equal("100", recharged["battery.charge"]);
    }

    [Fact]
    public async Task A_low_voltage_caps_the_runtime_estimate()
    {
        MegatecSettings settings = TestSettings.Megatec() with { RuntimeCalibration = RuntimeCalibration.Parse("240,100,720,50") };
        var (engine, link) = await StartAsync(settings, FakeMegatecUps.Status(230, 230, 230, 50, 50, 27.5, 30, "00001000"));
        await PollAsync(engine);

        // 23.4 V is half-way between 20.8 and 26.0 V: at most half the runtime is left, whatever the load model says.
        link.Reply("Q1", FakeMegatecUps.Status(0, 0, 230, 50, 50, 23.4, 30, "10000000"));
        var v = await PollAsync(engine);

        Assert.Equal("50", v["battery.charge"]);
        Assert.Equal("360", v["battery.runtime"]);
    }

    [Fact]
    public async Task Configured_voltages_override_the_guess()
    {
        MegatecSettings settings = TestSettings.Megatec() with { BatteryVoltageLow = 22, BatteryVoltageHigh = 26 };
        var (engine, _) = await StartAsync(settings, FakeMegatecUps.Status(230, 230, 230, 20, 50, 24.0, 30, "00001000"));

        var v = await PollAsync(engine);

        Assert.Equal("50", v["battery.charge"]);
        Assert.Equal("22", v["battery.voltage.low"]);
        Assert.Equal("26", v["battery.voltage.high"]);
        Assert.False(v.ContainsKey("battery.runtime"));
    }

    [Fact]
    public async Task Packs_are_detected_for_a_UPS_reporting_the_voltage_of_one_cell()
    {
        // 2.27 V per cell with a 24 V nominal battery: 12 cells.
        string status = "(230.0 230.0 230.0 020 50.0 2.27 30.0 00001000\r";
        var (engine, _) = await StartAsync(TestSettings.Megatec() with { BatteryVoltageReportsOnePack = true }, status);

        var v = await PollAsync(engine);

        Assert.Equal("27.24", v["battery.voltage"]);
        Assert.Equal("100", v["battery.charge"]);
    }

    [Fact]
    public async Task Configured_packs_are_used_as_given()
    {
        string status = "(230.0 230.0 230.0 020 50.0 1.95 30.0 00001000\r";
        var (engine, _) = await StartAsync(TestSettings.Megatec() with { BatteryPacks = 12 }, status);

        var v = await PollAsync(engine);

        Assert.Equal("1.95", v["battery.voltage"]); // Published as reported.
        Assert.Equal("50", v["battery.charge"]); // 23.4 V of 20.8-26.0 V.
    }

    [Theory]
    [InlineData("240,100,720")]
    [InlineData("240,100,120,50")]
    [InlineData("240,50,720,100")]
    [InlineData("240,100,720,x")]
    public void Invalid_calibrations_are_refused(string text)
    {
        Assert.Throws<FormatException>(() => RuntimeCalibration.Parse(text));
    }
}
