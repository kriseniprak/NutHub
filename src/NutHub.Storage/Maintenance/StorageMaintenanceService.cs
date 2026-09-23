using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NutHub.Core.Configuration;
using NutHub.Storage.Database;
using NutHub.Storage.Events;
using NutHub.Storage.History;

namespace NutHub.Storage.Maintenance;

/// <summary>
/// The database housekeeping: roll-ups shortly after each 5-minute bucket closes, retention at startup and every
/// hour, and once a day an integrity check, <c>PRAGMA optimize</c> and an incremental vacuum. Failures are logged
/// and retried at the next run; they never stop NutHub.
/// </summary>
internal sealed class StorageMaintenanceService : BackgroundService
{
    internal static readonly TimeSpan RetentionInterval = TimeSpan.FromHours(1);
    internal static readonly TimeSpan OptimizeInterval = TimeSpan.FromDays(1);

    // Leaves the sampler time to store the last sample of the bucket that just closed.
    internal static readonly TimeSpan RollupDelay = TimeSpan.FromSeconds(20);

    // The first daily job waits a while after startup, when the machine is busy with more urgent work.
    private static readonly TimeSpan FirstOptimizeDelay = TimeSpan.FromHours(1);

    private readonly SqliteDatabase _database;
    private readonly HistoryMaintenance _history;
    private readonly SqliteEventStore _events;
    private readonly IConfigStore _config;
    private readonly TimeProvider _time;
    private readonly ILogger<StorageMaintenanceService> _logger;

    public StorageMaintenanceService(SqliteDatabase database, HistoryMaintenance history, SqliteEventStore events,
                                     IConfigStore config, TimeProvider time,
                                     ILogger<StorageMaintenanceService> logger)
    {
        _database = database;
        _history = history;
        _events = events;
        _config = config;
        _time = time;
        _logger = logger;
    }

    /// <summary>The next time to roll up: <see cref="RollupDelay"/> after the next 5-minute boundary.</summary>
    internal static DateTimeOffset NextRollup(DateTimeOffset now)
    {
        long boundary = HistoryLayout.AlignDown(now.ToUnixTimeSeconds(), HistoryLayout.RollupSeconds);
        DateTimeOffset next = DateTimeOffset.FromUnixTimeSeconds(boundary) + RollupDelay;
        return next > now ? next : next.AddSeconds(HistoryLayout.RollupSeconds);
    }

    internal async Task RollUpAsync(CancellationToken cancellationToken)
    {
        try
        {
            int rows = await _history.RollUpAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogDebug("History roll-up wrote {Rows} rows.", rows);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "The history roll-up failed; it will be retried in a few minutes.");
        }
    }

    internal async Task ApplyRetentionAsync(CancellationToken cancellationToken)
    {
        try
        {
            RetentionResult history = await _history.DeleteExpiredAsync(cancellationToken).ConfigureAwait(false);
            int eventDays = Math.Max(1, _config.Current.History.EventRetentionDays);
            int events = await _events.DeleteOlderThanAsync(_time.GetUtcNow().AddDays(-eventDays), cancellationToken)
                                      .ConfigureAwait(false);
            if (history.Samples + history.Rollups + events > 0)
            {
                _logger.LogInformation(
                    "Retention: deleted {Samples} raw samples, {Rollups} roll-ups and {Events} events.",
                    history.Samples, history.Rollups, events);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Deleting expired history and events failed; it will be retried in an hour.");
        }
    }

    internal async Task OptimizeAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await _database.CheckIntegrityAsync(cancellationToken).ConfigureAwait(false))
            {
                await _database.OptimizeAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Replaces the damaged file now rather than at the next sample.
                await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "The daily database maintenance failed.");
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            try
            {
                // Opens (creating, migrating or replacing) the database now, so problems show in the log at startup.
                await _database.InitializeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "The database could not be opened.");
            }

            await RollUpAsync(stoppingToken).ConfigureAwait(false);
            await ApplyRetentionAsync(stoppingToken).ConfigureAwait(false);
            DateTimeOffset lastRetention = _time.GetUtcNow();
            DateTimeOffset lastOptimize = lastRetention - OptimizeInterval + FirstOptimizeDelay;

            while (!stoppingToken.IsCancellationRequested)
            {
                DateTimeOffset now = _time.GetUtcNow();
                await Task.Delay(NextRollup(now) - now, _time, stoppingToken).ConfigureAwait(false);
                await RollUpAsync(stoppingToken).ConfigureAwait(false);

                now = _time.GetUtcNow();
                if (IsDue(now, lastRetention, RetentionInterval))
                {
                    await ApplyRetentionAsync(stoppingToken).ConfigureAwait(false);
                    lastRetention = now;
                }

                if (IsDue(now, lastOptimize, OptimizeInterval))
                {
                    await OptimizeAsync(stoppingToken).ConfigureAwait(false);
                    lastOptimize = now;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    // A clock set back must not postpone the job by the amount it went back.
    private static bool IsDue(DateTimeOffset now, DateTimeOffset last, TimeSpan interval) =>
        now - last >= interval || now < last;
}
