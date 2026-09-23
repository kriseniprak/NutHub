using Microsoft.Extensions.Logging;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Drivers.Net.Common;

namespace NutHub.Drivers.Net.Apcupsd;

/// <summary>
/// Repeats a UPS managed by apcupsd, read through apcupsd's network information server. Read-only: the NIS protocol
/// has no commands, and apcupsd keeps control of the UPS.
/// </summary>
internal sealed class ApcupsdDriver : IUpsDriver
{
    private static readonly IReadOnlyDictionary<string, VariableInfo> NoVariableInfo = new Dictionary<string, VariableInfo>();

    private readonly string _upsName;
    private readonly ApcupsdNisClient _client;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    public ApcupsdDriver(string upsName, ApcupsdNisClient client, TimeProvider time, ILogger<ApcupsdDriver> logger)
    {
        _upsName = upsName;
        _client = client;
        _time = time;
        _logger = logger;
    }

    /// <summary>The waits after failed reads; null for <see cref="ReconnectBackoff.DefaultDelays"/>.</summary>
    internal IReadOnlyList<TimeSpan>? ReconnectDelays { get; init; }

    public async Task RunAsync(IDriverContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var reporter = new DriverStateReporter(context);
        var backoff = new ReconnectBackoff(ReconnectDelays);
        reporter.Connecting($"Connecting to apcupsd at {_client.Endpoint}");

        while (true)
        {
            TimeSpan wait = context.PollInterval;
            try
            {
                IReadOnlyList<string> lines = await _client.FetchStatusAsync(cancellationToken).ConfigureAwait(false);
                ApcupsdReading reading = ApcupsdStatusMapper.ToReading(ApcupsdStatusMapper.ParseFields(lines));
                backoff.Reset();
                if (reading.CommunicationLost)
                {
                    reporter.Disconnected(
                        $"apcupsd at {_client.Endpoint} has lost communication with the UPS (STATUS {reading.Status ?? "COMMLOST"}).");
                }
                else if (!reading.Variables.ContainsKey("ups.status"))
                {
                    reporter.Disconnected($"apcupsd at {_client.Endpoint} sent a status without a usable STATUS field.");
                }
                else
                {
                    var variables = new Dictionary<string, string>(reading.Variables, StringComparer.Ordinal)
                    {
                        ["driver.version.data"] = reading.Version is null
                            ? $"apcupsd at {_client.Endpoint}"
                            : $"apcupsd {reading.Version} at {_client.Endpoint}",
                    };
                    reporter.Publish(new DriverUpdate
                    {
                        Variables = variables,
                        VariableInfo = NoVariableInfo,
                        Commands = [],
                    });
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (NetworkErrors.IsTransient(ex) || ex is OperationCanceledException)
            {
                reporter.Disconnected($"Cannot read the status from apcupsd at {_client.Endpoint}: {NetworkErrors.Describe(ex)}.");
                TimeSpan delay = backoff.NextDelay();
                wait = delay > wait ? delay : wait;
                _logger.LogDebug(ex, "{Ups}: reading apcupsd failed; next attempt in {Delay}.", _upsName, wait);
            }

            await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<CommandResult> InstantCommandAsync(string command, string? parameter, CancellationToken cancellationToken) =>
        Task.FromResult(CommandResult.NotSupported("apcupsd does not accept commands over the network."));

    public Task<CommandResult> SetVariableAsync(string name, string value, CancellationToken cancellationToken) =>
        Task.FromResult(new CommandResult(CommandStatus.ReadOnly, "apcupsd does not accept variable writes over the network."));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
