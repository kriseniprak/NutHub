using NutHub.Core.Model;
using NutHub.Drivers.Serial.Megatec;
using NutHub.Drivers.Serial.Tests.Fakes;

namespace NutHub.Drivers.Serial.Tests.Megatec;

/// <summary>Instant commands and the delay variables, formatted as NUT's blazer_process_command / voltronic do.</summary>
public sealed class QxCommandTests
{
    private static async Task<(QxEngine Engine, TableQxLink Link)> MegatecAsync(string bits = "00001000")
    {
        var link = new TableQxLink()
            .Reply("Q1", FakeMegatecUps.Status(226, 195, 226, 14, 49, 27.5, 30, bits))
            .Reply("F", FakeMegatecUps.Rating)
            .Reply("I", FakeMegatecUps.Vendor);
        QxEngine engine = TestSettings.Engine("megatec", link);
        Assert.True(await engine.ClaimAsync(CancellationToken.None));
        Assert.Null(await engine.InitializeAsync(CancellationToken.None));
        Assert.NotNull((await engine.PollAsync(CancellationToken.None)).Update);
        link.Sent.Clear();
        return (engine, link);
    }

    [Theory]
    [InlineData("beeper.toggle", null, "Q\r")]
    [InlineData("load.off", null, "S00R0000\r")]
    [InlineData("load.on", null, "C\r")]
    [InlineData("shutdown.return", null, "S.5R0003\r")]
    [InlineData("shutdown.stayoff", null, "S.5R0000\r")]
    [InlineData("shutdown.stop", null, "C\r")]
    [InlineData("test.battery.start", null, "T10\r")]
    [InlineData("test.battery.start", "120", "T02\r")]
    [InlineData("test.battery.start.quick", null, "T\r")]
    [InlineData("test.battery.start.deep", null, "TL\r")]
    [InlineData("test.battery.stop", null, "CT\r")]
    public async Task Megatec_commands_are_formatted_as_NUT_does(string command, string? parameter, string sent)
    {
        var (engine, link) = await MegatecAsync();

        CommandResult result = await engine.InstantCommandAsync(command, parameter, CancellationToken.None);

        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal([sent], link.Sent);
    }

    [Fact]
    public async Task Shutdown_uses_the_written_delays()
    {
        var (engine, link) = await MegatecAsync();

        Assert.True(engine.SetVariable("ups.delay.shutdown", "120").IsSuccess);
        Assert.True(engine.SetVariable("ups.delay.start", "0").IsSuccess);
        await engine.InstantCommandAsync("shutdown.return", null, CancellationToken.None);
        Assert.True(engine.SetVariable("ups.delay.shutdown", "45").IsSuccess);
        Assert.True(engine.SetVariable("ups.delay.start", "600").IsSuccess);
        await engine.InstantCommandAsync("shutdown.return", null, CancellationToken.None);

        Assert.Equal(["S02\r", "S.7R0010\r"], link.Sent);
        Assert.Equal("42", engine.BuildUpdate().Variables["ups.delay.shutdown"]);
        Assert.Equal("600", engine.BuildUpdate().Variables["ups.delay.start"]);
    }

    [Theory]
    [InlineData("ups.delay.shutdown", "11")]
    [InlineData("ups.delay.shutdown", "601")]
    [InlineData("ups.delay.shutdown", "abc")]
    [InlineData("ups.delay.start", "-60")]
    public async Task Delays_out_of_range_are_refused(string name, string value)
    {
        var (engine, _) = await MegatecAsync();

        Assert.Equal(CommandStatus.InvalidValue, engine.SetVariable(name, value).Status);
    }

    [Fact]
    public async Task Delays_are_published_as_writable_ranges()
    {
        var (engine, _) = await MegatecAsync();

        var info = engine.BuildUpdate().VariableInfo!;

        Assert.True(info["ups.delay.shutdown"].Writable);
        Assert.Equal(new ValueRange("12", "600"), info["ups.delay.shutdown"].Ranges.Single());
        Assert.Equal(CommandStatus.NotSupported, engine.SetVariable("battery.charge", "10").Status);
    }

    [Theory]
    [InlineData("test.battery.start", "30")]
    [InlineData("test.battery.start", "6000")]
    [InlineData("load.off", "5")]
    public async Task Invalid_parameters_are_refused_without_sending(string command, string parameter)
    {
        var (engine, link) = await MegatecAsync();

        CommandResult result = await engine.InstantCommandAsync(command, parameter, CancellationToken.None);

        Assert.Equal(CommandStatus.InvalidArgument, result.Status);
        Assert.Empty(link.Sent);
    }

    [Fact]
    public async Task An_echoed_command_is_a_refusal_and_an_unknown_one_is_not_supported()
    {
        var (engine, link) = await MegatecAsync();
        link.Reply("T", "T\r");

        Assert.Equal(CommandStatus.Failed, (await engine.InstantCommandAsync("test.battery.start.quick", null, CancellationToken.None)).Status);
        Assert.Equal(CommandStatus.NotSupported, (await engine.InstantCommandAsync("bypass.start", null, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Beeper_enable_and_disable_go_through_the_toggle()
    {
        var (engine, link) = await MegatecAsync(bits: "00001000"); // Beeper off.

        Assert.Contains("beeper.enable", engine.Commands);
        Assert.True((await engine.InstantCommandAsync("beeper.disable", null, CancellationToken.None)).IsSuccess);
        Assert.Empty(link.Sent);
        Assert.True((await engine.InstantCommandAsync("beeper.enable", null, CancellationToken.None)).IsSuccess);
        Assert.True((await engine.InstantCommandAsync("beeper.enable", null, CancellationToken.None)).IsSuccess);

        Assert.Equal(["Q\r"], link.Sent);
    }

    [Fact]
    public async Task Voltronic_commands_need_an_ACK()
    {
        var link = new TableQxLink()
            .Reply("QPI", "(PI00\r")
            .Reply("QGS", "(234.9 50.0 229.8 50.0 000.0 000 369.1 ---.- 026.5 ---.- 018.8 100000000001\r")
            .Reply("SOFF", "(ACK\r")
            .Reply("SON", "(NAK\r")
            .Reply("BZOFF", "(ACK\r")
            .Reply("T02", "(ACK\r");
        QxEngine engine = TestSettings.Engine("voltronic", link);
        Assert.True(await engine.ClaimAsync(CancellationToken.None));
        Assert.Null(await engine.InitializeAsync(CancellationToken.None));
        await engine.PollAsync(CancellationToken.None);

        Assert.True((await engine.InstantCommandAsync("load.off", null, CancellationToken.None)).IsSuccess);
        Assert.Equal(CommandStatus.Failed, (await engine.InstantCommandAsync("load.on", null, CancellationToken.None)).Status);
        Assert.True((await engine.InstantCommandAsync("beeper.toggle", null, CancellationToken.None)).IsSuccess);
        Assert.True((await engine.InstantCommandAsync("test.battery.start", "120", CancellationToken.None)).IsSuccess);
        Assert.Equal(CommandStatus.Failed, (await engine.InstantCommandAsync("test.battery.stop", null, CancellationToken.None)).Status);
    }
}
