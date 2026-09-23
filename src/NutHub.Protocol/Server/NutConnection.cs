using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Protocol.Commands;
using NutHub.Protocol.Parsing;

namespace NutHub.Protocol.Server;

/// <summary>What every connection shares.</summary>
internal sealed record NutConnectionServices(
    NutCommandProcessor Processor,
    NutSessionRegistry Sessions,
    EventHub Hub,
    TimeProvider Time,
    NutProtocolOptions Options,
    ILogger Logger,
    Func<IPAddress, bool> IsAddressAllowed);

/// <summary>
/// One client connection: reads requests, runs them one after the other, writes the answers. Every failure stays
/// inside the connection; nothing a client sends or fails to read can affect the server or the other clients.
/// </summary>
internal sealed class NutConnection
{
    private static readonly byte[] TooLongAnswer = Encoding.ASCII.GetBytes("ERR " + NutErrors.InvalidArgument + "\n");

    private readonly Socket _socket;
    private readonly NutConnectionServices _services;
    private readonly NutProtocolOptions _options;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _abort = new();
    private Stream _stream;
    private string? _abortReason;
    private long _lastCommandTicks;

    public NutConnection(Socket socket, IPEndPoint remote, NutConnectionServices services)
    {
        _socket = socket;
        _services = services;
        _options = services.Options;
        _logger = services.Logger;
        Remote = remote;
        Address = remote.Address.IsIPv4MappedToIPv6 ? remote.Address.MapToIPv4() : remote.Address;
        _stream = new NetworkStream(socket, ownsSocket: true);
        _lastCommandTicks = services.Time.GetUtcNow().UtcTicks;
        Session = services.Sessions.Open(remote, () => Abort("disconnected by an administrator"));
    }

    public NutSession Session { get; }

    public IPEndPoint Remote { get; }

    /// <summary>The client address, IPv4-mapped addresses turned into plain IPv4.</summary>
    public IPAddress Address { get; }

    /// <summary>When the client last completed a request (or connected).</summary>
    public DateTimeOffset LastCommandAt => new(Interlocked.Read(ref _lastCommandTicks), TimeSpan.Zero);

    /// <summary>Closes the connection from outside (idle, administrator, access rules, shutdown). Idempotent.</summary>
    public void Abort(string reason)
    {
        if (Interlocked.CompareExchange(ref _abortReason, reason, null) is not null)
        {
            return;
        }

        try
        {
            _abort.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    /// <summary>Serves the connection until it ends; never throws.</summary>
    public async Task RunAsync(CancellationToken serverStopping)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverStopping, _abort.Token);
        CancellationToken token = linked.Token;
        var client = new NutClientState(Session, Address);
        string reason;
        bool linger = false;
        _logger.LogDebug("NUT connection {Id} from {Address} port {Port} accepted.", Session.Id, Session.Address,
                         Remote.Port);
        try
        {
            (reason, linger) = await ServeAsync(client, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            reason = Volatile.Read(ref _abortReason) ?? "the server is stopping";
        }
        catch (Exception ex) when (IsNetworkError(ex))
        {
            reason = ex.Message;
        }
        catch (Exception ex)
        {
            reason = "unexpected error";
            _logger.LogInformation(ex, "NUT connection {Id} from {Address} failed.", Session.Id, Session.Address);
        }

        // The protocol session is over: it leaves the registry (NUMLOGINS, LIST CLIENT, the web panel) now, not
        // after the socket has finished lingering.
        // The registry first, so that whoever reacts to the logout event already sees the new count.
        _logger.LogDebug("NUT connection {Id} from {Address} closed: {Reason}.", Session.Id, Session.Address, reason);
        _services.Sessions.Close(Session);
        if (client.LoginUps is { } ups)
        {
            ReportLogout(client, ups, reason);
        }

        try
        {
            if (linger)
            {
                await LingerAsync(serverStopping).ConfigureAwait(false);
            }
        }
        finally
        {
            await CloseStreamAsync().ConfigureAwait(false);
        }
    }

    private async Task<(string Reason, bool Linger)> ServeAsync(NutClientState client, CancellationToken token)
    {
        var parser = new NutLineParser(_options.MaxLineLength);
        byte[] buffer = new byte[4096];
        while (true)
        {
            int read = await _stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (read == 0)
            {
                return ("closed by the client", false);
            }

            for (int i = 0; i < read; i++)
            {
                ParseResult result = parser.Feed(buffer[i]);
                if (result == ParseResult.NeedMore)
                {
                    continue;
                }

                if (result == ParseResult.LineTooLong)
                {
                    await SendAsync(TooLongAnswer, token).ConfigureAwait(false);
                    return ($"request longer than {_options.MaxLineLength} bytes", true);
                }

                string[] words = parser.TakeWords();
                Interlocked.Exchange(ref _lastCommandTicks, _services.Time.GetUtcNow().UtcTicks);
                _services.Sessions.Touch(Session);

                // The access list may have changed since the connection was accepted.
                if (!_services.IsAddressAllowed(Address))
                {
                    return ("the client address is no longer allowed", false);
                }

                NutReply reply = await _services.Processor.ExecuteAsync(client, words, token).ConfigureAwait(false);
                if (reply.Text.Length > 0)
                {
                    await SendAsync(Encoding.UTF8.GetBytes(reply.Text), token).ConfigureAwait(false);
                }

                if (reply.Action == NutReplyAction.Close)
                {
                    return ("LOGOUT", true);
                }

                if (reply.Action == NutReplyAction.StartTls)
                {
                    // Whatever followed the STARTTLS line in this read is already the TLS handshake.
                    await UpgradeAsync(reply.Certificate!, buffer.AsMemory(i + 1, read - i - 1), token)
                        .ConfigureAwait(false);
                    client.Tls = true;
                    _services.Sessions.SetTls(Session);
                    break;
                }
            }
        }
    }

    private async Task SendAsync(byte[] bytes, CancellationToken token)
    {
        using var timeout = new CancellationTokenSource(_options.WriteTimeout, _services.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try
        {
            await _stream.WriteAsync(bytes, linked.Token).ConfigureAwait(false);
            await _stream.FlushAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new IOException(
                $"the client did not read its answers for {_options.WriteTimeout.TotalSeconds:0} s");
        }
    }

    private async Task UpgradeAsync(X509Certificate2 certificate, ReadOnlyMemory<byte> alreadyRead,
                                    CancellationToken token)
    {
        Stream inner = alreadyRead.IsEmpty ? _stream : new PrefixedStream(alreadyRead.ToArray(), _stream);
        var tls = new SslStream(inner, leaveInnerStreamOpen: false);
        _stream = tls;

        using var timeout = new CancellationTokenSource(_options.TlsHandshakeTimeout, _services.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = certificate,
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificateRequired = false,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
            }, linked.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!token.IsCancellationRequested &&
                                   ex is AuthenticationException or IOException or OperationCanceledException)
        {
            string why = timeout.IsCancellationRequested ? "timed out" : ex.Message;
            _logger.LogInformation("NUT connection {Id} from {Address}: TLS handshake failed: {Reason}", Session.Id,
                                   Session.Address, why);
            throw new IOException("TLS handshake failed: " + why, ex);
        }

        _logger.LogDebug("NUT connection {Id} from {Address} switched to {Protocol}.", Session.Id, Session.Address,
                         tls.SslProtocol);
    }

    /// <summary>
    /// After a final answer, closes our side first and drains what the client still sends for a moment: closing a
    /// socket with unread input makes the operating system reset the connection, and a reset can destroy the answer
    /// before the client reads it.
    /// </summary>
    private async Task LingerAsync(CancellationToken serverStopping)
    {
        using var timeout = new CancellationTokenSource(_options.CloseLinger, _services.Time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(serverStopping, timeout.Token);
        try
        {
            if (_stream is SslStream tls)
            {
                await tls.ShutdownAsync().WaitAsync(linked.Token).ConfigureAwait(false);
            }

            _socket.Shutdown(SocketShutdown.Send);
            byte[] sink = new byte[1024];
            int total = 0;
            while (total < 64 * 1024)
            {
                int n = await _socket.ReceiveAsync(sink, SocketFlags.None, linked.Token).ConfigureAwait(false);
                if (n == 0)
                {
                    break;
                }

                total += n;
            }
        }
        catch (Exception)
        {
            // The client went away first, or the stream is already broken: nothing left to protect.
        }
    }

    private async Task CloseStreamAsync()
    {
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Closing NUT connection {Id} failed.", Session.Id);
        }
        finally
        {
            _socket.Dispose();
        }
    }

    private void ReportLogout(NutClientState client, string ups, string reason)
    {
        bool loggedOut = reason == "LOGOUT";
        _logger.LogInformation("NUT client {User}@{Address} {Action} {Ups}{Reason}.", client.Username,
                               client.AddressText, loggedOut ? "logged out of" : "disconnected from", ups,
                               loggedOut ? "" : $" ({reason})");
        try
        {
            _services.Hub.Publish(new UpsEventMessage(UpsEvent.Create(
                UpsEventType.NutClientLogout, _services.Time.GetUtcNow(), ups,
                loggedOut
                    ? $"NUT client {client.Username}@{client.AddressText} logged out of {ups}."
                    : $"NUT client {client.Username}@{client.AddressText} disconnected from {ups} ({reason}).",
                CommandOrigin.Nut(client.Username, client.AddressText).ToString())));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not report the logout of NUT connection {Id}.", Session.Id);
        }
    }

    /// <summary>Failures of the network or of the client, as opposed to bugs, which are logged with their stack.</summary>
    private static bool IsNetworkError(Exception ex) =>
        ex is IOException or SocketException or ObjectDisposedException or AuthenticationException
            or OperationCanceledException;
}
