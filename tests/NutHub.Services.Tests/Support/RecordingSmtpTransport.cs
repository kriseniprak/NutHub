using System.Collections.Concurrent;
using MimeKit;
using NutHub.Services.Notifications.Email;

namespace NutHub.Services.Tests.Support;

/// <summary>Records the messages "sent"; <see cref="Failure"/> makes every attempt fail while set.</summary>
internal sealed class RecordingSmtpTransport : ISmtpTransport
{
    private int _attempts;

    public ConcurrentQueue<MimeMessage> Sent { get; } = new();

    public SmtpSendException? Failure { get; set; }

    public int Attempts => Volatile.Read(ref _attempts);

    public SmtpEndpoint? LastEndpoint { get; private set; }

    public Task SendAsync(SmtpEndpoint endpoint, MimeMessage message, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attempts);
        LastEndpoint = endpoint;
        if (Failure is { } failure)
        {
            throw failure;
        }

        Sent.Enqueue(message);
        return Task.CompletedTask;
    }
}

/// <summary>A temporary directory deleted at the end of the test.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nuthub-services-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
