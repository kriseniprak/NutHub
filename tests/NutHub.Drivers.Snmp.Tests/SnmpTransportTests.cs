using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Security;
using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Drivers.Snmp.Tests.Fakes;
using NutHub.Drivers.Snmp.Transport;
using static NutHub.Drivers.Snmp.Tests.Fakes.Profiles;

namespace NutHub.Drivers.Snmp.Tests;

/// <summary>The UDP transport and the batching client against the fake agent on the loopback interface.</summary>
public sealed class SnmpTransportTests
{
    private static readonly string[] SomeIetfOids =
    [
        Ietf + "1.1.0", Ietf + "1.2.0", Ietf + "9.9.9.0", Ietf + "2.4.0", Ietf + "2.5.0", Ietf + "8.8.8.0",
        Ietf + "4.1.0", Ietf + "9.5.0",
    ];

    private static SnmpSettings Settings(FakeSnmpAgent agent, SnmpProtocolVersion version = SnmpProtocolVersion.V2c) =>
        Profiles.Settings(agent.Port) with { Version = version, Timeout = TimeSpan.FromMilliseconds(150), Retries = 1 };

    private static (SnmpTransport Transport, SnmpClient Client) Connect(SnmpSettings settings, int batch = 16)
    {
        var transport = new SnmpTransport(settings, TimeProvider.System, NullLogger.Instance);
        return (transport, new SnmpClient(transport, batch, NullLogger.Instance));
    }

    [Fact]
    public async Task V2c_batch_returns_existing_objects_in_one_request()
    {
        await using var agent = new FakeSnmpAgent(IetfUps());
        var (transport, client) = Connect(Settings(agent));
        await using (transport)
        {
            IReadOnlyDictionary<string, ISnmpData> values = await client.GetAsync(SomeIetfOids, CancellationToken.None);
            Assert.Equal(6, values.Count);
            Assert.Equal(new OctetString("ACME"), values[Ietf + "1.1.0"]);
            Assert.Equal([8], agent.Store.RequestSizes);
        }
    }

    [Fact]
    public async Task V1_batch_drops_the_objects_the_agent_rejects()
    {
        await using var agent = new FakeSnmpAgent(IetfUps());
        var (transport, client) = Connect(Settings(agent, SnmpProtocolVersion.V1));
        await using (transport)
        {
            IReadOnlyDictionary<string, ISnmpData> values = await client.GetAsync(SomeIetfOids, CancellationToken.None);
            Assert.Equal(6, values.Count);
            Assert.Equal([8, 7, 6], agent.Store.RequestSizes); // noSuchName for 9.9.9.0, then for 8.8.8.0
        }
    }

    [Fact]
    public async Task Too_big_answers_split_the_batch()
    {
        await using var agent = new FakeSnmpAgent(IetfUps());
        agent.Store.MaxObjectsPerRequest = 3;
        var (transport, client) = Connect(Settings(agent));
        await using (transport)
        {
            IReadOnlyDictionary<string, ISnmpData> values = await client.GetAsync(SomeIetfOids, CancellationToken.None);
            Assert.Equal(6, values.Count);
            Assert.True(client.BatchSize <= 3);
        }
    }

    [Fact]
    public async Task Agent_ignoring_multi_object_requests_is_asked_one_object_at_a_time()
    {
        await using var agent = new FakeSnmpAgent(IetfUps());
        agent.DropMultiObjectRequests = true;
        var (transport, client) = Connect(Settings(agent));
        await using (transport)
        {
            Assert.Single(await client.GetAsync([Ietf + "1.1.0"], CancellationToken.None));
            IReadOnlyDictionary<string, ISnmpData> values = await client.GetAsync(SomeIetfOids, CancellationToken.None);
            Assert.Equal(6, values.Count);
            Assert.Equal(1, client.BatchSize);
        }
    }

    [Fact]
    public async Task Walk_reads_a_column_and_stops_at_its_end()
    {
        await using var agent = new FakeSnmpAgent(IetfUps());
        agent.Store.SetOid(Ietf + "6.2.1.2.1", Ietf + "6.3.1");
        agent.Store.SetOid(Ietf + "6.2.1.2.2", Ietf + "6.3.2");
        agent.Store.Set(Ietf + "6.2.1.3.1", 55);
        foreach (SnmpProtocolVersion version in new[] { SnmpProtocolVersion.V1, SnmpProtocolVersion.V2c })
        {
            var (transport, client) = Connect(Settings(agent, version));
            await using (transport)
            {
                IReadOnlyList<Variable> rows = await client.WalkAsync(Ietf + "6.2.1.2", 10, CancellationToken.None);
                Assert.Equal(2, rows.Count);
                Assert.Equal(new ObjectIdentifier(Ietf + "6.3.2"), rows[1].Data);
            }
        }
    }

    [Fact]
    public async Task Silent_agent_times_out_after_the_retries()
    {
        await using var agent = new FakeSnmpAgent(IetfUps());
        agent.Silent = true;
        var (transport, client) = Connect(Settings(agent));
        await using (transport)
        {
            var ex = await Assert.ThrowsAsync<SnmpNoResponseException>(() => client.GetAsync([Ietf + "1.1.0"], CancellationToken.None));
            Assert.Equal($"127.0.0.1:{agent.Port}", ex.Target);
            Assert.Equal(2, agent.Received); // first attempt + 1 retry
        }
    }

    [Fact]
    public async Task Closed_port_fails_without_waiting_for_every_timeout()
    {
        int port;
        using (var probe = new System.Net.Sockets.UdpClient(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0)))
        {
            port = ((System.Net.IPEndPoint)probe.Client.LocalEndPoint!).Port;
        }

        var (transport, client) = Connect(Profiles.Settings(port) with { Timeout = TimeSpan.FromMilliseconds(300), Retries = 1 });
        await using (transport)
        {
            var ex = await Assert.ThrowsAsync<SnmpNoResponseException>(() => client.GetAsync([Ietf + "1.1.0"], CancellationToken.None));
            Assert.StartsWith($"No answer from 127.0.0.1:{port}", ex.Message);
        }
    }

    [Fact]
    public async Task Wrong_community_looks_like_a_timeout()
    {
        await using var agent = new FakeSnmpAgent(IetfUps());
        var (transport, client) = Connect(Settings(agent) with { Community = "wrong" });
        await using (transport)
        {
            await Assert.ThrowsAsync<SnmpNoResponseException>(() => client.GetAsync([Ietf + "1.1.0"], CancellationToken.None));
        }
    }

    [Fact]
    public async Task Set_uses_the_write_community()
    {
        await using var agent = new FakeSnmpAgent(IetfUps());
        agent.WriteCommunity = "private";
        var (transport, client) = Connect(Settings(agent) with { WriteCommunity = "private" });
        await using (transport)
        {
            await client.SetAsync(Ietf + "8.5.0", new Integer32(2), CancellationToken.None);
            Assert.Equal(new Integer32(2), agent.Store.Get(Ietf + "8.5.0"));
        }
    }

    [Fact]
    public async Task Cancellation_interrupts_a_pending_request()
    {
        await using var agent = new FakeSnmpAgent(IetfUps());
        agent.Silent = true;
        var (transport, client) = Connect(Settings(agent) with { Timeout = TimeSpan.FromSeconds(10) });
        await using (transport)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync([Ietf + "1.1.0"], cts.Token));
        }
    }

    public static TheoryData<string, string, string> V3Combinations() => new()
    {
        { "NoAuthNoPriv", "Md5", "Des" },
        { "AuthNoPriv", "Md5", "Des" },
        { "AuthNoPriv", "Sha512", "Des" },
        { "AuthPriv", "Sha1", "Des" },
        { "AuthPriv", "Sha1", "Aes128" },
        { "AuthPriv", "Sha256", "Aes192" },
        { "AuthPriv", "Sha384", "Aes256" },
    };

    private static SnmpSettings V3(FakeSnmpAgent agent, SnmpSecurityLevel level, SnmpAuthProtocol auth = SnmpAuthProtocol.Sha1,
                                   SnmpPrivProtocol priv = SnmpPrivProtocol.Aes128) =>
        Settings(agent, SnmpProtocolVersion.V3) with
        {
            SecurityName = "nut", SecurityLevel = level, AuthProtocol = auth, AuthPassword = "authpass1",
            PrivProtocol = priv, PrivPassword = "privpass1",
        };

    [Theory]
    [MemberData(nameof(V3Combinations))]
    public async Task V3_get_and_set_with_each_security_level(string level, string auth, string priv)
    {
        await using var agent = new FakeSnmpAgent(IetfUps());
        SnmpSettings settings = V3(agent, Enum.Parse<SnmpSecurityLevel>(level), Enum.Parse<SnmpAuthProtocol>(auth),
                                   Enum.Parse<SnmpPrivProtocol>(priv));
        agent.AddUser("nut", SnmpSecurity.CreatePrivacyProvider(settings));
        var (transport, client) = Connect(settings);
        await using (transport)
        {
            IReadOnlyDictionary<string, ISnmpData> values = await client.GetAsync(SomeIetfOids, CancellationToken.None);
            Assert.Equal(6, values.Count);
            await client.SetAsync(Ietf + "8.5.0", new Integer32(2), CancellationToken.None);
            Assert.Equal(new Integer32(2), agent.Store.Get(Ietf + "8.5.0"));
            Assert.NotNull(transport.EngineId);
        }
    }

    [Fact]
    public async Task V3_unknown_user_is_reported_as_such()
    {
        await using var agent = new FakeSnmpAgent(IetfUps());
        var (transport, client) = Connect(V3(agent, SnmpSecurityLevel.AuthPriv));
        await using (transport)
        {
            var ex = await Assert.ThrowsAsync<SnmpAuthenticationException>(() => client.GetAsync([Ietf + "1.1.0"], CancellationToken.None));
            Assert.Equal(SnmpAuthFailure.UnknownUser, ex.Failure);
            Assert.Contains("does not know the user 'nut'", ex.Message);
        }
    }

    [Fact]
    public async Task V3_wrong_authentication_password_is_reported_as_such()
    {
        await using var agent = new FakeSnmpAgent(IetfUps());
        SnmpSettings good = V3(agent, SnmpSecurityLevel.AuthNoPriv);
        agent.AddUser("nut", SnmpSecurity.CreatePrivacyProvider(good));
        var (transport, client) = Connect(good with { AuthPassword = "wrongpass" });
        await using (transport)
        {
            var ex = await Assert.ThrowsAsync<SnmpAuthenticationException>(() => client.GetAsync([Ietf + "1.1.0"], CancellationToken.None));
            Assert.Equal(SnmpAuthFailure.WrongDigest, ex.Failure);
            Assert.Contains("wrong authentication password", ex.Message);
        }
    }

    [Fact]
    public async Task V3_wrong_privacy_password_is_reported_as_such()
    {
        await using var agent = new FakeSnmpAgent(IetfUps());
        SnmpSettings good = V3(agent, SnmpSecurityLevel.AuthPriv);
        agent.AddUser("nut", SnmpSecurity.CreatePrivacyProvider(good));
        var (transport, client) = Connect(good with { PrivPassword = "wrongpriv" });
        await using (transport)
        {
            var ex = await Assert.ThrowsAsync<SnmpAuthenticationException>(() => client.GetAsync([Ietf + "1.1.0"], CancellationToken.None));
            Assert.Equal(SnmpAuthFailure.DecryptionError, ex.Failure);
        }
    }

    [Fact]
    public async Task V3_security_level_mismatch_is_reported()
    {
        await using var agent = new FakeSnmpAgent(IetfUps());
        SnmpSettings good = V3(agent, SnmpSecurityLevel.AuthPriv);
        agent.AddUser("nut", SnmpSecurity.CreatePrivacyProvider(good));
        var (transport, client) = Connect(good with { SecurityLevel = SnmpSecurityLevel.AuthNoPriv });
        await using (transport)
        {
            var ex = await Assert.ThrowsAsync<SnmpAuthenticationException>(() => client.GetAsync([Ietf + "1.1.0"], CancellationToken.None));
            Assert.Equal(SnmpAuthFailure.UnsupportedSecurityLevel, ex.Failure);
        }
    }

    [Fact]
    public async Task V3_follows_an_agent_reboot_and_a_replaced_card()
    {
        await using var agent = new FakeSnmpAgent(IetfUps());
        SnmpSettings settings = V3(agent, SnmpSecurityLevel.AuthPriv);
        agent.AddUser("nut", SnmpSecurity.CreatePrivacyProvider(settings));
        var (transport, client) = Connect(settings);
        await using (transport)
        {
            Assert.Single(await client.GetAsync([Ietf + "1.1.0"], CancellationToken.None));
            string? firstEngine = transport.EngineId;

            agent.Reboot(); // boots + 1: our clock is outside the time window, the report resynchronises it
            int reports = agent.Reports;
            Assert.Single(await client.GetAsync([Ietf + "1.1.0"], CancellationToken.None));
            Assert.Equal(reports + 1, agent.Reports);
            Assert.Equal(firstEngine, transport.EngineId);

            agent.ReplaceEngine(); // new engine ID: discovered again
            Assert.Single(await client.GetAsync([Ietf + "1.1.0"], CancellationToken.None));
            Assert.NotEqual(firstEngine, transport.EngineId);
        }
    }
}
