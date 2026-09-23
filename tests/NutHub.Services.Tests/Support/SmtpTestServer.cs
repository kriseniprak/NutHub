using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MimeKit;

namespace NutHub.Services.Tests.Support;

/// <summary>
/// A minimal SMTP server on the loopback interface (no TLS): EHLO, AUTH PLAIN / LOGIN, MAIL, RCPT, DATA, RSET, NOOP,
/// QUIT. Records the accepted messages.
/// </summary>
internal sealed class SmtpTestServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;

    public SmtpTestServer(string? username = null, string? password = null)
    {
        Username = username;
        Password = password;
        _listener.Start();
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public string? Username { get; }

    public string? Password { get; }

    /// <summary>Recipients refused with 550.</summary>
    public ConcurrentBag<string> RejectedRecipients { get; } = [];

    public ConcurrentQueue<ReceivedMail> Messages { get; } = new();

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        try
        {
            await _acceptLoop;
        }
        catch (Exception)
        {
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (Exception)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                NetworkStream stream = client.GetStream();
                var reader = new StreamReader(stream, Encoding.ASCII);
                var writer = new StreamWriter(stream, Encoding.ASCII) { NewLine = "\r\n", AutoFlush = true };
                await writer.WriteLineAsync("220 localhost ESMTP test");
                string? from = null;
                var to = new List<string>();
                bool authenticated = Username is null;
                while (await reader.ReadLineAsync(_cts.Token) is { } line)
                {
                    string verb = line.Split(' ', 2)[0].ToUpperInvariant();
                    string arg = line.Length > verb.Length ? line[(verb.Length + 1)..] : "";
                    switch (verb)
                    {
                        case "EHLO":
                        case "HELO":
                            await writer.WriteLineAsync("250-localhost");
                            if (Username is not null)
                            {
                                await writer.WriteLineAsync("250-AUTH PLAIN LOGIN");
                            }

                            await writer.WriteLineAsync("250 8BITMIME");
                            break;
                        case "AUTH":
                            authenticated = await AuthenticateAsync(arg, reader, writer);
                            await writer.WriteLineAsync(authenticated
                                ? "235 2.7.0 Authentication successful"
                                : "535 5.7.8 Authentication credentials invalid");
                            break;
                        case "MAIL":
                            if (!authenticated)
                            {
                                await writer.WriteLineAsync("530 5.7.0 Authentication required");
                                break;
                            }

                            from = Address(arg);
                            to.Clear();
                            await writer.WriteLineAsync("250 2.1.0 OK");
                            break;
                        case "RCPT":
                            string rcpt = Address(arg);
                            if (RejectedRecipients.Contains(rcpt))
                            {
                                await writer.WriteLineAsync("550 5.1.1 No such user");
                            }
                            else
                            {
                                to.Add(rcpt);
                                await writer.WriteLineAsync("250 2.1.5 OK");
                            }

                            break;
                        case "DATA":
                            await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
                            var data = new StringBuilder();
                            while (await reader.ReadLineAsync(_cts.Token) is { } dataLine && dataLine != ".")
                            {
                                data.Append(dataLine.StartsWith("..", StringComparison.Ordinal) ? dataLine[1..] : dataLine).Append("\r\n");
                            }

                            Messages.Enqueue(new ReceivedMail(from ?? "", [.. to], data.ToString()));
                            await writer.WriteLineAsync("250 2.0.0 OK queued");
                            break;
                        case "RSET":
                        case "NOOP":
                            await writer.WriteLineAsync("250 2.0.0 OK");
                            break;
                        case "QUIT":
                            await writer.WriteLineAsync("221 2.0.0 Bye");
                            return;
                        default:
                            await writer.WriteLineAsync("502 5.5.2 Command not recognized");
                            break;
                    }
                }
            }
            catch (Exception)
            {
                // Connection closed by the client or the server stopping.
            }
        }
    }

    private async Task<bool> AuthenticateAsync(string arg, StreamReader reader, StreamWriter writer)
    {
        string[] parts = arg.Split(' ', 2);
        string mechanism = parts[0].ToUpperInvariant();
        string? initial = parts.Length > 1 ? parts[1] : null;
        if (mechanism == "PLAIN")
        {
            if (initial is null)
            {
                await writer.WriteLineAsync("334 ");
                initial = await reader.ReadLineAsync();
            }

            string[] fields = Decode(initial).Split('\0');
            return fields.Length == 3 && fields[1] == Username && fields[2] == Password;
        }

        if (mechanism == "LOGIN")
        {
            if (initial is null)
            {
                await writer.WriteLineAsync("334 VXNlcm5hbWU6");
                initial = await reader.ReadLineAsync();
            }

            string user = Decode(initial);
            await writer.WriteLineAsync("334 UGFzc3dvcmQ6");
            string pass = Decode(await reader.ReadLineAsync());
            return user == Username && pass == Password;
        }

        return false;
    }

    private static string Decode(string? base64)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64 ?? ""));
        }
        catch (FormatException)
        {
            return "";
        }
    }

    private static string Address(string arg)
    {
        int start = arg.IndexOf('<');
        int end = arg.IndexOf('>');
        return start >= 0 && end > start ? arg[(start + 1)..end] : arg;
    }
}

internal sealed record ReceivedMail(string From, IReadOnlyList<string> To, string Data)
{
    public MimeMessage Parse() => MimeMessage.Load(new MemoryStream(Encoding.UTF8.GetBytes(Data)));
}
