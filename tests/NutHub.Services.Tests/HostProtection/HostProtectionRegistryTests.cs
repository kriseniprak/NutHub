using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Core.Security;
using NutHub.Services.HostProtection;
using NutHub.Services.Tests.Support;

namespace NutHub.Services.Tests.HostProtection;

/// <summary>The host protection against the real registry and driver manager, with a scripted driver.</summary>
public sealed class HostProtectionRegistryTests : IAsyncLifetime
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero));
    private readonly EventHub _hub = new(NullLogger<EventHub>.Instance);
    private readonly ScriptedDriverFactory _drivers = new();
    private readonly RecordingPowerController _power = new();
    private readonly TestConfigStore _config;
    private readonly UpsRegistry _registry;
    private readonly DriverManager _manager;
    private readonly NutSessionRegistry _sessions;

    public HostProtectionRegistryTests()
    {
        var config = new NutHubConfig();
        config.Ups.Add(new UpsConfig { Name = "rack1", Driver = "scripted" });
        config.HostProtection = new HostProtectionSettings
        {
            Enabled = true,
            Ups = ["rack1"],
            NotifySecondaries = true,
            SecondariesTimeoutSeconds = 30,
            PowerOffUps = true,
            PowerOffCommand = "shutdown.return",
            PowerOffDelaySeconds = 120,
        };
        _config = new TestConfigStore(config);
        _registry = new UpsRegistry(_hub, _time, NullLoggerFactory.Instance, _config);
        _sessions = new NutSessionRegistry(_hub, _time);
        _manager = new DriverManager(_config, new DriverCatalog([_drivers]), _registry,
                                     new AesSecretProtector(new byte[32]), NullLoggerFactory.Instance, _time,
                                     new ServiceCollection().BuildServiceProvider());
    }

    public async Task InitializeAsync()
    {
        await _manager.StartAsync(CancellationToken.None);
        await Wait.UntilAsync(() => _registry.Find("rack1")?.Snapshot.IsAvailable == true, "the scripted UPS");
    }

    public async Task DisposeAsync()
    {
        await _manager.StopAsync(CancellationToken.None);
        _manager.Dispose();
    }

    [Fact]
    public async Task Low_battery_sets_fsd_waits_for_the_secondaries_powers_off_and_shuts_down()
    {
        var service = new HostProtectionService(_config, new RegistryUpsAccess(_registry), _sessions, _hub, _time, _power,
                                                new ShutdownSafety(() => null), NullLogger.Instance);
        NutSession secondary = _sessions.Open(new IPEndPoint(IPAddress.Loopback, 50000), () => { });
        _sessions.SetLogin(secondary, "rack1");

        await service.EvaluateAsync(CancellationToken.None);
        Assert.Equal(HostProtectionState.Monitoring, service.GetStatus().State);

        ScriptedDriver driver = _drivers.Drivers["rack1"];
        driver.Publish("OB DISCHRG LB", charge: "9", runtime: "120");
        await service.EvaluateAsync(CancellationToken.None);

        UpsSnapshot snapshot = _registry.Find("rack1")!.Snapshot;
        Assert.True(snapshot.ForcedShutdown);
        Assert.StartsWith("FSD", snapshot.StatusText);
        Assert.Equal(HostProtectionState.WaitingForSecondaries, service.GetStatus().State);
        Assert.Empty(_power.Requests);

        _sessions.Close(secondary);
        await service.EvaluateAsync(CancellationToken.None);

        Assert.Equal(HostProtectionState.ShuttingDown, service.GetStatus().State);
        Assert.Equal(new[] { "set ups.delay.shutdown=120", "cmd shutdown.return" }, driver.Calls.ToArray());
        Assert.Single(_power.Requests);
    }

    [Fact]
    public async Task Lost_driver_after_on_battery_is_critical_after_the_dead_time()
    {
        _config.Update(c =>
        {
            c.HostProtection.NotifySecondaries = false;
            c.HostProtection.PowerOffUps = false;
            c.HostProtection.CommunicationLostOnBatterySeconds = 5;
        });
        var service = new HostProtectionService(_config, new RegistryUpsAccess(_registry), _sessions, _hub, _time, _power,
                                                new ShutdownSafety(() => null), NullLogger.Instance);
        ScriptedDriver driver = _drivers.Drivers["rack1"];
        driver.Publish("OB DISCHRG");
        await service.EvaluateAsync(CancellationToken.None);

        // The device stops answering while on battery (the USB cable, or the UPS itself, died).
        driver.Disconnect("unplugged");
        Assert.False(_registry.Find("rack1")!.Snapshot.IsAvailable);
        await service.EvaluateAsync(CancellationToken.None);
        Assert.Equal(HostProtectionState.Monitoring, service.GetStatus().State);

        _time.Advance(TimeSpan.FromSeconds(6));
        await service.EvaluateAsync(CancellationToken.None);
        Assert.Equal(HostProtectionState.ShuttingDown, service.GetStatus().State);
    }
}
