using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MimeKit;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Core.Security;
using NutHub.Services.Notifications;
using NutHub.Services.Notifications.Email;
using NutHub.Services.Tests.Support;

namespace NutHub.Services.Tests.Notifications;

public sealed class EmailNotifierTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero));
    private readonly TempDirectory _dir = new();
    private readonly TestConfigStore _config = new();
    private readonly RecordingSmtpTransport _smtp = new();
    private readonly DeliveryLog _deliveries = new();
    private readonly List<EmailNotifier> _notifiers = [];

    public EmailNotifierTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("Test+2", TimeSpan.FromHours(2), "Test+2", "Test+2"));
        _config.Update(c =>
        {
            c.Server.Name = "nuthub-lab";
            c.Server.Location = "Rack room";
            c.Notifications.Email = new EmailSettings
            {
                Enabled = true,
                Host = "smtp.example.com",
                Port = 587,
                Username = "ups",
                Password = "secret",
                From = "nuthub@example.com",
                To = ["ops@example.com", "boss@example.com"],
            };
        });
    }

    public void Dispose()
    {
        foreach (EmailNotifier n in _notifiers)
        {
            n.Dispose();
        }

        _dir.Dispose();
    }

    [Fact]
    public async Task Events_within_ten_seconds_share_one_message()
    {
        EmailNotifier notifier = Create();
        notifier.Enqueue(Context(UpsEventType.OnBattery, "rack1 is on battery."), Email);
        _time.Advance(TimeSpan.FromSeconds(4));
        notifier.Enqueue(Context(UpsEventType.LowBattery, "rack1 has a low battery."), Email);
        _time.Advance(TimeSpan.FromSeconds(5.9));
        await notifier.FlushAsync(CancellationToken.None);
        Assert.Empty(_smtp.Sent);

        _time.Advance(TimeSpan.FromSeconds(0.2));
        await notifier.FlushAsync(CancellationToken.None);

        MimeMessage mail = Assert.Single(_smtp.Sent);
        Assert.Equal("[NutHub] rack1: rack1 has a low battery. (+1 more)", mail.Subject);
        Assert.Equal(new[] { "ops@example.com", "boss@example.com" }, mail.To.Mailboxes.Select(m => m.Address));
        Assert.Equal("nuthub@example.com", mail.From.Mailboxes.Single().Address);
        Assert.Contains("rack1 is on battery.", mail.TextBody);
        Assert.Contains("rack1 has a low battery.", mail.TextBody);
        Assert.Contains("2026-09-22 10:00:00 UTC / 2026-09-22 12:00:00 server time (UTC+02:00)", mail.TextBody);
        Assert.Contains("nuthub-lab (Rack room)", mail.TextBody);
        Assert.Contains("Battery charge", mail.TextBody);
        Assert.Contains("<b>rack1 has a low battery.</b>", mail.HtmlBody);
        Assert.Equal("auto-generated", mail.Headers["Auto-Submitted"]);
        Assert.Equal(new SmtpEndpoint("smtp.example.com", 587, SmtpSecurity.Auto, "ups", "secret"), _smtp.LastEndpoint);

        NotificationDelivery delivery = Assert.Single(_deliveries.Snapshot());
        Assert.True(delivery.Success);
        Assert.Equal(UpsEventType.LowBattery, delivery.EventType);
    }

    [Fact]
    public async Task Html_body_escapes_event_text()
    {
        EmailNotifier notifier = Create();
        notifier.Enqueue(Context(UpsEventType.OnBattery, "<script>alert(1)</script> & co"), Email);
        _time.Advance(TimeSpan.FromSeconds(10));
        await notifier.FlushAsync(CancellationToken.None);

        MimeMessage mail = Assert.Single(_smtp.Sent);
        Assert.DoesNotContain("<script>", mail.HtmlBody);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt; &amp; co", mail.HtmlBody);
    }

    [Fact]
    public async Task Events_farther_apart_make_separate_messages()
    {
        EmailNotifier notifier = Create();
        notifier.Enqueue(Context(UpsEventType.OnBattery, "on battery"), Email);
        _time.Advance(TimeSpan.FromSeconds(11));
        notifier.Enqueue(Context(UpsEventType.Online, "on line"), Email);
        _time.Advance(TimeSpan.FromSeconds(11));
        await notifier.FlushAsync(CancellationToken.None);

        Assert.Equal(new[] { "[NutHub] rack1: on battery", "[NutHub] rack1: on line" }, _smtp.Sent.Select(m => m.Subject));
    }

    [Fact]
    public async Task Shutdown_started_closes_the_window_at_once()
    {
        EmailNotifier notifier = Create();
        notifier.Enqueue(Context(UpsEventType.LowBattery, "low battery"), Email);
        notifier.Enqueue(Context(UpsEventType.ShutdownStarted, "nuthub-lab is shutting down.", ups: null), Email);
        await notifier.FlushAsync(CancellationToken.None);

        MimeMessage mail = Assert.Single(_smtp.Sent);
        Assert.StartsWith("[NutHub] nuthub-lab: ", mail.Subject);
        Assert.Equal(MessagePriority.Urgent, mail.Priority);
    }

    [Fact]
    public async Task Unreachable_server_is_retried_with_back_off_and_the_outbox_survives_a_restart()
    {
        EmailNotifier notifier = Create();
        _smtp.Failure = new SmtpSendException(SmtpFailureKind.Connection, "Cannot reach the SMTP server: refused");
        notifier.Enqueue(Context(UpsEventType.OnBattery, "on battery"), Email);
        _time.Advance(TimeSpan.FromSeconds(10));
        await notifier.FlushAsync(CancellationToken.None);
        Assert.Equal(1, _smtp.Attempts);
        Assert.Equal(1, notifier.Outbox.Count);
        NotificationDelivery queued = Assert.Single(_deliveries.Snapshot());
        Assert.False(queued.Success);
        Assert.Contains("queued", queued.Error);

        // Not before 30 s, then 1 min later.
        _time.Advance(TimeSpan.FromSeconds(29));
        await notifier.FlushAsync(CancellationToken.None);
        Assert.Equal(1, _smtp.Attempts);
        _time.Advance(TimeSpan.FromSeconds(1));
        await notifier.FlushAsync(CancellationToken.None);
        Assert.Equal(2, _smtp.Attempts);
        _time.Advance(TimeSpan.FromSeconds(59));
        await notifier.FlushAsync(CancellationToken.None);
        Assert.Equal(2, _smtp.Attempts);
        _time.Advance(TimeSpan.FromSeconds(1));
        await notifier.FlushAsync(CancellationToken.None);
        Assert.Equal(3, _smtp.Attempts);
        Assert.Single(_deliveries.Snapshot()); // one failure entry per message, not one per attempt

        // NutHub restarts; the network is back.
        notifier.Dispose();
        _smtp.Failure = null;
        EmailNotifier restarted = Create();
        restarted.Outbox.Load();
        Assert.Equal(1, restarted.Outbox.Count);
        _time.Advance(TimeSpan.FromMinutes(2));
        await restarted.FlushAsync(CancellationToken.None);

        Assert.Equal("[NutHub] rack1: on battery", Assert.Single(_smtp.Sent).Subject);
        Assert.Equal(0, restarted.Outbox.Count);
        Assert.False(File.Exists(_dir.File("outbox.json")));
        Assert.Contains("after 4 attempts", _deliveries.Snapshot()[0].Error);
    }

    [Fact]
    public async Task One_unreachable_attempt_per_round_for_the_whole_outbox()
    {
        EmailNotifier notifier = Create();
        _smtp.Failure = new SmtpSendException(SmtpFailureKind.Connection, "down");
        notifier.Enqueue(Context(UpsEventType.OnBattery, "one"), Email);
        _time.Advance(TimeSpan.FromSeconds(11));
        notifier.Enqueue(Context(UpsEventType.Online, "two"), Email);
        _time.Advance(TimeSpan.FromSeconds(11));

        await notifier.FlushAsync(CancellationToken.None);

        Assert.Equal(1, _smtp.Attempts);
        Assert.Equal(2, notifier.Outbox.Count);
    }

    [Fact]
    public async Task Messages_are_dropped_after_24_hours()
    {
        EmailNotifier notifier = Create();
        _smtp.Failure = new SmtpSendException(SmtpFailureKind.Connection, "down");
        notifier.Enqueue(Context(UpsEventType.OnBattery, "on battery"), Email);
        _time.Advance(TimeSpan.FromSeconds(10));
        for (int i = 0; i < 150; i++)
        {
            await notifier.FlushAsync(CancellationToken.None);
            _time.Advance(TimeSpan.FromMinutes(10));
        }

        await notifier.FlushAsync(CancellationToken.None);

        Assert.Equal(0, notifier.Outbox.Count);
        Assert.InRange(_smtp.Attempts, 140, 150);
        Assert.Contains("Given up", _deliveries.Snapshot()[0].Error);
    }

    [Fact]
    public async Task Permanent_refusal_is_not_retried()
    {
        EmailNotifier notifier = Create();
        _smtp.Failure = new SmtpSendException(SmtpFailureKind.Permanent, "SMTP error 550: No such user");
        notifier.Enqueue(Context(UpsEventType.OnBattery, "on battery"), Email);
        _time.Advance(TimeSpan.FromSeconds(10));
        await notifier.FlushAsync(CancellationToken.None);

        Assert.Equal(0, notifier.Outbox.Count);
        Assert.Equal("SMTP error 550: No such user", Assert.Single(_deliveries.Snapshot()).Error);
    }

    [Fact]
    public async Task Disabling_email_discards_the_outbox()
    {
        EmailNotifier notifier = Create();
        _smtp.Failure = new SmtpSendException(SmtpFailureKind.Connection, "down");
        notifier.Enqueue(Context(UpsEventType.OnBattery, "on battery"), Email);
        _time.Advance(TimeSpan.FromSeconds(10));
        await notifier.FlushAsync(CancellationToken.None);
        Assert.Equal(1, notifier.Outbox.Count);

        _config.Update(c => c.Notifications.Email.Enabled = false);
        await notifier.FlushAsync(CancellationToken.None);

        Assert.Equal(0, notifier.Outbox.Count);
    }

    [Fact]
    public async Task Run_loop_sends_when_the_window_closes()
    {
        EmailNotifier notifier = Create();
        using var cts = new CancellationTokenSource();
        Task loop = notifier.RunAsync(cts.Token);

        notifier.Enqueue(Context(UpsEventType.OnBattery, "on battery"), Email);
        _time.Advance(TimeSpan.FromSeconds(10));
        await Wait.UntilAsync(() => _smtp.Sent.Count == 1, "the e-mail");

        await cts.CancelAsync();
        await loop;
    }

    [Fact]
    public async Task Close_all_batches_keeps_collected_events_for_the_next_start()
    {
        EmailNotifier notifier = Create();
        notifier.Enqueue(Context(UpsEventType.OnBattery, "on battery"), Email);
        notifier.CloseAllBatches();
        notifier.Dispose();

        EmailNotifier restarted = Create();
        restarted.Outbox.Load();
        await restarted.FlushAsync(CancellationToken.None);

        Assert.Single(_smtp.Sent);
    }

    [Fact]
    public async Task A_damaged_message_in_the_outbox_does_not_block_the_others()
    {
        File.WriteAllText(_dir.File("outbox.json"), """
            [{"createdAt": "2026-09-22T09:59:00+00:00", "from": "nuthub@example.com", "to": ["ops@example.com"], "subject": null},
             {"createdAt": "2026-09-22T09:59:30+00:00", "from": "nuthub@example.com", "to": ["ops@example.com"],
              "subject": "Low battery", "textBody": "t", "htmlBody": "<p>t</p>", "critical": true}]
            """);
        EmailNotifier notifier = Create();
        notifier.Outbox.Load();

        await notifier.FlushAsync(CancellationToken.None);

        Assert.Contains(_smtp.Sent, m => m.Subject == "Low battery");
        Assert.Equal(0, notifier.Outbox.Count);
    }

    private EmailSettings Email => _config.Current.Notifications.Email;

    private EmailNotifier Create()
    {
        var outbox = new EmailOutbox(_dir.File("outbox.json"), NullLogger.Instance);
        var notifier = new EmailNotifier(_config, new AesSecretProtector(new byte[32]), _smtp, outbox, _deliveries,
                                         name => new UpsReadings(name, "OB DISCHRG", "76", "900", "30", "0", true),
                                         _time, NullLogger.Instance);
        _notifiers.Add(notifier);
        return notifier;
    }

    private NotificationContext Context(UpsEventType type, string message, string? ups = "rack1") =>
        new(UpsEvent.Create(type, _time.GetUtcNow(), ups, message), "nuthub-lab", "Rack room", null);
}
