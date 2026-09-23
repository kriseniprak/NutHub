using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Core.Runtime;

namespace NutHub.Services.HostProtection;

/// <summary>
/// Shuts down the machine NutHub runs on when its UPSes can no longer power it: the job of upsmon in primary mode
/// (clients/upsmon.c). Evaluated every second and whenever a snapshot changes:
/// Monitoring → Pending (grace period, cancelled if the condition clears) → WaitingForSecondaries (FSD set, NUT
/// clients logging out, like HOSTSYNC) → ShuttingDown (UPS power-off, shutdown command; terminal).
/// A dry run ends in DryRunCompleted instead and re-arms once the condition clears.
/// </summary>
public sealed class HostProtectionService : BackgroundService, IHostProtectionService
{
    /// <summary>Recorded as the origin of the FSD and power-off commands in the event log.</summary>
    public static readonly CommandOrigin Origin = new("host-protection");

    private static readonly TimeSpan PowerOffCommandTimeout = TimeSpan.FromSeconds(20);

    private readonly IConfigStore _config;
    private readonly IProtectedUpsAccess _ups;
    private readonly NutSessionRegistry _sessions;
    private readonly EventHub _hub;
    private readonly TimeProvider _time;
    private readonly IHostPowerController _power;
    private readonly ShutdownSafety _safety;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, UpsTracker> _trackers = new(StringComparer.OrdinalIgnoreCase);

    // Durations are measured on the monotonic clock from here: a change of the system time (NTP, an operator)
    // must neither shorten nor stretch a grace period, nor look like lost communication.
    private readonly long _origin;

    // Guarded by _sync. Transitions happen only under the lock; the slow actions (FSD, power-off, the shutdown
    // command) run outside it but inside _gate, so evaluations never overlap.
    private readonly object _sync = new();
    private HostProtectionState _state = HostProtectionState.Disabled;
    private string? _reason;
    private DateTimeOffset? _shutdownAt; // shown to people; _deadline decides
    private TimeSpan? _deadline;
    private IReadOnlyList<string> _critical = [];
    private bool _suppressed;
    private List<string> _fsdUps = [];
    private bool _warnedNoUps;

    public HostProtectionService(IConfigStore config, IUpsRegistry registry, NutSessionRegistry sessions, EventHub hub,
                                 TimeProvider time, ILoggerFactory loggerFactory)
        : this(config, new RegistryUpsAccess(registry), sessions, hub, time,
               new ProcessHostPowerController(loggerFactory.CreateLogger<HostProtectionService>()),
               ShutdownSafety.FromEnvironment(), loggerFactory.CreateLogger<HostProtectionService>())
    {
    }

    internal HostProtectionService(IConfigStore config, IProtectedUpsAccess ups, NutSessionRegistry sessions,
                                   EventHub hub, TimeProvider time, IHostPowerController power, ShutdownSafety safety,
                                   ILogger logger)
    {
        _config = config;
        _ups = ups;
        _sessions = sessions;
        _hub = hub;
        _time = time;
        _power = power;
        _safety = safety;
        _logger = logger;
        _origin = time.GetTimestamp();
    }

    public string DefaultShutdownCommand => HostShutdown.DefaultCommand;

    public HostProtectionStatus GetStatus()
    {
        HostProtectionSettings settings = _config.Current.HostProtection;
        lock (_sync)
        {
            return new HostProtectionStatus(settings.Enabled, _state, _reason, _shutdownAt,
                                            settings.DryRun || _safety.DisabledReason is not null, _critical);
        }
    }

    public bool CancelPending(CommandOrigin origin)
    {
        lock (_sync)
        {
            if (_state != HostProtectionState.Pending)
            {
                return false;
            }

            _state = HostProtectionState.Monitoring;
            _suppressed = true;
            _reason = null;
            _shutdownAt = null;
            _deadline = null;
        }

        string server = _config.Current.Server.Name;
        _logger.LogWarning("The pending shutdown of this machine was cancelled by {Origin}.", origin);
        Publish(UpsEventType.ShutdownCancelled,
                $"The pending shutdown of {server} was cancelled by {origin}. It will not be scheduled again until " +
                "the UPSes are back to normal.", origin.ToString());
        _hub.Publish(new HostProtectionChangedMessage());
        return true;
    }

    /// <summary>One evaluation step. Called every second and on changes; tests call it directly.</summary>
    internal async Task EvaluateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EvaluateCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using ChannelSubscription subscription = _hub.SubscribeChannel(256, m =>
            m is SnapshotChangedMessage or NutClientsChangedMessage or ConfigChangedMessage or UpsRemovedMessage);
        // Never evaluate inside StartAsync: the hosted services after this one (the NUT server that tells the
        // secondaries about FSD, the web panel) must not wait for it.
        await Task.Yield();
        try
        {
            await SafeEvaluateAsync(stoppingToken).ConfigureAwait(false);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), _time);
            Task<bool> tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
            Task<bool> changed = subscription.Reader.WaitToReadAsync(stoppingToken).AsTask();
            while (!stoppingToken.IsCancellationRequested)
            {
                Task<bool> done = await Task.WhenAny(tick, changed).ConfigureAwait(false);
                if (done == tick)
                {
                    if (!await tick.ConfigureAwait(false))
                    {
                        return;
                    }

                    tick = timer.WaitForNextTickAsync(stoppingToken).AsTask();
                }
                else
                {
                    bool open = await changed.ConfigureAwait(false);
                    while (subscription.Reader.TryRead(out _))
                    {
                    }

                    changed = open
                        ? subscription.Reader.WaitToReadAsync(stoppingToken).AsTask()
                        : Task.Delay(Timeout.Infinite, stoppingToken).ContinueWith(_ => false, TaskScheduler.Default);
                }

                await SafeEvaluateAsync(stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task SafeEvaluateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EvaluateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The host protection evaluation failed.");
        }
    }

    private async Task EvaluateCoreAsync(CancellationToken cancellationToken)
    {
        NutHubConfig config = _config.Current;
        HostProtectionSettings settings = config.HostProtection;
        DateTimeOffset now = _time.GetUtcNow();
        TimeSpan elapsed = _time.GetElapsedTime(_origin);
        Assessment assessment = Assess(settings, elapsed);

        var events = new List<(UpsEventType Type, string Message, IReadOnlyDictionary<string, string>? Data)>();
        bool setFsd = false;
        bool checkSecondaries = false;
        bool changed;
        IReadOnlyList<string> fsdUps = [];
        TimeSpan? secondariesDeadline = null;
        lock (_sync)
        {
            HostProtectionState before = _state;
            string? reasonBefore = _reason;
            IReadOnlyList<string> criticalBefore = _critical;
            _critical = assessment.Critical;

            if (!settings.Enabled)
            {
                if (_state is HostProtectionState.Pending or HostProtectionState.WaitingForSecondaries)
                {
                    events.Add((UpsEventType.ShutdownCancelled,
                        $"Host protection was disabled: the shutdown of {config.Server.Name} is cancelled." +
                        (_fsdUps.Count > 0 ? $" FSD stays set on {string.Join(", ", _fsdUps)} until it is cleared." : ""),
                        null));
                }

                if (_state != HostProtectionState.ShuttingDown)
                {
                    ResetToMonitoring();
                    _state = HostProtectionState.Disabled;
                    _suppressed = false;
                }
            }
            else
            {
                if (_state == HostProtectionState.Disabled)
                {
                    _state = HostProtectionState.Monitoring;
                }

                if (_state == HostProtectionState.DryRunCompleted && !assessment.Triggered)
                {
                    _logger.LogInformation("The UPSes are back to normal: host protection is armed again.");
                    ResetToMonitoring();
                }

                if (_state == HostProtectionState.Monitoring)
                {
                    if (_suppressed)
                    {
                        // After a manual cancel, wait for the condition to clear once before arming again.
                        _suppressed = assessment.Triggered;
                        _reason = assessment.Triggered ? "Shutdown cancelled by an operator; waiting for the UPSes to recover." : null;
                    }
                    else if (assessment.Triggered)
                    {
                        int delay = Math.Max(0, settings.ShutdownDelaySeconds);
                        _state = HostProtectionState.Pending;
                        _reason = assessment.Reason;
                        _shutdownAt = now + TimeSpan.FromSeconds(delay);
                        _deadline = elapsed + TimeSpan.FromSeconds(delay);
                        _logger.LogCritical("Power critical: {Reason}. This machine shuts down at {At:u}.", _reason, _shutdownAt);
                        events.Add((UpsEventType.ShutdownPending,
                            string.Create(CultureInfo.InvariantCulture,
                                $"{config.Server.Name} will shut down at {_shutdownAt:yyyy-MM-dd HH:mm:ss} UTC " +
                                $"(in {delay} s): {_reason}."),
                            new Dictionary<string, string>
                            {
                                ["reason"] = _reason!,
                                ["shutdownAt"] = _shutdownAt.Value.ToString("O", CultureInfo.InvariantCulture),
                                ["criticalUps"] = string.Join(",", assessment.Critical),
                            }));
                    }
                    else
                    {
                        _reason = null;
                    }
                }

                if (_state == HostProtectionState.Pending)
                {
                    if (!assessment.Triggered)
                    {
                        _logger.LogWarning("The critical condition cleared during the grace period: shutdown cancelled.");
                        events.Add((UpsEventType.ShutdownCancelled,
                            $"The UPSes recovered before the grace period ended: the shutdown of {config.Server.Name} is cancelled.",
                            null));
                        ResetToMonitoring();
                    }
                    else
                    {
                        _reason = assessment.Reason;
                        if (elapsed >= _deadline)
                        {
                            // A dry run (the setting) must not shut anything down, and FSD would make the NUT clients
                            // shut down for real: it is only logged. The safety switches (debug builds,
                            // NUTHUB_DISABLE_SHUTDOWN) keep the whole sequence, which is what they exist to exercise.
                            if (settings.NotifySecondaries && settings.DryRun)
                            {
                                _logger.LogWarning("Dry run: FSD would now be set on {Ups} so that the NUT clients shut down first.",
                                                   string.Join(", ", assessment.Critical));
                            }
                            else if (settings.NotifySecondaries)
                            {
                                TimeSpan timeout = TimeSpan.FromSeconds(Math.Max(0, settings.SecondariesTimeoutSeconds));
                                _state = HostProtectionState.WaitingForSecondaries;
                                _fsdUps = [.. assessment.Critical];
                                _shutdownAt = now + timeout;
                                _deadline = elapsed + timeout;
                                setFsd = true;
                            }

                            checkSecondaries = true;
                        }
                    }
                }
                else if (_state == HostProtectionState.WaitingForSecondaries)
                {
                    checkSecondaries = true;
                }
            }

            fsdUps = _fsdUps;
            secondariesDeadline = _state == HostProtectionState.WaitingForSecondaries ? _deadline : null;
            changed = before != _state || reasonBefore != _reason || !criticalBefore.SequenceEqual(_critical);
        }

        foreach (var (type, message, data) in events)
        {
            Publish(type, message, "system", data);
        }

        if (changed)
        {
            _hub.Publish(new HostProtectionChangedMessage());
        }

        if (setFsd)
        {
            foreach (string ups in fsdUps)
            {
                if (!_ups.SetForcedShutdown(ups, Origin))
                {
                    _logger.LogWarning("Could not set FSD on {Ups}: it is not in the registry.", ups);
                }
            }

            _logger.LogWarning("FSD set on {Ups}; waiting up to {Timeout} s for the NUT clients to log out.",
                               string.Join(", ", fsdUps), settings.SecondariesTimeoutSeconds);
        }

        if (!checkSecondaries)
        {
            return;
        }

        if (secondariesDeadline is { } deadline)
        {
            int logins = fsdUps.Sum(_sessions.GetLoginCount);
            if (logins > 0 && elapsed < deadline)
            {
                return;
            }

            if (logins > 0)
            {
                _logger.LogWarning("{Logins} NUT client(s) still logged in after {Timeout} s; shutting down anyway.",
                                   logins, settings.SecondariesTimeoutSeconds);
            }
        }

        await ShutdownAsync(config, cancellationToken).ConfigureAwait(false);
    }

    private async Task ShutdownAsync(NutHubConfig config, CancellationToken cancellationToken)
    {
        HostProtectionSettings settings = config.HostProtection;
        string? reason;
        IReadOnlyList<string> critical;
        lock (_sync)
        {
            if (_state is not (HostProtectionState.Pending or HostProtectionState.WaitingForSecondaries))
            {
                return;
            }

            _state = HostProtectionState.ShuttingDown;
            _shutdownAt = _time.GetUtcNow();
            reason = _reason;
            critical = _critical;
        }

        _hub.Publish(new HostProtectionChangedMessage());
        string? dryRun = settings.DryRun ? "dry run enabled in the settings" : _safety.DisabledReason;
        string server = config.Server.Name;
        _logger.LogCritical("Shutting down {Server}: {Reason}.{DryRun}", server, reason,
                            dryRun is null ? "" : $" Dry run ({dryRun}): nothing will be powered off.");
        Publish(UpsEventType.ShutdownStarted,
                dryRun is null
                    ? $"{server} is shutting down: {reason}."
                    : $"Dry run ({dryRun}): {server} would shut down now: {reason}.",
                "system",
                new Dictionary<string, string> { ["reason"] = reason ?? "", ["dryRun"] = dryRun is null ? "false" : "true" });

        if (settings.PowerOffUps)
        {
            foreach (string ups in critical)
            {
                await PowerOffAsync(ups, settings, dryRun, cancellationToken).ConfigureAwait(false);
            }
        }

        IReadOnlyList<ShutdownInvocation> invocations = HostShutdown.Resolve(settings.ShutdownCommand);
        bool executed = false;
        if (dryRun is not null)
        {
            _logger.LogCritical("Dry run ({DryRun}): this machine would now be shut down with: {Command}", dryRun, invocations[0]);
        }
        else
        {
            ShutdownOutcome outcome = await _power.ShutdownAsync(invocations, CancellationToken.None).ConfigureAwait(false);
            executed = outcome.Executed;
            if (outcome.Succeeded)
            {
                _logger.LogCritical("Shutdown command accepted: {Detail}", outcome.Detail);
            }
            else
            {
                _logger.LogCritical("The shutdown of this machine failed: {Detail}. Shut it down by hand.", outcome.Detail);
            }
        }

        if (!executed)
        {
            lock (_sync)
            {
                _state = HostProtectionState.DryRunCompleted;
                _shutdownAt = null;
                _deadline = null;
            }

            _hub.Publish(new HostProtectionChangedMessage());
        }
    }

    private async Task PowerOffAsync(string ups, HostProtectionSettings settings, string? dryRun,
                                     CancellationToken cancellationToken)
    {
        UpsSnapshot? snapshot = _ups.GetSnapshot(ups);
        if (snapshot is null || !snapshot.Has(UpsStatusFlags.OnBattery))
        {
            // Cutting the output of a UPS on line power would not come back by itself.
            _logger.LogWarning("{Ups} is not on battery: it is not powered off.", ups);
            return;
        }

        int delay = Math.Max(0, settings.PowerOffDelaySeconds);
        string delayText = delay.ToString(CultureInfo.InvariantCulture);
        string command = settings.PowerOffCommand.Trim();
        string? parameter = string.Equals(command, "load.off.delay", StringComparison.OrdinalIgnoreCase) ? delayText : null;
        bool writeDelay = snapshot.GetInfo("ups.delay.shutdown").Writable;
        if (dryRun is not null)
        {
            _logger.LogCritical("Dry run ({DryRun}): {Ups} would get {Delay}'{Command}{Parameter}'.", dryRun, ups,
                                writeDelay ? $"ups.delay.shutdown={delay}, then " : "", command,
                                parameter is null ? "" : " " + parameter);
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PowerOffCommandTimeout);
        try
        {
            if (writeDelay)
            {
                CommandResult set = await _ups.SetVariableAsync(ups, "ups.delay.shutdown", delayText, Origin, timeout.Token)
                    .ConfigureAwait(false);
                if (!set.IsSuccess)
                {
                    _logger.LogError("Could not set ups.delay.shutdown on {Ups}: {Result}", ups, set);
                }
            }

            CommandResult result = await _ups.InstantCommandAsync(ups, command, parameter, Origin, timeout.Token)
                .ConfigureAwait(false);
            if (result.IsSuccess)
            {
                _logger.LogCritical("{Ups} accepted '{Command}': it cuts its output in about {Delay} s.", ups, command, delay);
            }
            else
            {
                _logger.LogError("{Ups} refused '{Command}': {Result}", ups, command, result);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogError("{Ups} did not answer the power-off command in time.", ups);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "The power-off command for {Ups} failed.", ups);
        }
    }

    private Assessment Assess(HostProtectionSettings settings, TimeSpan now)
    {
        List<string> monitored = settings.Ups
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (string stale in _trackers.Keys.Where(k => !monitored.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
        {
            _trackers.Remove(stale);
        }

        var critical = new List<string>();
        var reasons = new List<string>();
        foreach (string name in monitored)
        {
            if (!_trackers.TryGetValue(name, out UpsTracker? tracker))
            {
                tracker = new UpsTracker();
                _trackers[name] = tracker;
            }

            UpsSnapshot? snapshot = _ups.GetSnapshot(name);
            tracker.Observe(snapshot, now);
            if (CriticalRules.Evaluate(settings, name, snapshot, tracker, now) is { } why)
            {
                critical.Add(name);
                reasons.Add(why);
            }
        }

        if (monitored.Count == 0)
        {
            if (settings.Enabled && !_warnedNoUps)
            {
                _warnedNoUps = true;
                _logger.LogWarning("Host protection is enabled but no UPS is selected: this machine is not protected.");
            }

            return new Assessment(critical, false, null);
        }

        _warnedNoUps = false;
        // The validator keeps MinimumSupplies within 1..count; a hand-edited file is clamped rather than turned into
        // an immediate shutdown.
        int minimum = Math.Clamp(settings.MinimumSupplies, 1, monitored.Count);
        int healthy = monitored.Count - critical.Count;
        bool triggered = healthy < minimum;
        string? reason = null;
        if (triggered)
        {
            reason = string.Join("; ", reasons);
            if (monitored.Count > 1)
            {
                reason += $" ({healthy} of {monitored.Count} UPSes healthy, {minimum} needed)";
            }
        }

        return new Assessment(critical, triggered, reason);
    }

    private void ResetToMonitoring()
    {
        _state = HostProtectionState.Monitoring;
        _reason = null;
        _shutdownAt = null;
        _deadline = null;
        _fsdUps = [];
    }

    private void Publish(UpsEventType type, string message, string actor, IReadOnlyDictionary<string, string>? data = null) =>
        _hub.Publish(new UpsEventMessage(UpsEvent.Create(type, _time.GetUtcNow(), null, message, actor, data)));

    private sealed record Assessment(IReadOnlyList<string> Critical, bool Triggered, string? Reason);
}
