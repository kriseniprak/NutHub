using System.Collections.Immutable;
using Microsoft.Extensions.Logging;
using NutHub.Core.Catalog;
using NutHub.Core.Configuration;
using NutHub.Core.Drivers;
using NutHub.Core.Model;

namespace NutHub.Core.Runtime;

/// <summary>
/// One configured UPS at runtime: its current snapshot, the driver serving it, and the operations clients can
/// perform (instant commands, variable writes, forced shutdown). Thread-safe.
/// </summary>
public sealed class UpsUnit
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan NoCommunicationDelay = TimeSpan.FromSeconds(60);

    private readonly object _sync = new();
    private readonly EventHub _hub;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Func<NutServerSettings> _serverSettings;

    private UpsConfig _config;
    private UpsSnapshot _snapshot;

    // What the driver reported last, before overrides and NutHub's own additions.
    private IReadOnlyDictionary<string, string> _driverVars = UpsSnapshot.NoVariables;
    private IReadOnlyDictionary<string, VariableInfo> _driverInfo = UpsSnapshot.NoVariableInfo;
    private IReadOnlyCollection<string> _driverCommands = [];
    private DriverState _driverState;
    private string? _driverMessage;
    private DateTimeOffset? _lastUpdate;

    // Monotonic time stamps (TimeProvider.GetTimestamp) for the durations: a wall-clock jump must neither make fresh
    // data stale nor keep a hung driver's data fresh, and must not clear an FSD early or report NOCOMM early.
    private long? _lastUpdateTicks;
    private long? _fsdOnlineSinceTicks;
    private long _runStartedTicks;
    private IUpsDriver? _driver;

    private bool _fsd;
    private bool _connectedThisRun;
    private bool _everConnected;
    private bool _noCommReported;
    private bool _commLostReported;
    private UpsStatusFlags? _lastEvaluatedFlags;
    private string? _lastFailureMessage;
    private long _sequence;

    internal UpsUnit(UpsConfig config, EventHub hub, TimeProvider time, ILogger logger,
                     Func<NutServerSettings> serverSettings)
    {
        _config = config;
        _hub = hub;
        _time = time;
        _logger = logger;
        _serverSettings = serverSettings;
        _driverState = config.Enabled ? DriverState.Starting : DriverState.Disabled;
        _runStartedTicks = time.GetTimestamp();
        _snapshot = Build(time.GetUtcNow(), out _);
    }

    public string Name => Volatile.Read(ref _config).Name;

    public UpsConfig Config => Volatile.Read(ref _config);

    /// <summary>The current state; replaced atomically on every change.</summary>
    public UpsSnapshot Snapshot => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Executes an instant command on the device. Checks that the UPS supports it and that the driver is
    /// connected; records the outcome in the event log. Permission checks are the caller's job.
    /// </summary>
    public async Task<CommandResult> InstantCommandAsync(string command, string? parameter, CommandOrigin origin,
                                                         CancellationToken cancellationToken = default)
    {
        UpsSnapshot snapshot = Snapshot;
        string? canonical = snapshot.Commands.FirstOrDefault(c => string.Equals(c, command, StringComparison.OrdinalIgnoreCase));
        if (canonical is null)
        {
            return CommandResult.NotSupported($"The UPS {Name} does not support the command '{command}'.");
        }

        IUpsDriver? driver = Volatile.Read(ref _driver);
        if (driver is null || snapshot.DriverState != DriverState.Connected)
        {
            return CommandResult.NotConnected($"The driver of {Name} is not connected to the device.");
        }

        CommandResult result = await RunDriverOperation(
            ct => driver.InstantCommandAsync(canonical, string.IsNullOrEmpty(parameter) ? null : parameter, ct),
            cancellationToken).ConfigureAwait(false);

        string what = parameter is null ? canonical : $"{canonical} {parameter}";
        var data = new Dictionary<string, string> { ["command"] = canonical };
        if (parameter is not null)
        {
            data["parameter"] = parameter;
        }

        if (result.IsSuccess)
        {
            _logger.LogInformation("{Origin} ran '{Command}' on {Ups}.", origin, what, Name);
            Raise(UpsEvent.Create(UpsEventType.CommandExecuted, _time.GetUtcNow(), Name,
                                  $"Command '{what}' executed on {Name}.", origin.ToString(), data));
        }
        else
        {
            _logger.LogWarning("{Origin} ran '{Command}' on {Ups}: {Result}", origin, what, Name, result);
            data["error"] = result.ToString();
            Raise(UpsEvent.Create(UpsEventType.CommandFailed, _time.GetUtcNow(), Name,
                                  $"Command '{what}' failed on {Name}: {result.Message ?? result.Status.ToString()}",
                                  origin.ToString(), data));
        }

        return result;
    }

    /// <summary>
    /// Writes a variable after checking it exists, is writable and accepts the value (enum, range, length,
    /// number). Permission checks are the caller's job.
    /// </summary>
    public async Task<CommandResult> SetVariableAsync(string name, string value, CommandOrigin origin,
                                                      CancellationToken cancellationToken = default)
    {
        UpsSnapshot snapshot = Snapshot;
        CommandResult? check = CheckWritable(snapshot, name, value);
        if (check is not null)
        {
            return check;
        }

        IUpsDriver? driver = Volatile.Read(ref _driver);
        if (driver is null || snapshot.DriverState != DriverState.Connected)
        {
            return CommandResult.NotConnected($"The driver of {Name} is not connected to the device.");
        }

        CommandResult result = await RunDriverOperation(ct => driver.SetVariableAsync(name, value, ct), cancellationToken)
            .ConfigureAwait(false);

        var data = new Dictionary<string, string> { ["variable"] = name, ["value"] = value };
        if (result.IsSuccess)
        {
            _logger.LogInformation("{Origin} set {Variable}={Value} on {Ups}.", origin, name, value, Name);
            Raise(UpsEvent.Create(UpsEventType.VariableChanged, _time.GetUtcNow(), Name,
                                  $"{name} set to '{value}' on {Name}.", origin.ToString(), data));
        }
        else
        {
            _logger.LogWarning("{Origin} set {Variable}={Value} on {Ups}: {Result}", origin, name, value, Name, result);
            data["error"] = result.ToString();
            Raise(UpsEvent.Create(UpsEventType.CommandFailed, _time.GetUtcNow(), Name,
                                  $"Setting {name} to '{value}' failed on {Name}: {result.Message ?? result.Status.ToString()}",
                                  origin.ToString(), data));
        }

        return result;
    }

    /// <summary>
    /// Checks a variable write without performing it; null when it would be accepted.
    /// </summary>
    public static CommandResult? CheckWritable(UpsSnapshot snapshot, string name, string value)
    {
        if (!snapshot.Variables.ContainsKey(name))
        {
            return CommandResult.NotSupported($"The UPS {snapshot.Name} has no variable '{name}'.");
        }

        VariableInfo info = snapshot.GetInfo(name);
        if (!info.Writable)
        {
            return new CommandResult(CommandStatus.ReadOnly, $"{name} is read-only.");
        }

        if (info.EnumValues.Count > 0)
        {
            return info.EnumValues.Contains(value, StringComparer.Ordinal)
                ? null
                : CommandResult.Invalid($"'{value}' is not one of: {string.Join(", ", info.EnumValues)}.");
        }

        if (info.Ranges.Count > 0)
        {
            if (!NutFormat.TryParseNumber(value, out double number) || !info.Ranges.Any(r => r.Contains(number)))
            {
                return CommandResult.Invalid(
                    $"'{value}' is outside the allowed range ({string.Join(", ", info.Ranges.Select(r => $"{r.Min}-{r.Max}"))}).");
            }

            return null;
        }

        if (info.Type == VariableType.String)
        {
            return info.MaxLength > 0 && value.Length > info.MaxLength
                ? new CommandResult(CommandStatus.TooLong, $"At most {info.MaxLength} characters.")
                : null;
        }

        return NutFormat.TryParseNumber(value, out _) ? null : CommandResult.Invalid($"'{value}' is not a number.");
    }

    /// <summary>
    /// Sets the forced-shutdown flag (NUT "FSD"): clients see FSD in ups.status and shut down.
    /// </summary>
    public void SetForcedShutdown(CommandOrigin origin)
    {
        lock (_sync)
        {
            if (_fsd)
            {
                return;
            }

            _fsd = true;
            _fsdOnlineSinceTicks = null;
            List<HubMessage> messages = Rebuild();
            _logger.LogWarning("Forced shutdown set on {Ups} by {Origin}.", Name, origin);
            PublishAll(messages);
            Raise(UpsEvent.Create(UpsEventType.ForcedShutdown, _time.GetUtcNow(), Name,
                                  $"Forced shutdown (FSD) set on {Name}: every client of this UPS must shut down.",
                                  origin.ToString()));
        }
    }

    /// <summary>Clears the forced-shutdown flag. Returns false when it was not set.</summary>
    public bool ClearForcedShutdown(CommandOrigin origin)
    {
        lock (_sync)
        {
            if (!_fsd)
            {
                return false;
            }

            _fsd = false;
            _fsdOnlineSinceTicks = null;
            List<HubMessage> messages = Rebuild();
            _logger.LogInformation("Forced shutdown cleared on {Ups} by {Origin}.", Name, origin);
            PublishAll(messages);
            Raise(UpsEvent.Create(UpsEventType.ForcedShutdownCleared, _time.GetUtcNow(), Name,
                                  $"Forced shutdown (FSD) cleared on {Name}.", origin.ToString()));
        }

        return true;
    }

    // ----- Called by the driver manager -----

    internal void UpdateConfig(UpsConfig config)
    {
        lock (_sync)
        {
            Volatile.Write(ref _config, config);
            if (!config.Enabled)
            {
                _driverState = DriverState.Disabled;
                _driverMessage = null;
                ClearDriverData();
            }

            PublishAll(Rebuild());
        }
    }

    /// <summary>A new driver run begins (after start, restart or configuration change).</summary>
    internal void BeginRun(bool clearData)
    {
        lock (_sync)
        {
            _driverState = DriverState.Starting;
            _driverMessage = null;
            _connectedThisRun = false;
            if (clearData)
            {
                // Retries after a failure keep counting from the first start, so NOCOMM is reported on time.
                _runStartedTicks = _time.GetTimestamp();
                _lastFailureMessage = null;
                ClearDriverData();
                _everConnected = false;

                // A loss already reported stays open, so that its end is reported: restarting the driver or changing
                // its settings is often how the loss gets fixed. NOCOMM itself counts again from this start.
                _commLostReported |= _noCommReported;
                _noCommReported = false;
                _lastEvaluatedFlags = null;
            }

            PublishAll(Rebuild());
        }
    }

    internal bool ConnectedThisRun
    {
        get
        {
            lock (_sync)
            {
                return _connectedThisRun;
            }
        }
    }

    internal void AttachDriver(IUpsDriver? driver) => Volatile.Write(ref _driver, driver);

    /// <summary>Detaches the driver of a run that ended, unless a newer run attached its own meanwhile.</summary>
    internal void DetachDriver(IUpsDriver? driver) => Interlocked.CompareExchange(ref _driver, null, driver);

    internal void SetDriverState(DriverState state, string? message)
    {
        lock (_sync)
        {
            _driverState = state;
            _driverMessage = message;
            if (state is DriverState.Disabled)
            {
                ClearDriverData();
            }

            PublishAll(Rebuild());
        }
    }

    internal void OnConnecting(string? detail)
    {
        lock (_sync)
        {
            _driverState = DriverState.Connecting;
            _driverMessage = detail;
            PublishAll(Rebuild());
        }
    }

    internal void OnDisconnected(string reason)
    {
        lock (_sync)
        {
            _driverState = DriverState.Disconnected;
            _driverMessage = reason;
            PublishAll(Rebuild());
        }
    }

    internal void OnPublish(DriverUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_sync)
        {
            _driverVars = Sanitize(update.Variables);
            if (update.VariableInfo is not null)
            {
                _driverInfo = update.VariableInfo.ToImmutableDictionary(StringComparer.Ordinal);
            }

            if (update.Commands is not null)
            {
                _driverCommands = update.Commands.Where(NutFormat.IsValidVariableName).ToArray();
            }

            bool firstConnection = !_connectedThisRun;
            _lastFailureMessage = null;
            _driverState = DriverState.Connected;
            _driverMessage = null;
            _lastUpdate = _time.GetUtcNow();
            _lastUpdateTicks = _time.GetTimestamp();
            _connectedThisRun = true;
            _everConnected = true;
            List<HubMessage> messages = Rebuild();
            if (firstConnection)
            {
                _logger.LogInformation("{Ups}: driver {Driver} is connected to the device.", Name, Config.Driver);
            }

            PublishAll(messages);
        }
    }

    /// <summary>Periodic housekeeping: data ageing, NOCOMM, automatic FSD clearing.</summary>
    internal void Tick()
    {
        DateTimeOffset now = _time.GetUtcNow();
        bool clearFsd = false;
        lock (_sync)
        {
            List<HubMessage> messages = Rebuild();
            UpsSnapshot snapshot = _snapshot;

            int clearDelay = _serverSettings().FsdClearDelaySeconds;
            if (_fsd && clearDelay > 0 && snapshot.IsAvailable && snapshot.Has(UpsStatusFlags.Online) &&
                !snapshot.Has(UpsStatusFlags.OnBattery) && !snapshot.Has(UpsStatusFlags.LowBattery))
            {
                _fsdOnlineSinceTicks ??= _time.GetTimestamp();
                clearFsd = _time.GetElapsedTime(_fsdOnlineSinceTicks.Value) >= TimeSpan.FromSeconds(clearDelay);
            }
            else
            {
                _fsdOnlineSinceTicks = null;
            }

            bool reportNoComm = false;
            if (_config.Enabled && !_everConnected && !_noCommReported && _time.GetElapsedTime(_runStartedTicks) >= NoCommunicationDelay)
            {
                _noCommReported = true;
                reportNoComm = true;
            }

            PublishAll(messages);
            if (reportNoComm)
            {
                Raise(UpsEvent.Create(UpsEventType.NoCommunication, now, Name,
                                      $"No communication with {Name} since the driver started" +
                                      (snapshot.DriverMessage is { } m ? $": {m}" : "."), "system"));
            }
        }

        if (clearFsd)
        {
            ClearForcedShutdown(new CommandOrigin("system", null, null));
        }
    }

    internal void RaiseDriverEvent(UpsEventType type, string message) =>
        Raise(UpsEvent.Create(type, _time.GetUtcNow(), Name, message, "system"));

    // ----- Snapshot construction -----

    private void ClearDriverData()
    {
        _driverVars = UpsSnapshot.NoVariables;
        _driverInfo = UpsSnapshot.NoVariableInfo;
        _driverCommands = [];
        _lastUpdate = null;
        _lastUpdateTicks = null;
    }

    /// <summary>Rebuilds the snapshot; returns the hub messages to publish.</summary>
    private List<HubMessage> Rebuild()
    {
        var messages = new List<HubMessage>(2);
        UpsSnapshot previous = _snapshot;
        UpsSnapshot next = Build(_time.GetUtcNow(), out bool _);

        bool changed = !SameContent(previous, next);
        if (changed)
        {
            next = CloneWithSequence(next, ++_sequence);
        }
        else
        {
            next = CloneWithSequence(next, previous.Sequence);
        }

        Volatile.Write(ref _snapshot, next);
        if (changed)
        {
            messages.Add(new SnapshotChangedMessage(previous, next));
            foreach (UpsEvent e in DetectEvents(previous, next))
            {
                messages.Add(new UpsEventMessage(e));
            }
        }

        return messages;
    }

    private UpsSnapshot Build(DateTimeOffset now, out bool hasStatus)
    {
        UpsConfig config = _config;
        var vars = new Dictionary<string, string>(_driverVars, StringComparer.Ordinal);

        foreach (var (key, value) in config.Overrides)
        {
            if (NutFormat.IsValidVariableName(key))
            {
                vars[key] = SingleLine(value);
            }
        }

        Mirror(vars, "ups.mfr", "device.mfr");
        Mirror(vars, "ups.model", "device.model");
        Mirror(vars, "ups.serial", "device.serial");
        if (vars.Count > 0)
        {
            vars.TryAdd("device.type", "ups");
            vars["driver.name"] = config.Driver;
            vars["driver.version"] = NutHubInfo.Version;
            vars["driver.parameter.pollinterval"] = NutFormat.Number(config.PollIntervalSeconds);
            foreach (var (key, value) in config.Options)
            {
                if (!value.StartsWith("enc:", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(value) &&
                    NutFormat.IsValidVariableName(key))
                {
                    vars["driver.parameter." + key.ToLowerInvariant()] = SingleLine(value);
                }
            }
        }

        // Effective status: device flags, NutHub's own low-battery thresholds, then FSD in front.
        hasStatus = vars.TryGetValue("ups.status", out string? rawStatus);
        UpsStatusFlags deviceFlags = UpsStatus.Parse(rawStatus);
        UpsStatusFlags add = UpsStatusFlags.None;
        UpsStatusFlags remove = UpsStatusFlags.None;
        LowBatteryPolicy lb = config.LowBattery;
        if (lb.IgnoreDeviceFlag)
        {
            remove |= UpsStatusFlags.LowBattery;
        }

        if ((deviceFlags & UpsStatusFlags.OnBattery) != 0)
        {
            double? charge = NutFormat.ParseNumberOrNull(vars.GetValueOrDefault("battery.charge"));
            double? runtime = NutFormat.ParseNumberOrNull(vars.GetValueOrDefault("battery.runtime"));
            if ((lb.ChargePercent is { } c && charge is { } ch && ch <= c) ||
                (lb.RuntimeSeconds is { } r && runtime is { } rt && rt <= r))
            {
                add |= UpsStatusFlags.LowBattery;
                remove &= ~UpsStatusFlags.LowBattery;
            }
        }

        if (_fsd)
        {
            add |= UpsStatusFlags.ForcedShutdown;
        }

        if (hasStatus || add != UpsStatusFlags.None)
        {
            vars["ups.status"] = UpsStatus.Combine(rawStatus, add, remove);
        }

        UpsStatusFlags flags = UpsStatus.Parse(vars.GetValueOrDefault("ups.status"));

        var info = _driverInfo
            .Where(kv => !config.Overrides.ContainsKey(kv.Key) && vars.ContainsKey(kv.Key))
            .ToImmutableDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        return new UpsSnapshot
        {
            Name = config.Name,
            Description = string.IsNullOrWhiteSpace(config.Description) ? null : config.Description,
            DriverId = config.Driver,
            DriverState = _driverState,
            DriverMessage = _driverMessage,
            Availability = ComputeAvailability(now),
            ForcedShutdown = _fsd,
            Status = flags,
            Variables = vars.ToImmutableSortedDictionary(StringComparer.Ordinal),
            VariableInfo = info,
            Commands = [.. _driverCommands.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal)],
            LastUpdate = _lastUpdate,
            Timestamp = now,
        };
    }

    private DataAvailability ComputeAvailability(DateTimeOffset now)
    {
        switch (_driverState)
        {
            case DriverState.Connected:
                return _lastUpdateTicks is { } last &&
                       _time.GetElapsedTime(last) <= TimeSpan.FromSeconds(MaxDataAgeSeconds())
                    ? DataAvailability.Available
                    : DataAvailability.Stale;
            case DriverState.Connecting:
            case DriverState.Disconnected:
                return DataAvailability.Stale;
            default:
                return DataAvailability.DriverNotConnected;
        }
    }

    /// <summary>
    /// How old the data may get before it is stale: MAXAGE, but at least two poll intervals, since a driver publishes
    /// once per poll and a poll interval longer than MAXAGE would otherwise make healthy data stale between polls.
    /// </summary>
    private double MaxDataAgeSeconds() =>
        Math.Max(Math.Max(1, _serverSettings().MaxAgeSeconds), 2 * _config.PollIntervalSeconds);

    private static void Mirror(Dictionary<string, string> vars, string a, string b)
    {
        if (vars.TryGetValue(a, out string? va))
        {
            vars.TryAdd(b, va);
        }
        else if (vars.TryGetValue(b, out string? vb))
        {
            vars.TryAdd(a, vb);
        }
    }

    private static IReadOnlyDictionary<string, string> Sanitize(IReadOnlyDictionary<string, string> vars)
    {
        var result = new Dictionary<string, string>(vars.Count, StringComparer.Ordinal);
        foreach (var (key, value) in vars)
        {
            if (!NutFormat.IsValidVariableName(key) || value is null)
            {
                continue;
            }

            // driver.* belongs to NutHub, except the data / internal versions a driver may describe.
            if (key.StartsWith("driver.", StringComparison.Ordinal) &&
                key is not ("driver.version.data" or "driver.version.internal" or "driver.version.usb"))
            {
                continue;
            }

            result[key] = SingleLine(value);
        }

        return result;
    }

    /// <summary>NUT values are single-line: strip the control characters a device or a configuration might hold.</summary>
    private static string SingleLine(string value) =>
        value.Any(char.IsControl) ? new string(value.Where(c => !char.IsControl(c)).ToArray()).Trim() : value.Trim();

    private static bool SameContent(UpsSnapshot a, UpsSnapshot b) =>
        a.Name == b.Name &&
        a.Description == b.Description &&
        a.DriverId == b.DriverId &&
        a.DriverState == b.DriverState &&
        a.DriverMessage == b.DriverMessage &&
        a.Availability == b.Availability &&
        a.ForcedShutdown == b.ForcedShutdown &&
        a.Variables.Count == b.Variables.Count &&
        a.Variables.All(kv => b.Variables.TryGetValue(kv.Key, out string? v) && v == kv.Value) &&
        a.VariableInfo.Count == b.VariableInfo.Count &&
        a.VariableInfo.All(kv => b.VariableInfo.TryGetValue(kv.Key, out VariableInfo? v) && v.Equals(kv.Value)) &&
        a.Commands.SequenceEqual(b.Commands);

    private static UpsSnapshot CloneWithSequence(UpsSnapshot s, long sequence) => new()
    {
        Name = s.Name,
        Description = s.Description,
        DriverId = s.DriverId,
        DriverState = s.DriverState,
        DriverMessage = s.DriverMessage,
        Availability = s.Availability,
        ForcedShutdown = s.ForcedShutdown,
        Status = s.Status,
        Variables = s.Variables,
        VariableInfo = s.VariableInfo,
        Commands = s.Commands,
        LastUpdate = s.LastUpdate,
        Timestamp = s.Timestamp,
        Sequence = sequence,
    };

    // ----- Event detection -----

    private static readonly (UpsStatusFlags Flag, UpsEventType Set, UpsEventType Cleared, string SetText, string ClearedText)[] FlagEvents =
    [
        (UpsStatusFlags.LowBattery, UpsEventType.LowBattery, UpsEventType.LowBatteryCleared, "battery is low", "battery is no longer low"),
        (UpsStatusFlags.ReplaceBattery, UpsEventType.ReplaceBattery, UpsEventType.ReplaceBatteryCleared, "battery needs to be replaced", "battery no longer needs replacement"),
        (UpsStatusFlags.Overload, UpsEventType.Overload, UpsEventType.OverloadCleared, "is overloaded", "is no longer overloaded"),
        (UpsStatusFlags.Bypass, UpsEventType.Bypass, UpsEventType.BypassCleared, "is on bypass: the load is not protected", "is no longer on bypass"),
        (UpsStatusFlags.Off, UpsEventType.Off, UpsEventType.OffCleared, "output is off", "output is on again"),
        (UpsStatusFlags.Calibrating, UpsEventType.Calibration, UpsEventType.CalibrationEnded, "is calibrating its battery", "finished calibrating"),
        (UpsStatusFlags.Trim, UpsEventType.Trim, UpsEventType.TrimEnded, "is trimming a high input voltage", "stopped trimming the input voltage"),
        (UpsStatusFlags.Boost, UpsEventType.Boost, UpsEventType.BoostEnded, "is boosting a low input voltage", "stopped boosting the input voltage"),
        (UpsStatusFlags.Alarm, UpsEventType.Alarm, UpsEventType.AlarmCleared, "reports an alarm", "alarm cleared"),
        (UpsStatusFlags.Test, UpsEventType.TestStarted, UpsEventType.TestEnded, "is running a self test", "finished its self test"),
    ];

    // Conditions worth reporting when NutHub first sees a UPS (or sees it again after a restart).
    private const UpsStatusFlags InitialAdverse = UpsStatusFlags.OnBattery | UpsStatusFlags.LowBattery |
                                                  UpsStatusFlags.ReplaceBattery | UpsStatusFlags.Overload |
                                                  UpsStatusFlags.Bypass | UpsStatusFlags.Off | UpsStatusFlags.Alarm;

    private List<UpsEvent> DetectEvents(UpsSnapshot previous, UpsSnapshot next)
    {
        var events = new List<UpsEvent>();
        DateTimeOffset now = next.Timestamp;
        string name = next.Name;

        // Communication. Planned restarts (configuration change) go through Starting and are not reported.
        if (previous.Availability == DataAvailability.Available && next.Availability != DataAvailability.Available &&
            next.DriverState is DriverState.Connected or DriverState.Connecting or DriverState.Disconnected or DriverState.Failed)
        {
            _commLostReported = true;
            string reason = next.DriverMessage ?? (next.DriverState == DriverState.Connected
                ? "no data for more than " + NutFormat.Number(MaxDataAgeSeconds()) + " s"
                : next.DriverState.ToString().ToLowerInvariant());
            events.Add(UpsEvent.Create(UpsEventType.CommunicationLost, now, name,
                                       $"Communication with {name} lost ({reason}).", "system"));
        }
        else if (previous.Availability != DataAvailability.Available && next.Availability == DataAvailability.Available)
        {
            if (_commLostReported || _noCommReported)
            {
                _commLostReported = false;
                _noCommReported = false;
                events.Add(UpsEvent.Create(UpsEventType.CommunicationRestored, now, name,
                                           $"Communication with {name} restored.", "system"));
            }
        }

        // A driver that keeps failing the same way is reported once, not at every retry of the back-off.
        if (next.DriverState == DriverState.Failed && previous.DriverState != DriverState.Failed &&
            next.DriverMessage != _lastFailureMessage)
        {
            _lastFailureMessage = next.DriverMessage;
            events.Add(UpsEvent.Create(UpsEventType.DriverFailed, now, name,
                                       $"The driver of {name} failed: {next.DriverMessage ?? "unknown error"}", "system"));
        }

        if (!next.IsAvailable)
        {
            return events;
        }

        // Power and device condition, from the last flags evaluated while data was fresh (so a change that happened
        // during a communication loss is still reported once communication is back). FSD is reported where it is
        // set and cleared, with the name of whoever did it.
        UpsStatusFlags current = next.Status & ~UpsStatusFlags.ForcedShutdown;
        UpsStatusFlags? before = _lastEvaluatedFlags;
        _lastEvaluatedFlags = current;

        var data = EventData(next);
        if (before is null)
        {
            if ((current & InitialAdverse) == 0)
            {
                return events;
            }

            before = current & ~InitialAdverse;
        }

        UpsStatusFlags was = before.Value;
        bool wasOb = (was & UpsStatusFlags.OnBattery) != 0;
        bool isOb = (current & UpsStatusFlags.OnBattery) != 0;
        if (!wasOb && isOb)
        {
            events.Add(UpsEvent.Create(UpsEventType.OnBattery, now, name,
                                       $"{name} is on battery{Describe(next)}.", "system", data));
        }
        else if (wasOb && !isOb && (current & UpsStatusFlags.Online) != 0)
        {
            events.Add(UpsEvent.Create(UpsEventType.Online, now, name,
                                       $"{name} is back on line power{Describe(next)}.", "system", data));
        }

        foreach (var (flag, setType, clearedType, setText, clearedText) in FlagEvents)
        {
            bool had = (was & flag) != 0;
            bool has = (current & flag) != 0;
            if (!had && has)
            {
                string extra = flag == UpsStatusFlags.Alarm && next.Get("ups.alarm") is { } alarm ? $": {alarm}" : "";
                events.Add(UpsEvent.Create(setType, now, name, $"{name} {setText}{extra}.", "system", data));
            }
            else if (had && !has)
            {
                events.Add(UpsEvent.Create(clearedType, now, name, $"{name} {clearedText}.", "system", data));
            }
        }

        return events;
    }

    private static Dictionary<string, string> EventData(UpsSnapshot s)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string key in new[] { "ups.status", "battery.charge", "battery.runtime", "ups.load", "input.voltage" })
        {
            if (s.Get(key) is { } value)
            {
                data[key] = value;
            }
        }

        return data;
    }

    private static string Describe(UpsSnapshot s)
    {
        var parts = new List<string>(2);
        if (s.GetNumber("battery.charge") is { } charge)
        {
            parts.Add($"battery {NutFormat.Number(charge, 0)}%");
        }

        if (s.GetNumber("battery.runtime") is { } runtime)
        {
            parts.Add($"runtime {FormatDuration(runtime)}");
        }

        return parts.Count == 0 ? "" : " (" + string.Join(", ", parts) + ")";
    }

    internal static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return span.TotalHours >= 1 ? $"{(int)span.TotalHours} h {span.Minutes} min"
             : span.TotalMinutes >= 1 ? $"{(int)span.TotalMinutes} min"
             : $"{(int)span.TotalSeconds} s";
    }

    private void Raise(UpsEvent e) => _hub.Publish(new UpsEventMessage(e));

    /// <summary>
    /// Called under the lock: the housekeeping timer, the driver and the clients change the unit from different
    /// threads, and subscribers must see the changes (a communication lost, then restored) in the order they were
    /// made. Hub handlers are quick and never wait for another thread, so holding the lock meanwhile is safe.
    /// </summary>
    private void PublishAll(List<HubMessage> messages)
    {
        foreach (HubMessage m in messages)
        {
            _hub.Publish(m);
        }
    }

    private async Task<CommandResult> RunDriverOperation(Func<CancellationToken, Task<CommandResult>> operation,
                                                         CancellationToken cancellationToken)
    {
        using var timeout = new CancellationTokenSource(CommandTimeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            // WaitAsync: a driver stuck in a call that ignores the token must not hold the caller beyond the timeout.
            return await operation(linked.Token).WaitAsync(linked.Token).ConfigureAwait(false)
                   ?? CommandResult.Fail("No result.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CommandResult.Fail("The device did not answer in time.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "{Ups}: driver operation failed.", Name);
            return CommandResult.Fail(ex.Message);
        }
    }
}
