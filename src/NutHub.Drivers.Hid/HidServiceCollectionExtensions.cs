using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NutHub.Core.Drivers;
using NutHub.Drivers.Hid.UsbHid;
using NutHub.Drivers.Hid.WinBattery;

namespace NutHub.Drivers.Hid;

public static class HidServiceCollectionExtensions
{
    /// <summary>
    /// Registers the USB HID Power Device driver ("usbhid") and the Windows battery driver ("winbattery"). The
    /// winbattery factory is registered on every system so the catalog can show it as unsupported elsewhere.
    /// </summary>
    public static IServiceCollection AddNutHubHidDrivers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IUpsDriverFactory>(sp => new UsbHidDriverFactory(sp.GetService<ILoggerFactory>()));
        services.AddSingleton<IUpsDriverFactory>(sp => new WinBatteryDriverFactory(sp.GetService<ILoggerFactory>()));
        return services;
    }
}
