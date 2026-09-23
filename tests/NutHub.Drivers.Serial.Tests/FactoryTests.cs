using Microsoft.Extensions.DependencyInjection;
using NutHub.Core.Drivers;
using NutHub.Drivers.Serial.ApcSmart;
using NutHub.Drivers.Serial.Megatec;
using NutHub.Drivers.Serial.Tests.Fakes;
using NutHub.Drivers.Serial.Transport;

namespace NutHub.Drivers.Serial.Tests;

/// <summary>Option parsing, validation messages and registration of both drivers.</summary>
public sealed class FactoryTests
{
    private static DriverOptionReader Read(params (string Key, string Value)[] values) => new(TestSettings.Options(values));

    [Fact]
    public void Both_factories_are_registered_once()
    {
        var services = new ServiceCollection();

        services.AddNutHubSerialDrivers().AddNutHubSerialDrivers();

        var factories = services.Where(d => d.ServiceType == typeof(IUpsDriverFactory)).Select(d => d.ImplementationType).ToList();
        Assert.Equal([typeof(MegatecDriverFactory), typeof(ApcSmartDriverFactory)], factories);
    }

    [Fact]
    public void Option_keys_are_unique_and_visibility_conditions_point_to_existing_options()
    {
        foreach (IUpsDriverFactory factory in new IUpsDriverFactory[] { new MegatecDriverFactory(), new ApcSmartDriverFactory() })
        {
            var keys = factory.Options.Select(o => o.Key).ToList();
            Assert.Equal(keys.Count, keys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            Assert.All(factory.Options.Where(o => o.VisibleWhen is not null), o => Assert.Contains(o.VisibleWhen!.Key, keys));
            Assert.True(factory.SupportsDiscovery);
        }
    }

    [Fact]
    public async Task Discovery_lists_candidates_without_opening_anything()
    {
        // Only enumerates serial ports and USB HID devices of this machine; nothing is opened or sent.
        foreach (IUpsDriverFactory factory in new IUpsDriverFactory[] { new MegatecDriverFactory(), new ApcSmartDriverFactory() })
        {
            IReadOnlyList<DiscoveredDevice> devices = await factory.DiscoverAsync(CancellationToken.None);

            Assert.All(devices, d =>
            {
                Assert.Contains(d.Options["transport"], new[] { "serial", "usb" });
                Assert.False(string.IsNullOrWhiteSpace(d.Title));
            });
        }
    }

    [Fact]
    public void Megatec_serial_defaults()
    {
        MegatecSettings settings = MegatecDriverFactory.ParseSettings(Read(("port", "COM3")));

        var serial = Assert.IsType<SerialPortSettings>(settings.Transport);
        Assert.Equal("COM3", serial.PortName);
        Assert.Equal(2400, serial.BaudRate);
        Assert.True(serial.Dtr);
        Assert.False(serial.Rts);
        Assert.Equal(QxProtocols.Auto, settings.Protocol);
        Assert.Equal(180, settings.OnDelay);
        Assert.Equal(30, settings.OffDelay);
        Assert.Null(settings.RuntimeCalibration);
    }

    [Fact]
    public void Megatec_tcp_usb_and_battery_options()
    {
        MegatecSettings tcp = MegatecDriverFactory.ParseSettings(Read(
            ("transport", "tcp"), ("host", "10.0.0.5"), ("tcpPort", "2001"), ("protocol", "voltronic-qs"),
            ("runtimeCal", "240,100,720,50"), ("idleLoad", "20"), ("batteryPacks", "12"), ("ignoreSab", "true")));
        MegatecSettings usb = MegatecDriverFactory.ParseSettings(Read(
            ("transport", "usb"), ("vendorId", "0665"), ("productId", "5161"), ("cablePower", "none")));

        Assert.Equal(new TcpSettings("10.0.0.5", 2001), tcp.Transport);
        Assert.Equal("voltronic-qs", tcp.Protocol);
        Assert.Equal(240, tcp.RuntimeCalibration!.NominalRuntime, 3);
        Assert.Equal(0.2, tcp.IdleLoad, 3);
        Assert.Equal(12, tcp.BatteryPacks);
        Assert.True(tcp.IgnoreShutdownActive);
        Assert.Equal(new UsbBridgeSettings(0x0665, 0x5161, null, null), usb.Transport);
        Assert.Equal(TimeSpan.FromSeconds(3), usb.ReplyTimeout);
    }

    public static TheoryData<string, string[]> InvalidMegatecOptions => new()
    {
        { "port", ["transport=serial"] },
        { "host", ["transport=tcp", "tcpPort=2001"] },
        { "tcpPort", ["transport=tcp", "host=nas"] },
        { "tcpPort", ["transport=tcp", "host=nas", "tcpPort=70000"] },
        { "protocol", ["port=COM1", "protocol=sec"] },
        { "runtimeCal", ["port=COM1", "runtimeCal=1,2,3"] },
        { "vendorId", ["transport=usb", "productId=5161"] },
        { "vendorId", ["transport=usb", "vendorId=12345"] },
        { "offDelay", ["port=COM1", "offDelay=5"] },
        { "batteryVoltageHigh", ["port=COM1", "batteryVoltageHigh=20", "batteryVoltageLow=24"] },
        { "baudRate", ["port=COM1", "baudRate=fast"] },
    };

    [Theory]
    [MemberData(nameof(InvalidMegatecOptions))]
    public void Megatec_invalid_options_name_the_option(string expectedKey, string[] options)
    {
        var reader = new DriverOptionReader(options.Select(o => o.Split('=', 2)).ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase));

        var ex = Assert.Throws<DriverConfigurationException>(() => MegatecDriverFactory.ParseSettings(reader));

        Assert.Equal(expectedKey, ex.OptionKey);
    }

    [Fact]
    public void Megatec_refuses_a_bridge_that_needs_raw_USB()
    {
        var ex = Assert.Throws<DriverConfigurationException>(() => MegatecDriverFactory.ParseSettings(Read(
            ("transport", "usb"), ("vendorId", "0001"), ("productId", "0000"))));

        Assert.Contains("nutdrv_qx", ex.Message);
    }

    [Fact]
    public void ApcSmart_options()
    {
        ApcSmartSettings defaults = ApcSmartDriverFactory.ParseSettings(Read(("port", "/dev/ttyS0")));
        ApcSmartSettings custom = ApcSmartDriverFactory.ParseSettings(Read(
            ("transport", "tcp"), ("host", "ser2net"), ("tcpPort", "3001"), ("shutdownType", "5"), ("wakeUpDelay", "12"),
            ("csDelay", "2"), ("timeoutMs", "5000")));

        Assert.Equal("/dev/ttyS0", Assert.IsType<SerialPortSettings>(defaults.Transport).PortName);
        Assert.Equal(0, defaults.ShutdownType);
        Assert.Equal("000", defaults.WakeUpDelay);
        Assert.Equal(TimeSpan.FromSeconds(3), defaults.ReplyTimeout);
        Assert.Equal(new TcpSettings("ser2net", 3001), custom.Transport);
        Assert.Equal(5, custom.ShutdownType);
        Assert.Equal("012", custom.WakeUpDelay);
        Assert.Equal(TimeSpan.FromSeconds(2), custom.CsDelay);
        Assert.Equal(TimeSpan.FromSeconds(5), custom.ReplyTimeout);
    }

    [Theory]
    [InlineData("transport", "usb")]
    [InlineData("shutdownType", "6")]
    [InlineData("wakeUpDelay", "1000")]
    [InlineData("csDelay", "12")]
    public void ApcSmart_invalid_options_name_the_option(string key, string value)
    {
        var ex = Assert.Throws<DriverConfigurationException>(() => ApcSmartDriverFactory.ParseSettings(Read(("port", "COM1"), (key, value))));

        Assert.Equal(key, ex.OptionKey);
    }
}
