using System.Text.Json;
using Microsoft.Extensions.Logging;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Core.Security;

namespace NutHub.Services.Notifications.Email;

/// <summary>An e-mail waiting to be sent, as stored in the outbox file.</summary>
internal sealed class OutboxMessage
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset CreatedAt { get; set; }

    public string From { get; set; } = "";

    public List<string> To { get; set; } = [];

    public string Subject { get; set; } = "";

    public string TextBody { get; set; } = "";

    public string HtmlBody { get; set; } = "";

    /// <summary>Contains a critical event: dropped last when the outbox is full.</summary>
    public bool Critical { get; set; }

    /// <summary>The main event, for the delivery list.</summary>
    public UpsEventType EventType { get; set; }

    public string? Ups { get; set; }

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public string? LastError { get; set; }
}

/// <summary>
/// E-mails that could not be sent yet, kept on disk: during a power outage the network switch or the mail
/// server is often down too, and the messages must survive a restart of NutHub (or of the whole machine).
/// Retried with a growing delay for up to <see cref="MaxAge"/>.
/// </summary>
internal sealed class EmailOutbox
{
    public const int DefaultCapacity = 500;

    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(10),
    ];

    private readonly string _path;
    private readonly int _capacity;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private List<OutboxMessage> _messages = [];

    public EmailOutbox(string path, ILogger logger, int capacity = DefaultCapacity)
    {
        _path = path;
        _logger = logger;
        _capacity = Math.Max(1, capacity);
    }

    public string FilePath => _path;

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _messages.Count;
            }
        }
    }

    /// <summary>The delay before attempt number <paramref name="failedAttempts"/> + 1.</summary>
    public static TimeSpan RetryDelay(int failedAttempts) =>
        RetryDelays[Math.Clamp(failedAttempts - 1, 0, RetryDelays.Length - 1)];

    /// <summary>Reads the outbox file left by a previous run. A damaged file is set aside, never fatal.</summary>
    public void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                _messages = [];
                return;
            }

            try
            {
                string json = File.ReadAllText(_path);
                List<OutboxMessage?> loaded = JsonSerializer.Deserialize<List<OutboxMessage?>>(json, NutHubJson.Compact) ?? [];
                _messages = loaded.OfType<OutboxMessage>().Select(Repair).Where(m => m.To.Count > 0).ToList();
                if (_messages.Count > 0)
                {
                    _logger.LogInformation("{Count} e-mail notification(s) waiting in the outbox from a previous run.",
                                           _messages.Count);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                _logger.LogError(ex, "The e-mail outbox {Path} is unreadable; it is set aside and a new one started.", _path);
                _messages = [];
                try
                {
                    File.Move(_path, _path + ".damaged", overwrite: true);
                }
                catch (Exception moveError) when (moveError is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(moveError, "Could not rename the damaged outbox.");
                }
            }
        }
    }

    public IReadOnlyList<OutboxMessage> Snapshot()
    {
        lock (_lock)
        {
            return _messages.ToList();
        }
    }

    /// <summary>Adds a message; when full, drops the oldest non-critical message (or the oldest one).</summary>
    /// <returns>The messages dropped to make room.</returns>
    public IReadOnlyList<OutboxMessage> Add(OutboxMessage message)
    {
        var dropped = new List<OutboxMessage>();
        lock (_lock)
        {
            _messages.Add(message);
            while (_messages.Count > _capacity)
            {
                OutboxMessage victim = _messages.FirstOrDefault(m => !m.Critical) ?? _messages[0];
                _messages.Remove(victim);
                dropped.Add(victim);
            }

            Save();
        }

        foreach (OutboxMessage m in dropped)
        {
            _logger.LogWarning("The e-mail outbox is full ({Capacity} messages): dropped '{Subject}' of {Created:u}.",
                               _capacity, m.Subject, m.CreatedAt);
        }

        return dropped;
    }

    public void Remove(OutboxMessage message)
    {
        lock (_lock)
        {
            if (_messages.Remove(message))
            {
                Save();
            }
        }
    }

    /// <summary>Records a failed attempt and schedules the next one.</summary>
    public void RecordFailure(OutboxMessage message, string error, DateTimeOffset now)
    {
        lock (_lock)
        {
            message.Attempts++;
            message.LastError = error;
            message.NextAttemptAt = now + RetryDelay(message.Attempts);
            Save();
        }
    }

    /// <summary>Postpones every message due before <paramref name="until"/> (the server is unreachable).</summary>
    public void Postpone(DateTimeOffset until)
    {
        lock (_lock)
        {
            bool changed = false;
            foreach (OutboxMessage m in _messages.Where(m => m.NextAttemptAt < until))
            {
                m.NextAttemptAt = until;
                changed = true;
            }

            if (changed)
            {
                Save();
            }
        }
    }

    public IReadOnlyList<OutboxMessage> Clear()
    {
        lock (_lock)
        {
            List<OutboxMessage> all = _messages;
            _messages = [];
            if (all.Count > 0)
            {
                Save();
            }

            return all;
        }
    }

    /// <summary>When the next message is due, or null when the outbox is empty.</summary>
    public DateTimeOffset? NextDue()
    {
        lock (_lock)
        {
            return _messages.Count == 0 ? null : _messages.Min(m => m.NextAttemptAt);
        }
    }

    // A file edited by hand may hold nulls where the messages NutHub writes never do: such a message would stop
    // the start of the notification service, or every message queued after it.
    private static OutboxMessage Repair(OutboxMessage m)
    {
        m.Id ??= Guid.NewGuid().ToString("N");
        m.From ??= "";
        m.To = (m.To ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        m.Subject ??= "";
        m.TextBody ??= "";
        m.HtmlBody ??= "";
        return m;
    }

    // Called under the lock. Written to a temporary file then renamed, so a power cut never leaves half a file.
    private void Save()
    {
        try
        {
            string? directory = Path.GetDirectoryName(_path);
            if (directory is not null && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
                FilePermissions.TryRestrictDirectory(directory);
            }

            if (_messages.Count == 0)
            {
                File.Delete(_path);
                return;
            }

            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_messages, NutHubJson.Compact));
            FilePermissions.TryRestrictFile(temp);
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The messages stay in memory and are still retried; only a restart would lose them.
            _logger.LogError(ex, "Could not save the e-mail outbox {Path}.", _path);
        }
    }
}
