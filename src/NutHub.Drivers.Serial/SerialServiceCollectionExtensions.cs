using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NutHub.Core.Drivers;
using NutHub.Drivers.Serial.ApcSmart;
using NutHub.Drivers.Serial.Megatec;

namespace NutHub.Drivers.Serial;

/// <summary>Registration of the serial-line drivers in the application services.</summary>
public static class SerialServiceCollectionExtensions
{
    /// <summary>
    /// Registers the serial-line drivers (Megatec/Q1 family, APC Smart). Calling it twice registers them once, since
    /// the driver catalog refuses two drivers with the same id.
    /// </summary>
    public static IServiceCollection AddNutHubSerialDrivers(this IServiceCollection services)
    {
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IUpsDriverFactory, MegatecDriverFactory>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IUpsDriverFactory, ApcSmartDriverFactory>());
        return services;
    }
}
