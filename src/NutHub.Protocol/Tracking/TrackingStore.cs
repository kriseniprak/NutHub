using NutHub.Core.Model;

namespace NutHub.Protocol.Tracking;

/// <summary>
/// Results of INSTCMD / SET VAR requested with TRACKING ON (protocol 1.3), queried with GET TRACKING &lt;id&gt;.
/// Shared by all connections like in upsd, where any client can ask about any id. Entries expire after
/// <see cref="NutProtocolOptions.TrackingRetention"/>; at most <see cref="NutProtocolOptions.TrackingCapacity"/> are
/// kept, the oldest being dropped first, so a busy client cannot grow the table without bound.
/// </summary>
internal sealed class TrackingStore
{
    public const string Pending = "PENDING";
    public const string Success = "SUCCESS";

    /// <summary>What upsd answers for an id it does not know (or no longer knows).</summary>
    public const string Unknown = "ERR UNKNOWN";

    private readonly TimeProvider _time;
    private readonly NutProtocolOptions _options;
    private readonly object _lock = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Entry> _byAge = new();

    public TrackingStore(TimeProvider time, NutProtocolOptions options)
    {
        _time = time;
        _options = options;
    }

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _byId.Count;
            }
        }
    }

    /// <summary>Registers a new pending operation and returns its id (a random UUID, like upsd's nut_uuid_v4).</summary>
    public string Create()
    {
        string id = Guid.NewGuid().ToString("D");
        DateTimeOffset now = _time.GetUtcNow();
        lock (_lock)
        {
            RemoveExpired(now);
            while (_byId.Count >= Math.Max(1, _options.TrackingCapacity) && _byAge.First is { } oldest)
            {
                _byAge.RemoveFirst();
                _byId.Remove(oldest.Value.Id);
            }

            _byId[id] = _byAge.AddLast(new Entry(id, now) { Result = Pending });
        }

        return id;
    }

    /// <summary>Stores the final result of an operation ("SUCCESS" or "ERR ...").</summary>
    public void Complete(string id, string result)
    {
        lock (_lock)
        {
            if (_byId.TryGetValue(id, out LinkedListNode<Entry>? node))
            {
                node.Value.Result = result;
            }
        }
    }

    /// <summary>The answer to GET TRACKING &lt;id&gt;: PENDING, SUCCESS or ERR &lt;word&gt;.</summary>
    public string Get(string id)
    {
        DateTimeOffset now = _time.GetUtcNow();
        lock (_lock)
        {
            if (_byId.TryGetValue(id, out LinkedListNode<Entry>? node) &&
                now - node.Value.CreatedAt <= _options.TrackingRetention)
            {
                return node.Value.Result;
            }

            return Unknown;
        }
    }

    public void Cleanup()
    {
        lock (_lock)
        {
            RemoveExpired(_time.GetUtcNow());
        }
    }

    /// <summary>
    /// The tracking answer for an outcome, with the words upsd uses (driver status codes STAT_HANDLED,
    /// STAT_UNKNOWN, STAT_INVALID, STAT_FAILED).
    /// </summary>
    public static string ResultOf(CommandResult result) => result.Status switch
    {
        CommandStatus.Success => Success,
        CommandStatus.NotSupported => "ERR UNKNOWN",
        CommandStatus.InvalidArgument or CommandStatus.InvalidValue or CommandStatus.TooLong or CommandStatus.ReadOnly
            => "ERR INVALID-ARGUMENT",
        _ => "ERR FAILED",
    };

    private void RemoveExpired(DateTimeOffset now)
    {
        while (_byAge.First is { } oldest && now - oldest.Value.CreatedAt > _options.TrackingRetention)
        {
            _byAge.RemoveFirst();
            _byId.Remove(oldest.Value.Id);
        }
    }

    private sealed class Entry(string id, DateTimeOffset createdAt)
    {
        public string Id { get; } = id;

        public DateTimeOffset CreatedAt { get; } = createdAt;

        public string Result { get; set; } = Pending;
    }
}
