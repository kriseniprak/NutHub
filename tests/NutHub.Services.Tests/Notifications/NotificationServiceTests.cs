using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NutHub.Core;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Services.HostProtection;
using NutHub.Services.Notifications;
using NutHub.Services.Tests.Support;

namespace NutHub.Services.Tests.Notifications;

public sealed class NotificationServiceTests : NotificationServiceTestBase
{
    [Fact]
    public async Task Email_uses_its_own_event_list_when_it_has_one()
    {
        var smtp = new RecordingSmtpTransport();
        Config.Update(c =>
        {
            c.Notifications.Events = [UpsEventType.OnBattery];
            c.Notifications.Email = new EmailSettings
            {
                Enabled = true,
                Host = "smtp.example.com",
                From = "nuthub@example.com",
                To = ["ops@example.com"],
                Events = [UpsEventType.ReplaceBattery],
            };
        });
        var service = CreateService(smtp);

        service.Dispatch(Event(UpsEventType.OnBattery));
        service.Dispatch(Event(UpsEventType.ReplaceBattery, message: "Replace the battery of rack1."));
        Time.Advance(TimeSpan.FromSeconds(10));
        await service.Email.FlushAsync(CancellationToken.None);

        Assert.Equal("[NutHub] rack1: Replace the battery of rack1.", Assert.Single(smtp.Sent).Subject);
    }

    [Fact]
    public void Disabled_email_sends_nothing()
    {
        var smtp = new RecordingSmtpTransport();
        Config.Update(c => c.Notifications.Email = new EmailSettings
        {
            Enabled = false,
            Host = "smtp.example.com",
            From = "nuthub@example.com",
            To = ["ops@example.com"],
        });
        var service = CreateService(smtp);

        service.Dispatch(Event(UpsEventType.OnBattery));
        Time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(0, service.Email.Outbox.Count);
    }

    [Fact]
    public void Delivery_log_keeps_the_last_200_newest_first()
    {
        var log = new DeliveryLog();
        DateTimeOffset start = Time.GetUtcNow();
        for (int i = 0; i < 250; i++)
        {
            log.Add(new NotificationDelivery(start.AddSeconds(i), NotificationChannelKind.Webhook, "t" + i,
                                             UpsEventType.OnBattery, "rack1", true, null));
        }

        IReadOnlyList<NotificationDelivery> items = log.Snapshot();
        Assert.Equal(200, items.Count);
        Assert.Equal("t249", items[0].Target);
        Assert.Equal("t50", items[^1].Target);
    }

    [Fact]
    public void Registration_replaces_the_core_defaults()
    {
        using var dir = new TempDirectory();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNutHubCore(new NutHubPaths(dir.Path)).AddNutHubServices();
        using ServiceProvider provider = services.BuildServiceProvider();

        var notifications = provider.GetRequiredService<INotificationService>();
        var protection = provider.GetRequiredService<IHostProtectionService>();

        Assert.IsType<NotificationService>(notifications);
        Assert.IsType<HostProtectionService>(protection);
        List<object> hosted = provider.GetServices<IHostedService>().Cast<object>().ToList();
        Assert.Contains(notifications, hosted);
        Assert.Contains(protection, hosted);
        Assert.NotNull(provider.GetRequiredService<IHttpClientFactory>());
        Assert.Equal(HostProtectionState.Disabled, protection.GetStatus().State);
        Assert.True(protection.GetStatus().DryRun || !IsDebug());
    }

    private static bool IsDebug()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }
}
