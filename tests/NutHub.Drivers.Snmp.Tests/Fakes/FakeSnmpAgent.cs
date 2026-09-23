using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using NutHub.Drivers.Snmp.Transport;

namespace NutHub.Drivers.Snmp.Tests.Fakes;

/// <summary>
/// An SNMP agent on 127.0.0.1 (random UDP port) serving an <see cref="AgentStore"/>: v1 and v2c with communities,
/// v3 with USM users, engine discovery, time windows and the usmStats reports real agents send.
/// </summary>
internal sealed class FakeSnmpAgent : IAsyncDisposable
{
    private const int MaxSize = 65507;
    private static readonly ObjectIdentifier UnsupportedSecLevels = new("1.3.6.1.6.3.15.1.1.1.0");
    private static readonly ObjectIdentifier NotInTimeWindows = new("1.3.6.1.6.3.15.1.1.2.0");
    private static readonly ObjectIdentifier UnknownUserNames = new("1.3.6.1.6.3.15.1.1.3.0");
    private static readonly ObjectIdentifier UnknownEngineIds = new("1.3.6.1.6.3.15.1.1.4.0");
    private static readonly ObjectIdentifier WrongDigests = new("1.3.6.1.6.3.15.1.1.5.0");
    private static readonly ObjectIdentifier DecryptionErrors = new("1.3.6.1.6.3.15.1.1.6.0");

    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private readonly UserRegistry _users = new();
    private readonly object _engineLock = new();
    private OctetString _engineId = new([0x80, 0x00, 0x1f, 0x88, 0x80, 0x4e, 0x55, 0x54, 0x48, 0x55, 0x42, 0x00, 0x01]);
    private int _engineBoots = 1;
    private long _engineStart = Stopwatch.GetTimestamp();
    private int _received;
    private int _reports;

    public FakeSnmpAgent(AgentStore? store = null)
    {
        Store = store ?? new AgentStore();
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        if (OperatingSystem.IsWindows())
        {
            // Without this, an ICMP "port unreachable" from a client that went away breaks the next receive.
            const int SioUdpConnReset = -1744830452;
            _udp.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        }

        Port = ((IPEndPoint)_udp.Client.LocalEndPoint!).Port;
        _loop = Task.Run(() => LoopAsync(_stop.Token));
    }

    public AgentStore Store { get; }

    public int Port { get; }

    public string Community { get; set; } = "public";

    public string WriteCommunity { get; set; } = "public";

    /// <summary>Drops every datagram, like an unplugged card.</summary>
    public bool Silent { get; set; }

    /// <summary>Drops requests for more than one object, like some broken agents.</summary>
    public bool DropMultiObjectRequests { get; set; }

    /// <summary>Datagrams received.</summary>
    public int Received => Volatile.Read(ref _received);

    /// <summary>usmStats reports sent (discoveries included).</summary>
    public int Reports => Volatile.Read(ref _reports);

    public void AddUser(string name, IPrivacyProvider privacy) => _users.Add(new OctetString(name), privacy);

    /// <summary>A restart of the agent: SNMPv3 boots incremented, engine time back to zero.</summary>
    public void Reboot()
    {
        lock (_engineLock)
        {
            _engineBoots++;
            _engineStart = Stopwatch.GetTimestamp();
        }
    }

    /// <summary>A replaced card: new SNMPv3 engine ID.</summary>
    public void ReplaceEngine()
    {
        lock (_engineLock)
        {
            byte[] id = _engineId.GetRaw().ToArray();
            id[^1]++;
            _engineId = new OctetString(id);
            _engineBoots = 1;
            _engineStart = Stopwatch.GetTimestamp();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _udp.Dispose();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private async Task LoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult datagram;
            try
            {
                datagram = await _udp.ReceiveAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                continue;
            }

            Interlocked.Increment(ref _received);
            if (Silent)
            {
                continue;
            }

            byte[]? reply;
            try
            {
                reply = Handle(datagram.Buffer);
            }
            catch (Exception ex) when (ex is SnmpException or ArgumentException or InvalidCastException or IndexOutOfRangeException)
            {
                reply = null; // undecodable: dropped, as real agents do
            }

            if (reply is not null)
            {
                try
                {
                    await _udp.SendAsync(reply, datagram.RemoteEndPoint, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private byte[]? Handle(byte[] bytes)
    {
        ISnmpMessage message = MessageFactory.ParseMessages(bytes, _users)[0];
        return message.Version == VersionCode.V3 ? HandleV3(message) : HandleCommunity(message);
    }

    private byte[]? HandleCommunity(ISnmpMessage message)
    {
        SnmpRequestKind? kind = KindOf(message.Pdu().TypeCode);
        if (kind is null)
        {
            return null;
        }

        string community = message.Community().ToString();
        bool allowed = kind == SnmpRequestKind.Set
            ? community == WriteCommunity
            : community == Community || community == WriteCommunity;
        IList<Variable> variables = message.Pdu().Variables;
        if (!allowed || (DropMultiObjectRequests && variables.Count > 1))
        {
            return null;
        }

        SnmpResponse r = Store.Handle(kind.Value, variables, message.Version == VersionCode.V1);
        return new ResponseMessage(message.RequestId(), message.Version, message.Community(), r.Status, r.ErrorIndex,
                                   r.Variables.ToList()).ToBytes();
    }

    private byte[]? HandleV3(ISnmpMessage message)
    {
        OctetString engineId;
        int boots;
        int time;
        lock (_engineLock)
        {
            engineId = _engineId;
            boots = _engineBoots;
            time = (int)Stopwatch.GetElapsedTime(_engineStart).TotalSeconds;
        }

        int messageId = message.MessageId();
        OctetString user = message.Parameters.UserName ?? OctetString.Empty;
        if (message is MalformedMessage)
        {
            // The library could not decode the scoped PDU: unknown user, or a known one whose privacy key is wrong.
            ObjectIdentifier reason = user.GetRaw().Length > 0 && _users.Find(user) is not null ? DecryptionErrors : UnknownUserNames;
            return Report(messageId, 0, reason, engineId, boots, time);
        }

        int requestId = message.RequestId();
        OctetString? requestEngine = message.Parameters.EngineId;
        if (requestEngine is null || requestEngine.GetRaw().Length == 0 || !requestEngine.Equals(engineId))
        {
            return Report(messageId, requestId, UnknownEngineIds, engineId, boots, time);
        }

        IPrivacyProvider? privacy = _users.Find(user);
        if (user.GetRaw().Length == 0 || privacy is null)
        {
            return Report(messageId, requestId, UnknownUserNames, engineId, boots, time);
        }

        Levels wanted = privacy.ToSecurityLevel();
        Levels asked = message.Header.SecurityLevel;
        if ((wanted & Levels.Authentication) != (asked & Levels.Authentication) ||
            (wanted & Levels.Privacy) != (asked & Levels.Privacy))
        {
            return Report(messageId, requestId, UnsupportedSecLevels, engineId, boots, time);
        }

        if (message.Parameters.IsInvalid)
        {
            return Report(messageId, requestId, WrongDigests, engineId, boots, time);
        }

        ISnmpPdu pdu = message.Pdu();
        SnmpRequestKind? kind = KindOf(pdu.TypeCode);
        if (kind is null)
        {
            return Report(messageId, requestId, DecryptionErrors, engineId, boots, time);
        }

        if ((wanted & Levels.Authentication) != 0 &&
            (message.Parameters.EngineBoots?.ToInt32() != boots ||
             Math.Abs((message.Parameters.EngineTime?.ToInt32() ?? 0) - time) > 150))
        {
            return Report(messageId, requestId, NotInTimeWindows, engineId, boots, time);
        }

        SnmpResponse r = Store.Handle(kind.Value, pdu.Variables, v1: false);
        var response = new ResponseMessage(
            VersionCode.V3,
            new Header(new Integer32(messageId), new Integer32(MaxSize), privacy.ToSecurityLevel()),
            new SecurityParameters(engineId, new Integer32(boots), new Integer32(time), user,
                                   privacy.AuthenticationProvider.CleanDigest, privacy.Salt),
            new Scope(engineId, OctetString.Empty,
                      new ResponsePdu(requestId, r.Status, r.ErrorIndex, r.Variables.ToList())),
            privacy, true, null);
        return response.ToBytes();
    }

    private byte[] Report(int messageId, int requestId, ObjectIdentifier reason, OctetString engineId, int boots, int time)
    {
        Interlocked.Increment(ref _reports);
        var report = new ReportMessage(
            VersionCode.V3,
            new Header(new Integer32(messageId), new Integer32(MaxSize), 0),
            new SecurityParameters(engineId, new Integer32(boots), new Integer32(time), OctetString.Empty,
                                   OctetString.Empty, OctetString.Empty),
            new Scope(engineId, OctetString.Empty,
                      new ReportPdu(requestId, ErrorCode.NoError, 0, [new Variable(reason, new Counter32(1))])),
            DefaultPrivacyProvider.DefaultPair, null);
        return report.ToBytes();
    }

    private static SnmpRequestKind? KindOf(SnmpType type) => type switch
    {
        SnmpType.GetRequestPdu => SnmpRequestKind.Get,
        SnmpType.GetNextRequestPdu => SnmpRequestKind.GetNext,
        SnmpType.SetRequestPdu => SnmpRequestKind.Set,
        _ => null,
    };
}
