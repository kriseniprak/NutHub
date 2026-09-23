using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NutHub.Core.Configuration;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Core.Security;

namespace NutHub.Core.Runtime;

/// <summary>Controls the drivers from outside (web panel).</summary>
public interface IDriverManager
{
    /// <summary>Stops and restarts the driver of a UPS; false when there is no such enabled UPS.</summary>
    Task<bool> RestartAsync(string upsName, CancellationToken cancellationToken = default);
}

/// <summary>
/// Starts one driver per enabled UPS, restarts drivers that fail (with a growing delay), and follows configuration
/// changes: added, removed and modified UPSes are applied without restarting NutHub.
/// </summary>
public sealed class DriverManager : BackgroundService, IDriverManager
{
    private static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
    ];

    private readonly IConfigStore _config;
    private readonly IDriverCatalog _catalog;
    private readonly UpsRegistry _registry;
    private readonly ISecretProtector _secrets;
    private readonly ILoggerFactory _loggerFactory;
    private readonly TimeProvider _time;
    private readonly IServiceProvider _services;
    private readonly ILogger<DriverManager> _logger;
    private readonly SemaphoreSlim _reconcileGate = new(1, 1);
    private readonly Dictionary<UpsUnit, Runner> _runners = [];
    private CancellationToken _stopping;

    public DriverManager(IConfigStore config, IDriverCatalog catalog, UpsRegistry registry, ISecretProtector secrets,
                         ILoggerFactory loggerFactory, TimeProvider time, IServiceProvider services)
    {
        _config = config;
        _catalog = catalog;
        _registry = registry;
        _secrets = secrets;
        _loggerFactory = loggerFactory;
        _time = time;
        _services = services;
        _logger = loggerFactory.CreateLogger<DriverManager>();
    }

    /// <summary>How long a driver may take to stop before it is abandoned.</summary>
    internal TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(15);

    public async Task<bool> RestartAsync(string upsName, CancellationToken cancellationToken = default)
    {
        await _reconcileGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            UpsUnit? unit = _registry.Find(upsName);
            if (unit is null || !unit.Config.Enabled)
            {
                return false;
            }

            _logger.LogInformation("Restarting the driver of {Ups}.", unit.Name);
            await StopRunnerAsync(unit).ConfigureAwait(false);
            StartRunner(unit, clearData: true);
            return true;
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;

        // Subscribe first: a change made while the initial set is starting must not be missed. Reconciling the
        // same configuration twice is harmless.
        _config.Changed += OnConfigChanged;
        await ReconcileAsync().ConfigureAwait(false);

        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    _registry.Tick();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "UPS housekeeping failed.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _config.Changed -= OnConfigChanged;
            await _reconcileGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await Task.WhenAll(_runners.Keys.ToList().Select(StopRunnerAsync)).ConfigureAwait(false);
            }
            finally
            {
                _reconcileGate.Release();
            }
        }
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await ReconcileAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Applying the new UPS configuration failed.");
            }
        });
    }

    private async Task ReconcileAsync()
    {
        await _reconcileGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // Read the configuration only once inside the gate: two quick changes may reach this point in either
            // order, and the latest configuration is the one to apply.
            NutHubConfig config = _config.Current;
            if (_stopping.IsCancellationRequested)
            {
                return;
            }

            // Removed UPSes.
            foreach (UpsUnit unit in _registry.Units.ToList())
            {
                if (!config.Ups.Any(u => string.Equals(u.Name, unit.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    _logger.LogInformation("UPS {Ups} removed.", unit.Name);
                    await StopRunnerAsync(unit).ConfigureAwait(false);
                    _registry.Remove(unit);
                }
            }

            // Added and changed UPSes.
            foreach (UpsConfig upsConfig in config.Ups)
            {
                UpsUnit? unit = _registry.Find(upsConfig.Name);
                if (unit is null)
                {
                    _logger.LogInformation("UPS {Ups} added (driver {Driver}).", upsConfig.Name, upsConfig.Driver);
                    unit = _registry.Add(upsConfig);
                    if (upsConfig.Enabled)
                    {
                        StartRunner(unit, clearData: true);
                    }

                    continue;
                }

                UpsConfig old = unit.Config;
                if (ReferenceEquals(old, upsConfig))
                {
                    continue;
                }

                bool restart = RequiresRestart(old, upsConfig) || !string.Equals(old.Name, upsConfig.Name, StringComparison.Ordinal);
                if (restart)
                {
                    await StopRunnerAsync(unit).ConfigureAwait(false);
                    unit.UpdateConfig(upsConfig);
                    if (upsConfig.Enabled)
                    {
                        _logger.LogInformation("UPS {Ups} reconfigured; restarting its driver.", upsConfig.Name);
                        StartRunner(unit, clearData: true);
                    }
                    else
                    {
                        _logger.LogInformation("UPS {Ups} disabled.", upsConfig.Name);
                        unit.SetDriverState(DriverState.Disabled, null);
                    }
                }
                else
                {
                    unit.UpdateConfig(upsConfig);
                }
            }

            _registry.Reorder(config.Ups.Select(u => u.Name).ToList());
        }
        finally
        {
            _reconcileGate.Release();
        }
    }

    private static bool RequiresRestart(UpsConfig a, UpsConfig b) =>
        a.Enabled != b.Enabled ||
        !string.Equals(a.Driver, b.Driver, StringComparison.OrdinalIgnoreCase) ||
        a.PollIntervalSeconds != b.PollIntervalSeconds ||
        a.Options.Count != b.Options.Count ||
        a.Options.Any(kv => !b.Options.TryGetValue(kv.Key, out string? v) || v != kv.Value);

    private void StartRunner(UpsUnit unit, bool clearData)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(_stopping);
        unit.BeginRun(clearData);
        var runner = new Runner(cts);
        _runners[unit] = runner;
        runner.Task = Task.Run(() => RunLoopAsync(unit, cts.Token));
    }

    private async Task StopRunnerAsync(UpsUnit unit)
    {
        if (!_runners.Remove(unit, out Runner? runner))
        {
            return;
        }

        await runner.Cts.CancelAsync().ConfigureAwait(false);
        try
        {
            await runner.Task!.WaitAsync(StopTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("The driver of {Ups} did not stop within {Seconds} s; abandoning it.", unit.Name,
                               StopTimeout.TotalSeconds);
        }
        catch (Exception ex) when (ex is OperationCanceledException)
        {
        }
        finally
        {
            runner.Cts.Dispose();
        }
    }

    private async Task RunLoopAsync(UpsUnit unit, CancellationToken ct)
    {
        int failures = 0;
        ILogger driverLogger = _loggerFactory.CreateLogger("NutHub.Driver." + unit.Name);

        while (!ct.IsCancellationRequested)
        {
            UpsConfig config = unit.Config;
            IUpsDriverFactory? factory = _catalog.Find(config.Driver);
            if (factory is null)
            {
                unit.SetDriverState(DriverState.Failed, $"Unknown driver '{config.Driver}'.");
                return;
            }

            if (!_catalog.IsSupportedHere(factory))
            {
                unit.SetDriverState(DriverState.Failed,
                                    $"The driver '{factory.DisplayName}' does not work on this operating system.");
                return;
            }

            var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var (key, value) in config.Options)
                {
                    options[key] = _secrets.Unprotect(value) ?? "";
                }
            }
            catch (Exception ex)
            {
                unit.SetDriverState(DriverState.Failed, "A secret option cannot be decrypted: " + ex.Message);
                return;
            }

            TimeSpan poll = TimeSpan.FromSeconds(Math.Clamp(config.PollIntervalSeconds, 0.5, 300));
            IUpsDriver? driver = null;
            DateTimeOffset started = _time.GetUtcNow();
            try
            {
                driver = factory.Create(new DriverCreateContext
                {
                    UpsName = config.Name,
                    Options = options,
                    PollInterval = poll,
                    LoggerFactory = _loggerFactory,
                    TimeProvider = _time,
                    Services = _services,
                });

                // A creation that took so long that the run was stopped (and perhaps replaced) meanwhile.
                ct.ThrowIfCancellationRequested();
                unit.AttachDriver(driver);
                var context = new DriverContext(unit, driverLogger, _time, poll, ct);
                await driver.RunAsync(context, ct).ConfigureAwait(false);
                if (!ct.IsCancellationRequested)
                {
                    throw new InvalidOperationException("The driver stopped unexpectedly.");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ct.IsCancellationRequested)
            {
                // Stopped on purpose: a driver whose port is closed under it may end with an I/O error, which says
                // nothing about the device.
                driverLogger.LogDebug(ex, "{Ups}: the driver ended with an error while stopping.", config.Name);
                return;
            }
            catch (DriverConfigurationException ex)
            {
                driverLogger.LogError("{Ups}: invalid configuration: {Message}", config.Name, ex.Message);
                unit.SetDriverState(DriverState.Failed, ex.Message);
                return;
            }
            catch (Exception ex)
            {
                failures++;
                driverLogger.LogError(ex, "{Ups}: the driver failed.", config.Name);
                unit.SetDriverState(DriverState.Failed, ex.Message);
            }
            finally
            {
                // Only this run's driver: a run abandoned because it did not stop must not detach its successor.
                unit.DetachDriver(driver);
                if (driver is not null)
                {
                    try
                    {
                        await driver.DisposeAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        driverLogger.LogWarning(ex, "{Ups}: disposing the driver failed.", config.Name);
                    }
                }
            }

            // A run that worked for a while starts the back-off again from the shortest delay.
            if (unit.ConnectedThisRun && _time.GetUtcNow() - started > TimeSpan.FromMinutes(1))
            {
                failures = 1;
            }

            TimeSpan delay = Backoff[Math.Clamp(failures - 1, 0, Backoff.Length - 1)];
            driverLogger.LogInformation("{Ups}: restarting the driver in {Delay} s.", config.Name, delay.TotalSeconds);
            try
            {
                await Task.Delay(delay, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            unit.BeginRun(clearData: false);
        }
    }

    private sealed class Runner(CancellationTokenSource cts)
    {
        public CancellationTokenSource Cts { get; } = cts;

        public Task? Task { get; set; }
    }

    /// <summary>
    /// What a driver reports about its UPS. Once its run is stopped, the driver no longer speaks for the UPS: what it
    /// still reports while stopping, or after it was abandoned, is ignored.
    /// </summary>
    private sealed class DriverContext(UpsUnit unit, ILogger logger, TimeProvider time, TimeSpan poll,
                                       CancellationToken stopped) : IDriverContext
    {
        public string UpsName => unit.Name;

        public ILogger Logger => logger;

        public TimeProvider TimeProvider => time;

        public TimeSpan PollInterval => poll;

        public void ReportConnecting(string? detail = null)
        {
            if (!stopped.IsCancellationRequested)
            {
                unit.OnConnecting(detail);
            }
        }

        public void ReportDisconnected(string reason)
        {
            if (!stopped.IsCancellationRequested)
            {
                logger.LogWarning("{Ups}: {Reason}", unit.Name, reason);
                unit.OnDisconnected(reason);
            }
        }

        public void Publish(DriverUpdate update)
        {
            if (!stopped.IsCancellationRequested)
            {
                unit.OnPublish(update);
            }
        }
    }
}
