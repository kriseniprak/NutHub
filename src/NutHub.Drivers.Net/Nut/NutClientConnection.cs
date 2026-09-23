using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using NutHub.Drivers.Net.Common;

namespace NutHub.Drivers.Net.Nut;

/// <summary>How to reach an upstream NUT server.</summary>
internal sealed record NutClientOptions
{
    public required string Host { get; init; }

    public int Port { get; init; } = 3493;

    /// <summary>Bound of the connection and of every request / response exchange.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Switch to TLS with STARTTLS right after connecting.</summary>
    public bool UseTls { get; init; }

    /// <summary>Require a certificate that chains to a trusted root and matches the host name.</summary>
    public bool VerifyCertificate { get; init; }

    public string Endpoint => Host.Contains(':') && !Host.StartsWith('[') ? $"[{Host}]:{Port}" : $"{Host}:{Port}";
}

/// <summary>
/// One connection to a NUT server (upsd or compatible) speaking the line protocol of docs/net-protocol.txt, with
/// the behaviour of NUT clients/upsclient.c: optional STARTTLS, one request at a time, answers that are either one
/// line or a BEGIN LIST / END LIST block.
/// <para>
/// Every exchange is atomic (an internal lock), so the poll loop and a command can share the connection. An exchange
/// that fails half way (timeout, cancellation, unexpected line) leaves the stream out of step with the server: the
/// connection is then marked <see cref="IsBroken"/> and refuses further requests, and the owner reconnects.
/// </para>
/// </summary>
internal sealed class NutClientConnection : IAsyncDisposable
{
    /// <summary>Longest accepted line; upsd lines are far shorter, so more means a wrong or hostile peer.</summary>
    internal const int MaxLineBytes = 16 * 1024;

    /// <summary>Most items accepted in one list, against a peer that never sends END LIST.</summary>
    internal const int MaxListItems = 20_000;

    private readonly Socket _socket;
    private readonly NutClientOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly byte[] _buffer = new byte[MaxLineBytes];
    private Stream _stream;
    private int _start;
    private int _end;
    private volatile bool _broken;
    private int _disposed;

    private NutClientConnection(Socket socket, NutClientOptions options, TimeProvider time)
    {
        _socket = socket;
        _options = options;
        _time = time;
        _stream = new NetworkStream(socket, ownsSocket: false);
    }

    /// <summary>"host:port", for messages.</summary>
    public string Endpoint => _options.Endpoint;

    /// <summary>Whether the connection runs over TLS.</summary>
    public bool IsTls { get; private set; }

    /// <summary>True once an exchange failed half way; the connection must be replaced.</summary>
    public bool IsBroken => _broken || Volatile.Read(ref _disposed) != 0;

    /// <summary>Connects and, when configured, negotiates TLS with STARTTLS.</summary>
    public static async Task<NutClientConnection> ConnectAsync(NutClientOptions options, TimeProvider time,
                                                               CancellationToken cancellationToken)
    {
        Socket socket = await TcpConnector.ConnectAsync(options.Host, options.Port, options.Timeout, time,
                                                        cancellationToken).ConfigureAwait(false);
        var connection = new NutClientConnection(socket, options, time);
        try
        {
            if (options.UseTls)
            {
                await connection.StartTlsAsync(cancellationToken).ConfigureAwait(false);
            }

            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Sends a request answered by one line and returns its words. "ERR ..." answers throw
    /// <see cref="NutErrorException"/> and leave the connection usable.
    /// </summary>
    public Task<List<string>> RequestAsync(string line, CancellationToken cancellationToken) =>
        ExchangeAsync(async ct =>
        {
            await WriteLineAsync(line, ct).ConfigureAwait(false);
            List<string> words = await ReadWordsAsync(ct).ConfigureAwait(false);
            ThrowIfError(words);
            return words;
        }, cancellationToken);

    /// <summary>Sends a request answered by one line and returns that line as received (for VER).</summary>
    public Task<string> RequestRawAsync(string line, CancellationToken cancellationToken) =>
        ExchangeAsync(async ct =>
        {
            await WriteLineAsync(line, ct).ConfigureAwait(false);
            string answer = await ReadNonEmptyLineAsync(ct).ConfigureAwait(false);
            ThrowIfError(NutLine.Split(answer));
            return answer;
        }, cancellationToken);

    /// <summary>
    /// Runs "LIST &lt;kind&gt; &lt;args&gt;" and returns, for each item line, the words after the kind
    /// ("VAR ups battery.charge 100" gives [ups, battery.charge, 100]).
    /// </summary>
    public Task<List<List<string>>> ListAsync(string kind, string[] arguments, CancellationToken cancellationToken) =>
        ExchangeAsync(async ct =>
        {
            await WriteLineAsync(NutLine.Build("LIST " + kind, arguments), ct).ConfigureAwait(false);
            List<string> first = await ReadWordsAsync(ct).ConfigureAwait(false);
            ThrowIfError(first);
            if (!IsListMarker(first, "BEGIN", kind, arguments))
            {
                throw new NutProtocolException(
                    $"expected 'BEGIN LIST {kind}' from {Endpoint}, got '{string.Join(' ', first)}'");
            }

            var items = new List<List<string>>();
            while (true)
            {
                List<string> words = await ReadWordsAsync(ct).ConfigureAwait(false);
                if (words.Count >= 2 && words[0] == "END" && words[1] == "LIST")
                {
                    if (!IsListMarker(words, "END", kind, arguments))
                    {
                        throw new NutProtocolException(
                            $"list {kind} from {Endpoint} ended with '{string.Join(' ', words)}'");
                    }

                    return items;
                }

                if (words[0] != kind)
                {
                    throw new NutProtocolException(
                        $"unexpected line in list {kind} from {Endpoint}: '{string.Join(' ', words)}'");
                }

                if (items.Count >= MaxListItems)
                {
                    throw new NutProtocolException($"list {kind} from {Endpoint} has more than {MaxListItems} items");
                }

                words.RemoveAt(0);
                items.Add(words);
            }
        }, cancellationToken);

    /// <summary>Says goodbye (LOGOUT) when the connection is healthy; never throws.</summary>
    public async Task TryLogoutAsync(CancellationToken cancellationToken)
    {
        if (IsBroken)
        {
            return;
        }

        try
        {
            await RequestAsync("LOGOUT", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is NutErrorException or OperationCanceledException || NetworkErrors.IsTransient(ex))
        {
            // The connection is closed right after anyway.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _broken = true;
        try
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (NetworkErrors.IsTransient(ex))
        {
            // Closing a dead connection may fail; nothing to do about it.
        }

        _socket.Dispose();
        // The lock is left alive: an exchange still waiting on it must be able to observe IsBroken and leave.
    }

    private async Task StartTlsAsync(CancellationToken cancellationToken)
    {
        await ExchangeAsync(async ct =>
        {
            await WriteLineAsync("STARTTLS", ct).ConfigureAwait(false);
            List<string> words = await ReadWordsAsync(ct).ConfigureAwait(false);
            if (words is ["ERR", var code, ..])
            {
                // Falling back to clear text would expose the password the user asked to protect.
                throw new AuthenticationException($"the server refused STARTTLS (ERR {code})");
            }

            if (words is not ["OK", "STARTTLS", ..])
            {
                throw new NutProtocolException($"unexpected answer to STARTTLS: '{string.Join(' ', words)}'");
            }

            if (_end > _start)
            {
                throw new NutProtocolException("the server sent data before the TLS handshake");
            }

            var ssl = new SslStream(_stream, leaveInnerStreamOpen: false);
            string host = _options.Host.StartsWith('[') ? _options.Host[1..^1] : _options.Host;
            var authentication = new SslClientAuthenticationOptions
            {
                TargetHost = host,
                RemoteCertificateValidationCallback = _options.VerifyCertificate
                    ? (_, _, _, errors) => errors == SslPolicyErrors.None
                    : (_, _, _, _) => true,
            };

            try
            {
                await ssl.AuthenticateAsClientAsync(authentication, ct).ConfigureAwait(false);
            }
            catch
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            _stream = ssl;
            IsTls = true;
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs one exchange under the lock and the timeout, and marks the connection broken when it did not complete
    /// cleanly.
    /// </summary>
    private async Task<T> ExchangeAsync<T>(Func<CancellationToken, Task<T>> exchange, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsBroken)
            {
                throw new IOException($"the connection to {Endpoint} is closed");
            }

            using var scope = new TimeoutScope(_options.Timeout, _time, cancellationToken);
            try
            {
                return await exchange(scope.Token).ConfigureAwait(false);
            }
            catch (NutErrorException)
            {
                throw;
            }
            catch (OperationCanceledException) when (scope.TimedOut)
            {
                _broken = true;
                throw new TimeoutException(
                    $"no answer from {Endpoint} within {_options.Timeout.TotalSeconds:0.#} s");
            }
            catch
            {
                _broken = true;
                throw;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        await _stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<string>> ReadWordsAsync(CancellationToken cancellationToken)
    {
        string line = await ReadNonEmptyLineAsync(cancellationToken).ConfigureAwait(false);
        List<string> words = NutLine.Split(line);
        if (words.Count == 0)
        {
            throw new NutProtocolException($"unreadable line from {Endpoint}: '{line}'");
        }

        return words;
    }

    private async Task<string> ReadNonEmptyLineAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            string line = await ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line.Length > 0)
            {
                return line;
            }
        }
    }

    private async Task<string> ReadLineAsync(CancellationToken cancellationToken)
    {
        int scanned = _start;
        while (true)
        {
            int newline = Array.IndexOf(_buffer, (byte)'\n', scanned, _end - scanned);
            if (newline >= 0)
            {
                int length = newline - _start;
                if (length > 0 && _buffer[newline - 1] == (byte)'\r')
                {
                    length--;
                }

                string line = Encoding.UTF8.GetString(_buffer, _start, length);
                _start = newline + 1;
                if (_start == _end)
                {
                    _start = _end = 0;
                }

                return line;
            }

            scanned = _end;
            if (_end == _buffer.Length)
            {
                if (_start == 0)
                {
                    throw new NutProtocolException($"a line from {Endpoint} is longer than {MaxLineBytes} bytes");
                }

                // Move the partial line to the front to make room.
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                scanned -= _start;
                _start = 0;
            }

            int read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException($"{Endpoint} closed the connection");
            }

            _end += read;
        }
    }

    private static void ThrowIfError(List<string> words)
    {
        if (words.Count > 0 && words[0] == "ERR")
        {
            string code = words.Count > 1 ? words[1].ToUpperInvariant() : "UNKNOWN";
            string? extra = words.Count > 2 ? string.Join(' ', words.Skip(2)) : null;
            throw new NutErrorException(code, extra);
        }
    }

    /// <summary>Checks "BEGIN LIST kind args..." / "END LIST kind args..." (names compared without case, like upsd).</summary>
    private static bool IsListMarker(List<string> words, string marker, string kind, string[] arguments)
    {
        if (words.Count < 3 + arguments.Length || words[0] != marker || words[1] != "LIST" || words[2] != kind)
        {
            return false;
        }

        for (int i = 0; i < arguments.Length; i++)
        {
            if (!string.Equals(words[3 + i], arguments[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}
