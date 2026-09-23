using NutHub.Core.Model;
using NutHub.Core.Runtime;

namespace NutHub.Core.Tests.Runtime;

public sealed class UpsUnitCheckWritableTests
{
    private static UpsSnapshot Snapshot()
    {
        using var h = new UpsUnitHarness();
        h.Unit.BeginRun(clearData: true);
        h.Publish("OL", new()
        {
            ["battery.charge"] = "100",
            ["battery.charge.low"] = "10",
            ["ups.delay.shutdown"] = "20",
            ["ups.beeper.status"] = "enabled",
            ["ups.id"] = "rack",
        }, new()
        {
            ["battery.charge.low"] = VariableInfo.WritableNumber(),
            ["ups.delay.shutdown"] = VariableInfo.WritableRange(0, 600),
            ["ups.beeper.status"] = VariableInfo.WritableEnum("enabled", "disabled"),
            ["ups.id"] = VariableInfo.WritableString(8),
            ["not.published"] = VariableInfo.WritableNumber(),
        });
        return h.Snapshot;
    }

    [Theory]
    [InlineData("ups.nothing", "1", CommandStatus.NotSupported)]
    [InlineData("not.published", "1", CommandStatus.NotSupported)]
    [InlineData("battery.charge", "50", CommandStatus.ReadOnly)]
    [InlineData("ups.beeper.status", "DISABLED", CommandStatus.InvalidValue)]
    [InlineData("ups.delay.shutdown", "601", CommandStatus.InvalidValue)]
    [InlineData("ups.delay.shutdown", "soon", CommandStatus.InvalidValue)]
    [InlineData("ups.id", "123456789", CommandStatus.TooLong)]
    [InlineData("battery.charge.low", "ten", CommandStatus.InvalidValue)]
    public void Refused_writes(string name, string value, CommandStatus expected) =>
        Assert.Equal(expected, UpsUnit.CheckWritable(Snapshot(), name, value)!.Status);

    [Theory]
    [InlineData("ups.beeper.status", "disabled")]
    [InlineData("ups.delay.shutdown", "0")]
    [InlineData("ups.delay.shutdown", "600")]
    [InlineData("ups.id", "12345678")]
    [InlineData("ups.id", "")]
    [InlineData("battery.charge.low", "15.5")]
    public void Accepted_writes(string name, string value) =>
        Assert.Null(UpsUnit.CheckWritable(Snapshot(), name, value));

    [Fact]
    public void The_metadata_of_variables_the_driver_did_not_publish_is_dropped() =>
        Assert.False(Snapshot().VariableInfo.ContainsKey("not.published"));
}

public sealed class UpsUnitCommandTests
{
    private static readonly CommandOrigin Alice = CommandOrigin.Web("alice", "192.0.2.4");

    private static (UpsUnitHarness Harness, Support.FakeDriver Driver) Connected()
    {
        var h = new UpsUnitHarness();
        h.Unit.BeginRun(clearData: true);
        h.Publish("OL", new() { ["ups.delay.shutdown"] = "20" },
                  new() { ["ups.delay.shutdown"] = VariableInfo.WritableRange(0, 600) },
                  ["test.battery.start.quick", "beeper.disable", "load.off.delay"]);
        Support.FakeDriver driver = h.AttachDriver();
        h.Recorder.Clear();
        return (h, driver);
    }

    [Fact]
    public async Task A_supported_command_runs_with_its_canonical_name_and_is_recorded()
    {
        var (h, driver) = Connected();
        using var _ = h;

        CommandResult result = await h.Unit.InstantCommandAsync("TEST.BATTERY.START.QUICK", null, Alice);

        Assert.True(result.IsSuccess);
        Assert.Equal(["test.battery.start.quick"], driver.Commands);
        UpsEvent e = Assert.Single(h.Recorder.Events);
        Assert.Equal(UpsEventType.CommandExecuted, e.Type);
        Assert.Equal("web:alice@192.0.2.4", e.Actor);
        Assert.Equal("test.battery.start.quick", e.Data!["command"]);
    }

    [Fact]
    public async Task Parameters_are_passed_and_an_empty_one_means_none()
    {
        var (h, driver) = Connected();
        using var _ = h;

        await h.Unit.InstantCommandAsync("load.off.delay", "30", Alice);
        await h.Unit.InstantCommandAsync("beeper.disable", "", Alice);

        Assert.Equal(["load.off.delay 30", "beeper.disable"], driver.Commands);
        Assert.Equal("30", h.Recorder.Events[0].Data!["parameter"]);
    }

    [Fact]
    public async Task Unsupported_commands_never_reach_the_driver()
    {
        var (h, driver) = Connected();
        using var _ = h;

        CommandResult result = await h.Unit.InstantCommandAsync("make.coffee", null, Alice);

        Assert.Equal(CommandStatus.NotSupported, result.Status);
        Assert.Empty(driver.Commands);
        Assert.Empty(h.Events);
    }

    [Fact]
    public async Task Commands_need_a_connected_driver()
    {
        using var h = new UpsUnitHarness();
        h.Unit.BeginRun(clearData: true);
        h.Publish("OL", commands: ["beeper.disable"]);
        Assert.Equal(CommandStatus.DriverNotConnected, (await h.Unit.InstantCommandAsync("beeper.disable", null, Alice)).Status);

        Support.FakeDriver driver = h.AttachDriver();
        h.Unit.OnDisconnected("cable");
        Assert.Equal(CommandStatus.DriverNotConnected, (await h.Unit.InstantCommandAsync("beeper.disable", null, Alice)).Status);
        Assert.Empty(driver.Commands);
    }

    [Fact]
    public async Task A_failure_reported_by_the_driver_is_recorded()
    {
        var (h, driver) = Connected();
        using var _ = h;
        driver.Operation = (_, _) => Task.FromResult(CommandResult.Fail("UPS busy"));

        CommandResult result = await h.Unit.InstantCommandAsync("beeper.disable", null, Alice);

        Assert.Equal(CommandStatus.Failed, result.Status);
        UpsEvent e = Assert.Single(h.Recorder.Events);
        Assert.Equal(UpsEventType.CommandFailed, e.Type);
        Assert.Contains("UPS busy", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_driver_exception_becomes_a_failure()
    {
        var (h, driver) = Connected();
        using var _ = h;
        driver.Operation = (_, _) => throw new IOException("write failed");

        CommandResult result = await h.Unit.InstantCommandAsync("beeper.disable", null, Alice);

        Assert.Equal(CommandStatus.Failed, result.Status);
        Assert.Equal("write failed", result.Message);
    }

    [Fact]
    public async Task A_driver_timeout_becomes_a_failure()
    {
        var (h, driver) = Connected();
        using var _ = h;
        driver.Operation = (_, _) => throw new OperationCanceledException();

        CommandResult result = await h.Unit.InstantCommandAsync("beeper.disable", null, Alice);

        Assert.Equal(CommandStatus.Failed, result.Status);
        Assert.Contains("did not answer in time", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Cancellation_by_the_caller_propagates()
    {
        var (h, driver) = Connected();
        using var _ = h;
        driver.Operation = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return CommandResult.Ok;
        };
        using var cts = new CancellationTokenSource();

        Task<CommandResult> pending = h.Unit.InstantCommandAsync("beeper.disable", null, Alice, cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    // The 20 s command timeout should follow the unit's TimeProvider, like every other delay in Core.
    [Fact]
    public async Task The_command_timeout_follows_the_time_provider()
    {
        var (h, driver) = Connected();
        using var _ = h;
        driver.Operation = async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return CommandResult.Ok;
        };

        Task<CommandResult> pending = h.Unit.InstantCommandAsync("beeper.disable", null, Alice);
        await Task.Delay(50);
        h.Time.Advance(TimeSpan.FromSeconds(21));

        CommandResult result = await pending.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(CommandStatus.Failed, result.Status);
    }

    // A driver stuck in a call that ignores the token (a blocked port) must not hold the NUT client or the web
    // request for ever.
    [Fact]
    public async Task The_command_timeout_holds_even_if_the_driver_ignores_it()
    {
        var (h, driver) = Connected();
        using var _ = h;
        var never = new TaskCompletionSource<CommandResult>();
        driver.Operation = (_, _) => never.Task;

        Task<CommandResult> pending = h.Unit.InstantCommandAsync("beeper.disable", null, Alice);
        await Task.Delay(50);
        h.Time.Advance(TimeSpan.FromSeconds(21));

        CommandResult result = await pending.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(CommandStatus.Failed, result.Status);
        Assert.Contains("did not answer in time", result.Message, StringComparison.Ordinal);

        driver.Operation = (_, _) => never.Task;
        using var cts = new CancellationTokenSource();
        Task<CommandResult> write = h.Unit.SetVariableAsync("ups.delay.shutdown", "30", Alice, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task Variable_writes_are_checked_then_passed_to_the_driver()
    {
        var (h, driver) = Connected();
        using var _ = h;

        Assert.Equal(CommandStatus.InvalidValue, (await h.Unit.SetVariableAsync("ups.delay.shutdown", "900", Alice)).Status);
        Assert.Empty(driver.Commands);
        Assert.Empty(h.Events);

        Assert.True((await h.Unit.SetVariableAsync("ups.delay.shutdown", "30", Alice)).IsSuccess);
        Assert.Equal(["SET ups.delay.shutdown=30"], driver.Commands);
        UpsEvent e = Assert.Single(h.Recorder.Events);
        Assert.Equal(UpsEventType.VariableChanged, e.Type);
        Assert.Equal("30", e.Data!["value"]);
    }

    [Fact]
    public async Task Variable_writes_need_a_driver_and_record_failures()
    {
        using var h = new UpsUnitHarness();
        h.Unit.BeginRun(clearData: true);
        h.Publish("OL", new() { ["ups.delay.shutdown"] = "20" },
                  new() { ["ups.delay.shutdown"] = VariableInfo.WritableRange(0, 600) });
        Assert.Equal(CommandStatus.DriverNotConnected, (await h.Unit.SetVariableAsync("ups.delay.shutdown", "30", Alice)).Status);

        Support.FakeDriver driver = h.AttachDriver();
        driver.Operation = (_, _) => Task.FromResult(CommandResult.Fail("refused"));
        h.Recorder.Clear();
        Assert.Equal(CommandStatus.Failed, (await h.Unit.SetVariableAsync("ups.delay.shutdown", "30", Alice)).Status);
        Assert.Equal([UpsEventType.CommandFailed], h.Events);
    }
}
