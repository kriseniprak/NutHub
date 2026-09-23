using Lextm.SharpSnmpLib;
using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Drivers;
using NutHub.Drivers.Snmp.Engine;
using NutHub.Drivers.Snmp.Transport;

namespace NutHub.Drivers.Snmp.Tests.Fakes;

/// <summary>Typical agents: an RFC 1628 UPS of an unknown vendor and an APC Smart-UPS, both on line power.</summary>
internal static class Profiles
{
    public const string Ietf = "1.3.6.1.2.1.33.1.";
    public const string Apc = "1.3.6.1.4.1.318.1.1.1.";
    public const string SysObjectId = "1.3.6.1.2.1.1.2.0";

    public static AgentStore SystemGroup(this AgentStore s, string sysObjectId)
    {
        s.Set("1.3.6.1.2.1.1.1.0", "Test network card");
        s.SetOid(SysObjectId, sysObjectId);
        s.Set("1.3.6.1.2.1.1.4.0", "admin@example.com");
        s.Set("1.3.6.1.2.1.1.6.0", "Server room");
        return s;
    }

    /// <summary>A single-phase RFC 1628 UPS on line power, from a vendor without its own MIB.</summary>
    public static AgentStore IetfUps(AgentStore? store = null)
    {
        AgentStore s = (store ?? new AgentStore()).SystemGroup("1.3.6.1.4.1.99999.1");
        s.Set(Ietf + "1.1.0", "ACME");
        s.Set(Ietf + "1.2.0", "ACME 3000");
        s.Set(Ietf + "1.3.0", "FW 1.2");
        s.Set(Ietf + "1.4.0", "Agent 4.5");
        s.Set(Ietf + "2.1.0", 2); // batteryNormal
        s.Set(Ietf + "2.2.0", 0);
        s.Set(Ietf + "2.3.0", 25); // minutes
        s.Set(Ietf + "2.4.0", 100);
        s.Set(Ietf + "2.5.0", 136); // 0.1 V
        s.Set(Ietf + "2.6.0", -1); // not available
        s.Set(Ietf + "2.7.0", 28);
        s.Set(Ietf + "3.2.0", 1);
        s.Set(Ietf + "3.3.1.2.1", 499); // 0.1 Hz
        s.Set(Ietf + "3.3.1.3.1", 231);
        s.Set(Ietf + "4.1.0", 3); // normal
        s.Set(Ietf + "4.2.0", 500);
        s.Set(Ietf + "4.3.0", 1);
        s.Set(Ietf + "4.4.1.2.1", 230);
        s.Set(Ietf + "4.4.1.3.1", 12);
        s.Set(Ietf + "4.4.1.4.1", 250);
        s.Set(Ietf + "4.4.1.5.1", 35);
        s.Set(Ietf + "6.1.0", 0);
        s.SetOid(Ietf + "7.1.0", Ietf + "7.7.1"); // upsTestNoTestsInitiated
        s.Set(Ietf + "7.3.0", 6);
        s.Set(Ietf + "8.2.0", -1);
        s.Set(Ietf + "8.3.0", -1);
        s.Set(Ietf + "8.4.0", -1);
        s.Set(Ietf + "8.5.0", 1);
        s.Set(Ietf + "9.1.0", 230);
        s.Set(Ietf + "9.2.0", 500);
        s.Set(Ietf + "9.3.0", 230);
        s.Set(Ietf + "9.4.0", 500);
        s.Set(Ietf + "9.5.0", 1500);
        s.Set(Ietf + "9.6.0", 1350);
        s.Set(Ietf + "9.7.0", 2);
        s.Set(Ietf + "9.8.0", 2);
        s.Set(Ietf + "9.9.0", 180);
        s.Set(Ietf + "9.10.0", 264);
        return s;
    }

    /// <summary>An APC Smart-UPS with an AP9631 card, on line power.</summary>
    public static AgentStore ApcUps(AgentStore? store = null)
    {
        AgentStore s = (store ?? new AgentStore()).SystemGroup("1.3.6.1.4.1.318.1.3.27");
        s.Set(Apc + "1.1.1.0", "Smart-UPS 1500");
        s.Set(Apc + "1.1.2.0", "UPS_IDEN");
        s.Set(Apc + "1.2.1.0", "UPS 09.3");
        s.Set(Apc + "1.2.2.0", "01/02/2019");
        s.Set(Apc + "1.2.3.0", "AS1234567890");
        s.Set(Apc + "2.1.1.0", 2); // batteryNormal
        s.Set(Apc + "2.1.3.0", "01/02/2023");
        s.Set(Apc + "2.2.1.0", 99);
        s.Set(Apc + "2.3.1.0", 1000); // high precision: 100.0 %
        s.Set(Apc + "2.2.2.0", 30);
        s.Set(Apc + "2.3.2.0", 301);
        s.SetTicks(Apc + "2.2.3.0", 180000); // 30 minutes
        s.Set(Apc + "2.2.4.0", 1); // noBatteryNeedsReplacing
        s.Set(Apc + "2.2.8.0", 27);
        s.Set(Apc + "2.3.4.0", 272);
        s.Set(Apc + "3.2.1.0", 229);
        s.Set(Apc + "3.3.1.0", 2295);
        s.Set(Apc + "3.2.4.0", 50);
        s.Set(Apc + "3.3.4.0", 500);
        s.Set(Apc + "3.2.5.0", 1);
        s.Set(Apc + "4.1.1.0", 2); // onLine
        s.Set(Apc + "4.2.1.0", 230);
        s.Set(Apc + "4.3.1.0", 2300);
        s.Set(Apc + "4.2.3.0", 20);
        s.Set(Apc + "4.3.3.0", 205);
        s.Set(Apc + "5.2.2.0", 253);
        s.Set(Apc + "5.2.3.0", 196);
        s.Set(Apc + "5.2.7.0", 4); // high
        s.SetTicks(Apc + "5.2.8.0", 12000);
        s.SetTicks(Apc + "5.2.9.0", 0);
        s.SetTicks(Apc + "5.2.10.0", 9000);
        s.Set(Apc + "7.2.3.0", 1);
        s.Set(Apc + "7.2.4.0", "03/04/2024");
        s.Set(Apc + "7.2.6.0", 1);

        // Control objects read back 1 ("no action"); upsAdvControlBypassSwitch is missing on this model.
        foreach (string control in new[] { "6.1.1.0", "6.2.1.0", "6.2.2.0", "6.2.4.0", "6.2.5.0", "6.2.6.0", "7.2.2.0", "7.2.5.0" })
        {
            s.Set(Apc + control, 1);
        }

        return s;
    }

    public static SnmpSettings Settings(int port = 161, string mib = "auto") =>
        new() { Host = "127.0.0.1", Port = port, Mib = mib };

    /// <summary>Detects the MIB and initialises a session over an in-memory transport.</summary>
    public static async Task<MibSession> OpenAsync(AgentStore store, string mib = "auto", bool v1 = false,
                                                   SnmpSettings? settings = null)
    {
        var client = new SnmpClient(new InMemoryTransport(store, v1), 16, NullLogger.Instance);
        MibDetector.Result detected = await MibDetector.DetectAsync(client, mib, NullLogger.Instance, CancellationToken.None);
        var session = new MibSession(client, detected.Mib, settings ?? Settings(mib: mib), NullLogger.Instance);
        await session.InitializeAsync(CancellationToken.None);
        return session;
    }

    public static async Task<DriverUpdate> PollOnceAsync(AgentStore store, string mib = "auto")
    {
        MibSession session = await OpenAsync(store, mib);
        return await session.PollAsync(CancellationToken.None);
    }

    public static string? Var(this DriverUpdate update, string name) => update.Variables.GetValueOrDefault(name);

    public static string[] Status(this DriverUpdate update) =>
        update.Var("ups.status")?.Split(' ', StringSplitOptions.RemoveEmptyEntries) ?? [];

    public static ISnmpData? SetValue(this AgentStore store, string oid) =>
        store.Sets.LastOrDefault(s => s.Oid == oid.TrimStart('.')).Value;
}
