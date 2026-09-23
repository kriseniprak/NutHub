using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Services.Notifications.Email;
using NutHub.Services.Tests.Support;

namespace NutHub.Services.Tests.Notifications;

public sealed class EmailOutboxTests : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 9, 22, 10, 0, 0, TimeSpan.Zero);
    private readonly TempDirectory _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void Messages_survive_a_restart()
    {
        string path = Path.Combine(_dir.Path, "notifications", "outbox.json");
        var outbox = new EmailOutbox(path, NullLogger.Instance);
        outbox.Add(Message("one", critical: false));
        outbox.RecordFailure(outbox.Snapshot()[0], "down", Start);

        var reloaded = new EmailOutbox(path, NullLogger.Instance);
        reloaded.Load();

        OutboxMessage m = Assert.Single(reloaded.Snapshot());
        Assert.Equal("one", m.Subject);
        Assert.Equal(1, m.Attempts);
        Assert.Equal("down", m.LastError);
        Assert.Equal(Start.AddSeconds(30), m.NextAttemptAt);
        Assert.Equal(new[] { "ops@example.com" }, m.To);
    }

    [Fact]
    public void The_cap_drops_the_oldest_non_critical_message_first()
    {
        var outbox = new EmailOutbox(_dir.File("outbox.json"), NullLogger.Instance, capacity: 3);
        outbox.Add(Message("critical-1", critical: true));
        outbox.Add(Message("normal-1", critical: false));
        outbox.Add(Message("normal-2", critical: false));

        IReadOnlyList<OutboxMessage> dropped = outbox.Add(Message("normal-3", critical: false));

        Assert.Equal("normal-1", Assert.Single(dropped).Subject);
        Assert.Equal(new[] { "critical-1", "normal-2", "normal-3" }, outbox.Snapshot().Select(m => m.Subject));

        outbox.Add(Message("critical-2", critical: true));
        outbox.Add(Message("critical-3", critical: true));
        outbox.Add(Message("critical-4", critical: true));
        Assert.Equal(new[] { "critical-2", "critical-3", "critical-4" }, outbox.Snapshot().Select(m => m.Subject));
    }

    [Theory]
    [InlineData(1, 30)]
    [InlineData(2, 60)]
    [InlineData(3, 120)]
    [InlineData(4, 300)]
    [InlineData(5, 600)]
    [InlineData(40, 600)]
    public void Retry_delays_grow_to_ten_minutes(int failures, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), EmailOutbox.RetryDelay(failures));

    [Fact]
    public void A_damaged_file_is_set_aside()
    {
        string path = _dir.File("outbox.json");
        File.WriteAllText(path, "{ not json");
        var outbox = new EmailOutbox(path, NullLogger.Instance);

        outbox.Load();

        Assert.Equal(0, outbox.Count);
        Assert.True(File.Exists(path + ".damaged"));
    }

    [Fact]
    public void Damaged_entries_are_repaired_or_dropped()
    {
        string path = _dir.File("outbox.json");
        File.WriteAllText(path, """
            [null,
             {"subject": "no recipients", "to": null},
             {"subject": "only blanks", "to": [null, " "]},
             {"id": null, "from": null, "to": ["ops@example.com", null], "subject": null, "textBody": null, "htmlBody": null},
             {"from": "nuthub@example.com", "to": ["ops@example.com"], "subject": "fine", "textBody": "t", "htmlBody": "h"}]
            """);
        var outbox = new EmailOutbox(path, NullLogger.Instance);

        outbox.Load();

        Assert.Equal(new[] { "", "fine" }, outbox.Snapshot().Select(m => m.Subject));
        OutboxMessage repaired = outbox.Snapshot()[0];
        Assert.Equal(new[] { "ops@example.com" }, repaired.To);
        Assert.Equal("", repaired.From);
        Assert.Equal("", repaired.TextBody);
        Assert.Equal("", repaired.HtmlBody);
        Assert.False(string.IsNullOrEmpty(repaired.Id));
    }

    private static OutboxMessage Message(string subject, bool critical) => new()
    {
        CreatedAt = Start,
        NextAttemptAt = Start,
        From = "nuthub@example.com",
        To = ["ops@example.com"],
        Subject = subject,
        TextBody = "text",
        HtmlBody = "<p>text</p>",
        Critical = critical,
    };
}
