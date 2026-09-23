using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NutHub.Core.Configuration;
using NutHub.Core.Runtime;
using NutHub.Web.Tests.TestSupport;

namespace NutHub.Web.Tests;

public sealed class SettingsTests
{
    [Fact]
    public async Task Settings_round_trip_with_masked_certificate_passwords()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient admin = await host.SignedInAsync("admin");

        var all = await Json.ReadAsync(await admin.GetAsync("/api/admin/settings"));
        Assert.Equal("NutHub", all["server"]!["name"]!.GetValue<string>());
        Assert.False(all["web"]!.AsObject().ContainsKey("certificatePassword"));
        Assert.False(all["web"]!["certificatePasswordSet"]!.GetValue<bool>());
        Assert.False(all["nut"]!["tls"]!["certificatePasswordSet"]!.GetValue<bool>());
        Assert.Equal(3493, all["nut"]!["listen"]![0]!["port"]!.GetValue<int>());
        Assert.NotEmpty(all["history"]!["variables"]!.AsArray());

        var set = await Json.ReadAsync(await admin.PutAsync("/api/admin/settings/web",
            Json.Body(new { certificatePassword = "pfx-secret", sessionHours = 4 })));
        Assert.True(set["settings"]!["certificatePasswordSet"]!.GetValue<bool>());
        Assert.False(set["settings"]!.AsObject().ContainsKey("certificatePassword"));
        Assert.Equal(4, set["settings"]!["sessionHours"]!.GetValue<int>());
        Assert.Null(set["notice"]);
        Assert.StartsWith("enc:", host.Config.Current.Web.CertificatePassword);

        await admin.PutAsync("/api/admin/settings/web", Json.Body(new { sessionHours = 6, certificatePassword = (string?)null }));
        Assert.StartsWith("enc:", host.Config.Current.Web.CertificatePassword);

        await admin.PutAsync("/api/admin/settings/web", Json.Body(new { certificatePassword = "" }));
        Assert.Null(host.Config.Current.Web.CertificatePassword);

        var nut = await Json.ReadAsync(await admin.PutAsync("/api/admin/settings/nut",
            Json.Body(new { maxAgeSeconds = 30, tls = new { requireTlsForAuthentication = true } })));
        Assert.Equal(30, nut["settings"]!["maxAgeSeconds"]!.GetValue<int>());
        Assert.True(host.Config.Current.Nut.Tls.RequireTlsForAuthentication);
        Assert.Equal(15, host.Config.Current.HostProtection.CommunicationLostOnBatterySeconds);

        var server = await Json.ReadAsync(await admin.PutAsync("/api/admin/settings/server",
            Json.Body(new { name = "Rack room", location = "Basement" })));
        Assert.Equal("Basement", server["settings"]!["location"]!.GetValue<string>());
        Assert.Equal("Rack room", (await Json.ReadAsync(await admin.GetAsync("/api/overview")))["server"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Invalid_settings_name_the_fields_of_the_body()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient admin = await host.SignedInAsync("admin");

        HttpResponseMessage name = await admin.PutAsync("/api/admin/settings/server", Json.Body(new { name = "" }));
        Assert.Equal(HttpStatusCode.BadRequest, name.StatusCode);
        Assert.True((await Json.ReadAsync(name))["fields"]!.AsObject().ContainsKey("name"));

        HttpResponseMessage port = await admin.PutAsync("/api/admin/settings/nut",
            Json.Body(new { listen = new[] { new { address = "*", port = 70000 } } }));
        Assert.True((await Json.ReadAsync(port))["fields"]!.AsObject().ContainsKey("listen[0].port"));

        HttpResponseMessage type = await admin.PutAsync("/api/admin/settings/history", Json.Raw("{\"retentionDays\":\"many\"}"));
        Assert.Equal(HttpStatusCode.BadRequest, type.StatusCode);

        HttpResponseMessage lockout = await admin.PutAsync("/api/admin/settings/web",
            Json.Body(new { allowedNetworks = new[] { "10.0.0.0/8" } }));
        Assert.Equal(HttpStatusCode.BadRequest, lockout.StatusCode);
        Assert.True((await Json.ReadAsync(lockout))["fields"]!.AsObject().ContainsKey("allowedNetworks"));
    }

    [Theory]
    [InlineData("/api/admin/settings/nut", "{\"tls\":null}", "tls")]
    [InlineData("/api/admin/settings/nut", "{\"listen\":null}", "listen")]
    [InlineData("/api/admin/settings/web", "{\"allowedNetworks\":null}", "allowedNetworks")]
    [InlineData("/api/admin/settings/history", "{\"variables\":null}", "variables")]
    [InlineData("/api/admin/host-protection", "{\"ups\":null}", "ups")]
    [InlineData("/api/admin/settings/nut", "{\"listen\":[null]}", "listen[0]")]
    [InlineData("/api/admin/settings/web", "{\"allowedNetworks\":[\"127.0.0.1\",null]}", "allowedNetworks[1]")]
    [InlineData("/api/admin/notifications", "{\"webhooks\":[null]}", "webhooks[0]")]
    [InlineData("/api/admin/notifications", "{\"commands\":[null]}", "commands[0]")]
    public async Task Null_where_a_value_is_required_is_a_validation_error(string path, string body, string field)
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient admin = await host.SignedInAsync("admin");

        HttpResponseMessage response = await admin.PutAsync(path, Json.Raw(body));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await Json.ReadAsync(response))["fields"]!.AsObject().ContainsKey(field));
        // Optional members still accept null.
        Assert.Equal(HttpStatusCode.OK,
                     (await admin.PutAsync("/api/admin/settings/server", Json.Raw("{\"location\":null}"))).StatusCode);
    }

    [Fact]
    public async Task Web_listener_changes_come_with_a_notice()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient admin = await host.SignedInAsync("admin");

        var moved = await Json.ReadAsync(await admin.PutAsync("/api/admin/settings/web", Json.Body(new { httpPort = 9001 })));

        Assert.Equal("The panel moves to http://localhost:9001/", moved["notice"]!.GetValue<string>());
        Assert.Equal(9001, host.Config.Current.Web.HttpPort);
    }

    [Fact]
    public async Task Notification_secrets_are_masked_and_kept()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient admin = await host.SignedInAsync("admin");

        HttpResponseMessage put = await admin.PutAsync("/api/admin/notifications", Json.Body(new
        {
            events = new[] { "onBattery", "lowBattery" },
            email = new { enabled = true, host = "smtp.example.com", from = "ups@example.com", to = new[] { "ops@example.com" }, password = "mail-secret" },
            webhooks = new[] { new { name = "hook", url = "https://example.com/hook", headers = new Dictionary<string, string> { ["Authorization"] = "Bearer abc" } } },
        }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);
        var view = await Json.ReadAsync(put);
        Assert.True(view["email"]!["passwordSet"]!.GetValue<bool>());
        Assert.False(view["email"]!.AsObject().ContainsKey("password"));
        JsonNode hook = view["webhooks"]![0]!;
        Assert.True(hook["headers"]!.AsObject().ContainsKey("Authorization"));
        Assert.Null(hook["headers"]!["Authorization"]);
        Assert.Equal(["onBattery", "lowBattery"], view["events"]!.AsArray().Select(e => e!.GetValue<string>()));
        string id = hook["id"]!.GetValue<string>();
        string storedHeader = host.Config.Current.Notifications.Webhooks[0].Headers["Authorization"];
        Assert.StartsWith("enc:", storedHeader);

        // Sending back what GET returned keeps every secret.
        var again = await Json.ReadAsync(await admin.PutAsync("/api/admin/notifications", Json.Raw(view.ToJsonString())));
        Assert.True(again["email"]!["passwordSet"]!.GetValue<bool>());
        Assert.Equal(storedHeader, host.Config.Current.Notifications.Webhooks[0].Headers["Authorization"]);
        Assert.Equal(id, host.Config.Current.Notifications.Webhooks[0].Id);

        // A header missing from the object is removed; "" clears the password.
        await admin.PutAsync("/api/admin/notifications", Json.Body(new
        {
            email = new { password = "" },
            webhooks = new[] { new { id, name = "hook", url = "https://example.com/hook", headers = new Dictionary<string, string>() } },
        }));
        Assert.Empty(host.Config.Current.Notifications.Webhooks[0].Headers);
        Assert.Null(host.Config.Current.Notifications.Email.Password);

        var test = await Json.ReadAsync(await admin.PostAsync("/api/admin/notifications/test", Json.Body(new { channel = "email" })));
        Assert.False(test["ok"]!.GetValue<bool>());
        Assert.Equal("failed", test["status"]!.GetValue<string>());
        Assert.IsType<JsonArray>(await Json.ReadAsync(await admin.GetAsync("/api/admin/notifications/deliveries")));
    }

    [Fact]
    public async Task Host_protection_settings_and_default_command()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient admin = await host.SignedInAsync("admin");

        var view = await Json.ReadAsync(await admin.GetAsync("/api/admin/host-protection"));
        // The command comes from IHostProtectionService (NutHub.Services); here the Core placeholder answers "".
        Assert.NotNull(view["defaultShutdownCommand"]!.GetValue<string>());
        Assert.Equal("disabled", view["status"]!["state"]!.GetValue<string>());
        Assert.False(view["settings"]!["enabled"]!.GetValue<bool>());

        HttpResponseMessage invalid = await admin.PutAsync("/api/admin/host-protection", Json.Body(new { enabled = true }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.True((await Json.ReadAsync(invalid))["fields"]!.AsObject().ContainsKey("ups"));

        var saved = await Json.ReadAsync(await admin.PutAsync("/api/admin/host-protection",
            Json.Body(new { enabled = true, ups = new[] { "sim1" }, dryRun = true })));
        Assert.True(saved["settings"]!["dryRun"]!.GetValue<bool>());
        Assert.Equal(["sim1"], saved["status"]!["ups"]!.AsArray().Select(u => u!.GetValue<string>()));
    }

    [Fact]
    public async Task Export_contains_no_secret()
    {
        await using var host = await WebTestHost.StartAsync(c =>
        {
            c.Web.CertificatePassword = "pfx-secret";
            c.Notifications.Email.Password = "mail-secret";
            c.NutUsers.Add(new NutUserConfig { Name = "upsmon", PasswordHash = WebTestHost.Hasher.Hash("x") });
            c.Ups.Add(new UpsConfig { Name = "net1", Driver = "testdrv", Options = { ["host"] = "10.0.0.1", ["community"] = "snmp-secret" } });
        });
        HttpClient admin = await host.SignedInAsync("admin");
        // Encrypts the clear-text secrets of the seeded file.
        await admin.PutAsync("/api/admin/settings/server", Json.Body(new { location = "Lab" }));

        HttpResponseMessage response = await admin.GetAsync("/api/admin/config/export");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Equal("nuthub-config.json", response.Content.Headers.ContentDisposition.FileName?.Trim('"'));
        string text = await response.Content.ReadAsStringAsync();
        foreach (string secret in new[] { "enc:", "pfx-secret", "mail-secret", "snmp-secret", "passwordHash", "pbkdf2", "securityStamp" })
        {
            Assert.DoesNotContain(secret, text);
        }

        var exported = JsonNode.Parse(text)!;
        Assert.Equal("10.0.0.1", exported["ups"]![1]!["options"]!["host"]!.GetValue<string>());
        Assert.Equal("Lab", exported["server"]!["location"]!.GetValue<string>());
    }

    [Fact]
    public async Task Logs_system_and_clients()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient admin = await host.SignedInAsync("admin");
        await admin.PutAsync("/api/admin/settings/server", Json.Body(new { location = "Lab" }));

        JsonArray logs = (await Json.ReadAsync(await admin.GetAsync("/api/admin/logs?minLevel=information&limit=50"))).AsArray();
        Assert.Contains(logs, l => l!["message"]!.GetValue<string>().Contains("Configuration changed"));
        Assert.All(logs, l => Assert.Contains(l!["level"]!.GetValue<string>(),
                                              new[] { "information", "warning", "error", "critical" }));
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.GetAsync("/api/admin/logs?minLevel=loud")).StatusCode);

        var system = await Json.ReadAsync(await admin.GetAsync("/api/admin/system"));
        Assert.Equal(Environment.ProcessId, system["processId"]!.GetValue<int>());
        Assert.EndsWith("nuthub.json", system["configFile"]!.GetValue<string>());
        Assert.Contains(system["drivers"]!.AsArray(), d => d!["id"]!.GetValue<string>() == "simulated");

        Assert.Empty((await Json.ReadAsync(await admin.GetAsync("/api/admin/clients"))).AsArray());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/admin/clients/42")).StatusCode);
    }

    [Fact]
    public async Task Nut_clients_are_listed_counted_and_disconnected()
    {
        await using var host = await WebTestHost.StartAsync();
        await host.WaitForUpsAsync("sim1", connected: false);
        var sessions = host.Services.GetRequiredService<NutSessionRegistry>();
        bool closed = false;
        NutSession session = sessions.Open(new IPEndPoint(IPAddress.Parse("192.168.1.20"), 50122), () => closed = true);
        sessions.SetUser(session, "upsmon");
        sessions.SetLogin(session, "sim1");
        HttpClient admin = await host.SignedInAsync("admin");

        JsonNode client = Assert.Single((await Json.ReadAsync(await admin.GetAsync("/api/admin/clients"))).AsArray())!;
        Assert.Equal("192.168.1.20", client["address"]!.GetValue<string>());
        Assert.Equal(50122, client["port"]!.GetValue<int>());
        Assert.Equal("upsmon", client["username"]!.GetValue<string>());
        Assert.Equal("sim1", client["loginUps"]!.GetValue<string>());
        Assert.False(client["primary"]!.GetValue<bool>());

        var overview = await Json.ReadAsync(await admin.GetAsync("/api/overview"));
        Assert.Equal(1, overview["ups"]![0]!["clients"]!.GetValue<int>());
        Assert.Equal(1, overview["server"]!["nut"]!["clients"]!.GetValue<int>());
        Assert.Single((await Json.ReadAsync(await admin.GetAsync("/api/ups/sim1")))["clients"]!.AsArray());

        Assert.Equal(HttpStatusCode.NoContent,
                     (await admin.DeleteAsync($"/api/admin/clients/{client["id"]!.GetValue<long>()}")).StatusCode);
        Assert.True(closed);
    }
}
