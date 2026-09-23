using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Drivers.Hid.Tests.Support;
using NutHub.Drivers.Hid.WinBattery;

namespace NutHub.Drivers.Hid.Tests.WinBattery;

public sealed class WinBatteryTests
{
    private const uint Ups = BatteryDevice.SystemBatteryFlag | BatteryDevice.RelativeCapacityFlag | BatteryDevice.ShortTermFlag;

    private static readonly BatteryDevice ApcUps = new(
        @"\\?\hid#vid_051d&pid_0002#6&2c4f1a3&0&0000#{72631e54-78a4-11d0-bcf7-00aa00b7b32a}",
        "Back-UPS ES 700", "American Power Conversion", "4B1234P56789", "4B1234P56789Back-UPS ES 700", Ups, "PbAc",
        100, 100, new DateOnly(2021, 3, 14));

    private static readonly BatteryDevice Laptop = new(
        @"\\?\acpi#pnp0c0a#1#{72631e54-78a4-11d0-bcf7-00aa00b7b32a}", "DELL 7FHHV", "SMP", "1234", "SMPDELL 7FHHV",
        BatteryDevice.SystemBatteryFlag, "LiP", 60000, 54000, null);

    [Theory]
    [InlineData(BatteryReading.OnLine | BatteryReading.Charging, "OL CHRG")]
    [InlineData(BatteryReading.OnLine, "OL")]
    [InlineData(BatteryReading.Discharging, "OB DISCHRG")]
    [InlineData(BatteryReading.Discharging | BatteryReading.Critical, "OB DISCHRG LB")]
    [InlineData(0u, "OB")]
    public void Power_states_become_ups_status(uint state, string expected)
    {
        Assert.Equal(expected, WinBatteryMapper.Status(state));
    }

    [Fact]
    public void Maps_a_ups_battery_to_nut_variables()
    {
        Dictionary<string, string> vars = WinBatteryMapper.BuildVariables(ApcUps, new BatteryReading(
            BatteryReading.OnLine | BatteryReading.Charging, 87, 13650, 0, 1740));

        Assert.Equal("OL CHRG", vars["ups.status"]);
        Assert.Equal("87", vars["battery.charge"]);
        Assert.Equal("1740", vars["battery.runtime"]);
        Assert.Equal("13.65", vars["battery.voltage"]);
        Assert.Equal("American Power Conversion", vars["ups.mfr"]);
        Assert.Equal("Back-UPS ES 700", vars["ups.model"]);
        Assert.Equal("4B1234P56789", vars["ups.serial"]);
        Assert.Equal("PbAc", vars["battery.type"]);
        Assert.Equal("2021/03/14", vars["battery.mfr.date"]);
        Assert.Equal("051d", vars["ups.vendorid"]);
        Assert.Equal("0002", vars["ups.productid"]);
    }

    [Fact]
    public void Unknown_readings_are_left_out()
    {
        Dictionary<string, string> vars = WinBatteryMapper.BuildVariables(ApcUps with { Serial = null }, new BatteryReading(
            BatteryReading.OnLine, BatteryReading.Unknown, BatteryReading.Unknown, 0, BatteryReading.Unknown));

        Assert.DoesNotContain("battery.charge", vars.Keys);
        Assert.DoesNotContain("battery.runtime", vars.Keys);
        Assert.DoesNotContain("battery.voltage", vars.Keys);
        Assert.DoesNotContain("ups.serial", vars.Keys);
    }

    [Fact]
    public void Absolute_capacities_are_turned_into_a_percentage()
    {
        Assert.Equal(50, WinBatteryMapper.Charge(Laptop, 27000));
        Assert.Equal(100, WinBatteryMapper.Charge(Laptop, 60000)); // above the last full charge
        Assert.Null(WinBatteryMapper.Charge(Laptop with { FullChargedCapacity = 0 }, 27000));
        Assert.Equal(64, WinBatteryMapper.Charge(ApcUps, 64));
    }

    [Fact]
    public void Selects_ups_batteries_unless_told_otherwise()
    {
        BatteryDevice[] all = [Laptop, ApcUps];

        Assert.Same(ApcUps, WinBatteryDriver.Select(all, new WinBatterySettings()));
        Assert.Same(Laptop, WinBatteryDriver.Select(all, new WinBatterySettings { IncludeSystemBatteries = true }));
        Assert.Null(WinBatteryDriver.Select([Laptop], new WinBatterySettings()));
        Assert.Same(ApcUps, WinBatteryDriver.Select(all, new WinBatterySettings { Battery = "4b1234p56789back-ups es 700" }));
        Assert.Same(ApcUps, WinBatteryDriver.Select(all, new WinBatterySettings { Battery = "ES 700" }));
        Assert.Null(WinBatteryDriver.Select(all, new WinBatterySettings { Battery = "DELL" }));
    }

    [Fact]
    public async Task Publishes_readings_and_recovers_from_a_lost_battery()
    {
        var source = new FakeBatterySource(ApcUps);
        var context = new RecordingDriverContext(TimeSpan.FromMilliseconds(30));
        var driver = new WinBatteryDriver("ups", new WinBatterySettings(), source, NullLogger.Instance, TimeProvider.System,
                                          new WinBatteryTimings { RetryDelay = TimeSpan.FromMilliseconds(20) });
        using var cts = new CancellationTokenSource();
        Task run = Task.Run(() => driver.RunAsync(context, cts.Token));

        await context.WaitUntilAsync(c => c.Updates.Count >= 2, "two readings");
        Assert.Equal("OL CHRG", context.Last!.Variables["ups.status"]);
        Assert.Empty(context.Last.Commands!);

        source.Lost = true;
        await context.WaitUntilAsync(c => c.Events.Any(e => e.StartsWith("disconnected: ", StringComparison.Ordinal)), "the loss");
        int published = context.Updates.Count;
        source.Lost = false;
        source.PowerState = BatteryReading.Discharging;
        await context.WaitUntilAsync(c => c.Updates.Count > published && c.Last!.Variables["ups.status"] == "OB DISCHRG", "the recovery");

        Assert.Equal(CommandStatus.NotSupported, (await driver.InstantCommandAsync("beeper.disable", null, CancellationToken.None)).Status);
        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(source.OpenHandles.IsEmpty);
    }

    [Fact]
    public async Task Explains_when_no_ups_battery_exists()
    {
        var context = new RecordingDriverContext(TimeSpan.FromMilliseconds(30));
        var driver = new WinBatteryDriver("ups", new WinBatterySettings(), new FakeBatterySource(Laptop), NullLogger.Instance,
                                          TimeProvider.System, new WinBatteryTimings { RetryDelay = TimeSpan.FromMilliseconds(20) });
        using var cts = new CancellationTokenSource();
        Task run = Task.Run(() => driver.RunAsync(context, cts.Token));

        await context.WaitUntilAsync(c => c.Events.Any(e => e.Contains("includeSystemBatteries", StringComparison.Ordinal)), "the explanation");

        await cts.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(CommandStatus.DriverNotConnected, (await driver.InstantCommandAsync("beeper.disable", null, CancellationToken.None)).Status);
    }

    [Fact]
    public void Factory_describes_a_windows_only_driver()
    {
        var factory = new WinBatteryDriverFactory();

        Assert.Equal("winbattery", factory.Id);
        Assert.Equal(DriverPlatforms.Windows, factory.Platforms);
        Assert.Equal(["battery", "includeSystemBatteries"], factory.Options.Select(o => o.Key));
    }

    [Fact]
    public void Factory_refuses_to_create_a_driver_without_the_battery_class()
    {
        var factory = new WinBatteryDriverFactory(source: null, NullLogger.Instance);

        var error = Assert.Throws<DriverConfigurationException>(() => factory.Create(new DriverCreateContext
        {
            UpsName = "ups",
            Options = new Dictionary<string, string>(),
            PollInterval = TimeSpan.FromSeconds(2),
            LoggerFactory = NullLoggerFactory.Instance,
            TimeProvider = TimeProvider.System,
            Services = null!,
        }));
        Assert.Contains("Windows only", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discovery_lists_ups_batteries_first()
    {
        var factory = new WinBatteryDriverFactory(new FakeBatterySource(Laptop, ApcUps), NullLogger.Instance);

        IReadOnlyList<DiscoveredDevice> found = await factory.DiscoverAsync(CancellationToken.None);

        Assert.Equal(2, found.Count);
        Assert.Equal("American Power Conversion Back-UPS ES 700", found[0].Title);
        Assert.Equal("4B1234P56789Back-UPS ES 700", found[0].Options["battery"]);
        Assert.Equal("back-ups-es-700", found[0].SuggestedName);
        Assert.Equal("true", found[1].Options["includeSystemBatteries"]);
    }

    [Fact]
    public async Task Enumerating_the_batteries_of_this_machine_does_not_fail()
    {
        // The real SetupAPI path: on a desktop without a UPS the list is simply empty.
        IReadOnlyList<DiscoveredDevice> found = await new WinBatteryDriverFactory().DiscoverAsync(CancellationToken.None);

        Assert.NotNull(found);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Empty(found);
        }
    }

    [Fact]
    public void Native_enumeration_runs_to_the_end_of_the_list()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        List<string> paths = WindowsBatteryNative.EnumerateBatteryPaths(out int stopError);

        // 259 is ERROR_NO_MORE_ITEMS: SetupAPI accepted the structures and listed every battery.
        Assert.Equal(WindowsBatteryNative.ErrorNoMoreItems, stopError);
        Assert.All(paths, p => Assert.StartsWith(@"\\?\", p, StringComparison.Ordinal));
        IReadOnlyList<BatteryDevice> batteries = new WindowsBatterySource(NullLogger.Instance).GetBatteries();
        Assert.True(batteries.Count <= paths.Count);
    }

    private sealed class FakeBatterySource(params BatteryDevice[] batteries) : IBatterySource
    {
        public volatile bool Lost;
        public volatile uint PowerState = BatteryReading.OnLine | BatteryReading.Charging;

        public ConcurrentDictionary<Handle, bool> OpenHandles { get; } = new();

        public IReadOnlyList<BatteryDevice> GetBatteries() => Lost ? [] : batteries;

        public IBatteryHandle Open(BatteryDevice battery)
        {
            if (Lost)
            {
                throw new BatteryLostException("gone");
            }

            var handle = new Handle(this);
            OpenHandles[handle] = true;
            return handle;
        }

        internal sealed class Handle(FakeBatterySource source) : IBatteryHandle
        {
            public BatteryReading Read() => source.Lost
                ? throw new BatteryLostException("The battery stopped answering.")
                : new BatteryReading(source.PowerState, 90, 13500, 0, 1800);

            public void Dispose() => source.OpenHandles.TryRemove(this, out _);
        }
    }
}
