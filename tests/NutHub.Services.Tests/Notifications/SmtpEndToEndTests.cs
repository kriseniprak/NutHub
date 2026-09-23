using System.Net;
using System.Net.Sockets;
using MimeKit;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Services.Notifications.Email;
using NutHub.Services.Tests.Support;

namespace NutHub.Services.Tests.Notifications;

/// <summary>MailKit against the in-process SMTP server, through the "Send test" path.</summary>
public sealed class SmtpEndToEndTests : NotificationServiceTestBase
{
    [Fact]
    public async Task Test_message_is_delivered_with_authentication()
    {
        await using var smtp = new SmtpTestServer("ups@example.com", "s3cret");
        Configure(smtp.Port, "ups@example.com", Secrets.Protect("s3cret"));
        var service = CreateService(new MailKitSmtpTransport(), TimeProvider.System);

        CommandResult result = await service.SendTestAsync(NotificationChannelKind.Email, null);

        Assert.True(result.IsSuccess, result.ToString());
        ReceivedMail mail = Assert.Single(smtp.Messages);
        Assert.Equal("nuthub@example.com", mail.From);
        Assert.Equal(new[] { "ops@example.com" }, mail.To);
        MimeMessage message = mail.Parse();
        Assert.StartsWith("[UPS] TEST: lab-server: Test notification", message.Subject);
        Assert.Contains("TEST MESSAGE", message.TextBody);
        NotificationDelivery delivery = Assert.Single(service.RecentDeliveries);
        Assert.True(delivery.Success);
        Assert.Equal(NotificationChannelKind.Email, delivery.Channel);
    }

    [Fact]
    public async Task Wrong_password_reports_the_server_answer()
    {
        await using var smtp = new SmtpTestServer("ups@example.com", "s3cret");
        Configure(smtp.Port, "ups@example.com", "wrong");
        var service = CreateService(new MailKitSmtpTransport(), TimeProvider.System);

        CommandResult result = await service.SendTestAsync(NotificationChannelKind.Email, null);

        Assert.False(result.IsSuccess);
        Assert.Contains("authentication failed", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Authentication credentials invalid", result.Message);
        Assert.Empty(smtp.Messages);
    }

    [Fact]
    public async Task Rejected_recipient_is_a_permanent_error()
    {
        await using var smtp = new SmtpTestServer();
        smtp.RejectedRecipients.Add("ops@example.com");
        Configure(smtp.Port, null, null);
        var transport = new MailKitSmtpTransport();
        MimeMessage mime = EmailComposer.ToMime(new OutboxMessage
        {
            From = "nuthub@example.com", To = ["ops@example.com"], Subject = "x", TextBody = "x", HtmlBody = "x",
        });

        var ex = await Assert.ThrowsAsync<SmtpSendException>(() =>
            transport.SendAsync(new SmtpEndpoint("127.0.0.1", smtp.Port, SmtpSecurity.None, null, null), mime, CancellationToken.None));

        Assert.Equal(SmtpFailureKind.Permanent, ex.Kind);
        Assert.Contains("550", ex.Message);
    }

    [Fact]
    public async Task Unreachable_server_is_a_connection_error()
    {
        // A port that was just free: nothing listens there.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var transport = new MailKitSmtpTransport();
        MimeMessage mime = EmailComposer.ToMime(new OutboxMessage
        {
            From = "nuthub@example.com", To = ["ops@example.com"], Subject = "x", TextBody = "x", HtmlBody = "x",
        });

        var ex = await Assert.ThrowsAsync<SmtpSendException>(() =>
            transport.SendAsync(new SmtpEndpoint("127.0.0.1", port, SmtpSecurity.None, null, null), mime, CancellationToken.None));

        Assert.Equal(SmtpFailureKind.Connection, ex.Kind);
    }

    [Fact]
    public async Task Missing_configuration_is_reported_without_connecting()
    {
        var service = CreateService(new RecordingSmtpTransport());

        CommandResult result = await service.SendTestAsync(NotificationChannelKind.Email, null);

        Assert.Equal(CommandStatus.InvalidArgument, result.Status);
    }

    private void Configure(int port, string? username, string? password) =>
        Config.Update(c =>
        {
            c.Server.Name = "lab-server";
            c.Notifications.Email = new EmailSettings
            {
                Enabled = true,
                Host = "127.0.0.1",
                Port = port,
                Security = SmtpSecurity.Auto, // the test server offers no STARTTLS: plain connection
                Username = username,
                Password = password,
                From = "nuthub@example.com",
                To = ["ops@example.com"],
                SubjectPrefix = "[UPS]",
            };
        });
}
