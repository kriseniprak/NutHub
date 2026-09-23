using Microsoft.Extensions.DependencyInjection;
using NutHub.Core.Drivers;

namespace NutHub.Drivers.Hid.Tests;

public sealed class ServiceRegistrationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Registers_both_driver_factories(bool withLogging)
    {
        var services = new ServiceCollection();
        if (withLogging)
        {
            services.AddLogging();
        }

        services.AddNutHubHidDrivers();
        using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        IUpsDriverFactory[] factories = provider.GetServices<IUpsDriverFactory>().ToArray();

        Assert.Equal(["usbhid", "winbattery"], factories.Select(f => f.Id));
        Assert.Same(factories[0], provider.GetServices<IUpsDriverFactory>().First());
    }
}
