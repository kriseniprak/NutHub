using System.Net;
using System.Text.Json.Nodes;
using NutHub.Core.Configuration;
using NutHub.Web.Tests.TestSupport;

namespace NutHub.Web.Tests;

public sealed class AdminUpsTests
{
    [Fact]
    public async Task Drivers_describe_their_option_schema()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient admin = await host.SignedInAsync("admin");

        JsonArray drivers = (await Json.ReadAsync(await admin.GetAsync("/api/admin/drivers"))).AsArray();

        JsonNode test = drivers.First(d => d!["id"]!.GetValue<string>() == "testdrv")!;
        Assert.True(test["supported"]!.GetValue<bool>());
        Assert.True(test["supportsDiscovery"]!.GetValue<bool>());
        Assert.Equal(["windows", "linux", "macOS"], test["platforms"]!.AsArray().Select(p => p!.GetValue<string>()));
        var options = test["options"]!.AsArray().ToDictionary(o => o!["key"]!.GetValue<string>(), o => o!);
        Assert.Equal("host", options["host"]["type"]!.GetValue<string>());
        Assert.True(options["host"]["required"]!.GetValue<bool>());
        Assert.Equal("secret", options["community"]["type"]!.GetValue<string>());
        Assert.Equal("161", options["port"]["default"]!.GetValue<string>());
        Assert.Equal("b", options["mode"]["choices"]![1]!["value"]!.GetValue<string>());
        Assert.Equal("mode", options["extra"]["visibleWhen"]!["key"]!.GetValue<string>());
        Assert.Null(options["host"]["visibleWhen"]);
        Assert.Equal(5, options["retries"]["max"]!.GetValue<double>());
    }

    [Fact]
    public async Task Discovery_and_serial_ports_answer()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient admin = await host.SignedInAsync("admin");

        var found = await Json.ReadAsync(await admin.PostAsync("/api/admin/drivers/testdrv/discover", null));
        JsonNode device = Assert.Single(found["devices"]!.AsArray())!;
        Assert.Equal("Test device", device["title"]!.GetValue<string>());
        Assert.Equal("10.0.0.9", device["options"]!["host"]!.GetValue<string>());
        Assert.Equal("bench", device["suggestedName"]!.GetValue<string>());

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/admin/drivers/nope/discover", null)).StatusCode);
        HttpResponseMessage ports = await admin.GetAsync("/api/admin/serial-ports");
        Assert.Equal(HttpStatusCode.OK, ports.StatusCode);
        Assert.IsType<JsonArray>(await Json.ReadAsync(ports));
    }

    [Fact]
    public async Task Missing_or_invalid_options_are_validation_errors()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient admin = await host.SignedInAsync("admin");

        HttpResponseMessage response = await admin.PostAsync("/api/admin/ups", Json.Body(new
        {
            name = "net1",
            driver = "testdrv",
            options = new Dictionary<string, object> { ["mode"] = "b", ["retries"] = 9, ["port"] = "70000" },
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await Json.ReadAsync(response);
        Assert.Equal("validation", body["error"]!.GetValue<string>());
        JsonObject fields = body["fields"]!.AsObject();
        Assert.True(fields.ContainsKey("options.host"));
        Assert.True(fields.ContainsKey("options.extra"));
        Assert.True(fields.ContainsKey("options.retries"));
        Assert.True(fields.ContainsKey("options.port"));

        HttpResponseMessage badName = await admin.PostAsync("/api/admin/ups", Json.Body(new
        {
            name = "bad name!",
            driver = "simulated",
        }));
        Assert.Equal(HttpStatusCode.BadRequest, badName.StatusCode);
        Assert.True((await Json.ReadAsync(badName))["fields"]!.AsObject().ContainsKey("name"));
    }

    [Fact]
    public async Task A_value_only_the_driver_can_judge_is_refused_before_it_is_saved()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient admin = await host.SignedInAsync("admin");

        // Well formed for its option type, but the driver itself refuses it: without this the UPS would be saved and
        // then fail as soon as its driver started.
        HttpResponseMessage response = await admin.PostAsync("/api/admin/ups", Json.Body(new
        {
            name = "net1",
            driver = "testdrv",
            options = new Dictionary<string, object> { ["host"] = "10.0.0.9:3493" },
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        JsonObject fields = (await Json.ReadAsync(response))["fields"]!.AsObject();
        Assert.Contains("must not carry a port", fields["options.host"]!.GetValue<string>());

        HttpResponseMessage good = await admin.PostAsync("/api/admin/ups", Json.Body(new
        {
            name = "net1",
            driver = "testdrv",
            options = new Dictionary<string, object> { ["host"] = "10.0.0.9" },
        }));
        Assert.Equal(HttpStatusCode.Created, good.StatusCode);
    }

    [Fact]
    public async Task Secret_options_are_never_returned_and_follow_keep_clear_replace()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient admin = await host.SignedInAsync("admin");

        HttpResponseMessage created = await admin.PostAsync("/api/admin/ups", Json.Body(new
        {
            name = "net1",
            description = "Network card",
            driver = "testdrv",
            options = new { host = "10.0.0.5", community = "s3cret" },
            overrides = new Dictionary<string, string> { ["battery.charge.low"] = "30" },
            lowBattery = new { chargePercent = 25, runtimeSeconds = 180, ignoreDeviceFlag = false },
        }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("/api/admin/ups/net1", created.Headers.Location!.OriginalString);
        var dto = await Json.ReadAsync(created);
        Assert.False(dto["options"]!.AsObject().ContainsKey("community"));
        Assert.Equal("10.0.0.5", dto["options"]!["host"]!.GetValue<string>());
        Assert.Equal(["community"], dto["secretsSet"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Equal(180, dto["lowBattery"]!["runtimeSeconds"]!.GetValue<int>());
        string file = await File.ReadAllTextAsync(Path.Combine(host.Directory, "nuthub.json"));
        Assert.DoesNotContain("s3cret", file);
        Assert.StartsWith("enc:", Stored(host, "net1", "community"));

        // Absent: kept.
        var kept = await Json.ReadAsync(await admin.PutAsync("/api/admin/ups/net1", Json.Body(new
        {
            name = "net1", driver = "testdrv", options = new { host = "10.0.0.6" },
        })));
        Assert.Equal(["community"], kept["secretsSet"]!.AsArray().Select(s => s!.GetValue<string>()));
        Assert.Equal("Network card", kept["description"]!.GetValue<string>());
        string before = Stored(host, "net1", "community")!;

        // Replaced.
        await admin.PutAsync("/api/admin/ups/net1", Json.Body(new { options = new { host = "10.0.0.6", community = "other" } }));
        Assert.NotEqual(before, Stored(host, "net1", "community"));

        // Cleared.
        var cleared = await Json.ReadAsync(await admin.PutAsync("/api/admin/ups/net1",
            Json.Body(new { options = new { host = "10.0.0.6", community = "" } })));
        Assert.Empty(cleared["secretsSet"]!.AsArray());
        Assert.Null(Stored(host, "net1", "community"));

        var list = (await Json.ReadAsync(await admin.GetAsync("/api/admin/ups"))).AsArray();
        Assert.Equal(["sim1", "net1"], list.Select(u => u!["name"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Duplicate_names_conflict_and_unknown_names_are_not_found()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient admin = await host.SignedInAsync("admin");

        HttpResponseMessage duplicate = await admin.PostAsync("/api/admin/ups", Json.Body(new { name = "SIM1", driver = "simulated" }));
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("conflict", await Json.ErrorAsync(duplicate));
        Assert.Equal(HttpStatusCode.NotFound,
                     (await admin.PutAsync("/api/admin/ups/nope", Json.Body(new { driver = "simulated" }))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/admin/ups/nope")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/admin/ups/nope/restart", null)).StatusCode);
    }

    [Fact]
    public async Task Rename_cascades_to_host_protection_and_nut_accounts()
    {
        await using var host = await WebTestHost.StartAsync(Seed);
        HttpClient admin = await host.SignedInAsync("admin");

        HttpResponseMessage response = await admin.PutAsync("/api/admin/ups/sim1", Json.Body(new { name = "rack1" }));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        NutHubConfig config = host.Config.Current;
        Assert.Equal(["rack1"], config.HostProtection.Ups);
        Assert.Equal(["rack1", "sim2"], config.NutUsers.Single(u => u.Name == "upsmon").AllowedUps);
        Assert.Equal("simulated", config.Ups[0].Driver);
    }

    [Fact]
    public async Task Delete_cascades_but_never_widens_a_restricted_account()
    {
        await using var host = await WebTestHost.StartAsync(c =>
        {
            Seed(c);
            c.NutUsers.Add(new NutUserConfig { Name = "only2", PasswordHash = "x", AllowedUps = ["sim2"] });
        });
        HttpClient admin = await host.SignedInAsync("admin");

        HttpResponseMessage refused = await admin.DeleteAsync("/api/admin/ups/sim2");
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/admin/nut-users/only2")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/admin/ups/sim2")).StatusCode);
        NutHubConfig config = host.Config.Current;
        Assert.Equal(["sim1"], config.NutUsers.Single(u => u.Name == "upsmon").AllowedUps);
        Assert.DoesNotContain(config.Ups, u => u.Name == "sim2");
    }

    [Fact]
    public async Task Order_restart_and_validation_of_the_order()
    {
        await using var host = await WebTestHost.StartAsync(Seed);
        await host.WaitForUpsAsync("sim1", connected: false);
        HttpClient admin = await host.SignedInAsync("admin");

        Assert.Equal(HttpStatusCode.NoContent,
                     (await admin.PutAsync("/api/admin/ups-order", Json.Body(new { names = new[] { "sim2" } }))).StatusCode);
        Assert.Equal(["sim2", "sim1"], host.Config.Current.Ups.Select(u => u.Name));

        HttpResponseMessage unknown = await admin.PutAsync("/api/admin/ups-order", Json.Body(new { names = new[] { "zz" } }));
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
        Assert.True((await Json.ReadAsync(unknown))["fields"]!.AsObject().ContainsKey("names"));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/api/admin/ups/sim1/restart", null)).StatusCode);
    }

    private static void Seed(NutHubConfig c)
    {
        c.Ups.Add(new UpsConfig { Name = "sim2", Driver = "simulated" });
        c.HostProtection.Ups = ["sim1"];
        c.NutUsers.Add(new NutUserConfig
        {
            Name = "upsmon",
            PasswordHash = WebTestHost.Hasher.Hash("upsmon-pass"),
            Monitor = NutMonitorRole.Secondary,
            AllowedUps = ["sim1", "sim2"],
        });
    }

    private static string? Stored(WebTestHost host, string ups, string key) =>
        host.Config.Current.Ups.Single(u => u.Name == ups).Options.GetValueOrDefault(key);
}
