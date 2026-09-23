using System.Collections.Concurrent;
using System.Net;

namespace NutHub.Core.Runtime;

/// <summary>
/// One connection to the NUT protocol server. The protocol server updates it through
/// <see cref="NutSessionRegistry"/>; everybody else reads it.
/// </summary>
public sealed class NutSession
{
    private readonly Action _disconnect;

    internal NutSession(long id, IPEndPoint remote, DateTimeOffset connectedAt, Action disconnect)
    {
        Id = id;
        Remote = remote;
        ConnectedAt = connectedAt;
        LastActivity = connectedAt;
        _disconnect = disconnect;
    }

    public long Id { get; }

    public IPEndPoint Remote { get; }

    /// <summary>The client address as NUT prints it (IPv4 clients of a dual-mode socket without the ::ffff: prefix).</summary>
    public string Address => (Remote.Address.IsIPv4MappedToIPv6 ? Remote.Address.MapToIPv4() : Remote.Address).ToString();

    public DateTimeOffset ConnectedAt { get; }

    /// <summary>The account, once USERNAME and a correct PASSWORD were given.</summary>
    public string? Username { get; internal set; }

    public bool Tls { get; internal set; }

    /// <summary>The UPS this client did LOGIN to (NUT allows one per connection).</summary>
    public string? LoginUps { get; internal set; }

    /// <summary>Whether the client declared itself upsmon primary (PRIMARY / MASTER).</summary>
    public bool Primary { get; internal set; }

    public DateTimeOffset LastActivity { get; internal set; }

    public long Commands { get; internal set; }

    internal void Disconnect() => _disconnect();
}

/// <summary>
/// The connections of NUT clients, shared by the protocol server (which maintains it), the host protection (which
/// counts logged-in secondaries, like upsmon primary with NUMLOGINS) and the web panel.
/// </summary>
public sealed class NutSessionRegistry(EventHub hub, TimeProvider time)
{
    private readonly ConcurrentDictionary<long, NutSession> _sessions = new();
    private long _nextId;

    public IReadOnlyList<NutSession> Sessions => _sessions.Values.OrderBy(s => s.Id).ToList();

    public NutSession Open(IPEndPoint remote, Action disconnect)
    {
        var session = new NutSession(Interlocked.Increment(ref _nextId), remote, time.GetUtcNow(), disconnect);
        _sessions[session.Id] = session;
        hub.Publish(new NutClientsChangedMessage());
        return session;
    }

    public void Close(NutSession session)
    {
        if (_sessions.TryRemove(session.Id, out _))
        {
            hub.Publish(new NutClientsChangedMessage());
        }
    }

    public void SetUser(NutSession session, string? username) => session.Username = username;

    public void SetTls(NutSession session) => session.Tls = true;

    public void SetLogin(NutSession session, string ups)
    {
        session.LoginUps = ups;
        hub.Publish(new NutClientsChangedMessage());
    }

    public void SetPrimary(NutSession session)
    {
        session.Primary = true;
        hub.Publish(new NutClientsChangedMessage());
    }

    public void Touch(NutSession session)
    {
        session.LastActivity = time.GetUtcNow();
        session.Commands++;
    }

    /// <summary>NUMLOGINS: how many connections did LOGIN to this UPS.</summary>
    public int GetLoginCount(string ups) =>
        _sessions.Values.Count(s => s.LoginUps is not null && string.Equals(s.LoginUps, ups, StringComparison.OrdinalIgnoreCase));

    /// <summary>The connections logged in to this UPS (LIST CLIENT).</summary>
    public IReadOnlyList<NutSession> GetLogins(string ups) =>
        _sessions.Values
            .Where(s => s.LoginUps is not null && string.Equals(s.LoginUps, ups, StringComparison.OrdinalIgnoreCase))
            .OrderBy(s => s.Id)
            .ToList();

    public NutSession? Find(long id) => _sessions.TryGetValue(id, out NutSession? s) ? s : null;

    /// <summary>Closes a connection (web panel "disconnect").</summary>
    public bool Disconnect(long id)
    {
        if (!_sessions.TryGetValue(id, out NutSession? session))
        {
            return false;
        }

        session.Disconnect();
        return true;
    }
}
