using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using NutHub.Drivers.Net.Nut;

namespace NutHub.Drivers.Net.Tests.Fakes;

/// <summary>A writable variable of the fake server: its GET TYPE words, enum values and ranges.</summary>
public sealed record FakeRw(string Type, string[]? Enums = null, (string Min, string Max)[]? Ranges = null);

/// <summary>
/// An in-process upsd on 127.0.0.1 speaking enough of the NUT protocol for the driver: VER, STARTTLS, USERNAME,
/// PASSWORD, LOGIN, LOGOUT, SET/GET TRACKING, LIST VAR/RW/CMD/ENUM/RANGE, GET TYPE, INSTCMD and SET VAR, with upsd's
/// quoting. Behaviour switches let tests inject errors and failures.
/// </summary>
public sealed class FakeUpsd : IAsyncDisposable
{
    private readonly ConcurrentDictionary<TcpClient, byte> _clients = new();
    private readonly ConcurrentDictionary<string, int> _trackingPolls = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _stop;
    private int _trackingCounter;

    public FakeUpsd(X509Certificate2? certificate = null)
    {
        Certificate = certificate;
    }

    public X509Certificate2? Certificate { get; set; }

    public int Port { get; private set; }

    public string UpsName { get; set; } = "myups";

    public string User { get; set; } = "admin";

    public string Password { get; set; } = "secret";

    public bool TrackingSupported { get; set; } = true;

    /// <summary>Returned for every LIST VAR while set, e.g. "DATA-STALE".</summary>
    public volatile string? ReadError;

    /// <summary>The next LIST VAR sends two items and then closes the connection.</summary>
    public volatile bool DropDuringNextListVar;

    /// <summary>The next LIST VAR contains a line that is not a VAR item.</summary>
    public volatile bool GarbageInNextListVar;

    /// <summary>Result of instant commands by name: null for success, else an error code for the reply.</summary>
    public ConcurrentDictionary<string, string?> CommandErrors { get; } = new();

    /// <summary>Tracking outcome reported by GET TRACKING after one PENDING: "SUCCESS" or "ERR FAILED"...</summary>
    public string TrackingOutcome { get; set; } = "SUCCESS";

    public ConcurrentDictionary<string, string> Vars { get; } = new(StringComparer.Ordinal);

    public ConcurrentDictionary<string, FakeRw> Rw { get; } = new(StringComparer.Ordinal);

    public List<string> Commands { get; } = [];

    /// <summary>Every request line received, raw, across all connections.</summary>
    public ConcurrentQueue<string> Received { get; } = new();

    /// <summary>Executed instant commands and variable writes, decoded: "INSTCMD load.off.delay 120".</summary>
    public ConcurrentQueue<string> Executed { get; } = new();

    public int ConnectionCount;

    public int TlsSessions;

    public int Logins;

    public void Start(int port = 0)
    {
        _stop = new CancellationTokenSource();
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        listener.Start();
        _listener = listener;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync(listener, _stop.Token);
    }

    /// <summary>Stops listening and drops every connection, like a stopped upsd.</summary>
    public void Stop()
    {
        _stop?.Cancel();
        _listener?.Stop();
        foreach (TcpClient client in _clients.Keys)
        {
            client.Dispose();
        }

        _clients.Clear();
    }

    public ValueTask DisposeAsync()
    {
        Stop();
        _stop?.Dispose();
        return ValueTask.CompletedTask;
    }

    public void SetDefaultUps()
    {
        Vars["battery.charge"] = "100";
        Vars["battery.runtime"] = "1800";
        Vars["ups.status"] = "OL CHRG";
        Vars["ups.model"] = "Smart-UPS 1500";
        Vars["ups.mfr"] = "APC";
        Vars["ups.id"] = "Rack \"A\" \\ #1";
        Vars["input.transfer.low"] = "100";
        Vars["ups.beeper.status"] = "enabled";
        Vars["driver.name"] = "usbhid-ups";
        Vars["driver.version.internal"] = "0.52";
        Rw["input.transfer.low"] = new FakeRw("RW RANGE NUMBER", Ranges: [("90", "100"), ("102", "105")]);
        Rw["ups.beeper.status"] = new FakeRw("RW ENUM STRING:8", Enums: ["enabled", "disabled", "muted"]);
        Rw["ups.id"] = new FakeRw("RW STRING:32");
        Commands.AddRange(["beeper.disable", "load.off.delay", "test.battery.start.quick"]);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(stop);
            }
            catch (Exception)
            {
                return;
            }

            _clients[client] = 0;
            Interlocked.Increment(ref ConnectionCount);
            _ = ServeAsync(client, stop);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken stop)
    {
        var session = new Session();
        try
        {
            Stream stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
            while (!stop.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(stop);
                if (line is null)
                {
                    return;
                }

                Received.Enqueue(line);
                if (line == "STARTTLS")
                {
                    if (Certificate is null)
                    {
                        await WriteAsync(stream, "ERR FEATURE-NOT-CONFIGURED\n", stop);
                        continue;
                    }

                    await WriteAsync(stream, "OK STARTTLS\n", stop);
                    var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsServerAsync(Certificate, false, false);
                    Interlocked.Increment(ref TlsSessions);
                    stream = ssl;
                    reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
                    continue;
                }

                string? reply = Handle(NutLine.Split(line), session, out bool close);
                if (reply is not null)
                {
                    await WriteAsync(stream, reply, stop);
                }

                if (close)
                {
                    return;
                }
            }
        }
        catch (Exception)
        {
            // Connection dropped by either side.
        }
        finally
        {
            _clients.TryRemove(client, out _);
            client.Dispose();
        }
    }

    private static async Task WriteAsync(Stream stream, string text, CancellationToken stop)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), stop);
        await stream.FlushAsync(stop);
    }

    /// <summary>What upsd's pconf_encode does to values.</summary>
    public static string Encode(string value)
    {
        var sb = new StringBuilder();
        foreach (char c in value)
        {
            if (c is '"' or '\\' or '#')
            {
                sb.Append('\\');
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private string? Handle(List<string> w, Session s, out bool close)
    {
        close = false;
        string Cmd(int i) => w.Count > i ? w[i].ToUpperInvariant() : string.Empty;
        bool KnownUps(int i) => w.Count > i && string.Equals(w[i], UpsName, StringComparison.OrdinalIgnoreCase);
        bool Authorized() => s.User == User && s.Password == Password;

        switch (Cmd(0))
        {
            case "VER":
                return "Network UPS Tools upsd 2.8.1 - https://www.networkupstools.org/\n";
            case "USERNAME":
                s.User = w[1];
                return "OK\n";
            case "PASSWORD":
                s.Password = w[1];
                return "OK\n";
            case "LOGOUT":
                close = true;
                return "OK Goodbye\n";
            case "LOGIN":
                if (!Authorized())
                {
                    return "ERR ACCESS-DENIED\n";
                }

                Interlocked.Increment(ref Logins);
                return "OK\n";
            case "SET" when Cmd(1) == "TRACKING":
                if (!TrackingSupported)
                {
                    return "ERR UNKNOWN-COMMAND\n";
                }

                s.Tracking = Cmd(2) == "ON";
                return "OK\n";
            case "GET" when Cmd(1) == "TRACKING":
                int polls = _trackingPolls.AddOrUpdate(w[2], 1, (_, n) => n + 1);
                return polls == 1 ? "PENDING\n" : TrackingOutcome + "\n";
            case "GET" when Cmd(1) == "TYPE":
                if (!KnownUps(2))
                {
                    return "ERR UNKNOWN-UPS\n";
                }

                return Rw.TryGetValue(w[3], out FakeRw? rw)
                    ? $"TYPE {w[2]} {w[3]} {rw.Type}\n"
                    : Vars.ContainsKey(w[3]) ? $"TYPE {w[2]} {w[3]} NUMBER\n" : "ERR VAR-NOT-SUPPORTED\n";
            case "LIST":
                return List(w, s, out close);
            case "INSTCMD":
                if (!KnownUps(1))
                {
                    return "ERR UNKNOWN-UPS\n";
                }

                if (s.User is null)
                {
                    return "ERR USERNAME-REQUIRED\n";
                }

                if (!Authorized())
                {
                    return "ERR ACCESS-DENIED\n";
                }

                if (!Commands.Contains(w[2]))
                {
                    return "ERR CMD-NOT-SUPPORTED\n";
                }

                if (CommandErrors.TryGetValue(w[2], out string? error) && error is not null)
                {
                    return $"ERR {error}\n";
                }

                Executed.Enqueue(string.Join(' ', w));
                return Accepted(s);
            case "SET" when Cmd(1) == "VAR":
                if (!Authorized())
                {
                    return "ERR ACCESS-DENIED\n";
                }

                if (!Vars.ContainsKey(w[3]))
                {
                    return "ERR VAR-NOT-SUPPORTED\n";
                }

                if (!Rw.ContainsKey(w[3]))
                {
                    return "ERR READONLY\n";
                }

                Vars[w[3]] = w[4];
                Executed.Enqueue($"SET {w[3]}={w[4]}");
                return Accepted(s);
            default:
                return "ERR UNKNOWN-COMMAND\n";
        }
    }

    private string Accepted(Session s)
    {
        if (!s.Tracking)
        {
            return "OK\n";
        }

        string id = "id-" + Interlocked.Increment(ref _trackingCounter);
        return $"OK TRACKING {id}\n";
    }

    private string? List(List<string> w, Session s, out bool close)
    {
        close = false;
        string kind = w.Count > 1 ? w[1].ToUpperInvariant() : string.Empty;
        string ups = w.Count > 2 ? w[2] : string.Empty;
        if (!string.Equals(ups, UpsName, StringComparison.OrdinalIgnoreCase))
        {
            return "ERR UNKNOWN-UPS\n";
        }

        var sb = new StringBuilder();
        switch (kind)
        {
            case "VAR":
                if (ReadError is { } error)
                {
                    return $"ERR {error}\n";
                }

                sb.Append($"BEGIN LIST VAR {ups}\n");
                int count = 0;
                foreach (var (name, value) in Vars.OrderBy(v => v.Key, StringComparer.Ordinal))
                {
                    sb.Append($"VAR {ups} {name} \"{Encode(value)}\"\n");
                    if (++count == 2 && DropDuringNextListVar)
                    {
                        DropDuringNextListVar = false;
                        close = true;
                        return sb.ToString();
                    }

                    if (count == 1 && GarbageInNextListVar)
                    {
                        GarbageInNextListVar = false;
                        sb.Append("HELLO THERE\n");
                    }
                }

                sb.Append($"END LIST VAR {ups}\n");
                return sb.ToString();
            case "RW":
                sb.Append($"BEGIN LIST RW {ups}\n");
                foreach (string name in Rw.Keys.Order(StringComparer.Ordinal))
                {
                    sb.Append($"RW {ups} {name} \"{Encode(Vars.GetValueOrDefault(name, ""))}\"\n");
                }

                sb.Append($"END LIST RW {ups}\n");
                return sb.ToString();
            case "CMD":
                sb.Append($"BEGIN LIST CMD {ups}\n");
                foreach (string c in Commands)
                {
                    sb.Append($"CMD {ups} {c}\n");
                }

                sb.Append($"END LIST CMD {ups}\n");
                return sb.ToString();
            case "ENUM" or "RANGE":
                string var = w[3];
                if (!Rw.TryGetValue(var, out FakeRw? rw))
                {
                    return "ERR VAR-NOT-SUPPORTED\n";
                }

                sb.Append($"BEGIN LIST {kind} {ups} {var}\n");
                if (kind == "ENUM")
                {
                    foreach (string e in rw.Enums ?? [])
                    {
                        sb.Append($"ENUM {ups} {var} \"{Encode(e)}\"\n");
                    }
                }
                else
                {
                    foreach (var (min, max) in rw.Ranges ?? [])
                    {
                        sb.Append($"RANGE {ups} {var} \"{min}\" \"{max}\"\n");
                    }
                }

                sb.Append($"END LIST {kind} {ups} {var}\n");
                return sb.ToString();
            default:
                return "ERR INVALID-ARGUMENT\n";
        }
    }

    private sealed class Session
    {
        public string? User;
        public string? Password;
        public bool Tracking;
    }
}
