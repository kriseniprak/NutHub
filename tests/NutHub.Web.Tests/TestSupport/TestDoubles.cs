using NutHub.Core.Abstractions;
using NutHub.Core.Drivers;
using NutHub.Core.Model;

namespace NutHub.Web.Tests.TestSupport;

/// <summary>A driver with a required host, a secret community and a conditionally required option.</summary>
internal sealed class TestDriverFactory : IUpsDriverFactory
{
    public string Id => "testdrv";

    public string DisplayName => "Test driver";

    public string Description => "A driver for the web tests.";

    public DriverPlatforms Platforms => DriverPlatforms.All;

    public IReadOnlyList<DriverOption> Options { get; } =
    [
        new() { Key = "host", Label = "Host", Type = DriverOptionType.Host, Required = true },
        new() { Key = "port", Label = "Port", Type = DriverOptionType.Port, Default = "161" },
        new() { Key = "community", Label = "Community", Type = DriverOptionType.Secret },
        new()
        {
            Key = "mode", Label = "Mode", Type = DriverOptionType.Choice, Default = "a",
            Choices = [new DriverOptionChoice("a", "Mode A"), new DriverOptionChoice("b", "Mode B")],
        },
        new() { Key = "extra", Label = "Extra", Required = true, VisibleWhen = new DriverOptionCondition("mode", ["b"]) },
        new() { Key = "retries", Label = "Retries", Type = DriverOptionType.Integer, Min = 0, Max = 5 },
    ];

    public bool SupportsDiscovery => true;

    public Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DiscoveredDevice>>(
            [new DiscoveredDevice("Test device", "on the bench", new Dictionary<string, string> { ["host"] = "10.0.0.9" }, "bench")]);

    public IUpsDriver Create(DriverCreateContext context) => new IdleDriver();

    /// <summary>A rule the option list cannot express, the way a driver checks its own values.</summary>
    public void ValidateOptions(DriverOptionReader read)
    {
        if (read.GetString("host")?.Contains(':', StringComparison.Ordinal) == true)
        {
            throw new DriverConfigurationException("The option 'host' must not carry a port.", "host");
        }
    }

    private sealed class IdleDriver : IUpsDriver
    {
        public async Task RunAsync(IDriverContext context, CancellationToken cancellationToken)
        {
            context.ReportConnecting("waiting");
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
            }
        }

        public Task<CommandResult> InstantCommandAsync(string command, string? parameter, CancellationToken cancellationToken) =>
            Task.FromResult(CommandResult.NotSupported());

        public Task<CommandResult> SetVariableAsync(string name, string value, CancellationToken cancellationToken) =>
            Task.FromResult(CommandResult.NotSupported());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

/// <summary>Answers every query with two points per variable and remembers the last query.</summary>
internal sealed class FakeHistoryStore : IHistoryStore
{
    public HistoryQuery? LastQuery { get; private set; }

    public Task<HistoryResult> QueryAsync(HistoryQuery query, CancellationToken cancellationToken = default)
    {
        LastQuery = query;
        var series = query.Variables.ToDictionary(
            v => v,
            v => (IReadOnlyList<HistoryPoint>)
            [
                new HistoryPoint(query.From, 99.5, 99, 100),
                new HistoryPoint(query.From.AddSeconds(30), 99.6, 99, 100),
            ]);
        return Task.FromResult(new HistoryResult(query.From, query.To, 30, series));
    }
}
