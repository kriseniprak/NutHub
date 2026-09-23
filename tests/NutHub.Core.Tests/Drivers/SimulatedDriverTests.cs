using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NutHub.Core.Drivers;
using NutHub.Core.Drivers.Simulated;
using NutHub.Core.Model;
using NutHub.Core.Tests.Support;

namespace NutHub.Core.Tests.Drivers;

public sealed class SimulatedDriverTests : IAsyncDisposable
{
    private readonly FakeTimeProvider _time = TestTime.Create();
    private readonly RecordingContext _context;
    private readonly CancellationTokenSource _stop = new();
    private readonly TempDirectory _dir = new();
    private IUpsDriver? _driver;
    private Task? _run;

    public SimulatedDriverTests()
    {
        _context = new RecordingContext(_time);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        if (_run is not null)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _run);
        }

        if (_driver is not null)
        {
            await _driver.DisposeAsync();
        }

        _stop.Dispose();
        _dir.Dispose();
    }

    private static DriverCreateContext CreateContext(Dictionary<string, string> options) => new()
    {
        UpsName = "ups1",
        Options = options,
        PollInterval = TimeSpan.FromSeconds(1),
        LoggerFactory = NullLoggerFactory.Instance,
        TimeProvider = TimeProvider.System,
        Services = new ServiceCollection().BuildServiceProvider(),
    };

    private async Task<DriverUpdate> StartAsync(Dictionary<string, string>? options = null)
    {
        _driver = new SimulatedDriverFactory().Create(CreateContext(options ?? []));
        _run = _driver.RunAsync(_context, _stop.Token);
        return await _context.WaitForAsync(_ => true);
    }

    /// <summary>Advances the simulation and waits for the poll that follows.</summary>
    private async Task<DriverUpdate> AdvanceAsync(double seconds, Func<DriverUpdate, bool>? until = null)
    {
        int before = _context.Count;
        _time.Advance(TimeSpan.FromSeconds(seconds));
        return await _context.WaitForAsync(u => until?.Invoke(u) ?? true, afterCount: before);
    }

    private static string Status(DriverUpdate u) => u.Variables["ups.status"];

    private static double Number(DriverUpdate u, string name) => double.Parse(u.Variables[name], System.Globalization.CultureInfo.InvariantCulture);

    private Task<CommandResult> Command(string name, string? parameter = null) =>
        _driver!.InstantCommandAsync(name, parameter, CancellationToken.None);

    [Fact]
    public async Task A_steady_ups_publishes_the_standard_variables()
    {
        DriverUpdate u = await StartAsync();

        Assert.Equal("OL", Status(u));
        Assert.Equal("100", u.Variables["battery.charge"]);
        Assert.Equal("NutHub", u.Variables["ups.mfr"]);
        Assert.Equal("Virtual UPS 1500", u.Variables["ups.model"]);
        Assert.Equal("SIM-UPS1", u.Variables["ups.serial"]);
        Assert.Equal("230", u.Variables["input.voltage.nominal"]);
        Assert.Equal("1500", u.Variables["battery.runtime"]); // 25 minutes at the configured load
        Assert.Contains("test.failure.start", u.Commands!);
        Assert.True(u.VariableInfo!["ups.delay.shutdown"].Writable);
        Assert.All(u.Variables.Keys, k => Assert.True(NutFormat.IsValidVariableName(k), k));
        Assert.Equal("Starting the simulation", _context.LastConnectingDetail);
    }

    [Fact]
    public async Task A_power_failure_discharges_the_battery_until_low_battery()
    {
        await StartAsync();

        Assert.True((await Command("test.failure.start")).IsSuccess);
        DriverUpdate u = await AdvanceAsync(1, x => Status(x).StartsWith("OB", StringComparison.Ordinal));
        Assert.Equal("OB DISCHRG", Status(u));
        Assert.Equal("0.0", u.Variables["input.voltage"]);

        // 25 minutes at full charge: 600 s take 40 %. (A long advance may publish intermediate values first.)
        u = await AdvanceAsync(600, x => Number(x, "battery.charge") <= 61);
        Assert.InRange(Number(u, "battery.charge"), 59, 61);
        Assert.Equal("OB DISCHRG", Status(u));

        u = await AdvanceAsync(600, x => Status(x).Contains("LB", StringComparison.Ordinal));
        Assert.InRange(Number(u, "battery.charge"), 19, 20); // battery.charge.low is 20
        Assert.Equal("OB DISCHRG LB", Status(u));

        Assert.True((await Command("test.failure.stop")).IsSuccess);
        u = await AdvanceAsync(1, x => Status(x).StartsWith("OL", StringComparison.Ordinal));
        Assert.Equal("OL CHRG", Status(u));

        // Recharges at about 100 % per hour.
        double charge = Number(u, "battery.charge");
        u = await AdvanceAsync(1800, x => Number(x, "battery.charge") >= charge + 49);
        Assert.InRange(Number(u, "battery.charge"), charge + 48, charge + 52);
    }

    [Fact]
    public async Task An_exhausted_battery_drops_the_load()
    {
        await StartAsync(new() { ["runtimeMinutes"] = "1" });
        await Command("test.failure.start");

        DriverUpdate u = await AdvanceAsync(120, x => Status(x).Contains("OFF", StringComparison.Ordinal));

        Assert.Equal("0", u.Variables["battery.charge"]);
        Assert.StartsWith("OFF OB", Status(u), StringComparison.Ordinal);
        Assert.Equal("0.0", u.Variables["output.voltage"]);
    }

    [Fact]
    public async Task Shutdown_return_turns_the_output_off_then_on_again()
    {
        await StartAsync();
        Assert.True((await _driver!.SetVariableAsync("ups.delay.shutdown", "5", CancellationToken.None)).IsSuccess);
        Assert.True((await _driver.SetVariableAsync("ups.delay.start", "10", CancellationToken.None)).IsSuccess);
        await AdvanceAsync(1);

        Assert.True((await Command("shutdown.return")).IsSuccess);
        DriverUpdate u = await AdvanceAsync(1, x => x.Variables["ups.timer.shutdown"] != "-1");
        Assert.Equal("4", u.Variables["ups.timer.shutdown"]);
        Assert.Equal("OL", Status(u));

        u = await AdvanceAsync(4, x => Status(x).Contains("OFF", StringComparison.Ordinal));
        Assert.Equal("OFF OL", Status(u));
        Assert.Equal("-1", u.Variables["ups.timer.shutdown"]);
        Assert.Equal("10", u.Variables["ups.timer.start"]);

        u = await AdvanceAsync(9);
        Assert.Contains("OFF", Status(u), StringComparison.Ordinal);
        u = await AdvanceAsync(1, x => !Status(x).Contains("OFF", StringComparison.Ordinal));
        Assert.StartsWith("OL", Status(u), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shutdown_stayoff_keeps_the_output_off_until_load_on()
    {
        await StartAsync(new() { });
        await _driver!.SetVariableAsync("ups.delay.shutdown", "2", CancellationToken.None);
        await AdvanceAsync(1);
        await Command("shutdown.stayoff");

        DriverUpdate u = await AdvanceAsync(3, x => Status(x).Contains("OFF", StringComparison.Ordinal));
        u = await AdvanceAsync(600);
        Assert.StartsWith("OFF", Status(u), StringComparison.Ordinal);

        await Command("load.on");
        u = await AdvanceAsync(1, x => !Status(x).Contains("OFF", StringComparison.Ordinal));
        Assert.Equal("OL", Status(u));
    }

    [Fact]
    public async Task The_outages_scenario_alternates_line_and_battery_power()
    {
        await StartAsync(new() { ["scenario"] = "outages", ["onlineMinutes"] = "1", ["outageMinutes"] = "1" });

        DriverUpdate u = await AdvanceAsync(61, x => Status(x).StartsWith("OB", StringComparison.Ordinal));
        Assert.StartsWith("OB", Status(u), StringComparison.Ordinal);
        u = await AdvanceAsync(60, x => Status(x).StartsWith("OL", StringComparison.Ordinal));
        Assert.StartsWith("OL", Status(u), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Commands_and_variables_update_the_published_values()
    {
        await StartAsync();

        Assert.True((await Command("beeper.disable")).IsSuccess);
        Assert.True((await Command("test.battery.start.quick")).IsSuccess);
        Assert.True((await _driver!.SetVariableAsync("ups.id", "rack-7", CancellationToken.None)).IsSuccess);
        DriverUpdate u = await AdvanceAsync(1, x => x.Variables["ups.test.result"] == "In progress");
        Assert.Equal("disabled", u.Variables["ups.beeper.status"]);
        Assert.Equal("rack-7", u.Variables["ups.id"]);
        Assert.Contains("TEST", Status(u), StringComparison.Ordinal);

        u = await AdvanceAsync(10, x => x.Variables["ups.test.result"] != "In progress");
        Assert.Equal("Done and passed", u.Variables["ups.test.result"]);

        Assert.Equal(CommandStatus.NotSupported, (await Command("make.coffee")).Status);
        Assert.Equal(CommandStatus.NotSupported,
                     (await _driver.SetVariableAsync("battery.charge", "50", CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Simulated_communication_loss_reports_a_disconnection()
    {
        await StartAsync();
        await Command("simulate.commlost", "5");

        await AdvanceAsync(1, _ => true);
        Assert.Equal("Simulated loss of communication", _context.LastDisconnectReason);
        int disconnectedAt = _context.Count;

        await AdvanceAsync(5, _ => true);
        Assert.True(_context.Count > disconnectedAt);
    }

    [Fact]
    public async Task A_dev_file_overrides_the_simulated_values_and_is_reread_when_it_changes()
    {
        string devFile = _dir.File("ups.dev");
        await File.WriteAllLinesAsync(devFile,
        [
            "# NUT dummy-ups file",
            "battery.charge: 42",
            "ups.status: \"OB DISCHRG\"",
            "TIMER 10",
            "driver.name: impostor",
            "not a variable line",
            "bad name: 1",
        ]);
        File.SetLastWriteTimeUtc(devFile, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        DriverUpdate u = await StartAsync(new() { ["devFile"] = devFile });
        Assert.Equal("42", u.Variables["battery.charge"]);
        Assert.Equal("OB DISCHRG", Status(u));
        Assert.False(u.Variables.ContainsKey("driver.name"));
        Assert.False(u.Variables.ContainsKey("bad name"));

        await File.WriteAllLinesAsync(devFile, ["battery.charge: 17"]);
        File.SetLastWriteTimeUtc(devFile, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));
        u = await AdvanceAsync(1, x => x.Variables["battery.charge"] == "17");
        Assert.Equal("OL", Status(u)); // the simulated status again
    }

    [Theory]
    [InlineData("devFile", "Z:\\does\\not\\exist.dev")]
    [InlineData("load", "abc")]
    [InlineData("load", "151")]
    [InlineData("scenario", "chaos")]
    [InlineData("voltage", "240")]
    public void Invalid_options_are_configuration_errors(string key, string value)
    {
        var ex = Assert.Throws<DriverConfigurationException>(() =>
            new SimulatedDriverFactory().Create(CreateContext(new() { [key] = value })));
        Assert.Equal(key, ex.OptionKey);
    }

    [Fact]
    public void The_factory_describes_itself()
    {
        var factory = new SimulatedDriverFactory();
        Assert.Equal("simulated", factory.Id);
        Assert.Equal(DriverPlatforms.All, factory.Platforms);
        Assert.False(factory.SupportsDiscovery);
        Assert.Contains(factory.Options, o => o.Key == "scenario" && o.Choices!.Count == 2);
    }

    /// <summary>Records what the driver reports.</summary>
    private sealed class RecordingContext(TimeProvider time) : IDriverContext
    {
        private readonly List<DriverUpdate> _updates = [];

        public string UpsName => "ups1";

        public ILogger Logger => NullLogger.Instance;

        public TimeProvider TimeProvider => time;

        public TimeSpan PollInterval => TimeSpan.FromSeconds(1);

        public string? LastConnectingDetail { get; private set; }

        public string? LastDisconnectReason { get; private set; }

        private int _reports;

        public int Count => Volatile.Read(ref _reports);

        public void ReportConnecting(string? detail = null) => LastConnectingDetail = detail;

        public void ReportDisconnected(string reason)
        {
            LastDisconnectReason = reason;
            Interlocked.Increment(ref _reports);
        }

        public void Publish(DriverUpdate update)
        {
            lock (_updates)
            {
                _updates.Add(update);
            }

            Interlocked.Increment(ref _reports);
        }

        /// <summary>Waits for a report after <paramref name="afterCount"/> reports, and a publish matching the condition.</summary>
        public async Task<DriverUpdate> WaitForAsync(Func<DriverUpdate, bool> condition, int afterCount = 0)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(5);
            while (true)
            {
                DriverUpdate? last = null;
                lock (_updates)
                {
                    if (_updates.Count > 0)
                    {
                        last = _updates[^1];
                    }
                }

                if (Count > afterCount && last is not null && condition(last))
                {
                    return last;
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("The simulated driver did not publish the expected update.");
                }

                await Task.Delay(3);
            }
        }
    }
}
