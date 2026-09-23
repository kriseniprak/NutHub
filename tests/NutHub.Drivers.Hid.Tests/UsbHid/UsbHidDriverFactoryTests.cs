using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Drivers;
using NutHub.Drivers.Hid.Tests.Support;
using NutHub.Drivers.Hid.UsbHid;
using NutHub.Drivers.Hid.UsbHid.Subdrivers;

namespace NutHub.Drivers.Hid.Tests.UsbHid;

public sealed class UsbHidDriverFactoryTests
{
    [Fact]
    public void Describes_itself_and_its_options()
    {
        var factory = new UsbHidDriverFactory();

        Assert.Equal("usbhid", factory.Id);
        Assert.True(factory.SupportsDiscovery);
        Assert.Equal(DriverPlatforms.Windows | DriverPlatforms.Linux, factory.Platforms);
        Assert.Equal(
            ["vendorId", "productId", "serial", "product", "devicePath", "subdriver", "onlineDischarge",
             "onlineDischargeCalibration", "offDelay", "onDelay"],
            factory.Options.Select(o => o.Key));
        DriverOption subdriver = factory.Options.Single(o => o.Key == "subdriver");
        Assert.Equal(["auto", .. SubdriverCatalog.Ids], subdriver.Choices!.Select(c => c.Value));
        Assert.Contains("apc", SubdriverCatalog.Ids);
        Assert.Contains("mge", SubdriverCatalog.Ids);
        Assert.Contains("cps", SubdriverCatalog.Ids);
        Assert.Contains("tripplite", SubdriverCatalog.Ids);
    }

    [Fact]
    public void Reads_valid_options()
    {
        UsbHidSettings settings = UsbHidDriverFactory.ReadSettings(Reader(new()
        {
            ["vendorId"] = "0x051D",
            ["productId"] = "0002",
            ["serial"] = " 3B1527X15613 ",
            ["subdriver"] = "APC",
            ["onlineDischarge"] = "true",
            ["offDelay"] = "60",
        }));

        Assert.Equal(0x051d, settings.VendorId);
        Assert.Equal(0x0002, settings.ProductId);
        Assert.Equal("3B1527X15613", settings.Serial);
        Assert.Equal("apc", settings.Subdriver);
        Assert.True(settings.OnlineDischargeOnBattery);
        Assert.False(settings.OnlineDischargeCalibration);
        Assert.Equal(60, settings.OffDelay);
        Assert.Null(settings.OnDelay);
    }

    [Theory]
    [InlineData("vendorId", "apc")]
    [InlineData("vendorId", "12345")]
    [InlineData("productId", "-1")]
    [InlineData("subdriver", "nosuch")]
    [InlineData("onlineDischarge", "maybe")]
    [InlineData("offDelay", "-5")]
    public void Refuses_invalid_options_naming_the_option(string key, string value)
    {
        var error = Assert.Throws<DriverConfigurationException>(() => UsbHidDriverFactory.ReadSettings(Reader(new() { [key] = value })));

        Assert.Equal(key, error.OptionKey);
    }

    [Fact]
    public void Creates_a_driver()
    {
        var factory = new UsbHidDriverFactory(new FakeHidDeviceSource(), NullLogger.Instance);

        IUpsDriver driver = factory.Create(new DriverCreateContext
        {
            UpsName = "rack1",
            Options = new Dictionary<string, string> { ["vendorId"] = "051d" },
            PollInterval = TimeSpan.FromSeconds(2),
            LoggerFactory = NullLoggerFactory.Instance,
            TimeProvider = TimeProvider.System,
            Services = new EmptyServices(),
        });

        Assert.IsType<UsbHidDriver>(driver);
    }

    [Fact]
    public async Task Discovers_through_its_device_source()
    {
        var factory = new UsbHidDriverFactory(new FakeHidDeviceSource(SyntheticUps.Create()), NullLogger.Instance);

        IReadOnlyList<DiscoveredDevice> found = await factory.DiscoverAsync(CancellationToken.None);

        Assert.Equal("Acme UPS 1000 (USB 1234:5678)", Assert.Single(found).Title);
    }

    [Fact]
    public async Task Discovery_on_this_machine_does_not_fail()
    {
        // Real HID enumeration: whatever is attached, the call must complete without throwing.
        IReadOnlyList<DiscoveredDevice> found = await new UsbHidDriverFactory().DiscoverAsync(CancellationToken.None);

        Assert.NotNull(found);
    }

    private static DriverOptionReader Reader(Dictionary<string, string> options) => new(options);

    private sealed class EmptyServices : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
