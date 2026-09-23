using System.Collections.Concurrent;
using System.Threading.Channels;
using NutHub.Core.Drivers;
using NutHub.Core.Model;

namespace NutHub.Protocol.Tests.Infrastructure;

/// <summary>A driver whose device the test controls: what it publishes, when it goes silent, how commands end.</summary>
internal sealed class FakeDriverFactory : IUpsDriverFactory
{
    public const string DriverId = "fake";

    private readonly ConcurrentDictionary<string, FakeDevice> _devices = new(StringComparer.OrdinalIgnoreCase);

    public string Id => DriverId;

    public string DisplayName => "Fake UPS";

    public string Description => "A device controlled by the tests.";

    public DriverPlatforms Platforms => DriverPlatforms.All;

    public IReadOnlyList<DriverOption> Options => [];

    public bool SupportsDiscovery => false;

    public FakeDevice Device(string upsName) => _devices.GetOrAdd(upsName, _ => new FakeDevice());

    public Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DiscoveredDevice>>([]);

    public IUpsDriver Create(DriverCreateContext context) => new FakeDriver(Device(context.UpsName));
}

internal sealed class FakeDevice
{
    public ConcurrentDictionary<string, string> Variables { get; } = new(StringComparer.Ordinal)
    {
        ["battery.charge"] = "100",
        ["battery.charge.low"] = "20",
        ["battery.runtime"] = "1800",
        ["device.type"] = "ups",
        ["input.L2.voltage"] = "231.0", // upper case: upsd lists it after input.frequency (strcasecmp order)
        ["input.frequency"] = "50.0",
        ["input.transfer.low"] = "180",
        ["input.voltage"] = "230.0",
        ["ups.beeper.status"] = "enabled",
        ["ups.delay.shutdown"] = "20",
        ["ups.id"] = "rack",
        ["ups.mfr"] = "NutHub",
        ["ups.model"] = "Fake 1500",
        ["ups.status"] = "OL",
    };

    public ConcurrentDictionary<string, VariableInfo> Info { get; } = new(StringComparer.Ordinal)
    {
        ["battery.charge.low"] = VariableInfo.WritableRange(5, 90),
        ["input.transfer.low"] = VariableInfo.WritableEnum("170", "180", "190"),
        ["ups.beeper.status"] = VariableInfo.WritableEnum("enabled", "disabled", "muted"),
        ["ups.delay.shutdown"] = VariableInfo.WritableNumber(),
        ["ups.id"] = VariableInfo.WritableString(16),
    };

    public List<string> Commands { get; } =
        ["beeper.disable", "beeper.enable", "load.off.delay", "shutdown.return", "test.battery.start.quick"];

    public ConcurrentQueue<(string Command, string? Parameter)> Executed { get; } = new();

    public ConcurrentQueue<(string Name, string Value)> Written { get; } = new();

    /// <summary>Replaces the default outcome (success) of instant commands.</summary>
    public Func<string, string?, CancellationToken, Task<CommandResult>>? OnCommand { get; set; }

    /// <summary>Replaces the default outcome (store the value, success) of variable writes.</summary>
    public Func<string, string, CancellationToken, Task<CommandResult>>? OnSet { get; set; }

    internal Channel<Action<IDriverContext>> Actions { get; } = Channel.CreateUnbounded<Action<IDriverContext>>();

    /// <summary>Publishes the current variables (a poll).</summary>
    public void Publish() => Actions.Writer.TryWrite(context => context.Publish(BuildUpdate()));

    /// <summary>Reports a lost device: the data becomes stale.</summary>
    public void Disconnect(string reason = "cable pulled") =>
        Actions.Writer.TryWrite(context => context.ReportDisconnected(reason));

    internal DriverUpdate BuildUpdate() => new()
    {
        Variables = new Dictionary<string, string>(Variables, StringComparer.Ordinal),
        VariableInfo = new Dictionary<string, VariableInfo>(Info, StringComparer.Ordinal),
        Commands = Commands.ToArray(),
    };
}

internal sealed class FakeDriver(FakeDevice device) : IUpsDriver
{
    public async Task RunAsync(IDriverContext context, CancellationToken cancellationToken)
    {
        context.Publish(device.BuildUpdate());
        await foreach (Action<IDriverContext> action in device.Actions.Reader.ReadAllAsync(cancellationToken))
        {
            action(context);
        }
    }

    public Task<CommandResult> InstantCommandAsync(string command, string? parameter,
                                                   CancellationToken cancellationToken)
    {
        device.Executed.Enqueue((command, parameter));
        return device.OnCommand?.Invoke(command, parameter, cancellationToken) ?? Task.FromResult(CommandResult.Ok);
    }

    public Task<CommandResult> SetVariableAsync(string name, string value, CancellationToken cancellationToken)
    {
        device.Written.Enqueue((name, value));
        if (device.OnSet is { } handler)
        {
            return handler(name, value, cancellationToken);
        }

        device.Variables[name] = value;
        return Task.FromResult(CommandResult.Ok);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
