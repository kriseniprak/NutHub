using System.Collections.Concurrent;
using NutHub.Core.Drivers;
using NutHub.Core.Model;

namespace NutHub.Services.Tests.Support;

/// <summary>A driver whose readings the test sets; records the commands and writes it receives.</summary>
internal sealed class ScriptedDriverFactory : IUpsDriverFactory
{
    public ConcurrentDictionary<string, ScriptedDriver> Drivers { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string Id => "scripted";

    public string DisplayName => "Scripted";

    public string Description => "A driver scripted by the tests.";

    public DriverPlatforms Platforms => DriverPlatforms.All;

    public IReadOnlyList<DriverOption> Options => [];

    public bool SupportsDiscovery => false;

    public Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DiscoveredDevice>>([]);

    public IUpsDriver Create(DriverCreateContext context)
    {
        var driver = new ScriptedDriver();
        Drivers[context.UpsName] = driver;
        return driver;
    }
}

internal sealed class ScriptedDriver : IUpsDriver
{
    private volatile IDriverContext? _context;

    public ConcurrentQueue<string> Calls { get; } = new();

    public bool Running => _context is not null;

    public async Task RunAsync(IDriverContext context, CancellationToken cancellationToken)
    {
        _context = context;
        Publish("OL");
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Publishes a reading with this status (heartbeat included).</summary>
    public void Publish(string status, string charge = "100", string runtime = "1800")
    {
        _context?.Publish(new DriverUpdate
        {
            Variables = new Dictionary<string, string>
            {
                ["ups.status"] = status,
                ["battery.charge"] = charge,
                ["battery.runtime"] = runtime,
                ["ups.load"] = "25",
                ["input.voltage"] = "230",
                ["ups.delay.shutdown"] = "20",
            },
            VariableInfo = new Dictionary<string, VariableInfo> { ["ups.delay.shutdown"] = VariableInfo.WritableNumber() },
            Commands = ["load.off.delay", "shutdown.return"],
        });
    }

    /// <summary>The device stops answering: the data goes stale at once.</summary>
    public void Disconnect(string reason) => _context?.ReportDisconnected(reason);

    public Task<CommandResult> InstantCommandAsync(string command, string? parameter, CancellationToken cancellationToken)
    {
        Calls.Enqueue(parameter is null ? $"cmd {command}" : $"cmd {command} {parameter}");
        return Task.FromResult(CommandResult.Ok);
    }

    public Task<CommandResult> SetVariableAsync(string name, string value, CancellationToken cancellationToken)
    {
        Calls.Enqueue($"set {name}={value}");
        return Task.FromResult(CommandResult.Ok);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
