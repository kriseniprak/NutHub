using System.Net;
using System.Text.Json.Nodes;
using NutHub.Core.Model;
using NutHub.Web.Api;
using NutHub.Web.Api.Dto;
using NutHub.Web.Api.Endpoints;
using NutHub.Web.Tests.TestSupport;

namespace NutHub.Web.Tests;

public sealed class ReadApiTests
{
    [Fact]
    public async Task Overview_has_the_server_and_every_ups()
    {
        await using var host = await WebTestHost.StartAsync();
        await host.WaitForUpsAsync("sim1");
        HttpClient client = await host.SignedInAsync("viewer");

        var overview = await Json.ReadAsync(await client.GetAsync("/api/overview"));

        JsonNode server = overview["server"]!;
        Assert.Equal("NutHub", server["name"]!.GetValue<string>());
        Assert.True(server["uptimeSeconds"]!.GetValue<long>() >= 0);
        Assert.EndsWith("Z", server["time"]!.GetValue<string>());
        Assert.False(server["nut"]!["enabled"]!.GetValue<bool>());
        Assert.Equal("disabled", server["hostProtection"]!["state"]!.GetValue<string>());

        JsonNode ups = Assert.Single(overview["ups"]!.AsArray())!;
        Assert.Equal("sim1", ups["name"]!.GetValue<string>());
        Assert.Equal("Simulated UPS", ups["description"]!.GetValue<string>());
        Assert.Equal("simulated", ups["driver"]!.GetValue<string>());
        Assert.Equal("connected", ups["driverState"]!.GetValue<string>());
        Assert.Equal("available", ups["availability"]!.GetValue<string>());
        Assert.Contains("OL", ups["flags"]!.AsArray().Select(f => f!.GetValue<string>()));
        Assert.InRange(ups["battery"]!["charge"]!.GetValue<double>(), 0, 100);
        Assert.True(ups.AsObject().ContainsKey("temperature"));
        Assert.Equal(0, ups["clients"]!.GetValue<int>());
        Assert.True(ups["sequence"]!.GetValue<long>() > 0);
    }

    [Fact]
    public async Task Detail_lists_variables_and_flags_dangerous_commands()
    {
        await using var host = await WebTestHost.StartAsync();
        await host.WaitForUpsAsync("sim1");
        HttpClient client = await host.SignedInAsync("viewer");

        var detail = await Json.ReadAsync(await client.GetAsync("/api/ups/SIM1"));

        Assert.Equal("sim1", detail["summary"]!["name"]!.GetValue<string>());
        JsonNode charge = detail["variables"]!.AsArray().First(v => v!["name"]!.GetValue<string>() == "battery.charge")!;
        Assert.False(string.IsNullOrEmpty(charge["description"]!.GetValue<string>()));
        Assert.Equal("number", charge["type"]!.GetValue<string>());
        JsonNode low = detail["variables"]!.AsArray().First(v => v!["name"]!.GetValue<string>() == "battery.charge.low")!;
        Assert.True(low["writable"]!.GetValue<bool>());
        Assert.Single(low["ranges"]!.AsArray());

        var commands = detail["commands"]!.AsArray().ToDictionary(c => c!["name"]!.GetValue<string>(),
                                                                   c => c!["dangerous"]!.GetValue<bool>());
        Assert.True(commands["load.off"]);
        Assert.True(commands["shutdown.return"]);
        Assert.False(commands["shutdown.stop"]);
        Assert.False(commands["test.battery.start.quick"]);
        Assert.Equal(2, detail["pollIntervalSeconds"]!.GetValue<double>());
        Assert.Empty(detail["clients"]!.AsArray());
    }

    [Fact]
    public async Task Unknown_ups_is_not_found()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient client = await host.SignedInAsync("viewer");

        HttpResponseMessage response = await client.GetAsync("/api/ups/nope");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("notFound", await Json.ErrorAsync(response));
    }

    [Fact]
    public async Task History_returns_points_as_arrays_for_the_requested_range()
    {
        await using var host = await WebTestHost.StartAsync();
        await host.WaitForUpsAsync("sim1", connected: false);
        HttpClient client = await host.SignedInAsync("viewer");

        var history = await Json.ReadAsync(await client.GetAsync("/api/ups/sim1/history?range=1h&vars=battery.charge,ups.load"));

        Assert.Equal(30, history["stepSeconds"]!.GetValue<int>());
        DateTimeOffset from = DateTimeOffset.Parse(history["from"]!.GetValue<string>());
        DateTimeOffset to = DateTimeOffset.Parse(history["to"]!.GetValue<string>());
        Assert.Equal(TimeSpan.FromHours(1), to - from);
        JsonArray points = history["series"]!["battery.charge"]!.AsArray();
        Assert.Equal(2, points.Count);
        Assert.Equal(from.ToUnixTimeMilliseconds(), points[0]![0]!.GetValue<long>());
        Assert.Equal(99.5, points[0]![1]!.GetValue<double>());
        Assert.Equal(99, points[0]![2]!.GetValue<double>());
        Assert.Equal(100, points[0]![3]!.GetValue<double>());
        Assert.Equal(500, host.History.LastQuery!.MaxPoints);
        Assert.Equal(["battery.charge", "ups.load"], host.History.LastQuery.Variables);

        await client.GetAsync("/api/ups/sim1/history");
        Assert.Equal(host.Config.Current.History.Variables, host.History.LastQuery!.Variables);
        Assert.Equal(TimeSpan.FromHours(24), host.History.LastQuery.To - host.History.LastQuery.From);

        await client.GetAsync("/api/ups/sim1/history?from=2026-01-01T00:00:00Z&to=2026-01-02T00:00:00Z");
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), host.History.LastQuery!.From);

        HttpResponseMessage bad = await client.GetAsync("/api/ups/sim1/history?range=2y");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.NotNull((await Json.ReadAsync(bad))["fields"]!["range"]);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/ups/nope/history")).StatusCode);
    }

    [Fact]
    public async Task Events_can_be_filtered_and_paged()
    {
        await using var host = await WebTestHost.StartAsync();
        await host.WaitForUpsAsync("sim1");
        HttpClient oper = await host.SignedInAsync("operator");
        Assert.True((await Json.ReadAsync(await oper.PostAsync("/api/ups/sim1/fsd", null)))["ok"]!.GetValue<bool>());
        var cleared = await Json.ReadAsync(await oper.DeleteAsync("/api/ups/sim1/fsd"));
        Assert.Equal("success", cleared["status"]!.GetValue<string>());

        await host.WaitUntilAsync(() => host.Services.GetService(typeof(Core.Abstractions.IEventStore)) is Core.Abstractions.IEventStore s &&
                                        s.QueryAsync(new Core.Abstractions.EventQuery(Category: EventCategory.Power)).Result.Items.Count >= 2,
                                  "FSD events");

        var page = await Json.ReadAsync(await oper.GetAsync("/api/events?ups=sim1&category=power&limit=1"));
        JsonNode e = Assert.Single(page["items"]!.AsArray())!;
        Assert.True(page["hasMore"]!.GetValue<bool>());
        Assert.Equal("forcedShutdownCleared", e["type"]!.GetValue<string>());
        Assert.Equal("power", e["category"]!.GetValue<string>());
        Assert.Equal("sim1", e["ups"]!.GetValue<string>());
        Assert.StartsWith("web:operator@", e["actor"]!.GetValue<string>());
        Assert.NotNull(e["data"]);

        long id = e["id"]!.GetValue<long>();
        var older = await Json.ReadAsync(await oper.GetAsync($"/api/events?ups=sim1&category=power&beforeId={id}"));
        Assert.Contains(older["items"]!.AsArray(), x => x!["type"]!.GetValue<string>() == "forcedShutdown");

        HttpResponseMessage bad = await oper.GetAsync("/api/events?minSeverity=loud");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.NotNull((await Json.ReadAsync(bad))["fields"]!["minSeverity"]);
    }

    [Fact]
    public async Task Command_results_have_the_documented_shape()
    {
        await using var host = await WebTestHost.StartAsync();
        await host.WaitForUpsAsync("sim1");
        HttpClient oper = await host.SignedInAsync("operator");

        var ok = await Json.ReadAsync(await oper.PostAsync("/api/ups/sim1/commands", Json.Body(new { command = "beeper.mute" })));
        Assert.True(ok["ok"]!.GetValue<bool>());
        Assert.Equal("success", ok["status"]!.GetValue<string>());

        var unknown = await Json.ReadAsync(await oper.PostAsync("/api/ups/sim1/commands", Json.Body(new { command = "no.such.command" })));
        Assert.False(unknown["ok"]!.GetValue<bool>());
        Assert.Equal("notSupported", unknown["status"]!.GetValue<string>());

        var invalid = await Json.ReadAsync(await oper.PutAsync("/api/ups/sim1/variables/battery.charge.low", Json.Body(new { value = "99" })));
        Assert.False(invalid["ok"]!.GetValue<bool>());
        Assert.Equal("invalidValue", invalid["status"]!.GetValue<string>());

        var written = await Json.ReadAsync(await oper.PutAsync("/api/ups/sim1/variables/battery.charge.low", Json.Body(new { value = "30" })));
        Assert.Equal("success", written["status"]!.GetValue<string>());

        HttpResponseMessage missing = await oper.PostAsync("/api/ups/sim1/commands", Json.Body(new { command = "" }));
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await oper.PostAsync("/api/ups/nope/commands", Json.Body(new { command = "x" }))).StatusCode);
    }

    [Theory]
    [InlineData("load.off", true)]
    [InlineData("load.off.delay", true)]
    [InlineData("load.on", false)]
    [InlineData("shutdown.return", true)]
    [InlineData("shutdown.stop", false)]
    [InlineData("calibrate.start", true)]
    [InlineData("calibrate.stop", false)]
    [InlineData("test.failure.start", true)]
    [InlineData("test.battery.start.deep", false)]
    [InlineData("bypass.start", true)]
    [InlineData("reset.input.minmax", true)]
    [InlineData("outlet.1.load.off", true)]
    [InlineData("outlet.2.shutdown.return", true)]
    [InlineData("outlet.1.load.on", false)]
    [InlineData("beeper.disable", false)]
    public void Dangerous_commands_follow_the_documented_patterns(string command, bool dangerous) =>
        Assert.Equal(dangerous, ApiViews.IsDangerous(command));

    [Theory]
    [InlineData("OL CHRG", DataAvailability.Available, true, UpsSeverity.Ok)]
    [InlineData("OL TRIM", DataAvailability.Available, true, UpsSeverity.Info)]
    [InlineData("OL CAL", DataAvailability.Available, true, UpsSeverity.Info)]
    [InlineData("OB DISCHRG", DataAvailability.Available, true, UpsSeverity.Warning)]
    [InlineData("OL RB", DataAvailability.Available, true, UpsSeverity.Warning)]
    [InlineData("OL", DataAvailability.Stale, true, UpsSeverity.Warning)]
    [InlineData("OB LB", DataAvailability.Available, true, UpsSeverity.Critical)]
    [InlineData("FSD OL", DataAvailability.Available, true, UpsSeverity.Critical)]
    [InlineData("OFF", DataAvailability.Available, true, UpsSeverity.Critical)]
    [InlineData("OB LB", DataAvailability.DriverNotConnected, true, UpsSeverity.Offline)]
    [InlineData("OL", DataAvailability.Available, false, UpsSeverity.Offline)]
    public void Severity_takes_the_first_matching_rule(string status, DataAvailability availability, bool enabled,
                                                       object expected)
    {
        var snapshot = new UpsSnapshot
        {
            Name = "u",
            DriverId = "simulated",
            DriverState = DriverState.Connected,
            Availability = availability,
            Status = UpsStatus.Parse(status),
        };
        Assert.Equal(expected, (object)ApiViews.SeverityOf(snapshot, enabled));
    }

    [Fact]
    public void Named_ranges_end_now()
    {
        var now = new DateTimeOffset(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal((now.AddDays(-7), now), ReadEndpoints.ParseInterval("7d", null, null, now));
        Assert.Equal((now.AddHours(-24), now), ReadEndpoints.ParseInterval(null, null, null, now));
        Assert.Throws<ApiException>(() => ReadEndpoints.ParseInterval(null, "2026-09-22T12:00:00Z", "2026-09-21T12:00:00Z", now));
    }
}
