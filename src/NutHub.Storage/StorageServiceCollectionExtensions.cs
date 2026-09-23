using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using NutHub.Core;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Runtime;
using NutHub.Storage.Database;
using NutHub.Storage.Events;
using NutHub.Storage.History;
using NutHub.Storage.Maintenance;

namespace NutHub.Storage;

public static class StorageServiceCollectionExtensions
{
    /// <summary>
    /// Registers the SQLite event log and history (<see cref="IEventStore"/>, <see cref="IHistoryStore"/>) in
    /// <c>nuthub.db</c> in the data directory, the history recorder and the database housekeeping. They replace
    /// Core's in-memory defaults whatever the order of the calls (Core only adds its defaults when nothing else is
    /// registered, and the last registration wins otherwise). Requires <see cref="NutHubPaths"/>,
    /// <see cref="IConfigStore"/>, <see cref="IUpsRegistry"/> and logging, all provided by the host and
    /// <c>AddNutHubCore</c>.
    /// </summary>
    public static IServiceCollection AddNutHubStorage(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton(sp => new SqliteDatabase(sp.GetRequiredService<NutHubPaths>(),
                                                       sp.GetRequiredService<TimeProvider>(),
                                                       sp.GetRequiredService<ILogger<SqliteDatabase>>()));

        services.AddSingleton(sp => new SqliteEventStore(sp.GetRequiredService<SqliteDatabase>(),
                                                         sp.GetRequiredService<ILogger<SqliteEventStore>>()));
        services.AddSingleton<IEventStore>(sp => sp.GetRequiredService<SqliteEventStore>());

        services.AddSingleton(sp => new SqliteHistoryStore(sp.GetRequiredService<SqliteDatabase>(),
                                                           sp.GetRequiredService<IConfigStore>(),
                                                           sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IHistoryStore>(sp => sp.GetRequiredService<SqliteHistoryStore>());

        services.AddSingleton(sp => new HistoryMaintenance(sp.GetRequiredService<SqliteDatabase>(),
                                                           sp.GetRequiredService<IConfigStore>(),
                                                           sp.GetRequiredService<TimeProvider>()));

        services.AddHostedService(sp => new StorageMaintenanceService(
            sp.GetRequiredService<SqliteDatabase>(),
            sp.GetRequiredService<HistoryMaintenance>(),
            sp.GetRequiredService<SqliteEventStore>(),
            sp.GetRequiredService<IConfigStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<StorageMaintenanceService>>()));

        services.AddHostedService(sp => new HistoryRecorderService(
            sp.GetRequiredService<IUpsRegistry>(),
            sp.GetRequiredService<IConfigStore>(),
            sp.GetRequiredService<SqliteHistoryStore>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<HistoryRecorderService>>()));

        return services;
    }
}
