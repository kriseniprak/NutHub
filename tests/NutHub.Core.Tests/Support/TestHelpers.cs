using Microsoft.Extensions.Time.Testing;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace NutHub.Core.Tests.Support;

/// <summary>A private temporary directory, deleted with its content on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nuthub-core-tests", Guid.NewGuid().ToString("N"));
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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file still held by a watcher: left for the temporary folder cleanup.
        }
    }
}

/// <summary>Collects everything published on an <see cref="EventHub"/>.</summary>
internal sealed class HubRecorder : IDisposable
{
    private readonly List<HubMessage> _messages = [];
    private readonly IDisposable _subscription;

    public HubRecorder(EventHub hub)
    {
        _subscription = hub.Subscribe(m =>
        {
            lock (_messages)
            {
                _messages.Add(m);
            }
        });
    }

    public IReadOnlyList<HubMessage> Messages
    {
        get
        {
            lock (_messages)
            {
                return [.. _messages];
            }
        }
    }

    public IReadOnlyList<UpsEvent> Events => Messages.OfType<UpsEventMessage>().Select(m => m.Event).ToList();

    public IReadOnlyList<UpsEventType> EventTypes => Events.Select(e => e.Type).ToList();

    public void Clear()
    {
        lock (_messages)
        {
            _messages.Clear();
        }
    }

    public void Dispose() => _subscription.Dispose();
}

internal static class TestTime
{
    public static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    public static FakeTimeProvider Create() => new(Start);

    public static EventHub Hub() => new(NullLogger<EventHub>.Instance);
}

internal static class Wait
{
    /// <summary>Waits (real time) for a condition set by another thread; fails the test after the timeout.</summary>
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 30000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Timed out waiting for: {what}");
            }

            await Task.Delay(5);
        }
    }

    /// <summary>
    /// Advances fake time in small steps, letting background loops run between steps, until the condition holds.
    /// Returns the fake time that passed. Robust against a timer being registered a moment after the call.
    /// </summary>
    public static async Task<TimeSpan> AdvanceUntilAsync(FakeTimeProvider time, Func<bool> condition, string what,
                                                         TimeSpan step, TimeSpan max)
    {
        DateTimeOffset start = time.GetUtcNow();
        while (!condition())
        {
            if (time.GetUtcNow() - start > max)
            {
                throw new TimeoutException($"Fake time exceeded {max} waiting for: {what}");
            }

            time.Advance(step);
            await Task.Delay(3);
        }

        return time.GetUtcNow() - start;
    }
}
