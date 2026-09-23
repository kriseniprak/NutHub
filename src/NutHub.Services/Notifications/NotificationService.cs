using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NutHub.Core;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Core.Security;
using NutHub.Services.Notifications.Commands;
using NutHub.Services.Notifications.Email;
using NutHub.Services.Notifications.Webhooks;

namespace NutHub.Services.Notifications;

/// <summary>
/// Sends notifications for the events of the <see cref="EventHub"/> through e-mail, webhooks and command hooks.
/// Each enabled channel whose event list contains the event gets a delivery, with the configuration current at
/// that moment. Deliveries of one target run in order and never hold up the others.
/// </summary>
public sealed class NotificationService : BackgroundService, INotificationService
{
    private const string EmailKey = "email";

    private readonly IConfigStore _config;
    private readonly IUpsRegistry _registry;
    private readonly EventHub _hub;
    private readonly TimeProvider _time;
    private readonly ILogger<NotificationService> _logger;
    private readonly DeliveryLog _deliveries = new();
    private readonly SerialQueue _queue = new();
    private readonly FloodGuard _flood;
    private readonly EmailNotifier _email;
    private readonly WebhookSender _webhooks;
    private readonly CommandHookRunner _commands;
    private readonly ChannelSubscription _subscription;
    private CancellationToken _stopping = CancellationToken.None;

    public NotificationService(IConfigStore config, ISecretProtector secrets, IUpsRegistry registry, EventHub hub,
                               IHttpClientFactory httpClientFactory, NutHubPaths paths, TimeProvider time,
                               ILoggerFactory loggerFactory)
        : this(config, secrets, registry, hub, httpClientFactory, new MailKitSmtpTransport(),
               Path.Combine(paths.DataDirectory, "notifications", "outbox.json"), time, loggerFactory)
    {
    }

    internal NotificationService(IConfigStore config, ISecretProtector secrets, IUpsRegistry registry, EventHub hub,
                                 IHttpClientFactory httpClientFactory, ISmtpTransport smtp, string outboxPath,
                                 TimeProvider time, ILoggerFactory loggerFactory)
    {
        _config = config;
        _registry = registry;
        _hub = hub;
        _time = time;
        _logger = loggerFactory.CreateLogger<NotificationService>();
        _flood = new FloodGuard(time, OnFloodSummary);
        var outbox = new EmailOutbox(outboxPath, _logger);
        _email = new EmailNotifier(config, secrets, smtp, outbox, _deliveries, name => UpsReadings.From(registry, name),
                                   time, _logger);
        _webhooks = new WebhookSender(httpClientFactory, secrets, time, _logger);
        _commands = new CommandHookRunner(_logger);
        // Subscribed here rather than when started: the host creates every hosted service before starting the
        // first one, so the events of drivers that start earlier (a UPS already on battery) are not missed.
        _subscription = hub.SubscribeChannel(2048, m => m is UpsEventMessage);
    }

    public IReadOnlyList<NotificationDelivery> RecentDeliveries => _deliveries.Snapshot();

    internal EmailNotifier Email => _email;

    internal WebhookSender Webhooks => _webhooks;

    public async Task<CommandResult> SendTestAsync(NotificationChannelKind channel, string? id,
                                                   CancellationToken cancellationToken = default)
    {
        NutHubConfig config = _config.Current;
        UpsEvent e = UpsEvent.Create(
            UpsEventType.NotificationTest, _time.GetUtcNow(), null,
            $"Test notification from the NutHub server {config.Server.Name}: this {channel.ToString().ToLowerInvariant()} channel works.",
            "test", new Dictionary<string, string> { ["test"] = "true" }, EventSeverity.Info);
        var context = NotificationContext.Create(e, config, _registry, isTest: true);

        try
        {
            switch (channel)
            {
                case NotificationChannelKind.Email:
                    return await _email.SendTestAsync(context, cancellationToken).ConfigureAwait(false);

                case NotificationChannelKind.Webhook:
                {
                    WebhookSettings? hook = config.Notifications.Webhooks.FirstOrDefault(h => h.Id == id);
                    if (hook is null)
                    {
                        return CommandResult.InvalidArgument($"There is no webhook with the id '{id}'.");
                    }

                    // One attempt: the person pressing the button wants the answer now.
                    DeliveryResult result = await _webhooks.SendAsync(hook, context, 1, cancellationToken).ConfigureAwait(false);
                    Record(NotificationChannelKind.Webhook, WebhookSender.DisplayName(hook), context, result);
                    return ToCommandResult(result);
                }

                case NotificationChannelKind.Command:
                {
                    CommandHookSettings? hook = config.Notifications.Commands.FirstOrDefault(h => h.Id == id);
                    if (hook is null)
                    {
                        return CommandResult.InvalidArgument($"There is no command with the id '{id}'.");
                    }

                    DeliveryResult result = await _commands.RunAsync(hook, context, cancellationToken).ConfigureAwait(false);
                    Record(NotificationChannelKind.Command, CommandHookRunner.DisplayName(hook), context, result);
                    return ToCommandResult(result);
                }

                default:
                    return CommandResult.InvalidArgument($"Unknown channel '{channel}'.");
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The test notification through {Channel} failed.", channel);
            return CommandResult.Fail(ex.Message);
        }
    }

    public override void Dispose()
    {
        _subscription.Dispose();
        _flood.Dispose();
        _email.Dispose();
        base.Dispose();
    }

    /// <summary>Routes one event to every channel that wants it (tests call it directly, without the hub).</summary>
    internal void Dispatch(UpsEvent e)
    {
        NutHubConfig config = _config.Current;
        NotificationSettings settings = config.Notifications;
        NotificationContext? context = null;
        NotificationContext Context() => context ??= NotificationContext.Create(e, config, _registry);

        EmailSettings email = settings.Email;
        if (email.Enabled && email.To.Count > 0 && Wants(email.Events, settings, e.Type))
        {
            foreach (UpsEvent admitted in _flood.Admit(EmailKey, e))
            {
                _email.Enqueue(admitted == e ? Context() : NotificationContext.Create(admitted, config, _registry), email);
            }
        }

        foreach (WebhookSettings hook in settings.Webhooks)
        {
            if (hook.Enabled && Wants(hook.Events, settings, e.Type))
            {
                foreach (UpsEvent admitted in _flood.Admit(WebhookKey(hook), e))
                {
                    QueueWebhook(hook, admitted == e ? Context() : NotificationContext.Create(admitted, config, _registry));
                }
            }
        }

        foreach (CommandHookSettings hook in settings.Commands)
        {
            if (hook.Enabled && Wants(hook.Events, settings, e.Type))
            {
                foreach (UpsEvent admitted in _flood.Admit(CommandKey(hook), e))
                {
                    QueueCommand(hook, admitted == e ? Context() : NotificationContext.Create(admitted, config, _registry));
                }
            }
        }
    }

    /// <summary>Completes when the queued webhook and command deliveries have run (tests, shutdown).</summary>
    internal Task WhenIdleAsync() => _queue.WhenIdleAsync();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stopping = stoppingToken;
        await Task.Yield();

        _email.Outbox.Load();
        Task emailLoop = _email.RunAsync(stoppingToken);
        try
        {
            await foreach (HubMessage message in _subscription.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    Dispatch(((UpsEventMessage)message).Event);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not dispatch the notification of an event.");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            // Whatever was being collected goes to the outbox file, to be sent at the next start.
            _email.CloseAllBatches();
            await emailLoop.ConfigureAwait(false);
        }
    }

    private static bool Wants(List<UpsEventType>? own, NotificationSettings settings, UpsEventType type) =>
        (own ?? settings.Events).Contains(type);

    private static string WebhookKey(WebhookSettings hook) => "webhook:" + hook.Id;

    private static string CommandKey(CommandHookSettings hook) => "command:" + hook.Id;

    private void QueueWebhook(WebhookSettings hook, NotificationContext context)
    {
        string target = WebhookSender.DisplayName(hook);
        if (!_queue.TryEnqueue(WebhookKey(hook), async () =>
            {
                DeliveryResult result = await _webhooks.SendAsync(hook, context, 3, _stopping).ConfigureAwait(false);
                if (!result.Success)
                {
                    _logger.LogWarning("Webhook {Name} failed for {Type}: {Error}", target, context.TypeName, result.Error);
                }

                Record(NotificationChannelKind.Webhook, target, context, result);
            }, FloodGuard.IsExempt(context.Event.Type)))
        {
            Record(NotificationChannelKind.Webhook, target, context,
                   DeliveryResult.Fail("Dropped: too many deliveries are already waiting for this webhook."));
        }
    }

    private void QueueCommand(CommandHookSettings hook, NotificationContext context)
    {
        string target = CommandHookRunner.DisplayName(hook);
        if (!_queue.TryEnqueue(CommandKey(hook), async () =>
            {
                DeliveryResult result = await _commands.RunAsync(hook, context, _stopping).ConfigureAwait(false);
                Record(NotificationChannelKind.Command, target, context, result);
            }, FloodGuard.IsExempt(context.Event.Type)))
        {
            Record(NotificationChannelKind.Command, target, context,
                   DeliveryResult.Fail("Dropped: too many runs are already waiting for this command."));
        }
    }

    private void OnFloodSummary(string channel, UpsEvent summary)
    {
        try
        {
            NutHubConfig config = _config.Current;
            NotificationSettings settings = config.Notifications;
            var context = NotificationContext.Create(summary, config, _registry);
            if (channel == EmailKey)
            {
                if (settings.Email.Enabled)
                {
                    _email.Enqueue(context, settings.Email);
                }
            }
            else if (settings.Webhooks.FirstOrDefault(h => WebhookKey(h) == channel && h.Enabled) is { } hook)
            {
                QueueWebhook(hook, context);
            }
            else if (settings.Commands.FirstOrDefault(h => CommandKey(h) == channel && h.Enabled) is { } command)
            {
                QueueCommand(command, context);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not send the flood protection summary for {Channel}.", channel);
        }
    }

    private void Record(NotificationChannelKind channel, string target, NotificationContext context, DeliveryResult result) =>
        _deliveries.Add(new NotificationDelivery(_time.GetUtcNow(), channel, target, context.Event.Type,
                                                 context.Event.Ups, result.Success, result.Error));

    private static CommandResult ToCommandResult(DeliveryResult result) =>
        result.Success ? CommandResult.Ok : CommandResult.Fail(result.Error ?? "Failed.");
}
