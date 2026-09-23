using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NutHub.Web.Auth;
using NutHub.Web.Hosting;

namespace NutHub.Web;

public static class WebServiceCollectionExtensions
{
    /// <summary>Registers the web panel (its own Kestrel server, started and reconfigured from the configuration).</summary>
    public static IServiceCollection AddNutHubWeb(this IServiceCollection services)
    {
        // Kept by NutHub's container so that restarting the panel does not reset the sign-in counters.
        services.TryAddSingleton<LoginRateLimiter>();
        services.AddSingleton<WebPanelHost>();
        services.AddHostedService(sp => sp.GetRequiredService<WebPanelHost>());
        return services;
    }
}
