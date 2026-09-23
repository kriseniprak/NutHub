using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Model;
using NutHub.Drivers.Snmp.Engine;
using NutHub.Drivers.Snmp.Mibs;
using NutHub.Drivers.Snmp.Tests.Fakes;
using NutHub.Drivers.Snmp.Transport;
using static NutHub.Drivers.Snmp.Tests.Fakes.Profiles;

namespace NutHub.Drivers.Snmp.Tests;

public sealed class MibDetectionTests
{
    private static async Task<string> DetectAsync(AgentStore store, string mib = "auto")
    {
        var client = new SnmpClient(new InMemoryTransport(store), 16, NullLogger.Instance);
        return (await MibDetector.DetectAsync(client, mib, NullLogger.Instance, CancellationToken.None)).Mib.Name;
    }

    [Fact]
    public async Task Apc_card_is_recognised_by_its_sysObjectID() =>
        Assert.Equal("apcc", await DetectAsync(ApcUps(IetfUps()))); // implements both: the vendor MIB wins

    [Fact]
    public async Task Unknown_vendor_falls_back_to_rfc1628() => Assert.Equal("ietf", await DetectAsync(IetfUps()));

    [Fact]
    public async Task Announced_mib_that_does_not_answer_is_skipped()
    {
        AgentStore s = IetfUps();
        s.SetOid(SysObjectId, "1.3.6.1.4.1.318.1.3.27"); // says APC, but has no PowerNet objects
        Assert.Equal("ietf", await DetectAsync(s));
    }

    [Fact]
    public async Task Without_sysObjectID_vendor_mibs_are_probed_before_rfc1628()
    {
        AgentStore s = IetfUps();
        s.Remove(SysObjectId);
        s.Set("1.3.6.1.4.1.705.1.1.1.0", "Pulsar"); // upsmgIdentFamilyName
        Assert.Equal("mge", await DetectAsync(s));
    }

    [Fact]
    public async Task Tripp_lite_is_recognised_only_by_its_sysObjectID()
    {
        AgentStore s = IetfUps();
        s.SetOid(SysObjectId, "1.3.6.1.4.1.850.1");
        Assert.Equal("tripplite", await DetectAsync(s));
    }

    [Fact]
    public async Task Sysobjectid_sent_as_text_is_accepted()
    {
        AgentStore s = ApcUps();
        s.Set(SysObjectId, ".1.3.6.1.4.1.318.1.3.27");
        Assert.Equal("apcc", await DetectAsync(s));
    }

    [Fact]
    public async Task Explicit_mib_must_be_implemented()
    {
        Assert.Equal("ietf", await DetectAsync(ApcUps(IetfUps()), "ietf"));
        var ex = await Assert.ThrowsAsync<SnmpMibNotFoundException>(() => DetectAsync(IetfUps(), "apcc"));
        Assert.Contains("APC PowerNet", ex.Message);
    }

    [Fact]
    public async Task Agent_without_ups_mib_is_reported()
    {
        AgentStore s = new AgentStore().SystemGroup("1.3.6.1.4.1.8072.3.2.10"); // a Linux host
        var ex = await Assert.ThrowsAsync<SnmpMibNotFoundException>(() => DetectAsync(s));
        Assert.Contains("none of the supported UPS MIBs", ex.Message);
    }

    [Fact]
    public async Task V1_agent_is_probed_in_one_request_despite_noSuchName()
    {
        AgentStore s = IetfUps();
        var client = new SnmpClient(new InMemoryTransport(s, v1: true), 16, NullLogger.Instance);
        MibDetector.Result result = await MibDetector.DetectAsync(client, "auto", NullLogger.Instance, CancellationToken.None);
        Assert.Equal("ietf", result.Mib.Name);

        // sysObjectID, then the probe batch shrinking by one rejected object per request.
        Assert.True(s.RequestSizes.Count <= MibCatalog.All.Count(m => m.Probeable) + 1);
    }

    /// <summary>Guards the hand-ported tables: well-formed OIDs and NUT names, unique MIB names.</summary>
    [Fact]
    public void Mapping_tables_are_well_formed()
    {
        Assert.Equal(MibCatalog.All.Count, MibCatalog.All.Select(m => m.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        foreach (MibDefinition mib in MibCatalog.All)
        {
            Assert.False(string.IsNullOrEmpty(mib.EffectiveProbeOid));
            foreach (MibEntry entry in mib.Entries)
            {
                Assert.True(NutFormat.IsValidVariableName(entry.Name), $"{mib.Name}: {entry.Name}");
                if (entry.Oid is not null)
                {
                    Assert.Matches(@"^\d+(\.\d+)+$", entry.Oid);
                }

                if (entry.Kind == MibEntryKind.Status)
                {
                    Assert.NotNull(entry.Lookup);
                }

                if (entry.Kind == MibEntryKind.Command)
                {
                    Assert.NotNull(entry.Oid);
                }
            }
        }
    }
}
