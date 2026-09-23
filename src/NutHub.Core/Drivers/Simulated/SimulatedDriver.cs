using System.Globalization;
using Microsoft.Extensions.Logging;
using NutHub.Core.Model;

namespace NutHub.Core.Drivers.Simulated;

/// <summary>
/// A UPS that exists only in software: for trying NutHub, demonstrations, and testing NUT clients (upsmon, NutDesk)
/// and notifications without pulling a plug. Power failures come from a schedule or from the
/// "test.failure.start" command; a NUT .dev file (as used by dummy-ups) can supply the variables of a real model.
/// </summary>
public sealed class SimulatedDriverFactory : IUpsDriverFactory
{
    public string Id => "simulated";

    public string DisplayName => "Simulated UPS";

    public string Description =>
        "A virtual UPS for testing: battery that charges and discharges, power failures on demand or on a schedule, " +
        "working instant commands. Can also replay the variables of a NUT .dev file.";

    public DriverPlatforms Platforms => DriverPlatforms.All;

    public bool SupportsDiscovery => false;

    public IReadOnlyList<DriverOption> Options { get; } =
    [
        new() { Key = "model", Label = "Model", Default = "Virtual UPS 1500" },
        new() { Key = "mfr", Label = "Manufacturer", Default = "NutHub" },
        new() { Key = "serial", Label = "Serial number", Help = "Defaults to SIM- followed by the UPS name." },
        new()
        {
            Key = "scenario", Label = "Scenario", Type = DriverOptionType.Choice, Default = "steady",
            Choices =
            [
                new("steady", "Always on line power (use test.failure.start to simulate an outage)"),
                new("outages", "Periodic power outages"),
            ],
        },
        new()
        {
            Key = "onlineMinutes", Label = "Minutes on line power between outages", Type = DriverOptionType.Integer,
            Default = "10", Min = 1, Max = 1440, VisibleWhen = new("scenario", ["outages"]),
        },
        new()
        {
            Key = "outageMinutes", Label = "Minutes of each outage", Type = DriverOptionType.Integer, Default = "3",
            Min = 1, Max = 600, VisibleWhen = new("scenario", ["outages"]),
        },
        new() { Key = "load", Label = "Load (%)", Type = DriverOptionType.Integer, Default = "35", Min = 0, Max = 150 },
        new()
        {
            Key = "runtimeMinutes", Label = "Runtime on a full battery at that load (minutes)",
            Type = DriverOptionType.Integer, Default = "25", Min = 1, Max = 600,
        },
        new()
        {
            Key = "nominalPower", Label = "Rated power (VA)", Type = DriverOptionType.Integer, Default = "1500",
            Min = 100, Max = 100000,
        },
        new()
        {
            Key = "voltage", Label = "Nominal voltage (V)", Type = DriverOptionType.Choice, Default = "230",
            Choices = [new("230", "230 V"), new("220", "220 V"), new("120", "120 V"), new("100", "100 V")],
        },
        new()
        {
            Key = "frequency", Label = "Nominal frequency (Hz)", Type = DriverOptionType.Choice, Default = "50",
            Choices = [new("50", "50 Hz"), new("60", "60 Hz")],
        },
        new()
        {
            Key = "devFile", Label = "NUT .dev file", Type = DriverOptionType.FilePath, Advanced = true,
            Help = "Variables read from this file (\"battery.charge: 100\" lines, as used by dummy-ups) replace the " +
                   "simulated ones. The file is re-read when it changes.",
        },
    ];

    public Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DiscoveredDevice>>([]);

    public IUpsDriver Create(DriverCreateContext context)
    {
        var read = context.Read;
        var settings = new SimulatedSettings
        {
            Model = read.GetString("model", "Virtual UPS 1500")!,
            Manufacturer = read.GetString("mfr", "NutHub")!,
            Serial = read.GetString("serial", "SIM-" + context.UpsName.ToUpperInvariant())!,
            Outages = read.GetChoice("scenario", "steady", "steady", "outages") == "outages",
            OnlineMinutes = read.GetInt("onlineMinutes", 10, 1, 1440),
            OutageMinutes = read.GetInt("outageMinutes", 3, 1, 600),
            Load = read.GetInt("load", 35, 0, 150),
            RuntimeMinutes = read.GetInt("runtimeMinutes", 25, 1, 600),
            NominalPower = read.GetInt("nominalPower", 1500, 100, 100000),
            Voltage = int.Parse(read.GetChoice("voltage", "230", "230", "220", "120", "100"), CultureInfo.InvariantCulture),
            Frequency = int.Parse(read.GetChoice("frequency", "50", "50", "60"), CultureInfo.InvariantCulture),
            DevFile = read.GetString("devFile"),
        };

        if (settings.DevFile is not null && !File.Exists(settings.DevFile))
        {
            throw new DriverConfigurationException($"The file '{settings.DevFile}' does not exist.", "devFile");
        }

        return new SimulatedDriver(context.UpsName, settings);
    }
}

internal sealed class SimulatedSettings
{
    public required string Model { get; init; }
    public required string Manufacturer { get; init; }
    public required string Serial { get; init; }
    public bool Outages { get; init; }
    public int OnlineMinutes { get; init; }
    public int OutageMinutes { get; init; }
    public int Load { get; init; }
    public int RuntimeMinutes { get; init; }
    public int NominalPower { get; init; }
    public int Voltage { get; init; }
    public int Frequency { get; init; }
    public string? DevFile { get; init; }
}

internal sealed class SimulatedDriver : IUpsDriver
{
    private static readonly string[] SupportedCommands =
    [
        "beeper.disable", "beeper.enable", "beeper.mute", "calibrate.start", "calibrate.stop", "load.off",
        "load.off.delay", "load.on", "load.on.delay", "shutdown.return", "shutdown.stayoff", "shutdown.stop",
        "test.battery.start.quick", "test.battery.start.deep", "test.battery.stop", "test.failure.start",
        "test.failure.stop", "simulate.commlost",
    ];

    private readonly object _lock = new();
    private readonly SimulatedSettings _s;
    private readonly Random _noise;
    private readonly Dictionary<string, string> _writable = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VariableInfo> _info;

    private DateTimeOffset _last;
    private DateTimeOffset _scenarioStart;
    private double _charge = 100;
    private bool _forcedFailure;
    private bool _outputOn = true;
    private bool _stayOff;
    private DateTimeOffset? _shutdownAt;
    private DateTimeOffset? _startAt;
    private DateTimeOffset? _testUntil;
    private DateTimeOffset? _calibrateUntil;
    private DateTimeOffset? _commLostUntil;
    private string _testResult = "No test initiated";
    private Dictionary<string, string> _devVars = [];
    private DateTime _devFileStamp;

    public SimulatedDriver(string upsName, SimulatedSettings settings)
    {
        _s = settings;
        _noise = new Random(upsName.Aggregate(17, (h, c) => unchecked(h * 31 + c)));
        bool high = settings.Voltage >= 200;
        _writable["battery.charge.low"] = "20";
        _writable["battery.runtime.low"] = "120";
        _writable["ups.delay.shutdown"] = "20";
        _writable["ups.delay.start"] = "30";
        _writable["ups.id"] = upsName;
        _writable["ups.beeper.status"] = "enabled";
        _writable["input.transfer.low"] = high ? "180" : "92";
        _writable["input.transfer.high"] = high ? "264" : "139";
        _writable["ups.test.interval"] = "1209600";
        _info = new Dictionary<string, VariableInfo>(StringComparer.Ordinal)
        {
            ["battery.charge.low"] = VariableInfo.WritableRange(5, 90),
            ["battery.runtime.low"] = VariableInfo.WritableRange(30, 1800),
            ["ups.delay.shutdown"] = VariableInfo.WritableRange(0, 600),
            ["ups.delay.start"] = VariableInfo.WritableRange(0, 600),
            ["ups.id"] = VariableInfo.WritableString(32),
            ["ups.beeper.status"] = VariableInfo.WritableEnum("enabled", "disabled", "muted"),
            ["input.transfer.low"] = high ? VariableInfo.WritableEnum("170", "180", "190", "196")
                                          : VariableInfo.WritableEnum("88", "92", "97", "102"),
            ["input.transfer.high"] = high ? VariableInfo.WritableEnum("250", "253", "260", "264")
                                           : VariableInfo.WritableEnum("132", "136", "139", "144"),
            ["ups.test.interval"] = VariableInfo.WritableEnum("0", "604800", "1209600"),
        };
    }

    public async Task RunAsync(IDriverContext context, CancellationToken cancellationToken)
    {
        TimeProvider time = context.TimeProvider;
        _last = _scenarioStart = time.GetUtcNow();
        context.ReportConnecting("Starting the simulation");

        using var timer = new PeriodicTimer(context.PollInterval, time);
        do
        {
            DateTimeOffset now = time.GetUtcNow();
            DriverUpdate? update;
            string? lost;
            lock (_lock)
            {
                Step(now);
                lost = _commLostUntil is { } until && now < until ? "Simulated loss of communication" : null;
                update = lost is null ? BuildUpdate(now, context.Logger) : null;
            }

            if (update is not null)
            {
                context.Publish(update);
            }
            else
            {
                context.ReportDisconnected(lost!);
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    public Task<CommandResult> InstantCommandAsync(string command, string? parameter, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            DateTimeOffset now = _last;
            int Delay(string key, int fallback) =>
                parameter is not null && int.TryParse(parameter, NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) && p >= 0
                    ? p
                    : int.TryParse(_writable.GetValueOrDefault(key), out int v) ? v : fallback;

            switch (command)
            {
                case "beeper.enable":
                    _writable["ups.beeper.status"] = "enabled";
                    break;
                case "beeper.disable":
                    _writable["ups.beeper.status"] = "disabled";
                    break;
                case "beeper.mute":
                    _writable["ups.beeper.status"] = "muted";
                    break;
                case "calibrate.start":
                    _calibrateUntil = now.AddSeconds(90);
                    break;
                case "calibrate.stop":
                    _calibrateUntil = null;
                    break;
                case "load.off":
                    _outputOn = false;
                    _stayOff = true;
                    _shutdownAt = null;
                    break;
                case "load.on":
                    _outputOn = true;
                    _stayOff = false;
                    _startAt = null;
                    break;
                case "load.off.delay":
                    _shutdownAt = now.AddSeconds(Delay("ups.delay.shutdown", 20));
                    _stayOff = true;
                    break;
                case "load.on.delay":
                    _startAt = now.AddSeconds(Delay("ups.delay.start", 30));
                    break;
                case "shutdown.return":
                    _shutdownAt = now.AddSeconds(Delay("ups.delay.shutdown", 20));
                    _stayOff = false;
                    break;
                case "shutdown.stayoff":
                    _shutdownAt = now.AddSeconds(Delay("ups.delay.shutdown", 20));
                    _stayOff = true;
                    break;
                case "shutdown.stop":
                    _shutdownAt = null;
                    break;
                case "test.battery.start.quick":
                    _testUntil = now.AddSeconds(10);
                    _testResult = "In progress";
                    break;
                case "test.battery.start.deep":
                    _testUntil = now.AddSeconds(60);
                    _testResult = "In progress";
                    break;
                case "test.battery.stop":
                    _testUntil = null;
                    _testResult = "Aborted";
                    break;
                case "test.failure.start":
                    _forcedFailure = true;
                    break;
                case "test.failure.stop":
                    _forcedFailure = false;
                    break;
                case "simulate.commlost":
                    _commLostUntil = now.AddSeconds(Delay("", 30));
                    break;
                default:
                    return Task.FromResult(CommandResult.NotSupported());
            }
        }

        return Task.FromResult(CommandResult.Ok);
    }

    public Task<CommandResult> SetVariableAsync(string name, string value, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            if (!_writable.ContainsKey(name))
            {
                return Task.FromResult(CommandResult.NotSupported());
            }

            _writable[name] = value;
        }

        return Task.FromResult(CommandResult.Ok);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private bool MainsPresent(DateTimeOffset now)
    {
        if (_forcedFailure)
        {
            return false;
        }

        if (!_s.Outages)
        {
            return true;
        }

        double cycle = (_s.OnlineMinutes + _s.OutageMinutes) * 60.0;
        double position = (now - _scenarioStart).TotalSeconds % cycle;
        return position < _s.OnlineMinutes * 60.0;
    }

    private double CurrentLoad() => _outputOn ? _s.Load : 0;

    /// <summary>Seconds of runtime a full battery gives at the current load.</summary>
    private double FullRuntime()
    {
        double load = Math.Max(CurrentLoad(), 3);
        double reference = Math.Max(_s.Load, 3);
        return _s.RuntimeMinutes * 60.0 * Math.Pow(reference / load, 1.1);
    }

    private void Step(DateTimeOffset now)
    {
        double dt = Math.Clamp((now - _last).TotalSeconds, 0, 3600);
        _last = now;

        bool testing = _testUntil is { } t && now < t;
        if (_testUntil is { } end && now >= end)
        {
            _testUntil = null;
            _testResult = "Done and passed";
        }

        if (_calibrateUntil is { } c && now >= c)
        {
            _calibrateUntil = null;
        }

        bool calibrating = _calibrateUntil is not null;
        bool mains = MainsPresent(now);
        bool onBattery = _outputOn && (!mains || testing || calibrating);

        if (_shutdownAt is { } sd && now >= sd)
        {
            _shutdownAt = null;
            _outputOn = false;
            if (!_stayOff)
            {
                // shutdown.return: power comes back once the mains is back, after ups.delay.start.
                _startAt = null;
            }
        }

        if (!_outputOn && !_stayOff && mains && _startAt is null)
        {
            _startAt = now.AddSeconds(int.TryParse(_writable["ups.delay.start"], out int ds) ? ds : 30);
        }

        if (_startAt is { } st && now >= st && mains)
        {
            _startAt = null;
            _outputOn = true;
            _stayOff = false;
        }

        if (onBattery)
        {
            _charge -= dt * 100.0 / FullRuntime();
            if (_charge <= 0)
            {
                _charge = 0;
                _outputOn = false; // battery exhausted: the load is dropped
                _testUntil = null;
                _calibrateUntil = null;
            }
        }
        else if (mains)
        {
            // Recharges to full in about an hour.
            _charge = Math.Min(100, _charge + dt * 100.0 / 3600.0);
        }
    }

    private DriverUpdate BuildUpdate(DateTimeOffset now, ILogger logger)
    {
        bool mains = MainsPresent(now);
        bool testing = _testUntil is not null;
        bool calibrating = _calibrateUntil is not null;
        bool onBattery = _outputOn && (!mains || testing || calibrating);
        double load = CurrentLoad() * (1 + (_noise.NextDouble() - 0.5) * 0.04);
        double runtime = _charge / 100.0 * FullRuntime();
        double chargeLow = double.TryParse(_writable["battery.charge.low"], NumberStyles.Float, CultureInfo.InvariantCulture, out double cl) ? cl : 20;
        double runtimeLow = double.TryParse(_writable["battery.runtime.low"], NumberStyles.Float, CultureInfo.InvariantCulture, out double rl) ? rl : 120;

        var status = new List<string>();
        if (!_outputOn)
        {
            status.Add("OFF");
        }

        status.Add(onBattery || (!mains && !_outputOn) ? "OB" : "OL");
        if (onBattery)
        {
            status.Add("DISCHRG");
        }
        else if (mains && _charge < 99.5)
        {
            status.Add("CHRG");
        }

        if ((onBattery || !mains) && (_charge <= chargeLow || runtime <= runtimeLow))
        {
            status.Add("LB");
        }

        if (calibrating)
        {
            status.Add("CAL");
        }

        if (testing)
        {
            status.Add("TEST");
        }

        if (_s.Load > 100 && _outputOn)
        {
            status.Add("OVER");
        }

        double nominalBattery = _s.NominalPower >= 2000 ? 48 : 24;
        double cells = nominalBattery / 2.0;
        double battVoltage = onBattery
            ? cells * (1.75 + 0.35 * _charge / 100.0)
            : cells * (mains && _charge < 99.5 ? 2.30 : 2.27);
        double inputVoltage = mains ? _s.Voltage * (1 + (_noise.NextDouble() - 0.5) * 0.02) : 0;
        double outputVoltage = _outputOn ? (onBattery ? _s.Voltage : inputVoltage) : 0;
        double realNominal = Math.Round(_s.NominalPower * 0.6);

        string? shutdownTimer = _shutdownAt is { } sd ? NutFormat.Number(Math.Ceiling((sd - now).TotalSeconds), 0) : "-1";
        string? startTimer = _startAt is { } sa ? NutFormat.Number(Math.Ceiling((sa - now).TotalSeconds), 0) : "-1";

        var vars = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["battery.charge"] = NutFormat.Number(_charge, 0),
            ["battery.charge.low"] = _writable["battery.charge.low"],
            ["battery.runtime"] = NutFormat.Number(runtime, 0),
            ["battery.runtime.low"] = _writable["battery.runtime.low"],
            ["battery.voltage"] = NutFormat.Fixed(battVoltage, 1),
            ["battery.voltage.nominal"] = NutFormat.Number(nominalBattery),
            ["battery.type"] = "PbAc",
            ["device.type"] = "ups",
            ["input.frequency"] = mains ? NutFormat.Fixed(_s.Frequency + (_noise.NextDouble() - 0.5) * 0.2, 1) : "0.0",
            ["input.frequency.nominal"] = NutFormat.Number(_s.Frequency),
            ["input.transfer.high"] = _writable["input.transfer.high"],
            ["input.transfer.low"] = _writable["input.transfer.low"],
            ["input.voltage"] = NutFormat.Fixed(inputVoltage, 1),
            ["input.voltage.nominal"] = NutFormat.Number(_s.Voltage),
            ["output.frequency"] = _outputOn ? NutFormat.Fixed(_s.Frequency, 1) : "0.0",
            ["output.voltage"] = NutFormat.Fixed(outputVoltage, 1),
            ["output.voltage.nominal"] = NutFormat.Number(_s.Voltage),
            ["ups.beeper.status"] = _writable["ups.beeper.status"],
            ["ups.delay.shutdown"] = _writable["ups.delay.shutdown"],
            ["ups.delay.start"] = _writable["ups.delay.start"],
            ["ups.firmware"] = "SIM " + NutHubInfo.Version,
            ["ups.id"] = _writable["ups.id"],
            ["ups.load"] = NutFormat.Number(load, 0),
            ["ups.mfr"] = _s.Manufacturer,
            ["ups.model"] = _s.Model,
            ["ups.power"] = NutFormat.Number(_s.NominalPower * load / 100.0, 0),
            ["ups.power.nominal"] = NutFormat.Number(_s.NominalPower),
            ["ups.realpower"] = NutFormat.Number(realNominal * load / 100.0, 0),
            ["ups.realpower.nominal"] = NutFormat.Number(realNominal),
            ["ups.serial"] = _s.Serial,
            ["ups.status"] = string.Join(' ', status),
            ["ups.temperature"] = NutFormat.Fixed(27 + load * 0.08 + (onBattery ? 2 : 0), 1),
            ["ups.test.interval"] = _writable["ups.test.interval"],
            ["ups.test.result"] = _testResult,
            ["ups.timer.shutdown"] = shutdownTimer,
            ["ups.timer.start"] = startTimer,
            ["ups.type"] = "line-interactive",
            ["driver.version.internal"] = "simulated 1.0",
        };

        foreach (var (key, value) in ReadDevFile(logger))
        {
            vars[key] = value;
        }

        return new DriverUpdate
        {
            Variables = vars,
            VariableInfo = _info,
            Commands = SupportedCommands,
        };
    }

    private Dictionary<string, string> ReadDevFile(ILogger logger)
    {
        if (_s.DevFile is null)
        {
            return _devVars;
        }

        try
        {
            DateTime stamp = File.GetLastWriteTimeUtc(_s.DevFile);
            if (stamp == _devFileStamp)
            {
                return _devVars;
            }

            var vars = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string raw in File.ReadLines(_s.DevFile))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line.StartsWith("TIMER", StringComparison.Ordinal))
                {
                    continue;
                }

                int colon = line.IndexOf(':');
                if (colon <= 0)
                {
                    continue;
                }

                string key = line[..colon].Trim();
                string value = line[(colon + 1)..].Trim().Trim('"');
                if (NutFormat.IsValidVariableName(key) && !key.StartsWith("driver.", StringComparison.Ordinal))
                {
                    vars[key] = value;
                }
            }

            _devVars = vars;
            _devFileStamp = stamp;
        }
        catch (IOException ex)
        {
            logger.LogWarning("Cannot read {File}: {Message}", _s.DevFile, ex.Message);
        }

        return _devVars;
    }
}
