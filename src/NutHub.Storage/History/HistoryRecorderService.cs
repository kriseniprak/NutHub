using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Core.Runtime;

namespace NutHub.Storage.History;

/// <summary>
/// Samples every UPS each <c>history.sampleIntervalSeconds</c> (on multiples of the interval, so series from
/// different UPSes line up) and stores the numeric values of <c>history.variables</c>. Only fresh data is recorded:
/// a UPS whose data is stale or whose driver is not connected leaves a gap rather than repeating its last values.
/// </summary>
internal sealed class HistoryRecorderService : BackgroundService
{
    private const int MinimumIntervalSeconds = 5;
    private const int MaximumIntervalSeconds = 3600;

    private readonly Func<IEnumerable<UpsSnapshot>> _snapshots;
    private readonly IConfigStore _config;
    private readonly SqliteHistoryStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<HistoryRecorderService> _logger;
    private CancellationTokenSource? _wait;
    private int _consecutiveFailures;

    public HistoryRecorderService(IUpsRegistry registry, IConfigStore config, SqliteHistoryStore store,
                                  TimeProvider time, ILogger<HistoryRecorderService> logger)
        : this(() => registry.Units.Select(u => u.Snapshot), config, store, time, logger)
    {
    }

    internal HistoryRecorderService(Func<IEnumerable<UpsSnapshot>> snapshots, IConfigStore config,
                                    SqliteHistoryStore store, TimeProvider time,
                                    ILogger<HistoryRecorderService> logger)
    {
        _snapshots = snapshots;
        _config = config;
        _store = store;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Takes one sample of every UPS, stamped <paramref name="timestamp"/> (default: now); returns the number of
    /// values stored.
    /// </summary>
    internal async Task<int> SampleOnceAsync(DateTimeOffset? timestamp = null,
                                             CancellationToken cancellationToken = default)
    {
        HistorySettings settings = _config.Current.History;
        if (!settings.Enabled)
        {
            return 0;
        }

        List<HistorySample> samples = CollectSamples(_snapshots(), settings.Variables);
        return await _store.AppendSamplesAsync(timestamp ?? _time.GetUtcNow(), samples, cancellationToken)
                           .ConfigureAwait(false);
    }

    /// <summary>The finite numeric values of <paramref name="variables"/> in the snapshots with fresh data.</summary>
    internal static List<HistorySample> CollectSamples(IEnumerable<UpsSnapshot> snapshots,
                                                       IReadOnlyList<string>? variables)
    {
        var samples = new List<HistorySample>();
        if (variables is null || variables.Count == 0)
        {
            return samples;
        }

        string[] wanted = variables.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim())
                                   .Distinct(StringComparer.Ordinal).ToArray();
        foreach (UpsSnapshot snapshot in snapshots)
        {
            if (!snapshot.IsAvailable)
            {
                continue;
            }

            foreach (string variable in wanted)
            {
                // GetNumber rejects text ("OL CHRG"), NaN and infinities.
                if (snapshot.GetNumber(variable) is { } value)
                {
                    samples.Add(new HistorySample(snapshot.Name, variable, value));
                }
            }
        }

        return samples;
    }

    /// <summary>The next multiple of the interval strictly after <paramref name="now"/>.</summary>
    internal static DateTimeOffset NextTick(DateTimeOffset now, int intervalSeconds)
    {
        long seconds = now.ToUnixTimeSeconds();
        return DateTimeOffset.FromUnixTimeSeconds((seconds / intervalSeconds + 1) * intervalSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _config.Changed += OnConfigChanged;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                int interval = CurrentInterval();
                DateTimeOffset tick = NextTick(_time.GetUtcNow(), interval);
                if (!await WaitAsync(tick, interval, stoppingToken).ConfigureAwait(false))
                {
                    // The interval changed: compute the next tick again with the new one.
                    continue;
                }

                // Stamped with the tick rather than the moment the timer fired, so series stay regular; after a
                // late wake-up (a suspended machine) or a clock change during the wait, the real time is used.
                DateTimeOffset now = _time.GetUtcNow();
                bool onTime = (now - tick).Duration() < TimeSpan.FromSeconds(interval);
                await RecordAsync(onTime ? tick : now, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _config.Changed -= OnConfigChanged;
        }
    }

    private int CurrentInterval() =>
        Math.Clamp(_config.Current.History.SampleIntervalSeconds, MinimumIntervalSeconds, MaximumIntervalSeconds);

    private async Task RecordAsync(DateTimeOffset timestamp, CancellationToken stoppingToken)
    {
        try
        {
            await SampleOnceAsync(timestamp, stoppingToken).ConfigureAwait(false);
            if (_consecutiveFailures > 0)
            {
                _logger.LogInformation("History recording works again after {Count} failed rounds.",
                                       _consecutiveFailures);
                _consecutiveFailures = 0;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            // Logged once per outage, not every interval.
            if (_consecutiveFailures++ == 0)
            {
                _logger.LogError(ex, "Could not record the UPS history; will keep trying every interval.");
            }
            else
            {
                _logger.LogDebug(ex, "History recording failed again.");
            }
        }
    }

    /// <summary>Waits until <paramref name="tick"/>; false when woken early by a change of the interval.</summary>
    private async Task<bool> WaitAsync(DateTimeOffset tick, int interval, CancellationToken stoppingToken)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Volatile.Write(ref _wait, wait);
        try
        {
            // A change made between reading the interval and publishing the wait would otherwise go unnoticed.
            if (CurrentInterval() != interval)
            {
                return false;
            }

            TimeSpan delay = tick - _time.GetUtcNow();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _time, wait.Token).ConfigureAwait(false);
            }

            return true;
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            Interlocked.CompareExchange(ref _wait, null, wait);
        }
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        HistorySettings before = e.Previous.History;
        HistorySettings after = e.Current.History;
        if (before.SampleIntervalSeconds == after.SampleIntervalSeconds && before.Enabled == after.Enabled)
        {
            return;
        }

        try
        {
            Volatile.Read(ref _wait)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The wait just ended on its own.
        }
    }
}
