using Microsoft.Extensions.DependencyInjection;
using NutHub.Core.Abstractions;
using NutHub.Services.HostProtection;
using NutHub.Services.Notifications;
using NutHub.Services.Notifications.Webhooks;

namespace NutHub.Services;

public static class ServicesServiceCollectionExtensions
{
    /// <summary>
    /// Registers notifications (INotificationService) and host protection (IHostProtectionService), replacing the
    /// defaults of Core, and runs both as hosted services. Call after AddNutHubCore.
    /// </summary>
    public static IServiceCollection AddNutHubServices(this IServiceCollection services)
    {
        services.AddHttpClient();
        // Certificates are validated (the default handler); the per-attempt timeout is enforced by the sender.
        services.AddHttpClient(WebhookSender.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(30));

        services.AddSingleton<NotificationService>();
        services.AddSingleton<INotificationService>(sp => sp.GetRequiredService<NotificationService>());
        services.AddHostedService(sp => sp.GetRequiredService<NotificationService>());

        services.AddSingleton<HostProtectionService>();
        services.AddSingleton<IHostProtectionService>(sp => sp.GetRequiredService<HostProtectionService>());
        services.AddHostedService(sp => sp.GetRequiredService<HostProtectionService>());
        return services;
    }
}
