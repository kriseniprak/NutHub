using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Drivers;
using NutHub.Drivers.Net.Apcupsd;
using NutHub.Drivers.Net.Nut;

namespace NutHub.Drivers.Net.Tests;

public sealed class FactoryTests
{
    private static DriverCreateContext Context(params (string Key, string Value)[] options) => new()
    {
        UpsName = "test",
        Options = options.ToDictionary(o => o.Key, o => o.Value, StringComparer.OrdinalIgnoreCase),
        PollInterval = TimeSpan.FromSeconds(2),
        LoggerFactory = NullLoggerFactory.Instance,
        TimeProvider = TimeProvider.System,
        Services = new ServiceCollection().BuildServiceProvider(),
    };

    [Fact]
    public void Both_factories_are_registered_once()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNutHubNetworkDrivers().AddNutHubNetworkDrivers();
        using ServiceProvider provider = services.BuildServiceProvider();

        IUpsDriverFactory[] factories = provider.GetServices<IUpsDriverFactory>().ToArray();

        Assert.Equal(["nut", "apcupsd"], factories.Select(f => f.Id));
        _ = new DriverCatalog(factories);
    }

    [Fact]
    public void Nut_factory_describes_its_options()
    {
        var factory = new NutUpstreamDriverFactory();

        Assert.False(factory.SupportsDiscovery);
        Assert.Equal(DriverPlatforms.All, factory.Platforms);
        Assert.Equal(["host", "port", "upsName", "username", "password", "useTls", "tlsVerify", "login", "timeoutMs"],
                     factory.Options.Select(o => o.Key));
        Assert.True(factory.Options.Single(o => o.Key == "password").IsSecret);
        Assert.Equal(["none", "chain"], factory.Options.Single(o => o.Key == "tlsVerify").Choices!.Select(c => c.Value));
        Assert.True(factory.Options.Single(o => o.Key == "login").Advanced);
    }

    [Fact]
    public void Nut_factory_creates_a_driver_with_valid_options()
    {
        var factory = new NutUpstreamDriverFactory();
        IUpsDriver driver = factory.Create(Context(("host", "nas.local"), ("upsName", "ups"), ("useTls", "true"),
                                                   ("tlsVerify", "chain"), ("username", "u"), ("password", "p"),
                                                   ("login", "true")));
        Assert.IsType<NutUpstreamDriver>(driver);

        NutUpstreamSettings settings = NutUpstreamDriverFactory.ReadSettings(
            new DriverOptionReader(new Dictionary<string, string> { ["host"] = "::1", ["upsName"] = "ups" }));
        Assert.Equal(3493, settings.Client.Port);
        Assert.Equal("[::1]:3493", settings.Client.Endpoint);
        Assert.Equal(TimeSpan.FromSeconds(5), settings.Client.Timeout);
        Assert.False(settings.Client.UseTls);
        Assert.False(settings.HasCredentials);
    }

    [Theory]
    [InlineData("host", "upsName", "ups")]
    [InlineData("upsName", "host", "nas")]
    [InlineData("password", "username", "admin")]
    [InlineData("username", "password", "secret")]
    public void Nut_factory_rejects_incomplete_options(string expectedKey, string key, string value)
    {
        var options = new List<(string, string)> { (key, value) };
        if (key != "host" && expectedKey != "host")
        {
            options.Add(("host", "nas"));
        }

        if (key != "upsName" && expectedKey != "upsName")
        {
            options.Add(("upsName", "ups"));
        }

        var ex = Assert.Throws<DriverConfigurationException>(() => new NutUpstreamDriverFactory().Create(Context([.. options])));
        Assert.Equal(expectedKey, ex.OptionKey);
    }

    [Theory]
    [InlineData("login", "true", "username")]
    [InlineData("host", "bad host", "host")]
    [InlineData("upsName", "two words", "upsName")]
    [InlineData("port", "70000", "port")]
    [InlineData("timeoutMs", "10", "timeoutMs")]
    [InlineData("tlsVerify", "sometimes", "tlsVerify")]
    public void Nut_factory_rejects_invalid_options(string key, string value, string expectedKey)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["host"] = "nas",
            ["upsName"] = "ups",
            [key] = value,
        };

        var ex = Assert.Throws<DriverConfigurationException>(
            () => NutUpstreamDriverFactory.ReadSettings(new DriverOptionReader(options)));
        Assert.Equal(expectedKey, ex.OptionKey);
    }

    [Fact]
    public void Apcupsd_factory_uses_defaults_and_validates()
    {
        var factory = new ApcupsdDriverFactory();

        Assert.True(factory.SupportsDiscovery);
        Assert.Equal(["host", "port", "timeoutMs"], factory.Options.Select(o => o.Key));
        Assert.IsType<ApcupsdDriver>(factory.Create(Context()));
        Assert.Equal("port", Assert.Throws<DriverConfigurationException>(() => factory.Create(Context(("port", "0")))).OptionKey);
        Assert.Equal("host", Assert.Throws<DriverConfigurationException>(() => factory.Create(Context(("host", "a b")))).OptionKey);
    }
}
