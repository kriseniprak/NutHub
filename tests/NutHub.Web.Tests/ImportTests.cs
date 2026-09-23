using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NutHub.Core.Configuration;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Web.Import;
using NutHub.Web.Tests.TestSupport;

namespace NutHub.Web.Tests;

public sealed class ImportTests
{
    private const string UpsConf = """
        # Global directives come first
        maxretry = 3

        [rack1]
            driver = usbhid-ups
            port = auto
            vendorid = 051d
            desc = "APC \"Back-UPS\" rack 1"   # trailing comment
            override.battery.charge.low = 30
            ignorelb
            weird_option = 1

        [card]
            driver=snmp-ups
            port = 192.168.1.50:1161
            community = "s3cret#1"
            snmp_version = v3
            secLevel = authPriv

        [remote]
            driver = dummy-ups
            port = ups@nas.local:3493

        [serial]
            driver = blazer_ser
            port = /dev/ttyS0

        [old]
            driver = bcmxcp
            port = /dev/ttyS1
        """;

    private const string UpsdUsers = """
        [admin]
            password = "adm pass"
            actions = SET FSD
            instcmds = ALL

        [monuser]
            password = mon
            upsmon master

        [slave]
            password = x
            upsmon = slave
            instcmds = test.battery.start
            instcmds = beeper.mute

        [nopass]
            actions = SET
        """;

    [Fact]
    public void Parser_follows_the_nut_quoting_rules()
    {
        var lines = NutConfParser.ReadLines("a=b # c\n\"x y\" = \"q\\\"z\" \\#w\n[s]\nflag\n");

        Assert.Equal(["a", "=", "b"], lines[0].Words);
        Assert.Equal(["x y", "=", "q\"z", "#w"], lines[1].Words);
        Assert.Equal(2, lines[1].Number);
        Assert.Equal(["[s]"], lines[2].Words);
        Assert.Equal(["flag"], lines[3].Words);
    }

    [Fact]
    public void Ups_conf_is_mapped_to_the_drivers_nuthub_has()
    {
        NutImportResult result = Importer().Import(UpsConf, null, hashPasswords: false);

        Assert.Equal(["rack1", "card", "remote", "serial"], result.Ups.Select(u => u.Name));
        UpsConfig rack1 = result.Ups[0];
        Assert.Equal("usbhid", rack1.Driver);
        Assert.Equal("051d", rack1.Options["vendorId"]);
        Assert.Equal("APC \"Back-UPS\" rack 1", rack1.Description);
        Assert.Equal("30", rack1.Overrides["battery.charge.low"]);
        Assert.True(rack1.LowBattery.IgnoreDeviceFlag);

        UpsConfig card = result.Ups[1];
        Assert.Equal("snmp", card.Driver);
        Assert.Equal("192.168.1.50", card.Options["host"]);
        Assert.Equal("1161", card.Options["port"]);
        Assert.Equal("s3cret#1", card.Options["community"]);
        Assert.Equal("3", card.Options["version"]);

        UpsConfig remote = result.Ups[2];
        Assert.Equal("nut", remote.Driver);
        Assert.Equal("ups", remote.Options["upsName"]);
        Assert.Equal("nas.local", remote.Options["host"]);
        Assert.Equal("3493", remote.Options["port"]);

        Assert.Equal("megatec", result.Ups[3].Driver);
        Assert.Equal("serial", result.Ups[3].Options["transport"]);
        Assert.Equal("/dev/ttyS0", result.Ups[3].Options["port"]);

        Assert.Contains(result.Warnings, w => w.Contains("weird_option"));
        Assert.Contains(result.Warnings, w => w.Contains("bcmxcp"));
        Assert.Contains(result.Warnings, w => w.Contains("secLevel"));
        Assert.DoesNotContain(result.Warnings, w => w.Contains("maxretry"));
    }

    [Fact]
    public void Upsd_users_are_mapped_with_their_rights()
    {
        NutImportResult result = Importer().Import(null, UpsdUsers, hashPasswords: true);

        Assert.Equal(["admin", "monuser", "slave"], result.Users.Select(u => u.Name));
        NutUserConfig admin = result.Users[0];
        Assert.Equal(["SET", "FSD"], admin.Actions);
        Assert.Equal(["ALL"], admin.InstantCommands);
        Assert.True(WebTestHost.Hasher.Verify("adm pass", admin.PasswordHash));
        Assert.Equal(NutMonitorRole.Primary, result.Users[1].Monitor);
        Assert.Equal(NutMonitorRole.Secondary, result.Users[2].Monitor);
        Assert.Equal(["test.battery.start", "beeper.mute"], result.Users[2].InstantCommands);
        Assert.Contains(result.Warnings, w => w.Contains("nopass"));
    }

    [Fact]
    public void Account_names_made_of_dots_are_skipped()
    {
        // The panel could never change or delete them: "/api/admin/nut-users/.." is "/api/admin/".
        NutImportResult result = Importer().Import(null, "[..]\n  password = dots-password\n", hashPasswords: false);

        Assert.Empty(result.Users);
        Assert.Contains(result.Warnings, w => w.Contains("'..' is not a valid account name"));
    }

    [Fact]
    public async Task Import_endpoint_previews_then_applies()
    {
        await using var host = await WebTestHost.StartAsync(services: s =>
        {
            s.AddSingleton<IUpsDriverFactory>(Fake("usbhid", "vendorId", "productId", "serial", "product"));
        });
        HttpClient admin = await host.SignedInAsync("admin");
        const string conf = "[rack1]\n driver = usbhid-ups\n port = auto\n[sim1]\n driver = usbhid-ups\n port = auto\n";
        const string users = "[upsmon]\n password = secret\n upsmon secondary\n";

        var preview = await Json.ReadAsync(await admin.PostAsync("/api/admin/import/nut",
            Json.Body(new { upsConf = conf, upsdUsers = users, apply = false })));
        Assert.False(preview["applied"]!.GetValue<bool>());
        Assert.Equal(2, preview["ups"]!.AsArray().Count);
        Assert.Equal("upsmon", preview["nutUsers"]![0]!["name"]!.GetValue<string>());
        Assert.False(preview["nutUsers"]![0]!.AsObject().ContainsKey("password"));
        Assert.Single(host.Config.Current.Ups);

        HttpResponseMessage applied = await admin.PostAsync("/api/admin/import/nut",
            Json.Body(new { upsConf = conf, upsdUsers = users, apply = true }));
        var result = await Json.ReadAsync(applied);
        Assert.True(result["applied"]!.GetValue<bool>());
        Assert.Contains(result["warnings"]!.AsArray(), w => w!.GetValue<string>().Contains("sim1"));
        Assert.Equal(["sim1", "rack1"], host.Config.Current.Ups.Select(u => u.Name));
        NutUserConfig upsmon = Assert.Single(host.Config.Current.NutUsers);
        Assert.True(WebTestHost.Hasher.Verify("secret", upsmon.PasswordHash));
    }

    private static NutImporter Importer() => new(
        new DriverCatalog(
        [
            Fake("usbhid", "vendorId", "productId", "serial", "product", "subdriver"),
            Fake("snmp", "host", "port", "community:secret", "version", "mibs", "secName", "authPassword:secret"),
            Fake("nut", "host", "port", "upsName"),
            Fake("megatec", "transport", "port", "vendorId", "protocol"),
        ]),
        WebTestHost.Hasher);

    /// <summary>A factory with the given option keys ("key" or "key:secret").</summary>
    private static IUpsDriverFactory Fake(string id, params string[] keys) => new FakeFactory(id, keys);

    private sealed class FakeFactory(string id, string[] keys) : IUpsDriverFactory
    {
        public string Id => id;

        public string DisplayName => id;

        public string Description => id;

        public DriverPlatforms Platforms => DriverPlatforms.All;

        public IReadOnlyList<DriverOption> Options { get; } = keys.Select(k => new DriverOption
        {
            Key = k.Split(':')[0],
            Label = k.Split(':')[0],
            Type = k.EndsWith(":secret", StringComparison.Ordinal) ? DriverOptionType.Secret : DriverOptionType.String,
        }).ToList();

        public bool SupportsDiscovery => false;

        public Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DiscoveredDevice>>([]);

        public IUpsDriver Create(DriverCreateContext context) =>
            throw new DriverConfigurationException("Not a real driver.");
    }
}
