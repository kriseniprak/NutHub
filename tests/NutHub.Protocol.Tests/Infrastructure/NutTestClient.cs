using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace NutHub.Protocol.Tests.Infrastructure;

/// <summary>
/// A minimal NUT client speaking raw protocol lines, with its own line buffer so the connection can switch to TLS.
/// Its quoting is written independently of the server's, so round trips test both sides.
/// </summary>
internal sealed class NutTestClient : IAsyncDisposable
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly TcpClient _tcp;
    private readonly byte[] _buffer = new byte[16384];
    private Stream _stream;
    private int _start;
    private int _end;

    private NutTestClient(TcpClient tcp)
    {
        _tcp = tcp;
        _stream = tcp.GetStream();
    }

    public Socket Socket => _tcp.Client;

    public X509Certificate? RemoteCertificate { get; private set; }

    public SslProtocols? TlsProtocol => (_stream as SslStream)?.SslProtocol;

    public static async Task<NutTestClient> ConnectAsync(IPEndPoint endpoint, int? receiveBufferSize = null)
    {
        var tcp = new TcpClient(endpoint.AddressFamily);
        if (receiveBufferSize is { } size)
        {
            tcp.ReceiveBufferSize = size;
        }

        using var timeout = new CancellationTokenSource(DefaultTimeout);
        await tcp.ConnectAsync(endpoint, timeout.Token);
        tcp.NoDelay = true;
        return new NutTestClient(tcp);
    }

    /// <summary>Quotes a value the way NUT clients do (escaping backslash and double quote).</summary>
    public static string Quote(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    public Task SendAsync(string line) => SendRawAsync(Encoding.UTF8.GetBytes(line + "\n"));

    public async Task SendRawAsync(byte[] bytes)
    {
        using var timeout = new CancellationTokenSource(DefaultTimeout);
        await _stream.WriteAsync(bytes, timeout.Token);
        await _stream.FlushAsync(timeout.Token);
    }

    /// <summary>The next line without its "\n", or null when the server closed the connection.</summary>
    public async Task<string?> ReadLineAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? DefaultTimeout);
        while (true)
        {
            int newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline >= 0)
            {
                string line = Encoding.UTF8.GetString(_buffer, _start, newline - _start);
                _start = newline + 1;
                return line;
            }

            if (_start > 0)
            {
                Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
                _end -= _start;
                _start = 0;
            }

            if (_end == _buffer.Length)
            {
                throw new InvalidOperationException("Line too long for the test client.");
            }

            int read;
            try
            {
                read = await _stream.ReadAsync(_buffer.AsMemory(_end), cts.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("No answer from the server.");
            }

            if (read == 0)
            {
                return null;
            }

            _end += read;
        }
    }

    public async Task<string> CommandAsync(string line)
    {
        await SendAsync(line);
        return await ReadLineAsync() ?? throw new EndOfStreamException($"Connection closed after '{line}'.");
    }

    /// <summary>Sends a LIST request and reads up to END LIST (or a single ERR line).</summary>
    public async Task<List<string>> ListAsync(string line)
    {
        await SendAsync(line);
        var lines = new List<string>();
        while (true)
        {
            string answer = await ReadLineAsync() ?? throw new EndOfStreamException($"Connection closed during '{line}'.");
            lines.Add(answer);
            if (answer.StartsWith("END LIST", StringComparison.Ordinal) ||
                (lines.Count == 1 && answer.StartsWith("ERR ", StringComparison.Ordinal)))
            {
                return lines;
            }
        }
    }

    /// <summary>STARTTLS, then the TLS handshake accepting any server certificate (it is self-signed).</summary>
    public async Task StartTlsAsync()
    {
        string answer = await CommandAsync("STARTTLS");
        Assert.Equal("OK STARTTLS", answer);
        Assert.Equal(_start, _end); // nothing may follow "OK STARTTLS" in clear text

        var tls = new SslStream(_stream, leaveInnerStreamOpen: false);
        using var timeout = new CancellationTokenSource(DefaultTimeout);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = "localhost",
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            RemoteCertificateValidationCallback = (_, _, _, _) => true,
            CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
        }, timeout.Token);
        RemoteCertificate = tls.RemoteCertificate;
        _stream = tls;
    }

    /// <summary>True when the server closes the connection (end of stream or reset) within the timeout.</summary>
    public async Task<bool> WaitForCloseAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? DefaultTimeout);
        byte[] sink = new byte[4096];
        try
        {
            while (true)
            {
                if (_start < _end)
                {
                    _start = _end; // discard buffered answers
                }

                int read = await _stream.ReadAsync(sink, cts.Token);
                if (read == 0)
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _stream.DisposeAsync();
        }
        catch (IOException)
        {
        }

        _tcp.Dispose();
    }
}
