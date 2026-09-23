using System.Collections.Concurrent;
using NutHub.Core.Configuration;
using NutHub.Core.Drivers;
using NutHub.Core.Model;

namespace NutHub.Core.Tests.Support;

/// <summary>What a <see cref="FakeDriver"/> does in <see cref="IUpsDriver.RunAsync"/>.</summary>
internal enum FakeRunBehavior
{
    /// <summary>Publishes once, then waits for cancellation.</summary>
    PublishAndWait,

    /// <summary>Throws an ordinary exception (a crash, retried with back-off).</summary>
    Throw,

    /// <summary>Throws <see cref="DriverConfigurationException"/> (never retried).</summary>
    ThrowConfiguration,

    /// <summary>Returns without being cancelled ("stopped unexpectedly").</summary>
    Return,

    /// <summary>Publishes, works for <see cref="FakeDriverFactory.FailAfter"/>, then throws.</summary>
    PublishThenThrow,

    /// <summary>Publishes, and ends with an I/O error when cancelled (a port closed under it).</summary>
    ThrowWhenCancelled,

    /// <summary>Publishes, ignores cancellation until <see cref="FakeDriver.Release"/>, then publishes OB and throws.</summary>
    IgnoreCancellation,
}

/// <summary>A driver factory whose drivers are scripted by the test.</summary>
internal sealed class FakeDriverFactory(string id = "fake") : IUpsDriverFactory
{
    private readonly ConcurrentQueue<FakeDriver> _created = new();

    public string Id => id;

    public string DisplayName => "Fake " + id;

    public string Description => "Test driver";

    public DriverPlatforms Platforms { get; set; } = DriverPlatforms.All;

    public IReadOnlyList<DriverOption> Options { get; set; } =
        [new() { Key = "port", Label = "Port" }, new() { Key = "community", Label = "Secret", Type = DriverOptionType.Secret }];

    public bool SupportsDiscovery => false;

    /// <summary>The behaviour of the next drivers created (applies from the next Create).</summary>
    public FakeRunBehavior Behavior { get; set; } = FakeRunBehavior.PublishAndWait;

    public TimeSpan FailAfter { get; set; } = TimeSpan.FromMinutes(2);

    public Dictionary<string, string> Variables { get; set; } = new() { ["ups.status"] = "OL", ["battery.charge"] = "100" };

    public IReadOnlyList<FakeDriver> Created => [.. _created];

    public int CreateCount => _created.Count;

    public FakeDriver? Last => _created.LastOrDefault();

    public Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DiscoveredDevice>>([]);

    private ManualResetEventSlim? _createGate;

    /// <summary>The next Create waits for <paramref name="gate"/> (a driver whose constructor blocks on its device).</summary>
    public void BlockNextCreate(ManualResetEventSlim gate) => _createGate = gate;

    public IUpsDriver Create(DriverCreateContext context)
    {
        Interlocked.Exchange(ref _createGate, null)?.Wait(TimeSpan.FromSeconds(10));
        var driver = new FakeDriver(Behavior, new Dictionary<string, string>(Variables), context, FailAfter);
        _created.Enqueue(driver);
        return driver;
    }
}

/// <summary>A scriptable driver: publishes what the test tells it and records commands.</summary>
internal sealed class FakeDriver(FakeRunBehavior behavior, Dictionary<string, string> variables, DriverCreateContext created,
                                 TimeSpan failAfter = default) : IUpsDriver
{
    private readonly TaskCompletionSource _running = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IDriverContext? _context;

    public DriverCreateContext CreateContext => created;

    public IReadOnlyDictionary<string, string> Options => created.Options;

    public ConcurrentQueue<string> Commands { get; } = new();

    public bool Disposed { get; private set; }

    public bool Cancelled { get; private set; }

    /// <summary>What InstantCommandAsync / SetVariableAsync do; defaults to success.</summary>
    public Func<string, CancellationToken, Task<CommandResult>> Operation { get; set; } =
        (_, _) => Task.FromResult(CommandResult.Ok);

    /// <summary>Completes once RunAsync has published (or failed).</summary>
    public Task Running => _running.Task;

    public async Task RunAsync(IDriverContext context, CancellationToken cancellationToken)
    {
        _context = context;
        switch (behavior)
        {
            case FakeRunBehavior.Throw:
                _running.TrySetResult();
                throw new IOException("Device unplugged");
            case FakeRunBehavior.ThrowConfiguration:
                _running.TrySetResult();
                throw new DriverConfigurationException("The option 'port' is required.", "port");
            case FakeRunBehavior.Return:
                _running.TrySetResult();
                return;
        }

        context.Publish(new DriverUpdate
        {
            Variables = variables,
            VariableInfo = new Dictionary<string, VariableInfo> { ["ups.delay.shutdown"] = VariableInfo.WritableRange(0, 600) },
            Commands = ["test.battery.start.quick", "beeper.toggle"],
        });
        _running.TrySetResult();
        if (behavior == FakeRunBehavior.PublishThenThrow)
        {
            await Task.Delay(failAfter, context.TimeProvider, cancellationToken);
            throw new IOException("Connection lost after a while");
        }

        if (behavior == FakeRunBehavior.IgnoreCancellation)
        {
            await _release.Task;
            Cancelled = cancellationToken.IsCancellationRequested;
            context.Publish(new DriverUpdate { Variables = new Dictionary<string, string> { ["ups.status"] = "OB" } });
            throw new IOException("Late failure");
        }

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Cancelled = true;
            if (behavior == FakeRunBehavior.ThrowWhenCancelled)
            {
                throw new IOException("The port was closed");
            }

            throw;
        }
    }

    /// <summary>Lets a driver created with <see cref="FakeRunBehavior.IgnoreCancellation"/> go on.</summary>
    public void Release() => _release.TrySetResult();

    public void Publish(Dictionary<string, string> vars) =>
        _context!.Publish(new DriverUpdate { Variables = vars });

    public Task<CommandResult> InstantCommandAsync(string command, string? parameter, CancellationToken cancellationToken)
    {
        Commands.Enqueue(parameter is null ? command : $"{command} {parameter}");
        return Operation(command, cancellationToken);
    }

    public Task<CommandResult> SetVariableAsync(string name, string value, CancellationToken cancellationToken)
    {
        Commands.Enqueue($"SET {name}={value}");
        return Operation(name, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>An in-memory configuration store: the test applies configurations and the Changed event follows.</summary>
internal sealed class FakeConfigStore(NutHubConfig initial) : IConfigStore
{
    private NutHubConfig _current = initial;

    public NutHubConfig Current => Volatile.Read(ref _current);

    public event EventHandler<ConfigChangedEventArgs>? Changed;

    public Task<NutHubConfig> UpdateAsync(Action<NutHubConfig> mutate, CommandOrigin origin, string description,
                                          CancellationToken cancellationToken = default)
    {
        NutHubConfig previous = Current;
        NutHubConfig next = NutHubJson.Clone(previous);
        mutate(next);
        Volatile.Write(ref _current, next);
        Changed?.Invoke(this, new ConfigChangedEventArgs(previous, next, origin, description));
        return Task.FromResult(next);
    }
}
