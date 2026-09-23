using System.Net.Sockets;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using NutHub.Core.Configuration;

namespace NutHub.Services.Notifications.Email;

/// <summary>Where and how to deliver e-mail; the password is already decrypted.</summary>
internal sealed record SmtpEndpoint(string Host, int Port, SmtpSecurity Security, string? Username, string? Password);

/// <summary>What went wrong while sending, classified for the retry logic.</summary>
internal enum SmtpFailureKind
{
    /// <summary>The server could not be reached or the connection broke: retry later, the whole outbox waits.</summary>
    Connection,

    /// <summary>The server refused the credentials: retry later, but log in once per round (account lockouts).</summary>
    Authentication,

    /// <summary>A temporary refusal (4xx): retry this message later.</summary>
    Transient,

    /// <summary>A definitive refusal of this message (5xx on sender, recipient or content): retrying is useless.</summary>
    Permanent,
}

internal sealed class SmtpSendException(SmtpFailureKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public SmtpFailureKind Kind { get; } = kind;
}

/// <summary>Sends one message; replaced by a recorder in tests.</summary>
internal interface ISmtpTransport
{
    /// <exception cref="SmtpSendException">Always this type, with the server's own words in the message.</exception>
    Task SendAsync(SmtpEndpoint endpoint, MimeMessage message, CancellationToken cancellationToken);
}

/// <summary>MailKit, with the configured security mode and optional authentication.</summary>
internal sealed class MailKitSmtpTransport : ISmtpTransport
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task SendAsync(SmtpEndpoint endpoint, MimeMessage message, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            await client.ConnectAsync(endpoint.Host, endpoint.Port, ToSocketOptions(endpoint), cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrEmpty(endpoint.Username))
            {
                await client.AuthenticateAsync(endpoint.Username, endpoint.Password ?? "", cancellationToken)
                    .ConfigureAwait(false);
            }

            await client.SendAsync(message, cancellationToken).ConfigureAwait(false);
            await client.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (Classify(ex) is { } failure)
        {
            throw failure;
        }
    }

    internal static SecureSocketOptions ToSocketOptions(SmtpEndpoint endpoint) => endpoint.Security switch
    {
        SmtpSecurity.None => SecureSocketOptions.None,
        SmtpSecurity.StartTls => SecureSocketOptions.StartTls,
        SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
        // Port 465 is implicit TLS by definition; elsewhere use STARTTLS whenever the server offers it.
        _ => endpoint.Port == 465 ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTlsWhenAvailable,
    };

    private static SmtpSendException? Classify(Exception ex) => ex switch
    {
        AuthenticationException auth => new SmtpSendException(
            SmtpFailureKind.Authentication,
            auth.InnerException is SmtpCommandException inner
                ? $"SMTP authentication failed: {(int)inner.StatusCode} {inner.Message}"
                : $"SMTP authentication failed: {auth.Message}", ex),
        SmtpCommandException cmd => new SmtpSendException(
            (int)cmd.StatusCode >= 500 && cmd.ErrorCode is SmtpErrorCode.RecipientNotAccepted
                or SmtpErrorCode.SenderNotAccepted or SmtpErrorCode.MessageNotAccepted
                ? SmtpFailureKind.Permanent
                : SmtpFailureKind.Transient,
            $"SMTP error {(int)cmd.StatusCode}: {cmd.Message}", ex),
        SslHandshakeException ssl => new SmtpSendException(
            SmtpFailureKind.Connection, $"TLS negotiation with the SMTP server failed: {FirstLine(ssl.Message)}", ex),
        NotSupportedException ns => new SmtpSendException(
            SmtpFailureKind.Connection, $"The SMTP server does not support the requested security: {ns.Message}", ex),
        SocketException sock => new SmtpSendException(
            SmtpFailureKind.Connection, $"Cannot reach the SMTP server: {sock.Message}", ex),
        TimeoutException or OperationCanceledException => new SmtpSendException(
            SmtpFailureKind.Connection, "The SMTP server did not answer in time.", ex),
        SmtpProtocolException or IOException or ServiceNotConnectedException => new SmtpSendException(
            SmtpFailureKind.Connection, $"The connection to the SMTP server failed: {ex.Message}", ex),
        ServiceNotAuthenticatedException => new SmtpSendException(
            SmtpFailureKind.Authentication, $"The SMTP server requires authentication: {ex.Message}", ex),
        _ => null,
    };

    private static string FirstLine(string text)
    {
        int newline = text.IndexOfAny(['\r', '\n']);
        return newline < 0 ? text : text[..newline];
    }
}
