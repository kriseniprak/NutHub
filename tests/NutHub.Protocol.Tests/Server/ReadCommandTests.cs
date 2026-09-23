using NutHub.Core;
using NutHub.Core.Model;
using NutHub.Protocol.Parsing;
using NutHub.Protocol.Tests.Infrastructure;
using Xunit.Abstractions;

namespace NutHub.Protocol.Tests.Server;

public sealed class ReadCommandTests(ITestOutputHelper output)
{
    [Fact]
    public async Task List_ups_quotes_descriptions()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();

        List<string> lines = await client.ListAsync("LIST UPS");
        Assert.Equal(
        [
            "BEGIN LIST UPS",
            "UPS ups1 \"Rack \\\"A\\\" \\#1 \\\\ main\"",
            "UPS ups2 \"Description unavailable\"",
            "UPS off \"Description unavailable\"",
            "END LIST UPS",
        ], lines);

        Assert.True(NutLineParser.TryParse(lines[1], out string[] words));
        Assert.Equal("Rack \"A\" #1 \\ main", words[2]);
    }

    [Fact]
    public async Task Upsdesc_answers_unavailable_without_a_description()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();

        Assert.Equal("UPSDESC ups1 \"Rack \\\"A\\\" \\#1 \\\\ main\"", await client.CommandAsync("GET UPSDESC ups1"));
        Assert.Equal("UPSDESC UPS2 \"Unavailable\"", await client.CommandAsync("GET UPSDESC UPS2"));
        Assert.Equal("UPSDESC off \"Unavailable\"", await client.CommandAsync("GET UPSDESC off"));
        Assert.Equal("ERR UNKNOWN-UPS", await client.CommandAsync("GET UPSDESC nope"));
    }

    [Fact]
    public async Task Get_var_looks_names_up_ignoring_case_and_echoes_them()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();

        Assert.Equal("VAR ups1 ups.status \"OL\"", await client.CommandAsync("GET VAR ups1 ups.status"));
        Assert.Equal("VAR UPS1 UPS.Status \"OL\"", await client.CommandAsync("GET VAR UPS1 UPS.Status"));
        Assert.Equal("VAR ups1 battery.charge \"100\"", await client.CommandAsync("get var ups1 battery.charge"));
        Assert.Equal("VAR ups1 driver.name \"fake\"", await client.CommandAsync("GET VAR ups1 driver.name"));
        Assert.Equal("VAR ups1 device.model \"Fake 1500\"", await client.CommandAsync("GET VAR ups1 device.model"));
        Assert.Equal("ERR VAR-NOT-SUPPORTED", await client.CommandAsync("GET VAR ups1 no.such.var"));
        Assert.Equal("ERR UNKNOWN-UPS", await client.CommandAsync("GET VAR nope ups.status"));
    }

    [Fact]
    public async Task Server_variables_ignore_the_ups_name()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();

        Assert.Equal($"VAR nope server.version \"{NutHubInfo.Version}\"",
                     await client.CommandAsync("GET VAR nope server.version"));
        Assert.StartsWith("VAR ups1 server.info \"NutHub ", await client.CommandAsync("GET VAR ups1 SERVER.INFO"));
        Assert.Equal("ERR VAR-NOT-SUPPORTED", await client.CommandAsync("GET VAR ups1 server.other"));
    }

    [Fact]
    public async Task List_var_lists_every_variable_in_upsd_order()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();

        List<string> lines = await client.ListAsync("LIST VAR ups1");
        Assert.Equal("BEGIN LIST VAR ups1", lines[0]);
        Assert.Equal("END LIST VAR ups1", lines[^1]);

        var names = new List<string>();
        var values = new Dictionary<string, string>();
        foreach (string line in lines[1..^1])
        {
            Assert.True(NutLineParser.TryParse(line, out string[] words));
            Assert.Equal(4, words.Length);
            Assert.Equal("VAR", words[0]);
            Assert.Equal("ups1", words[1]);
            names.Add(words[2]);
            values[words[2]] = words[3];
        }

        Assert.Equal(host.Registry.Find("ups1")!.Snapshot.Variables.Count, names.Count);
        Assert.Equal(names.Order(NutNameComparer.Instance), names);
        Assert.True(names.IndexOf("input.frequency") < names.IndexOf("input.L2.voltage"));
        Assert.Equal("OL", values["ups.status"]);
        Assert.Equal("fake", values["driver.name"]);
    }

    [Fact]
    public async Task Values_with_special_characters_round_trip()
    {
        await using var host = await NutTestHost.StartAsync(output);
        const string model = "Model \"X\" #2 \\ back";
        FakeDevice device = host.Device("ups1");
        device.Variables["ups.model"] = model;
        device.Publish();
        await host.WaitForUpsAsync("ups1", s => s.Get("ups.model") == model, "the new model");

        await using var client = await host.ConnectAsync();
        string line = await client.CommandAsync("GET VAR ups1 ups.model");
        Assert.Equal("VAR ups1 ups.model \"Model \\\"X\\\" \\#2 \\\\ back\"", line);
        Assert.True(NutLineParser.TryParse(line, out string[] words));
        Assert.Equal(model, words[3]);
    }

    [Fact]
    public async Task List_rw_lists_only_writable_variables()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();

        Assert.Equal(
        [
            "BEGIN LIST RW ups1",
            "RW ups1 battery.charge.low \"20\"",
            "RW ups1 input.transfer.low \"180\"",
            "RW ups1 ups.beeper.status \"enabled\"",
            "RW ups1 ups.delay.shutdown \"20\"",
            "RW ups1 ups.id \"rack\"",
            "END LIST RW ups1",
        ], await client.ListAsync("LIST RW ups1"));
    }

    [Fact]
    public async Task Get_type_describes_the_variable()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();

        Assert.Equal("TYPE ups1 battery.charge.low RW RANGE", await client.CommandAsync("GET TYPE ups1 battery.charge.low"));
        Assert.Equal("TYPE ups1 input.transfer.low RW ENUM", await client.CommandAsync("GET TYPE ups1 input.transfer.low"));
        Assert.Equal("TYPE ups1 ups.id RW STRING:16", await client.CommandAsync("GET TYPE ups1 ups.id"));
        Assert.Equal("TYPE ups1 ups.delay.shutdown RW NUMBER", await client.CommandAsync("GET TYPE ups1 ups.delay.shutdown"));
        Assert.Equal("TYPE ups1 battery.charge NUMBER", await client.CommandAsync("GET TYPE ups1 battery.charge"));
        Assert.Equal("TYPE ups1 UPS.ID RW STRING:16", await client.CommandAsync("GET TYPE ups1 UPS.ID"));
        Assert.Equal("ERR VAR-NOT-SUPPORTED", await client.CommandAsync("GET TYPE ups1 no.such.var"));
        Assert.Equal("ERR UNKNOWN-UPS", await client.CommandAsync("GET TYPE nope ups.id"));
    }

    [Fact]
    public async Task Enum_and_range_lists()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();

        Assert.Equal(
        [
            "BEGIN LIST ENUM ups1 input.transfer.low",
            "ENUM ups1 input.transfer.low \"170\"",
            "ENUM ups1 input.transfer.low \"180\"",
            "ENUM ups1 input.transfer.low \"190\"",
            "END LIST ENUM ups1 input.transfer.low",
        ], await client.ListAsync("LIST ENUM ups1 input.transfer.low"));

        Assert.Equal(["BEGIN LIST ENUM ups1 battery.charge", "END LIST ENUM ups1 battery.charge"],
                     await client.ListAsync("LIST ENUM ups1 battery.charge"));

        Assert.Equal(
        [
            "BEGIN LIST RANGE ups1 battery.charge.low",
            "RANGE ups1 battery.charge.low \"5\" \"90\"",
            "END LIST RANGE ups1 battery.charge.low",
        ], await client.ListAsync("LIST RANGE ups1 battery.charge.low"));

        Assert.Equal(["ERR VAR-NOT-SUPPORTED"], await client.ListAsync("LIST ENUM ups1 no.such.var"));
        Assert.Equal(["ERR VAR-NOT-SUPPORTED"], await client.ListAsync("LIST RANGE ups1 no.such.var"));
        Assert.Equal(["ERR UNKNOWN-UPS"], await client.ListAsync("LIST ENUM nope x"));
    }

    [Fact]
    public async Task List_cmd_lists_the_instant_commands()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();

        Assert.Equal(
        [
            "BEGIN LIST CMD UPS1",
            "CMD UPS1 beeper.disable",
            "CMD UPS1 beeper.enable",
            "CMD UPS1 load.off.delay",
            "CMD UPS1 shutdown.return",
            "CMD UPS1 test.battery.start.quick",
            "END LIST CMD UPS1",
        ], await client.ListAsync("LIST CMD UPS1"));
        Assert.Equal(["ERR UNKNOWN-UPS"], await client.ListAsync("LIST CMD nope"));
    }

    [Fact]
    public async Task Descriptions_come_from_the_catalog()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();

        Assert.Equal("DESC ups1 battery.charge \"Battery charge (percent of full)\"",
                     await client.CommandAsync("GET DESC ups1 battery.charge"));
        Assert.Equal("DESC ups1 BATTERY.Charge \"Battery charge (percent of full)\"",
                     await client.CommandAsync("GET DESC ups1 BATTERY.Charge"));
        Assert.Equal("DESC ups1 upstream.ups.status \"UPS status\"",
                     await client.CommandAsync("GET DESC ups1 upstream.ups.status"));
        Assert.Equal("DESC ups1 no.such.var \"Description unavailable\"",
                     await client.CommandAsync("GET DESC ups1 no.such.var"));
        Assert.Equal("CMDDESC ups1 load.off.delay \"Turn off the load with a delay (seconds)\"",
                     await client.CommandAsync("GET CMDDESC ups1 load.off.delay"));
        Assert.Equal("CMDDESC ups1 BEEPER.ENABLE \"Enable the UPS beeper\"",
                     await client.CommandAsync("GET CMDDESC ups1 BEEPER.ENABLE"));
        Assert.Equal("CMDDESC ups1 no.such.cmd \"Description unavailable\"",
                     await client.CommandAsync("GET CMDDESC ups1 no.such.cmd"));
        Assert.Equal("ERR UNKNOWN-UPS", await client.CommandAsync("GET DESC nope battery.charge"));
        Assert.Equal("ERR UNKNOWN-UPS", await client.CommandAsync("GET CMDDESC nope load.off"));
    }

    [Fact]
    public async Task Numlogins_starts_at_zero()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();
        Assert.Equal("NUMLOGINS ups1 0", await client.CommandAsync("GET NUMLOGINS ups1"));
        Assert.Equal("ERR UNKNOWN-UPS", await client.CommandAsync("GET NUMLOGINS nope"));
    }

    [Fact]
    public async Task Works_with_the_simulated_driver()
    {
        await using var host = await NutTestHost.StartAsync(output, c =>
            c.Ups.Add(new() { Name = "sim", Driver = "simulated", Description = "Simulated" }));
        await using var client = await host.ConnectAsAsync("admin");

        Assert.Equal("VAR sim ups.status \"OL\"", await client.CommandAsync("GET VAR sim ups.status"));
        List<string> vars = await client.ListAsync("LIST VAR sim");
        Assert.Contains("VAR sim ups.model \"Virtual UPS 1500\"", vars);
        Assert.Contains("CMD sim test.failure.start", await client.ListAsync("LIST CMD sim"));

        Assert.Equal("OK", await client.CommandAsync("INSTCMD sim test.failure.start"));
        host.Time.Advance(TimeSpan.FromSeconds(2));
        await host.WaitForUpsAsync("sim", s => s.Has(UpsStatusFlags.OnBattery), "the simulated outage");
        Assert.StartsWith("VAR sim ups.status \"OB", await client.CommandAsync("GET VAR sim ups.status"));
    }
}
