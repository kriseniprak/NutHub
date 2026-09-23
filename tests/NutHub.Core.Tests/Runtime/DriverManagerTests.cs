using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NutHub.Core.Configuration;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Core.Security;
using NutHub.Core.Tests.Support;

namespace NutHub.Core.Tests.Runtime;

public sealed class DriverManagerTests : IAsyncLifetime
{
    private static readonly TimeSpan Step = TimeSpan.FromMilliseconds(100);

    private readonly FakeTimeProvider _time = TestTime.Create();
    private readonly EventHub _hub = TestTime.Hub();
    private readonly FakeDriverFactory _factory = new("fake");
    private readonly AesSecretProtector _secrets = new(RandomNumberGenerator.GetBytes(32));
    private readonly HubRecorder _recorder;
    private FakeConfigStore _config = null!;
    private UpsRegistry _registry = null!;
    private DriverManager _manager = null!;

    public DriverManagerTests()
    {
        _recorder = new HubRecorder(_hub);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_manager is not null)
        {
            await _manager.StopAsync(CancellationToken.None);
            _manager.Dispose();
        }

        _recorder.Dispose();
    }

    private static UpsConfig Ups(string name, bool enabled = true) => new() { Name = name, Driver = "fake", Enabled = enabled };

    private async Task StartAsync(params UpsConfig[] ups)
    {
        _config = new FakeConfigStore(new NutHubConfig { Ups = [.. ups] });
        _registry = new UpsRegistry(_hub, _time, NullLoggerFactory.Instance, () => _config.Current.Nut);
        _manager = new DriverManager(_config, new DriverCatalog([_factory]), _registry, _secrets,
                                     NullLoggerFactory.Instance, _time, new ServiceCollection().BuildServiceProvider());
        await _manager.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => _registry.Units.Count == ups.Length, "the units to be created");
    }

    private UpsUnit Unit(string name) => _registry.Find(name) ?? throw new InvalidOperationException($"No unit {name}");

    private Task WaitAvailable(string name) =>
        Wait.UntilAsync(() => _registry.Find(name)?.Snapshot.IsAvailable == true, $"{name} to be available");

    private Task WaitState(string name, DriverState state) =>
        Wait.UntilAsync(() => _registry.Find(name)?.Snapshot.DriverState == state, $"{name} to be {state}");

    private Task ChangeAsync(Action<NutHubConfig> change) => _config.UpdateAsync(change, CommandOrigin.System, "test");

    [Fact]
    public async Task One_driver_runs_per_enabled_ups_with_its_options()
    {
        string secret = _secrets.Protect("public-community")!;
        UpsConfig first = Ups("first");
        first.Options["port"] = "COM3";
        first.Options["community"] = secret;
        first.PollIntervalSeconds = 0.1; // clamped to the minimum

        await StartAsync(first, Ups("second"), Ups("off", enabled: false));
        await WaitAvailable("first");
        await WaitAvailable("second");

        Assert.Equal(2, _factory.CreateCount);
        FakeDriver driver = _factory.Created.Single(d => d.CreateContext.UpsName == "first");
        Assert.Equal("COM3", driver.Options["PORT"]);
        Assert.Equal("public-community", driver.Options["community"]); // decrypted for the driver
        Assert.Equal(TimeSpan.FromSeconds(0.5), driver.CreateContext.PollInterval);
        Assert.Equal(DriverState.Disabled, Unit("off").Snapshot.DriverState);
        Assert.Equal(["first", "second", "off"], _registry.Units.Select(u => u.Name));
        Assert.Same(Unit("FIRST"), Unit("first"));
    }

    [Fact]
    public async Task Stopping_the_manager_stops_every_driver()
    {
        await StartAsync(Ups("a"), Ups("b"));
        await WaitAvailable("a");
        await WaitAvailable("b");

        await _manager.StopAsync(CancellationToken.None);

        Assert.All(_factory.Created, d => Assert.True(d.Cancelled && d.Disposed));
    }

    [Fact]
    public async Task Changed_driver_options_restart_the_driver_without_communication_events()
    {
        await StartAsync(Ups("ups1"));
        await WaitAvailable("ups1");
        FakeDriver first = _factory.Last!;
        _recorder.Clear();

        await ChangeAsync(c => c.Ups[0].Options["port"] = "COM4");
        await Wait.UntilAsync(() => _factory.CreateCount == 2, "a new driver");
        await WaitAvailable("ups1");

        Assert.True(first.Cancelled);
        Assert.True(first.Disposed);
        Assert.Equal("COM4", _factory.Last!.Options["port"]);
        Assert.DoesNotContain(_recorder.EventTypes, t => t is UpsEventType.CommunicationLost or UpsEventType.CommunicationRestored);
    }

    [Fact]
    public async Task Other_changes_apply_in_place()
    {
        await StartAsync(Ups("ups1"));
        await WaitAvailable("ups1");

        await ChangeAsync(c =>
        {
            c.Ups[0].Description = "Rack 1";
            c.Ups[0].Overrides["battery.charge"] = "42";
            c.Ups[0].LowBattery.ChargePercent = 50;
        });
        await Wait.UntilAsync(() => Unit("ups1").Snapshot.Get("battery.charge") == "42", "the override");

        Assert.Equal(1, _factory.CreateCount);
        Assert.Equal("Rack 1", Unit("ups1").Snapshot.Description);
        Assert.True(Unit("ups1").Snapshot.IsAvailable);
    }

    [Fact]
    public async Task Added_removed_and_reordered_upses_follow_the_configuration()
    {
        await StartAsync(Ups("a"), Ups("b"));
        await WaitAvailable("a");
        FakeDriver driverOfA = _factory.Created.Single(d => d.CreateContext.UpsName == "a");

        await ChangeAsync(c =>
        {
            c.Ups.RemoveAll(u => u.Name == "a");
            c.Ups.Insert(0, Ups("c"));
        });
        await Wait.UntilAsync(() => _registry.Find("a") is null && _registry.Find("c") is not null, "a removed, c added");
        await WaitAvailable("c");

        Assert.True(driverOfA.Cancelled && driverOfA.Disposed);
        Assert.Contains(_recorder.Messages, m => m is UpsRemovedMessage { Name: "a" });
        Assert.Equal(["c", "b"], _registry.Units.Select(u => u.Name));
    }

    [Fact]
    public async Task Disabling_stops_the_driver_and_enabling_starts_it_again()
    {
        await StartAsync(Ups("ups1"));
        await WaitAvailable("ups1");
        FakeDriver first = _factory.Last!;

        await ChangeAsync(c => c.Ups[0].Enabled = false);
        await WaitState("ups1", DriverState.Disabled);
        Assert.True(first.Disposed);
        Assert.Empty(Unit("ups1").Snapshot.Variables);

        await ChangeAsync(c => c.Ups[0].Enabled = true);
        await WaitAvailable("ups1");
        Assert.Equal(2, _factory.CreateCount);
    }

    [Fact]
    public async Task A_crashing_driver_is_restarted_with_a_growing_delay()
    {
        _factory.Behavior = FakeRunBehavior.Throw;
        await StartAsync(Ups("ups1"));
        await WaitState("ups1", DriverState.Failed);
        Assert.Equal("Device unplugged", Unit("ups1").Snapshot.DriverMessage);

        TimeSpan first = await Wait.AdvanceUntilAsync(_time, () => _factory.CreateCount >= 2, "second attempt", Step,
                                                      TimeSpan.FromSeconds(30));
        TimeSpan second = await Wait.AdvanceUntilAsync(_time, () => _factory.CreateCount >= 3, "third attempt", Step,
                                                       TimeSpan.FromSeconds(30));
        TimeSpan third = await Wait.AdvanceUntilAsync(_time, () => _factory.CreateCount >= 4, "fourth attempt", Step,
                                                      TimeSpan.FromSeconds(30));

        AssertAbout(2, first);
        AssertAbout(5, second);
        AssertAbout(10, third);
    }

    [Fact]
    public async Task A_run_that_worked_for_a_while_restarts_the_back_off_from_the_shortest_delay()
    {
        _factory.Behavior = FakeRunBehavior.Throw;
        await StartAsync(Ups("ups1"));
        await WaitState("ups1", DriverState.Failed);
        await Wait.AdvanceUntilAsync(_time, () => _factory.CreateCount >= 2, "second attempt", Step, TimeSpan.FromSeconds(30));

        // The third attempt connects and works for two minutes before losing the device.
        _factory.Behavior = FakeRunBehavior.PublishThenThrow;
        await Wait.AdvanceUntilAsync(_time, () => Unit("ups1").Snapshot.IsAvailable, "the third attempt to connect",
                                     Step, TimeSpan.FromSeconds(30));
        await Wait.AdvanceUntilAsync(_time, () => Unit("ups1").Snapshot.DriverState == DriverState.Failed,
                                     "the connection loss", TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(3));

        TimeSpan delay = await Wait.AdvanceUntilAsync(_time, () => _factory.CreateCount >= 4, "the next attempt", Step,
                                                      TimeSpan.FromSeconds(30));
        AssertAbout(2, delay);
    }

    [Fact]
    public async Task A_driver_that_returns_on_its_own_counts_as_a_failure()
    {
        _factory.Behavior = FakeRunBehavior.Return;
        await StartAsync(Ups("ups1"));
        await WaitState("ups1", DriverState.Failed);

        Assert.Contains("stopped unexpectedly", Unit("ups1").Snapshot.DriverMessage, StringComparison.Ordinal);
        await Wait.AdvanceUntilAsync(_time, () => _factory.CreateCount >= 2, "a retry", Step, TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task A_configuration_error_is_not_retried()
    {
        _factory.Behavior = FakeRunBehavior.ThrowConfiguration;
        await StartAsync(Ups("ups1"));
        await WaitState("ups1", DriverState.Failed);

        for (int i = 0; i < 20; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(10));
            await Task.Delay(3);
        }

        Assert.Equal(1, _factory.CreateCount);
        Assert.Equal("The option 'port' is required.", Unit("ups1").Snapshot.DriverMessage);
        Assert.Contains(_recorder.EventTypes, t => t == UpsEventType.DriverFailed);
    }

    [Fact]
    public async Task A_configuration_change_retries_a_failed_driver()
    {
        _factory.Behavior = FakeRunBehavior.ThrowConfiguration;
        await StartAsync(Ups("ups1"));
        await WaitState("ups1", DriverState.Failed);

        _factory.Behavior = FakeRunBehavior.PublishAndWait;
        await ChangeAsync(c => c.Ups[0].Options["port"] = "COM1");

        await WaitAvailable("ups1");
        Assert.Equal(2, _factory.CreateCount);
    }

    [Fact]
    public async Task RestartAsync_restarts_one_driver()
    {
        await StartAsync(Ups("ups1"), Ups("off", enabled: false));
        await WaitAvailable("ups1");
        FakeDriver first = _factory.Last!;

        Assert.True(await _manager.RestartAsync("UPS1"));
        await Wait.UntilAsync(() => _factory.CreateCount == 2, "the restart");
        await WaitAvailable("ups1");

        Assert.True(first.Cancelled && first.Disposed);
        Assert.False(await _manager.RestartAsync("off"));
        Assert.False(await _manager.RestartAsync("missing"));
    }

    // Serial and network drivers often end with an I/O error when the cancellation closes their port.
    [Fact]
    public async Task A_driver_that_fails_while_being_stopped_is_not_reported_as_failed()
    {
        _factory.Behavior = FakeRunBehavior.ThrowWhenCancelled;
        await StartAsync(Ups("ups1"));
        await WaitAvailable("ups1");
        _recorder.Clear();

        await ChangeAsync(c => c.Ups[0].Options["port"] = "COM4");
        await Wait.UntilAsync(() => _factory.CreateCount == 2, "a new driver");
        await WaitAvailable("ups1");
        await _manager.StopAsync(CancellationToken.None);

        Assert.All(_factory.Created, d => Assert.True(d.Cancelled && d.Disposed));
        Assert.DoesNotContain(_recorder.EventTypes, t => t is UpsEventType.DriverFailed or UpsEventType.CommunicationLost);
        Assert.Equal(DriverState.Connected, Unit("ups1").Snapshot.DriverState);
    }

    [Fact]
    public async Task A_driver_that_ignores_the_stop_is_abandoned_without_disturbing_the_next_one()
    {
        _factory.Behavior = FakeRunBehavior.IgnoreCancellation;
        await StartAsync(Ups("ups1"));
        await WaitAvailable("ups1");
        FakeDriver stuck = _factory.Last!;
        _factory.Behavior = FakeRunBehavior.PublishAndWait;
        _manager.StopTimeout = TimeSpan.FromMilliseconds(200);

        Assert.True(await _manager.RestartAsync("ups1"));
        await WaitAvailable("ups1");
        Assert.Equal(2, _factory.CreateCount);

        // The abandoned driver wakes up late: it publishes what it still had, then fails.
        stuck.Release();
        await Wait.UntilAsync(() => stuck.Disposed, "the abandoned driver to end");

        UpsSnapshot snapshot = Unit("ups1").Snapshot;
        Assert.Equal(DriverState.Connected, snapshot.DriverState);
        Assert.Equal("OL", snapshot.Get("ups.status"));
        CommandResult result = await Unit("ups1").InstantCommandAsync("beeper.toggle", null, CommandOrigin.System);
        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(["beeper.toggle"], _factory.Last!.Commands);
    }

    [Fact]
    public async Task A_driver_abandoned_while_being_created_is_never_attached()
    {
        using var gate = new ManualResetEventSlim();
        _factory.BlockNextCreate(gate);
        await StartAsync(Ups("ups1"));
        _manager.StopTimeout = TimeSpan.FromMilliseconds(200);

        Assert.True(await _manager.RestartAsync("ups1"));
        await WaitAvailable("ups1");
        FakeDriver current = _factory.Last!;

        // The first creation finally returns, long after its run was abandoned.
        gate.Set();
        await Wait.UntilAsync(() => _factory.CreateCount == 2 && _factory.Created.All(d => d == current || d.Disposed),
                              "the late driver to be disposed");

        CommandResult result = await Unit("ups1").InstantCommandAsync("beeper.toggle", null, CommandOrigin.System);
        Assert.True(result.IsSuccess, result.ToString());
        Assert.Equal(["beeper.toggle"], current.Commands);
    }

    [Fact]
    public async Task Unknown_or_unsupported_drivers_and_undecryptable_secrets_fail_without_retry()
    {
        UpsConfig unknown = Ups("unknown");
        unknown.Driver = "nope";
        UpsConfig secret = Ups("secret");
        secret.Options["community"] = "enc:v1:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";

        await StartAsync(unknown, secret);
        await WaitState("unknown", DriverState.Failed);
        await WaitState("secret", DriverState.Failed);

        Assert.Contains("Unknown driver", Unit("unknown").Snapshot.DriverMessage, StringComparison.Ordinal);
        Assert.Contains("cannot be decrypted", Unit("secret").Snapshot.DriverMessage, StringComparison.Ordinal);
        Assert.Equal(0, _factory.CreateCount);
    }

    [Fact]
    public async Task A_driver_for_another_operating_system_fails()
    {
        _factory.Platforms = DriverPlatforms.None;
        await StartAsync(Ups("ups1"));
        await WaitState("ups1", DriverState.Failed);
        Assert.Contains("does not work on this operating system", Unit("ups1").Snapshot.DriverMessage,
                        StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_housekeeping_timer_ages_the_data()
    {
        await StartAsync(Ups("ups1"));
        await WaitAvailable("ups1");

        await Wait.AdvanceUntilAsync(_time, () => Unit("ups1").Snapshot.Availability == DataAvailability.Stale,
                                     "stale data", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30));

        Assert.Contains(_recorder.EventTypes, t => t == UpsEventType.CommunicationLost);
    }

    private static void AssertAbout(double expectedSeconds, TimeSpan actual) =>
        Assert.InRange(actual.TotalSeconds, expectedSeconds - 0.15, expectedSeconds + 0.6);
}
