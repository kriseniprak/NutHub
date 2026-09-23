using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NutHub.Core.Drivers;

namespace NutHub.Drivers.Snmp;

public static class SnmpServiceCollectionExtensions
{
    /// <summary>Registers the "snmp" driver factory.</summary>
    public static IServiceCollection AddNutHubSnmpDrivers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IUpsDriverFactory, SnmpDriverFactory>());
        return services;
    }
}
