using Lextm.SharpSnmpLib.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Drivers;
using NutHub.Drivers.Snmp.Transport;

namespace NutHub.Drivers.Snmp.Tests;

public sealed class SnmpOptionsTests
{
    private static SnmpSettings Parse(params (string Key, string Value)[] options) =>
        SnmpOptions.Parse(new DriverOptionReader(options.ToDictionary(o => o.Key, o => o.Value, StringComparer.OrdinalIgnoreCase)));

    private static DriverConfigurationException Invalid(params (string Key, string Value)[] options) =>
        Assert.Throws<DriverConfigurationException>(() => Parse(options));

    [Fact]
    public void Defaults_follow_snmp_ups()
    {
        SnmpSettings s = Parse(("host", "192.168.1.20"));
        Assert.Equal(161, s.Port);
        Assert.Equal(SnmpProtocolVersion.V2c, s.Version);
        Assert.Equal("public", s.Community);
        Assert.Equal("public", s.WriteCommunity);
        Assert.Equal("auto", s.Mib);
        Assert.Equal(TimeSpan.FromSeconds(1), s.Timeout);
        Assert.Equal(3, s.Retries);
        Assert.Equal("192.168.1.20:161", s.Target);
    }

    [Fact]
    public void Community_options_and_version_spellings()
    {
        SnmpSettings s = Parse(("host", "ups.example.com"), ("version", "v1"), ("community", "secret"),
                               ("writeCommunity", "private"), ("mibs", "APCC"), ("port", "1161"));
        Assert.Equal(SnmpProtocolVersion.V1, s.Version);
        Assert.Equal("secret", s.Community);
        Assert.Equal("private", s.WriteCommunity);
        Assert.Equal("apcc", s.Mib);
        Assert.Equal("ups.example.com:1161", s.Target);
        Assert.Equal("[fe80::1]:161", Parse(("host", "[fe80::1]")).Target);
    }

    [Fact]
    public void V3_auth_priv()
    {
        SnmpSettings s = Parse(("host", "10.0.0.5"), ("version", "3"), ("secName", "nut"), ("secLevel", "authPriv"),
                               ("authProtocol", "SHA256"), ("authPassword", "authpass1"),
                               ("privProtocol", "AES256"), ("privPassword", "privpass1"));
        Assert.Equal(SnmpSecurityLevel.AuthPriv, s.SecurityLevel);
        Assert.Equal(SnmpAuthProtocol.Sha256, s.AuthProtocol);
        Assert.Equal(SnmpPrivProtocol.Aes256, s.PrivProtocol);
        Assert.IsType<AES256PrivacyProvider>(SnmpSecurity.CreatePrivacyProvider(s));
    }

    // MD5 and SHA-1 are obsolete but still what many cards offer.
#pragma warning disable CS0618
    [Theory]
    [InlineData("MD5", typeof(MD5AuthenticationProvider))]
    [InlineData("SHA", typeof(SHA1AuthenticationProvider))]
#pragma warning restore CS0618
    [InlineData("SHA384", typeof(SHA384AuthenticationProvider))]
    [InlineData("SHA512", typeof(SHA512AuthenticationProvider))]
    public void V3_auth_no_priv_protocols(string protocol, Type provider)
    {
        SnmpSettings s = Parse(("host", "10.0.0.5"), ("version", "3"), ("secName", "nut"), ("secLevel", "authNoPriv"),
                               ("authProtocol", protocol), ("authPassword", "authpass1"));
        IPrivacyProvider privacy = SnmpSecurity.CreatePrivacyProvider(s);
        Assert.IsType<DefaultPrivacyProvider>(privacy);
        Assert.IsType(provider, privacy.AuthenticationProvider);
    }

    [Theory]
    [InlineData("host", "", "host")]
    [InlineData("host", "bad host", "host")]
    [InlineData("version", "4", "version")]
    [InlineData("mibs", "nosuchmib", "mibs")]
    [InlineData("port", "70000", "port")]
    [InlineData("timeoutMs", "10", "timeoutMs")]
    [InlineData("retries", "many", "retries")]
    public void Invalid_options_name_the_option(string key, string value, string expected)
    {
        var options = new List<(string, string)> { ("host", "192.168.1.20") };
        options.RemoveAll(o => o.Item1 == key);
        options.Add((key, value));
        Assert.Equal(expected, Invalid([.. options]).OptionKey);
    }

    [Fact]
    public void V3_combinations_are_checked()
    {
        Assert.Equal("secName", Invalid(("host", "h"), ("version", "3")).OptionKey);
        Assert.Equal("authPassword",
                     Invalid(("host", "h"), ("version", "3"), ("secName", "u"), ("secLevel", "authNoPriv")).OptionKey);
        Assert.Equal("authPassword",
                     Invalid(("host", "h"), ("version", "3"), ("secName", "u"), ("secLevel", "authNoPriv"),
                             ("authPassword", "short")).OptionKey);
        Assert.Equal("privPassword",
                     Invalid(("host", "h"), ("version", "3"), ("secName", "u"), ("secLevel", "authPriv"),
                             ("authPassword", "longenough")).OptionKey);
        Assert.Equal("authProtocol",
                     Invalid(("host", "h"), ("version", "3"), ("secName", "u"), ("secLevel", "authNoPriv"),
                             ("authProtocol", "SHA224"), ("authPassword", "longenough")).OptionKey);
        Assert.Equal("secLevel", Invalid(("host", "h"), ("version", "3"), ("secName", "u"), ("secLevel", "paranoid")).OptionKey);
    }

    [Fact]
    public void Factory_describes_its_options_and_is_registered()
    {
        var factory = new SnmpDriverFactory();
        Assert.Equal("snmp", factory.Id);
        Assert.False(factory.SupportsDiscovery);
        DriverOption community = factory.Options.Single(o => o.Key == "community");
        Assert.True(community.IsSecret);
        Assert.Equal(["1", "2c"], community.VisibleWhen!.Values);
        Assert.Equal(["3"], factory.Options.Single(o => o.Key == "secName").VisibleWhen!.Values);
        Assert.Contains(factory.Options.Single(o => o.Key == "mibs").Choices!, c => c.Value == "ietf");
        Assert.True(factory.Options.Single(o => o.Key == "timeoutMs").Advanced);

        using ServiceProvider services = new ServiceCollection().AddNutHubSnmpDrivers().AddNutHubSnmpDrivers().BuildServiceProvider();
        Assert.Single(services.GetServices<IUpsDriverFactory>(), f => f.Id == "snmp");
    }

    [Fact]
    public void Factory_validates_in_create()
    {
        var factory = new SnmpDriverFactory();
        var context = new DriverCreateContext
        {
            UpsName = "ups",
            Options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["version"] = "3", ["host"] = "h" },
            PollInterval = TimeSpan.FromSeconds(2),
            LoggerFactory = NullLoggerFactory.Instance,
            TimeProvider = TimeProvider.System,
            Services = new ServiceCollection().BuildServiceProvider(),
        };
        Assert.Equal("secName", Assert.Throws<DriverConfigurationException>(() => factory.Create(context)).OptionKey);
    }
}
