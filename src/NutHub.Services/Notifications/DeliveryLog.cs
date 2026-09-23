using NutHub.Core.Abstractions;

namespace NutHub.Services.Notifications;

/// <summary>The last deliveries (successful or not), newest first, for the web panel.</summary>
internal sealed class DeliveryLog
{
    public const int Capacity = 200;

    private readonly LinkedList<NotificationDelivery> _items = new();
    private readonly object _lock = new();

    public void Add(NotificationDelivery delivery)
    {
        lock (_lock)
        {
            _items.AddFirst(delivery);
            while (_items.Count > Capacity)
            {
                _items.RemoveLast();
            }
        }
    }

    public IReadOnlyList<NotificationDelivery> Snapshot()
    {
        lock (_lock)
        {
            return _items.ToList();
        }
    }
}
