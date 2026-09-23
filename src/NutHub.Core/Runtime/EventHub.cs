using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NutHub.Core.Configuration;
using NutHub.Core.Model;

namespace NutHub.Core.Runtime;

/// <summary>Base of everything published on the <see cref="EventHub"/>.</summary>
public abstract record HubMessage;

/// <summary>The snapshot of a UPS changed (data, driver state, FSD...). <see cref="Previous"/> is null for a new UPS.</summary>
public sealed record SnapshotChangedMessage(UpsSnapshot? Previous, UpsSnapshot Current) : HubMessage;

/// <summary>A UPS was removed from the configuration.</summary>
public sealed record UpsRemovedMessage(string Name) : HubMessage;

/// <summary>
/// Something worth logging or notifying happened. Published before the event store records it; the event
/// recorder then publishes <see cref="EventRecordedMessage"/> with the stored event (which has its Id).
/// </summary>
public sealed record UpsEventMessage(UpsEvent Event) : HubMessage;

/// <summary>An event was stored in the event log.</summary>
public sealed record EventRecordedMessage(UpsEvent Event) : HubMessage;

/// <summary>NUT clients connected, logged in or left.</summary>
public sealed record NutClientsChangedMessage : HubMessage;

/// <summary>The configuration changed.</summary>
public sealed record ConfigChangedMessage(NutHubConfig Previous, NutHubConfig Current, CommandOrigin Origin,
                                          string Description) : HubMessage;

/// <summary>The host protection (automatic shutdown of this machine) changed state.</summary>
public sealed record HostProtectionChangedMessage : HubMessage;

/// <summary>
/// In-process publish/subscribe. Handlers subscribed with <see cref="Subscribe"/> run synchronously on the
/// publishing thread and must be quick; <see cref="SubscribeChannel"/> gives an asynchronous, bounded queue for slow
/// consumers (web streams, notifications). A consumer that falls behind loses its oldest messages, never blocks the
/// publisher.
/// </summary>
public sealed class EventHub(ILogger<EventHub> logger)
{
    private readonly object _lock = new();
    private Action<HubMessage>[] _handlers = [];

    public IDisposable Subscribe(Action<HubMessage> handler)
    {
        lock (_lock)
        {
            _handlers = [.. _handlers, handler];
        }

        return new Unsubscriber(() =>
        {
            lock (_lock)
            {
                // One entry only: the same handler subscribed twice is two subscriptions.
                int index = Array.IndexOf(_handlers, handler);
                if (index >= 0)
                {
                    _handlers = [.. _handlers[..index], .. _handlers[(index + 1)..]];
                }
            }
        });
    }

    /// <summary>
    /// Subscribes a bounded queue. Dispose the returned subscription to stop; its reader then completes.
    /// </summary>
    public ChannelSubscription SubscribeChannel(int capacity = 512, Func<HubMessage, bool>? filter = null)
    {
        var channel = Channel.CreateBounded<HubMessage>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        IDisposable inner = Subscribe(message =>
        {
            if (filter is null || filter(message))
            {
                channel.Writer.TryWrite(message);
            }
        });
        return new ChannelSubscription(channel.Reader, () =>
        {
            inner.Dispose();
            channel.Writer.TryComplete();
        });
    }

    public void Publish(HubMessage message)
    {
        foreach (Action<HubMessage> handler in Volatile.Read(ref _handlers))
        {
            try
            {
                handler(message);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "An event hub subscriber failed on {Message}.", message.GetType().Name);
            }
        }
    }

    private sealed class Unsubscriber(Action action) : IDisposable
    {
        private Action? _action = action;

        public void Dispose() => Interlocked.Exchange(ref _action, null)?.Invoke();
    }
}

/// <summary>A queue of hub messages; dispose to unsubscribe.</summary>
public sealed class ChannelSubscription(ChannelReader<HubMessage> reader, Action dispose) : IDisposable
{
    private Action? _dispose = dispose;

    public ChannelReader<HubMessage> Reader { get; } = reader;

    public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
}
