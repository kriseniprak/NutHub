using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Core.Security;
using NutHub.Services.Notifications;
using NutHub.Services.Notifications.Email;
using NutHub.Services.Tests.Support;

namespace NutHub.Services.Tests.Notifications;

/// <summary>A notification service with an empty registry, a temporary outbox and the given SMTP transport.</summary>
public abstract class NotificationServiceTestBase : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly List<NotificationService> _services = [];

    internal FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 9, 22, 10, 0, 0, TimeSpan.Zero));

    internal TestConfigStore Config { get; } = new();

    internal EventHub Hub { get; } = new(NullLogger<EventHub>.Instance);

    internal AesSecretProtector Secrets { get; } = new(new byte[32]);

    public virtual void Dispose()
    {
        foreach (NotificationService s in _services)
        {
            s.Dispose();
        }

        _dir.Dispose();
        GC.SuppressFinalize(this);
    }

    internal NotificationService CreateService(ISmtpTransport? smtp = null, TimeProvider? time = null)
    {
        TimeProvider clock = time ?? Time;
        var registry = new UpsRegistry(Hub, clock, NullLoggerFactory.Instance, Config);
        var service = new NotificationService(Config, Secrets, registry, Hub, new TestHttpClientFactory(),
                                              smtp ?? new RecordingSmtpTransport(), _dir.File("outbox.json"), clock,
                                              NullLoggerFactory.Instance);
        service.Webhooks.RetryDelays = [TimeSpan.Zero];
        _services.Add(service);
        return service;
    }

    internal UpsEvent Event(UpsEventType type, string? ups = "rack1", string? message = null) =>
        UpsEvent.Create(type, Time.GetUtcNow(), ups, message ?? $"{ups}: {type}", "system");
}
