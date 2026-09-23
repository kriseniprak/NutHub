using NutHub.Core.Drivers;
using NutHub.Drivers.Snmp.Mibs;
using NutHub.Drivers.Snmp.Tests.Fakes;
using static NutHub.Drivers.Snmp.Tests.Fakes.Profiles;

namespace NutHub.Drivers.Snmp.Tests;

public sealed class MibStatusTests
{
    [Theory]
    [InlineData(3, 2, "OL")]
    [InlineData(5, 2, "OB")]
    [InlineData(5, 3, "OB LB")]
    [InlineData(5, 4, "OB LB")]
    [InlineData(4, 2, "OL BYPASS")]
    [InlineData(6, 2, "OL BOOST")]
    [InlineData(7, 2, "OL TRIM")]
    [InlineData(2, 2, "OFF")]
    public async Task Ietf_status_comes_from_output_source_and_battery_status(int source, int battery, string expected)
    {
        AgentStore s = IetfUps();
        s.Set(Ietf + "4.1.0", source);
        s.Set(Ietf + "2.1.0", battery);
        Assert.Equal(expected, (await PollOnceAsync(s)).Var("ups.status"));
    }

    [Fact]
    public async Task Ietf_alarm_table_adds_tokens_and_messages()
    {
        AgentStore s = IetfUps();
        s.Set(Ietf + "6.1.0", 3);
        s.SetOid(Ietf + "6.2.1.2.1", Ietf + "6.3.1"); // upsAlarmBatteryBad
        s.SetOid(Ietf + "6.2.1.2.2", Ietf + "6.3.8"); // upsAlarmOutputOverload
        s.SetOid(Ietf + "6.2.1.2.3", Ietf + "6.3.99"); // unknown alarm: ignored
        s.Set(Ietf + "6.2.1.3.1", 1234); // upsAlarmTime column: must not be taken for descriptions

        DriverUpdate u = await PollOnceAsync(s);
        Assert.Equal("ALARM OL RB OVER", u.Var("ups.status"));
        Assert.Equal("Battery bad! Output overload!", u.Var("ups.alarm"));
    }

    [Fact]
    public async Task Ietf_test_in_progress_is_reported()
    {
        AgentStore s = IetfUps();
        s.SetOid(Ietf + "7.1.0", Ietf + "7.7.5"); // upsTestDeepBatteryCalibration
        Assert.Equal("OL CAL", (await PollOnceAsync(s)).Var("ups.status"));
        s.SetOid(Ietf + "7.1.0", Ietf + "7.7.4"); // upsTestQuickBatteryTest
        Assert.Equal("OL TEST", (await PollOnceAsync(s)).Var("ups.status"));
    }

    [Theory]
    [InlineData(2, 2, 1, 1, "OL")]
    [InlineData(3, 2, 1, 1, "OB")]
    [InlineData(3, 3, 1, 1, "OB LB")]
    [InlineData(2, 2, 2, 1, "OL RB")]
    [InlineData(9, 2, 1, 1, "BYPASS")]
    [InlineData(12, 2, 1, 1, "OL TRIM")]
    [InlineData(4, 2, 1, 1, "OL BOOST")]
    [InlineData(7, 2, 1, 1, "OFF")]
    [InlineData(2, 2, 1, 3, "OL CAL")]
    public async Task Apc_status(int output, int battery, int replace, int calibration, string expected)
    {
        AgentStore s = ApcUps();
        s.Set(Apc + "4.1.1.0", output);
        s.Set(Apc + "2.1.1.0", battery);
        s.Set(Apc + "2.2.4.0", replace);
        s.Set(Apc + "7.2.6.0", calibration);
        Assert.Equal(expected, (await PollOnceAsync(s)).Var("ups.status"));
    }

    [Fact]
    public async Task Eaton_xups_on_battery_with_low_battery_alarm()
    {
        const string pw = "1.3.6.1.4.1.534.1.";
        AgentStore s = new AgentStore().SystemGroup("1.3.6.1.4.1.534.1");
        s.Set(pw + "1.1.0", "EATON");
        s.Set(pw + "1.2.0", "9PX 3000");
        s.Set(pw + "4.5.0", 5); // xupsOutputSource: battery
        s.Set(pw + "11.0", 1); // unrelated object
        s.Set(pw + "7.1.0", 1); // xupsAlarms
        s.SetOid(pw + "7.2.1.2.1", pw + "7.4"); // xupsLowBattery

        MibSessionAssert u = new(await PollOnceAsync(s));
        Assert.Equal("pw MIB 0.106", u.Update.Var("driver.version.data"));
        Assert.Contains("OB", u.Tokens);
        Assert.Contains("LB", u.Tokens);
    }

    [Theory]
    [InlineData("7.3.0", "OB")] // upsmgOutputOnBattery
    [InlineData("7.4.0", "BYPASS")] // upsmgOutputOnByPass
    [InlineData("5.11.0", "RB")] // upsmgBatteryReplacement
    [InlineData("5.14.0", "LB")] // upsmgBatteryLowBattery
    [InlineData("7.10.0", "OVER")] // upsmgOutputOverLoad
    public async Task Mge_yes_no_objects_set_their_token(string oid, string token)
    {
        const string mge = "1.3.6.1.4.1.705.1.";
        AgentStore s = new AgentStore().SystemGroup("1.3.6.1.4.1.705.1");
        s.Set(mge + "1.1.0", "Galaxy");
        foreach (string flag in new[] { "5.11.0", "5.14.0", "5.16.0", "7.3.0", "7.4.0", "7.7.0", "7.8.0", "7.10.0", "7.12.0" })
        {
            s.Set(mge + flag, 2); // no
        }

        s.Set(mge + oid, 1); // yes
        DriverUpdate update = await PollOnceAsync(s);
        Assert.Equal("mge", update.Var("driver.version.data")!.Split(' ')[0]);
        Assert.Contains(token, update.Status());
    }

    public static TheoryData<string, string, long, string> StatusLookups()
    {
        var data = new TheoryData<string, string, long, string>();
        foreach (MibDefinition mib in MibCatalog.All.Where(m => m.Probeable))
        {
            foreach (MibEntry entry in mib.Entries.Where(e => e.Kind == MibEntryKind.Status && e.Lookup is not null))
            {
                foreach (var (value, text) in entry.Lookup!.Pairs.Where(p => p.Text.Length > 0).DistinctBy(p => p.Value))
                {
                    data.Add(mib.Name, entry.Oid!, value, text);
                }
            }
        }

        return data;
    }

    /// <summary>
    /// Every status value of every MIB, read from an agent of that MIB, gives its tokens: guards the tables and the
    /// detection together (a table whose probe object or status OIDs were mistyped fails here).
    /// </summary>
    [Theory]
    [MemberData(nameof(StatusLookups))]
    public async Task Every_status_value_of_every_mib_gives_its_tokens(string mibName, string oid, long value, string tokens)
    {
        MibDefinition mib = MibCatalog.Find(mibName)!;
        AgentStore s = new AgentStore().SystemGroup(mib.SysObjectIds.FirstOrDefault() ?? "1.3.6.1.4.1.99999");
        s.Set(mib.EffectiveProbeOid, "Model");
        s.Set(oid, (int)value);

        MibSessionAssert u = new(await PollOnceAsync(s, mibName));
        Assert.Equal($"{mib.Name} MIB {mib.Version}", u.Update.Var("driver.version.data"));
        foreach (string token in tokens.Split(' '))
        {
            Assert.Contains(token, u.Tokens);
        }
    }

    private sealed class MibSessionAssert(DriverUpdate update)
    {
        public DriverUpdate Update { get; } = update;

        public string[] Tokens { get; } = update.Status();
    }
}
