using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NutHub.Core.Drivers;
using NutHub.Drivers.Net.Apcupsd;
using NutHub.Drivers.Net.Nut;

namespace NutHub.Drivers.Net;

/// <summary>Registration of the drivers that reach UPSes through other UPS servers on the network.</summary>
public static class NetServiceCollectionExtensions
{
    /// <summary>Registers the network drivers (upstream NUT server, apcupsd).</summary>
    public static IServiceCollection AddNutHubNetworkDrivers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // TryAddEnumerable keeps a second call from registering the same driver id twice, which DriverCatalog rejects.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IUpsDriverFactory, NutUpstreamDriverFactory>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IUpsDriverFactory, ApcupsdDriverFactory>());
        return services;
    }
}
