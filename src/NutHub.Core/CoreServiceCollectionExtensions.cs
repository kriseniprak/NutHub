using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Drivers;
using NutHub.Core.Drivers.Simulated;
using NutHub.Core.Logging;
using NutHub.Core.Runtime;
using NutHub.Core.Security;

namespace NutHub.Core;

public static class CoreServiceCollectionExtensions
{
    /// <summary>
    /// Registers the core of NutHub: configuration, UPS registry, driver manager, event hub, security primitives,
    /// the simulated driver, and default (in-memory / unavailable) implementations of the contracts in
    /// <see cref="Abstractions"/> that other projects replace.
    /// </summary>
    public static IServiceCollection AddNutHubCore(this IServiceCollection services, NutHubPaths paths)
    {
        services.AddSingleton(paths);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<EventHub>();

        services.AddSingleton<ISecretProtector>(_ =>
        {
            paths.EnsureCreated();
            return AesSecretProtector.LoadOrCreate(paths.SecretKeyFile);
        });
        services.AddSingleton<IPasswordHasher, Pbkdf2PasswordHasher>();

        services.AddSingleton<IUpsDriverFactory, SimulatedDriverFactory>();
        services.AddSingleton<IDriverCatalog>(sp => new DriverCatalog(sp.GetServices<IUpsDriverFactory>()));

        services.AddSingleton<JsonConfigStore>();
        services.AddSingleton<IConfigStore>(sp => sp.GetRequiredService<JsonConfigStore>());

        services.AddSingleton<UpsRegistry>();
        services.AddSingleton<IUpsRegistry>(sp => sp.GetRequiredService<UpsRegistry>());
        services.AddSingleton<DriverManager>();
        services.AddSingleton<IDriverManager>(sp => sp.GetRequiredService<DriverManager>());
        services.AddSingleton<NutSessionRegistry>();

        services.AddSingleton<InMemoryLogSink>();
        services.AddSingleton<ILoggerProvider, InMemoryLogProvider>();

        // Defaults, replaced by the projects that implement them (they register with AddSingleton, which wins).
        services.TryAddSingleton<IEventStore, InMemoryEventStore>();
        services.TryAddSingleton<IHistoryStore, NullHistoryStore>();
        services.TryAddSingleton<INotificationService, NullNotificationService>();
        services.TryAddSingleton<IHostProtectionService, NullHostProtectionService>();
        services.TryAddSingleton<INutServerStatus, NullNutServerStatus>();

        // Order matters: the event recorder subscribes before the drivers start producing events.
        services.AddHostedService<EventRecorderService>();
        services.AddHostedService<ConfigWatcherService>();
        services.AddHostedService(sp => sp.GetRequiredService<DriverManager>());
        return services;
    }
}

/// <summary>Starts watching the configuration file for edits made by hand.</summary>
internal sealed class ConfigWatcherService(JsonConfigStore store) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        store.StartWatching();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
