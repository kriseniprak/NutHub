using System.Net;
using System.Net.Sockets;
using Lextm.SharpSnmpLib;
using Lextm.SharpSnmpLib.Messaging;
using Lextm.SharpSnmpLib.Security;
using Microsoft.Extensions.Logging;

namespace NutHub.Drivers.Snmp.Transport;

/// <summary>The kinds of request the driver sends.</summary>
internal enum SnmpRequestKind
{
    Get,
    GetNext,
    Set,
}

/// <summary>The PDU of an agent's answer.</summary>
internal sealed record SnmpResponse(ErrorCode Status, int ErrorIndex, IReadOnlyList<Variable> Variables);

/// <summary>Sends one request and returns the agent's answer; the seam between the protocol logic and UDP.</summary>
internal interface ISnmpTransport : IAsyncDisposable
{
    /// <summary>"host:port", for messages.</summary>
    string Target { get; }

    /// <summary>
    /// Sends a request (retrying after each timeout) and returns the matching answer. Throws
    /// <see cref="SnmpNoResponseException"/> when every attempt timed out, <see cref="SnmpAuthenticationException"/>
    /// for SNMPv3 security failures.
    /// </summary>
    Task<SnmpResponse> SendAsync(SnmpRequestKind kind, IList<Variable> variables, CancellationToken cancellationToken);
}

/// <summary>
/// SNMP over UDP for one agent: v1, v2c and v3 (USM). It owns a connected UDP socket, matches answers by request
/// (and SNMPv3 message) id so late answers to earlier attempts are recognised and stray datagrams ignored, and keeps
/// the SNMPv3 engine state: discovery, clock synchronisation from the agent's reports and answers, and a new
/// discovery when the agent stops recognising its engine ID (card replaced or reset).
/// </summary>
/// <remarks>
/// SharpSnmpLib is used for encoding, decoding and USM cryptography only; the exchange is done here so timeouts,
/// retries and cancellation follow the driver's rules instead of the library's blocking helpers.
/// </remarks>
internal sealed class SnmpTransport : ISnmpTransport
{
    /// <summary>Largest UDP payload; the agent may answer anything up to this size.</summary>
    internal const int MaxMessageSize = 65507;

    private static readonly ObjectIdentifier UnsupportedSecLevels = new("1.3.6.1.6.3.15.1.1.1.0");
    private static readonly ObjectIdentifier NotInTimeWindows = new("1.3.6.1.6.3.15.1.1.2.0");
    private static readonly ObjectIdentifier UnknownUserNames = new("1.3.6.1.6.3.15.1.1.3.0");
    private static readonly ObjectIdentifier UnknownEngineIds = new("1.3.6.1.6.3.15.1.1.4.0");
    private static readonly ObjectIdentifier WrongDigests = new("1.3.6.1.6.3.15.1.1.5.0");
    private static readonly ObjectIdentifier DecryptionErrors = new("1.3.6.1.6.3.15.1.1.6.0");

    private readonly SnmpSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly byte[] _buffer = new byte[MaxMessageSize + 1];
    private readonly IPrivacyProvider _privacy;
    private readonly UserRegistry _users;
    private readonly UserRegistry _noUsers = new();
    private readonly OctetString _community;
    private readonly OctetString _writeCommunity;
    private readonly OctetString _userName;
    private readonly VersionCode _version;

    private Socket? _socket;
    private int _nextRequestId;
    private int _nextMessageId;
    private EngineState? _engine;
    private bool _disposed;

    public SnmpTransport(SnmpSettings settings, TimeProvider time, ILogger logger)
    {
        _settings = settings;
        _time = time;
        _logger = logger;
        _version = settings.Version switch
        {
            SnmpProtocolVersion.V1 => VersionCode.V1,
            SnmpProtocolVersion.V2c => VersionCode.V2,
            _ => VersionCode.V3,
        };
        _community = new OctetString(settings.Community);
        _writeCommunity = new OctetString(settings.WriteCommunity);
        _userName = new OctetString(settings.SecurityName);
        _privacy = SnmpSecurity.CreatePrivacyProvider(settings);
        _users = SnmpSecurity.CreateUserRegistry(settings, _privacy);

        // Random starting ids: answers meant for a previous run of the driver cannot be mistaken for ours.
        _nextRequestId = Random.Shared.Next(1, int.MaxValue / 2);
        _nextMessageId = Random.Shared.Next(1, int.MaxValue / 2);
    }

    public string Target => _settings.Target;

    /// <summary>The SNMPv3 engine ID of the agent once discovered (hex), for diagnostics.</summary>
    public string? EngineId => _engine?.EngineId.ToHexString();

    public async Task<SnmpResponse> SendAsync(SnmpRequestKind kind, IList<Variable> variables,
                                              CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(variables);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Socket socket = await EnsureSocketAsync(cancellationToken).ConfigureAwait(false);
            return _version == VersionCode.V3
                ? await SendV3Async(socket, kind, variables, cancellationToken).ConfigureAwait(false)
                : await SendCommunityAsync(socket, kind, variables, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            _socket?.Dispose();
            _socket = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ----- SNMP v1 / v2c -----

    private async Task<SnmpResponse> SendCommunityAsync(Socket socket, SnmpRequestKind kind, IList<Variable> variables,
                                                        CancellationToken cancellationToken)
    {
        int requestId = NextRequestId();
        OctetString community = kind == SnmpRequestKind.Set ? _writeCommunity : _community;
        ISnmpMessage request = kind switch
        {
            SnmpRequestKind.Get => new GetRequestMessage(requestId, _version, community, variables),
            SnmpRequestKind.GetNext => new GetNextRequestMessage(requestId, _version, community, variables),
            _ => new SetRequestMessage(requestId, _version, community, variables),
        };
        byte[] bytes = request.ToBytes();

        string? refused = null;
        for (int attempt = 0; attempt <= _settings.Retries; attempt++)
        {
            Exchange result = await ExchangeAsync(
                socket, bytes,
                m => m is ResponseMessage && m.Version == _version && m.RequestId() == requestId,
                cancellationToken).ConfigureAwait(false);
            if (result.Message is ResponseMessage response)
            {
                return ToResponse(response);
            }

            refused ??= result.Refused;
            _logger.LogDebug("No answer from {Target} to request {RequestId} (attempt {Attempt}).",
                             Target, requestId, attempt + 1);
        }

        throw new SnmpNoResponseException(Target, refused);
    }

    // ----- SNMP v3 -----

    private async Task<SnmpResponse> SendV3Async(Socket socket, SnmpRequestKind kind, IList<Variable> variables,
                                                 CancellationToken cancellationToken)
    {
        int requestId = NextRequestId();
        var messageIds = new HashSet<int>();
        bool resynchronised = false;
        bool rediscovered = false;
        string? refused = null;
        int attempt = 0;

        while (true)
        {
            _engine ??= await DiscoverAsync(socket, cancellationToken).ConfigureAwait(false);

            int messageId = NextMessageId();
            messageIds.Add(messageId);
            byte[] bytes = BuildV3Request(kind, messageId, requestId, variables, _engine).ToBytes();

            Exchange result = await ExchangeAsync(
                socket, bytes,
                m => m.Version == VersionCode.V3 && m is ReportMessage or ResponseMessage && messageIds.Contains(m.MessageId()),
                cancellationToken).ConfigureAwait(false);

            switch (result.Message)
            {
                case null:
                    refused ??= result.Refused;
                    _logger.LogDebug("No answer from {Target} to request {RequestId} (attempt {Attempt}).",
                                     Target, requestId, attempt + 1);
                    if (++attempt > _settings.Retries)
                    {
                        throw new SnmpNoResponseException(Target, refused);
                    }

                    continue;

                case ReportMessage report:
                    ObjectIdentifier? reason = report.Pdu().Variables.Count > 0 ? report.Pdu().Variables[0].Id : null;
                    if (reason == NotInTimeWindows && !resynchronised)
                    {
                        // The report carries the agent's clock: adopt it and send again (RFC 3414, 4).
                        resynchronised = true;
                        _engine = EngineState.From(report.Parameters, _time);
                        _logger.LogDebug("SNMPv3 clock of {Target} resynchronised (boots {Boots}).",
                                         Target, _engine.Boots);
                        continue;
                    }

                    if (reason == UnknownEngineIds && !rediscovered)
                    {
                        rediscovered = true;
                        _engine = null;
                        _logger.LogInformation("{Target} no longer recognises its SNMPv3 engine ID; discovering it again.",
                                               Target);
                        continue;
                    }

                    throw ReportToException(reason);

                case ResponseMessage response:
                    ISnmpPdu pdu = response.Pdu();
                    if (pdu.TypeCode != SnmpType.ResponsePdu)
                    {
                        throw new SnmpAuthenticationException(
                            SnmpAuthFailure.DecryptionError,
                            $"The answer of {Target} cannot be decrypted: check the privacy password and protocol " +
                            $"({_settings.PrivProtocol}) of user '{_settings.SecurityName}'.");
                    }

                    if (_settings.SecurityLevel != SnmpSecurityLevel.NoAuthNoPriv && response.Parameters.IsInvalid)
                    {
                        throw new SnmpAuthenticationException(
                            SnmpAuthFailure.WrongDigest,
                            $"The answer of {Target} does not match the authentication password: check the password " +
                            $"and protocol ({_settings.AuthProtocol}) of user '{_settings.SecurityName}'.");
                    }

                    if (pdu.RequestId.ToInt32() != requestId)
                    {
                        // Our message id but not our request: a confused agent. Count it as a failed attempt.
                        if (++attempt > _settings.Retries)
                        {
                            throw new SnmpProtocolException($"{Target} answers with mismatched request ids.");
                        }

                        continue;
                    }

                    _engine = EngineState.From(response.Parameters, _time);
                    return ToResponse(response);
            }
        }
    }

    private ISnmpMessage BuildV3Request(SnmpRequestKind kind, int messageId, int requestId, IList<Variable> variables,
                                        EngineState engine)
    {
        // The request constructors take the engine ID, boots and time from a "report" message: give them one
        // carrying the agent's current clock as estimated from the last synchronisation.
        ISnmpMessage clock = engine.ToReport(_time);
        OctetString context = OctetString.Empty;
        return kind switch
        {
            SnmpRequestKind.Get => new GetRequestMessage(VersionCode.V3, messageId, requestId, _userName, context,
                                                         variables, _privacy, MaxMessageSize, clock),
            SnmpRequestKind.GetNext => new GetNextRequestMessage(VersionCode.V3, messageId, requestId, _userName, context,
                                                                 variables, _privacy, MaxMessageSize, clock),
            _ => new SetRequestMessage(VersionCode.V3, messageId, requestId, _userName, context, variables, _privacy,
                                       MaxMessageSize, clock),
        };
    }

    /// <summary>
    /// SNMPv3 discovery (RFC 3414, 4): an unauthenticated request with an empty engine ID, which the agent answers
    /// with a report carrying its engine ID, boots and time.
    /// </summary>
    private async Task<EngineState> DiscoverAsync(Socket socket, CancellationToken cancellationToken)
    {
        var discovery = new Discovery(NextMessageId(), NextRequestId(), MaxMessageSize);
        byte[] bytes = discovery.ToBytes();

        // Read the ids back from the encoded message rather than relying on the constructor's parameter order.
        ISnmpMessage sent = MessageFactory.ParseMessages(bytes, _noUsers)[0];
        int messageId = sent.MessageId();

        string? refused = null;
        for (int attempt = 0; attempt <= _settings.Retries; attempt++)
        {
            Exchange result = await ExchangeAsync(
                socket, bytes,
                m => m.Version == VersionCode.V3 && m is ReportMessage or ResponseMessage && m.MessageId() == messageId,
                cancellationToken).ConfigureAwait(false);
            if (result.Message is { } answer)
            {
                if (answer.Parameters.EngineId is null || answer.Parameters.EngineId.GetRaw().Length == 0)
                {
                    throw new SnmpProtocolException($"{Target} did not reveal its SNMPv3 engine ID.");
                }

                EngineState engine = EngineState.From(answer.Parameters, _time);
                _logger.LogDebug("SNMPv3 engine of {Target}: {EngineId}, boots {Boots}, time {Time}.",
                                 Target, engine.EngineId.ToHexString(), engine.Boots, engine.Time);
                return engine;
            }

            refused ??= result.Refused;
        }

        throw new SnmpNoResponseException(Target, refused);
    }

    private SnmpAuthenticationException ReportToException(ObjectIdentifier? reason)
    {
        string user = _settings.SecurityName;
        if (reason == UnknownUserNames)
        {
            return new(SnmpAuthFailure.UnknownUser,
                       $"SNMPv3 authentication failed: {Target} does not know the user '{user}'.");
        }

        if (reason == WrongDigests)
        {
            return new(SnmpAuthFailure.WrongDigest,
                       $"SNMPv3 authentication failed for user '{user}': wrong authentication password, or the card " +
                       $"does not use {_settings.AuthProtocol} for this user.");
        }

        if (reason == DecryptionErrors)
        {
            return new(SnmpAuthFailure.DecryptionError,
                       $"SNMPv3 privacy failed for user '{user}': wrong privacy password, or the card does not use " +
                       $"{_settings.PrivProtocol} for this user.");
        }

        if (reason == UnsupportedSecLevels)
        {
            return new(SnmpAuthFailure.UnsupportedSecurityLevel,
                       $"SNMPv3: {Target} does not accept the security level {_settings.SecurityLevel} for user '{user}'.");
        }

        if (reason == NotInTimeWindows)
        {
            return new(SnmpAuthFailure.NotInTimeWindow,
                       $"SNMPv3: {Target} keeps rejecting the request time; check the authentication password of user '{user}'.");
        }

        if (reason == UnknownEngineIds)
        {
            return new(SnmpAuthFailure.UnknownEngineId, $"SNMPv3: {Target} does not accept its own engine ID.");
        }

        return new(SnmpAuthFailure.WrongDigest,
                   $"SNMPv3: {Target} refused the request (report {reason?.ToString() ?? "without variables"}).");
    }

    // ----- UDP -----

    /// <summary>
    /// Sends a datagram and waits up to the timeout for a message accepted by <paramref name="matches"/>; other
    /// datagrams (late answers, garbage) are skipped. A null message means the attempt timed out.
    /// </summary>
    private async Task<Exchange> ExchangeAsync(Socket socket, byte[] bytes, Func<ISnmpMessage, bool> matches,
                                               CancellationToken cancellationToken)
    {
        await socket.SendAsync(bytes, SocketFlags.None, cancellationToken).ConfigureAwait(false);

        using var timeout = new CancellationTokenSource(_settings.Timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        while (true)
        {
            int received;
            try
            {
                received = await socket.ReceiveAsync(_buffer, SocketFlags.None, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return default;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.ConnectionRefused)
            {
                // ICMP "port unreachable": nothing listens on the agent's port.
                return new Exchange(null, "the host reports that nothing listens on this UDP port");
            }

            IList<ISnmpMessage> messages;
            try
            {
                messages = MessageFactory.ParseMessages(_buffer, 0, received, _version == VersionCode.V3 ? _users : _noUsers);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug("Ignoring an undecodable datagram of {Length} bytes from {Target}: {Message}",
                                 received, Target, ex.Message);
                continue;
            }

            foreach (ISnmpMessage message in messages)
            {
                bool match;
                try
                {
                    match = matches(message);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A message whose PDU cannot be read (for instance a malformed one) is not ours.
                    _logger.LogDebug("Ignoring an unusable message from {Target}: {Message}", Target, ex.Message);
                    match = false;
                }

                if (match)
                {
                    return new Exchange(message, null);
                }
            }
        }
    }

    private async Task<Socket> EnsureSocketAsync(CancellationToken cancellationToken)
    {
        if (_socket is not null)
        {
            return _socket;
        }

        IPAddress address = await ResolveAsync(_settings.Host, cancellationToken).ConfigureAwait(false);
        var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            // Connecting a UDP socket filters out datagrams from other hosts and surfaces ICMP errors.
            await socket.ConnectAsync(new IPEndPoint(address, _settings.Port), cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        _socket = socket;
        return socket;
    }

    private static async Task<IPAddress> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host.Trim('[', ']'), out IPAddress? literal))
        {
            return literal;
        }

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        // Network cards are mostly IPv4-only; prefer IPv4 when the name has both.
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
               ?? addresses.FirstOrDefault()
               ?? throw new SocketException((int)SocketError.HostNotFound);
    }

    private static SnmpResponse ToResponse(ISnmpMessage message)
    {
        ISnmpPdu pdu = message.Pdu();
        return new SnmpResponse(pdu.ErrorStatus.ToErrorCode(), pdu.ErrorIndex.ToInt32(), pdu.Variables.ToArray());
    }

    private int NextRequestId()
    {
        int id = Interlocked.Increment(ref _nextRequestId);
        if (id <= 0 || id == int.MaxValue)
        {
            Interlocked.Exchange(ref _nextRequestId, 1);
            id = 1;
        }

        return id;
    }

    private int NextMessageId()
    {
        int id = Interlocked.Increment(ref _nextMessageId);
        if (id <= 0 || id == int.MaxValue)
        {
            Interlocked.Exchange(ref _nextMessageId, 1);
            id = 1;
        }

        return id;
    }

    private readonly record struct Exchange(ISnmpMessage? Message, string? Refused);

    /// <summary>
    /// What the agent told us about its SNMPv3 engine, and when: the agent's clock is estimated by adding the time
    /// elapsed here, which keeps authenticated requests inside its 150 s window between synchronisations.
    /// </summary>
    private sealed record EngineState(OctetString EngineId, int Boots, int Time, long Timestamp)
    {
        public static EngineState From(SecurityParameters parameters, TimeProvider time) =>
            new(parameters.EngineId ?? OctetString.Empty,
                parameters.EngineBoots?.ToInt32() ?? 0,
                parameters.EngineTime?.ToInt32() ?? 0,
                time.GetTimestamp());

        public ISnmpMessage ToReport(TimeProvider time)
        {
            long elapsed = (long)time.GetElapsedTime(Timestamp).TotalSeconds;
            int now = (int)Math.Min(int.MaxValue, Time + Math.Max(0, elapsed));
            var parameters = new SecurityParameters(EngineId, new Integer32(Boots), new Integer32(now),
                                                    OctetString.Empty, OctetString.Empty, OctetString.Empty);
            var scope = new Scope(EngineId, OctetString.Empty, new ReportPdu(0, ErrorCode.NoError, 0, []));
            return new ReportMessage(VersionCode.V3, new Header(new Integer32(0), new Integer32(MaxMessageSize), 0),
                                     parameters, scope, DefaultPrivacyProvider.DefaultPair, null);
        }
    }
}
