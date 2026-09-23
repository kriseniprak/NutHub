using NutHub.Core.Configuration;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Core.Tests.Support;

namespace NutHub.Core.Tests.Configuration;

public sealed class ConfigValidatorTests
{
    private static readonly IDriverCatalog Drivers = new DriverCatalog([new FakeDriverFactory("fake")]);

    internal static NutHubConfig Valid() => new()
    {
        WebUsers = [new WebUserConfig { Name = "admin", Role = WebRole.Admin, PasswordHash = "hash" }],
        Ups = [new UpsConfig { Name = "ups1", Driver = "fake" }],
    };

    private static IReadOnlyDictionary<string, string> Errors(Action<NutHubConfig> change)
    {
        NutHubConfig config = Valid();
        change(config);
        return ConfigValidator.Validate(config, Drivers);
    }

    private static void AssertError(string key, Action<NutHubConfig> change)
    {
        IReadOnlyDictionary<string, string> errors = Errors(change);
        Assert.True(errors.ContainsKey(key), $"Expected an error on '{key}', got: {string.Join("; ", errors.Keys)}");
    }

    [Fact]
    public void The_defaults_with_an_administrator_are_valid()
    {
        Assert.Empty(ConfigValidator.Validate(Valid(), Drivers));
        ConfigValidator.ThrowIfInvalid(Valid(), Drivers);
    }

    [Fact]
    public void An_enabled_administrator_is_required()
    {
        AssertError("webUsers", c => c.WebUsers.Clear());
        AssertError("webUsers", c => c.WebUsers[0].Disabled = true);
        AssertError("webUsers", c => c.WebUsers[0].Role = WebRole.Operator);
    }

    [Fact]
    public void ThrowIfInvalid_lists_every_error()
    {
        NutHubConfig config = Valid();
        config.WebUsers.Clear();
        config.Web.HttpPort = 0;
        var ex = Assert.Throws<ConfigValidationException>(() => ConfigValidator.ThrowIfInvalid(config, Drivers));
        Assert.Contains("webUsers", ex.Errors.Keys);
        Assert.Contains("web.httpPort", ex.Errors.Keys);
        Assert.Contains("web.httpPort", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Newer_schema_and_bad_server_name()
    {
        AssertError("schemaVersion", c => c.SchemaVersion = NutHubConfig.CurrentSchemaVersion + 1);
        AssertError("server.name", c => c.Server.Name = " ");
        AssertError("server.name", c => c.Server.Name = new string('x', 101));
    }

    [Fact]
    public void Nut_server_rules()
    {
        AssertError("nut.listen", c => c.Nut.Listen.Clear());
        Assert.Empty(Errors(c =>
        {
            c.Nut.Listen.Clear();
            c.Nut.Enabled = false;
        }));
        AssertError("nut.listen[0].address", c => c.Nut.Listen[0].Address = "localhost");
        AssertError("nut.listen[0].port", c => c.Nut.Listen[0].Port = 70000);
        AssertError("nut.maxConnections", c => c.Nut.MaxConnections = 0);
        AssertError("nut.maxAgeSeconds", c => c.Nut.MaxAgeSeconds = 0);
        AssertError("nut.fsdClearDelaySeconds", c => c.Nut.FsdClearDelaySeconds = -1);
        AssertError("nut.allowedNetworks", c => c.Nut.AllowedNetworks.Add("10.0.0.0/33"));
        Assert.Empty(Errors(c => c.Nut.Listen[0].Address = "::1"));
        Assert.Empty(Errors(c => c.Nut.AllowedNetworks.AddRange(["10.0.0.0/8", "fd00::/8", "192.168.1.5"])));
    }

    [Fact]
    public void Web_rules()
    {
        AssertError("web.bindAddress", c => c.Web.BindAddress = "my-host");
        AssertError("web.httpEnabled", c => c.Web.HttpEnabled = false);
        AssertError("web.httpsPort", c =>
        {
            c.Web.HttpsEnabled = true;
            c.Web.HttpsPort = c.Web.HttpPort;
        });
        AssertError("web.sessionHours", c => c.Web.SessionHours = 0);
        AssertError("web.allowedNetworks", c => c.Web.AllowedNetworks.Add("nope"));
        AssertError("web.httpPort", c => c.Web.HttpPort = c.Nut.Listen[0].Port);
        // No clash when one of the two servers is off.
        Assert.Empty(Errors(c =>
        {
            c.Web.HttpPort = c.Nut.Listen[0].Port;
            c.Nut.Enabled = false;
        }));
    }

    [Fact]
    public void Ups_rules()
    {
        AssertError("ups[0].name", c => c.Ups[0].Name = "bad name");
        AssertError("ups[1].name", c => c.Ups.Add(new UpsConfig { Name = "UPS1", Driver = "fake" }));
        AssertError("ups[0].description", c => c.Ups[0].Description = new string('d', 201));
        AssertError("ups[0].driver", c => c.Ups[0].Driver = "");
        AssertError("ups[0].driver", c => c.Ups[0].Driver = "unknown");
        AssertError("ups[0].pollIntervalSeconds", c => c.Ups[0].PollIntervalSeconds = 0.1);
        AssertError("ups[0].pollIntervalSeconds", c => c.Ups[0].PollIntervalSeconds = 301);
        AssertError("ups[0].overrides", c => c.Ups[0].Overrides["bad key"] = "1");
        AssertError("ups[0].lowBattery.chargePercent", c => c.Ups[0].LowBattery.ChargePercent = 101);
        AssertError("ups[0].lowBattery.runtimeSeconds", c => c.Ups[0].LowBattery.RuntimeSeconds = -1);
    }

    [Fact]
    public void Unknown_drivers_are_only_checked_with_a_catalog()
    {
        NutHubConfig config = Valid();
        config.Ups[0].Driver = "unknown";
        Assert.Empty(ConfigValidator.Validate(config));
    }

    [Fact]
    public void Nut_account_rules()
    {
        static NutUserConfig User(string name) => new() { Name = name, PasswordHash = "hash" };
        AssertError("nutUsers[0].name", c => c.NutUsers.Add(User("bad name")));
        AssertError("nutUsers[1].name", c => c.NutUsers.AddRange([User("mon"), User("mon")]));
        Assert.Empty(Errors(c => c.NutUsers.AddRange([User("mon"), User("Mon")]))); // case-sensitive, like upsd.users
        AssertError("nutUsers[0].password", c => c.NutUsers.Add(new NutUserConfig { Name = "mon" }));
        AssertError("nutUsers[0].actions", c => c.NutUsers.Add(User("mon").WithActions("SET", "KILL")));
        Assert.Empty(Errors(c => c.NutUsers.Add(User("mon").WithActions("set", "FSD"))));
        AssertError("nutUsers[0].instantCommands", c =>
        {
            var user = User("mon");
            user.InstantCommands.Add("bad command");
            c.NutUsers.Add(user);
        });
        AssertError("nutUsers[0].allowedUps", c =>
        {
            var user = User("mon");
            user.AllowedUps.Add("other");
            c.NutUsers.Add(user);
        });
    }

    [Fact]
    public void Web_account_rules()
    {
        AssertError("webUsers[1].name", c => c.WebUsers.Add(new WebUserConfig { Name = "ADMIN", PasswordHash = "h" }));
        AssertError("webUsers[1].name", c => c.WebUsers.Add(new WebUserConfig { Name = "a b", PasswordHash = "h" }));
        AssertError("webUsers[1].password", c => c.WebUsers.Add(new WebUserConfig { Name = "viewer" }));
        Assert.True(ConfigValidator.IsValidAccountName("john.doe@example.com"));
        Assert.False(ConfigValidator.IsValidAccountName(new string('a', 65)));
        Assert.False(ConfigValidator.IsValidAccountName(""));
    }

    [Fact]
    public void Notification_rules()
    {
        AssertError("notifications.email.host", c => c.Notifications.Email.Enabled = true);
        AssertError("notifications.email.from", c => c.Notifications.Email.Enabled = true);
        AssertError("notifications.email.to", c =>
        {
            c.Notifications.Email.Enabled = true;
            c.Notifications.Email.To.Add("not-an-address");
        });
        Assert.Empty(Errors(c =>
        {
            c.Notifications.Email.Enabled = true;
            c.Notifications.Email.Host = "smtp.example.com";
            c.Notifications.Email.From = "ups@example.com";
            c.Notifications.Email.To.Add("admin@example.com");
        }));

        AssertError("notifications.webhooks[0].url", c =>
            c.Notifications.Webhooks.Add(new WebhookSettings { Url = "ftp://example.com/x" }));
        AssertError("notifications.webhooks[0].method", c =>
            c.Notifications.Webhooks.Add(new WebhookSettings { Url = "https://example.com/x", Method = "DELETE" }));
        AssertError("notifications.webhooks[1].id", c =>
            c.Notifications.Webhooks.AddRange([new WebhookSettings { Id = "same", Url = "https://a.example/" },
                                               new WebhookSettings { Id = "same", Url = "https://b.example/" }]));
        AssertError("notifications.commands[0].command", c => c.Notifications.Commands.Add(new CommandHookSettings()));
        AssertError("notifications.commands[0].timeoutSeconds", c =>
            c.Notifications.Commands.Add(new CommandHookSettings { Command = "notify", TimeoutSeconds = 0 }));
        // Webhooks and commands share the id space.
        AssertError("notifications.commands[0].id", c =>
        {
            c.Notifications.Webhooks.Add(new WebhookSettings { Id = "x1", Url = "https://a.example/" });
            c.Notifications.Commands.Add(new CommandHookSettings { Id = "x1", Command = "notify" });
        });
    }

    [Fact]
    public void Host_protection_rules()
    {
        AssertError("hostProtection.ups", c => c.HostProtection.Enabled = true);
        AssertError("hostProtection.ups", c => c.HostProtection.Ups.Add("missing"));
        AssertError("hostProtection.minimumSupplies", c =>
        {
            c.HostProtection.Enabled = true;
            c.HostProtection.Ups.Add("ups1");
            c.HostProtection.MinimumSupplies = 2;
        });
        AssertError("hostProtection.batteryChargeBelow", c => c.HostProtection.BatteryChargeBelow = 150);
        AssertError("hostProtection", c => c.HostProtection.ShutdownDelaySeconds = 4000);
        AssertError("hostProtection.powerOffCommand", c => c.HostProtection.PowerOffCommand = "shutdown return");
        Assert.Empty(Errors(c =>
        {
            c.HostProtection.Enabled = true;
            c.HostProtection.Ups.Add("UPS1"); // UPS names ignore case
        }));
    }

    [Fact]
    public void History_rules()
    {
        AssertError("history.sampleIntervalSeconds", c => c.History.SampleIntervalSeconds = 1);
        AssertError("history.retentionDays", c => c.History.RetentionDays = 0);
        AssertError("history.eventRetentionDays", c => c.History.EventRetentionDays = 4000);
        AssertError("history.variables", c => c.History.Variables.Add("bad var"));
    }
}

internal static class NutUserConfigExtensions
{
    public static NutUserConfig WithActions(this NutUserConfig user, params string[] actions)
    {
        user.Actions.AddRange(actions);
        return user;
    }
}
