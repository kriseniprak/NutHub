using Microsoft.Extensions.Time.Testing;
using NutHub.Drivers.Net.Common;
using NutHub.Drivers.Net.Tests.Fakes;

namespace NutHub.Drivers.Net.Tests;

public sealed class CommonTests
{
    [Fact]
    public void Backoff_grows_then_repeats_its_last_delay_and_resets()
    {
        var backoff = new ReconnectBackoff();

        TimeSpan[] delays = Enumerable.Range(0, 7).Select(_ => backoff.NextDelay()).ToArray();

        Assert.Equal([2, 5, 10, 20, 30, 30, 30], delays.Select(d => d.TotalSeconds));
        backoff.Reset();
        Assert.Equal(TimeSpan.FromSeconds(2), backoff.NextDelay());
    }

    [Fact]
    public void Reporter_forwards_a_reason_once_until_it_changes_or_data_is_published()
    {
        var context = new RecordingDriverContext();
        var reporter = new DriverStateReporter(context);

        reporter.Connecting("starting");
        reporter.Disconnected("down");
        reporter.Disconnected("down");
        reporter.Disconnected("still down, differently");
        reporter.Publish(new() { Variables = new Dictionary<string, string> { ["ups.status"] = "OL" } });
        reporter.Disconnected("down");

        Assert.Equal(["connecting", "disconnected", "disconnected", "publish", "disconnected"],
                     context.Reports.Select(r => r.Kind));
        Assert.False(reporter.IsConnected);
    }

    [Fact]
    public async Task Timeout_scope_tells_a_timeout_from_a_cancellation()
    {
        var time = new FakeTimeProvider();
        using var outer = new CancellationTokenSource();

        using (var scope = new TimeoutScope(TimeSpan.FromSeconds(5), time, outer.Token))
        {
            time.Advance(TimeSpan.FromSeconds(4));
            Assert.False(scope.Token.IsCancellationRequested);
            time.Advance(TimeSpan.FromSeconds(1));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Task.Delay(Timeout.Infinite, scope.Token));
            Assert.True(scope.TimedOut);
        }

        using (var scope = new TimeoutScope(TimeSpan.FromSeconds(5), time, outer.Token))
        {
            outer.Cancel();
            Assert.True(scope.Token.IsCancellationRequested);
            Assert.False(scope.TimedOut);
        }
    }
}
