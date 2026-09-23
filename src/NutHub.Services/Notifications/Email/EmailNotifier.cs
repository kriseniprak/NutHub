using Microsoft.Extensions.Logging;
using MimeKit;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Core.Security;

namespace NutHub.Services.Notifications.Email;

/// <summary>
/// The e-mail channel. Events for the same recipients that arrive within <see cref="CoalesceWindow"/> share one
/// message (a power failure produces "on battery", "low battery", "shutdown pending"... within seconds). Messages
/// go through the persistent <see cref="EmailOutbox"/> and are retried until the server accepts them.
/// </summary>
internal sealed class EmailNotifier : IDisposable
{
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(10);

    private readonly IConfigStore _config;
    private readonly ISecretProtector _secrets;
    private readonly ISmtpTransport _transport;
    private readonly EmailOutbox _outbox;
    private readonly EmailComposer _composer;
    private readonly DeliveryLog _deliveries;
    private readonly Func<string, UpsReadings?> _readings;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly Dictionary<string, Batch> _batches = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly AsyncSignal _wake = new();
    private bool _disposed;

    public EmailNotifier(IConfigStore config, ISecretProtector secrets, ISmtpTransport transport, EmailOutbox outbox,
                         DeliveryLog deliveries, Func<string, UpsReadings?> readings, TimeProvider time, ILogger logger)
    {
        _config = config;
        _secrets = secrets;
        _transport = transport;
        _outbox = outbox;
        _composer = new EmailComposer(time);
        _deliveries = deliveries;
        _readings = readings;
        _time = time;
        _logger = logger;
    }

    public EmailOutbox Outbox => _outbox;

    /// <summary>Adds an event to the message being collected for the current recipients.</summary>
    public void Enqueue(NotificationContext context, EmailSettings settings)
    {
        List<string> recipients = settings.To.Select(t => t.Trim()).Where(t => t.Length > 0).ToList();
        if (recipients.Count == 0)
        {
            return;
        }

        string key = string.Join(',', recipients.Select(r => r.ToLowerInvariant()).Order(StringComparer.Ordinal));
        bool closeNow;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (!_batches.TryGetValue(key, out Batch? batch))
            {
                batch = new Batch(recipients);
                _batches[key] = batch;
                batch.Timer = _time.CreateTimer(_ => CloseBatch(key, batch), null, CoalesceWindow, Timeout.InfiniteTimeSpan);
            }

            batch.Items.Add(context);
            // The machine is going down: waiting for the window could lose the message.
            closeNow = context.Event.Type == UpsEventType.ShutdownStarted;
        }

        if (closeNow)
        {
            CloseBatch(key, null);
        }
    }

    /// <summary>
    /// Sends the waiting messages until the service stops: immediately when a message is queued, otherwise when the
    /// next retry is due.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "The e-mail outbox could not be processed.");
            }

            DateTimeOffset? next = _outbox.NextDue();
            TimeSpan delay = next is null
                ? Timeout.InfiniteTimeSpan
                : Max(next.Value - _time.GetUtcNow(), TimeSpan.FromMilliseconds(100));
            try
            {
                await _wake.WaitAsync(delay, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Closes every open batch now (used when stopping, so nothing collected is lost).</summary>
    public void CloseAllBatches()
    {
        List<string> keys;
        lock (_lock)
        {
            keys = _batches.Keys.ToList();
        }

        foreach (string key in keys)
        {
            CloseBatch(key, null);
        }
    }

    /// <summary>Sends the messages that are due. Also used by tests to avoid waiting on timers.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        await _sendGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await FlushCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Sends a test message right away, bypassing the outbox, and reports the server's answer.</summary>
    public async Task<CommandResult> SendTestAsync(NotificationContext context, CancellationToken cancellationToken)
    {
        NutHubConfig config = _config.Current;
        EmailSettings settings = config.Notifications.Email;
        if (string.IsNullOrWhiteSpace(settings.Host))
        {
            return CommandResult.InvalidArgument("No SMTP server is configured.");
        }

        if (settings.To.Count == 0 || string.IsNullOrWhiteSpace(settings.From))
        {
            return CommandResult.InvalidArgument("A sender and at least one recipient are required.");
        }

        OutboxMessage message = _composer.Compose([context], settings, config.Server, _readings);
        string? error = await TrySendAsync(message, settings, cancellationToken).ConfigureAwait(false) is { } failure
            ? failure.Message
            : null;
        _deliveries.Add(new NotificationDelivery(_time.GetUtcNow(), NotificationChannelKind.Email, Target(message),
                                                 context.Event.Type, null, error is null, error));
        return error is null ? CommandResult.Ok : CommandResult.Fail(error);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (Batch batch in _batches.Values)
            {
                batch.Timer?.Dispose();
            }

            _batches.Clear();
        }
    }

    private void CloseBatch(string key, Batch? expected)
    {
        Batch? batch;
        lock (_lock)
        {
            if (!_batches.TryGetValue(key, out batch) || (expected is not null && batch != expected))
            {
                return;
            }

            _batches.Remove(key);
            batch.Timer?.Dispose();
        }

        try
        {
            NutHubConfig config = _config.Current;
            // The recipients the events were collected for; the rest of the settings as they are now.
            EmailSettings settings = NutHubJson.Clone(config.Notifications.Email);
            settings.To = batch.Recipients;
            OutboxMessage message = _composer.Compose(batch.Items, settings, config.Server, _readings);
            foreach (OutboxMessage dropped in _outbox.Add(message))
            {
                _deliveries.Add(new NotificationDelivery(_time.GetUtcNow(), NotificationChannelKind.Email,
                                                         Target(dropped), dropped.EventType, dropped.Ups, false,
                                                         "Dropped: the outbox is full."));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not prepare the e-mail notification.");
        }

        _wake.Set();
    }

    private async Task FlushCoreAsync(CancellationToken cancellationToken)
    {
        EmailSettings settings = _config.Current.Notifications.Email;
        if (!settings.Enabled)
        {
            foreach (OutboxMessage m in _outbox.Clear())
            {
                _logger.LogInformation("E-mail notifications are disabled: discarded '{Subject}'.", m.Subject);
            }

            return;
        }

        DateTimeOffset now = _time.GetUtcNow();
        foreach (OutboxMessage message in _outbox.Snapshot().OrderBy(m => m.CreatedAt))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (now - message.CreatedAt > EmailOutbox.MaxAge)
            {
                _outbox.Remove(message);
                string reason = $"Given up after {message.Attempts} attempts in {EmailOutbox.MaxAge.TotalHours:0} hours: {message.LastError}";
                _logger.LogError("E-mail notification '{Subject}' dropped. {Reason}", message.Subject, reason);
                _deliveries.Add(new NotificationDelivery(now, NotificationChannelKind.Email, Target(message),
                                                         message.EventType, message.Ups, false, reason));
                continue;
            }

            if (message.NextAttemptAt > now)
            {
                continue;
            }

            SmtpSendException? failure = await TrySendAsync(message, settings, cancellationToken).ConfigureAwait(false);
            now = _time.GetUtcNow();
            if (failure is null)
            {
                _outbox.Remove(message);
                _logger.LogInformation("E-mail notification '{Subject}' sent to {To}.", message.Subject, Target(message));
                _deliveries.Add(new NotificationDelivery(now, NotificationChannelKind.Email, Target(message),
                                                         message.EventType, message.Ups, true,
                                                         message.Attempts > 0 ? $"Sent after {message.Attempts + 1} attempts." : null));
                continue;
            }

            if (failure.Kind == SmtpFailureKind.Permanent)
            {
                _outbox.Remove(message);
                _logger.LogError("E-mail notification '{Subject}' refused by the server: {Error}", message.Subject, failure.Message);
                _deliveries.Add(new NotificationDelivery(now, NotificationChannelKind.Email, Target(message),
                                                         message.EventType, message.Ups, false, failure.Message));
                continue;
            }

            bool firstFailure = message.Attempts == 0;
            _outbox.RecordFailure(message, failure.Message, now);
            TimeSpan retryIn = message.NextAttemptAt - now;
            _logger.LogWarning("E-mail notification '{Subject}' not sent ({Error}); next attempt in {Delay:0} s.",
                               message.Subject, failure.Message, retryIn.TotalSeconds);
            if (firstFailure)
            {
                _deliveries.Add(new NotificationDelivery(now, NotificationChannelKind.Email, Target(message),
                                                         message.EventType, message.Ups, false,
                                                         $"{failure.Message} (queued, retrying for up to {EmailOutbox.MaxAge.TotalHours:0} hours)"));
            }

            if (failure.Kind is SmtpFailureKind.Connection or SmtpFailureKind.Authentication)
            {
                // The server is down or refuses us: the other messages would fail the same way. One attempt per
                // round also keeps a wrong password from locking the account.
                _outbox.Postpone(message.NextAttemptAt);
                return;
            }
        }
    }

    private async Task<SmtpSendException?> TrySendAsync(OutboxMessage message, EmailSettings settings,
                                                        CancellationToken cancellationToken)
    {
        MimeMessage mime;
        try
        {
            mime = EmailComposer.ToMime(message);
        }
        catch (ParseException ex)
        {
            return new SmtpSendException(SmtpFailureKind.Permanent, $"Invalid e-mail address: {ex.Message}", ex);
        }

        string? password;
        try
        {
            password = _secrets.Unprotect(settings.Password);
        }
        catch (Exception ex)
        {
            return new SmtpSendException(SmtpFailureKind.Authentication,
                                         "The SMTP password cannot be decrypted (was the secret key replaced?). Enter it again.", ex);
        }

        var endpoint = new SmtpEndpoint(settings.Host.Trim(), settings.Port, settings.Security,
                                        string.IsNullOrWhiteSpace(settings.Username) ? null : settings.Username.Trim(),
                                        password);
        try
        {
            await _transport.SendAsync(endpoint, mime, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (SmtpSendException ex)
        {
            return ex;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new SmtpSendException(SmtpFailureKind.Transient, ex.Message, ex);
        }
    }

    private static string Target(OutboxMessage message) => string.Join(", ", message.To);

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    private sealed class Batch(List<string> recipients)
    {
        public List<string> Recipients { get; } = recipients;

        public List<NotificationContext> Items { get; } = [];

        public ITimer? Timer { get; set; }
    }
}
