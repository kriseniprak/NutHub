using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using NutHub.Core.Model;
using NutHub.Services.HostProtection;

namespace NutHub.Services.Tests.Support;

/// <summary>Scripted UPSes for the host protection: snapshots set by the test, operations recorded.</summary>
internal sealed class FakeUpsAccess : IProtectedUpsAccess
{
    private readonly ConcurrentDictionary<string, UpsSnapshot> _snapshots = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentQueue<string> Operations { get; } = new();

    public void Set(UpsSnapshot snapshot) => _snapshots[snapshot.Name] = snapshot;

    public void Remove(string name) => _snapshots.TryRemove(name, out _);

    public UpsSnapshot? GetSnapshot(string name) => _snapshots.TryGetValue(name, out UpsSnapshot? s) ? s : null;

    public bool SetForcedShutdown(string name, CommandOrigin origin)
    {
        if (!_snapshots.TryGetValue(name, out UpsSnapshot? s))
        {
            return false;
        }

        Operations.Enqueue($"fsd {name} by {origin.Source}");
        _snapshots[name] = Snapshots.With(s, forcedShutdown: true);
        return true;
    }

    public Task<CommandResult> SetVariableAsync(string name, string variable, string value, CommandOrigin origin,
                                                CancellationToken cancellationToken)
    {
        Operations.Enqueue($"set {name} {variable}={value}");
        return Task.FromResult(CommandResult.Ok);
    }

    public Task<CommandResult> InstantCommandAsync(string name, string command, string? parameter, CommandOrigin origin,
                                                   CancellationToken cancellationToken)
    {
        Operations.Enqueue(parameter is null ? $"cmd {name} {command}" : $"cmd {name} {command} {parameter}");
        return Task.FromResult(CommandResult.Ok);
    }
}

/// <summary>Records the shutdown requests; never starts a process.</summary>
internal sealed class RecordingPowerController : IHostPowerController
{
    public ConcurrentQueue<IReadOnlyList<ShutdownInvocation>> Requests { get; } = new();

    /// <summary>Called when the request arrives, e.g. to check what happened before it.</summary>
    public Action? OnRequest { get; set; }

    public Task<ShutdownOutcome> ShutdownAsync(IReadOnlyList<ShutdownInvocation> invocations,
                                               CancellationToken cancellationToken)
    {
        OnRequest?.Invoke();
        Requests.Enqueue(invocations);
        return Task.FromResult(new ShutdownOutcome(true, true, "recorded"));
    }
}

internal static class Snapshots
{
    public static UpsSnapshot Create(string name, string status, double? charge = 100, double? runtime = 1800,
                                     bool available = true, bool forcedShutdown = false, bool writableDelay = true,
                                     params string[] commands)
    {
        var variables = ImmutableSortedDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        UpsStatusFlags flags = UpsStatus.Parse(status) | (forcedShutdown ? UpsStatusFlags.ForcedShutdown : UpsStatusFlags.None);
        variables["ups.status"] = UpsStatus.Format(flags);
        if (charge is { } c)
        {
            variables["battery.charge"] = c.ToString(CultureInfo.InvariantCulture);
        }

        if (runtime is { } r)
        {
            variables["battery.runtime"] = r.ToString(CultureInfo.InvariantCulture);
        }

        var info = ImmutableDictionary.CreateBuilder<string, VariableInfo>(StringComparer.Ordinal);
        if (writableDelay)
        {
            variables["ups.delay.shutdown"] = "20";
            info["ups.delay.shutdown"] = VariableInfo.WritableNumber();
        }

        return new UpsSnapshot
        {
            Name = name,
            DriverId = "fake",
            DriverState = available ? DriverState.Connected : DriverState.Disconnected,
            Availability = available ? DataAvailability.Available : DataAvailability.Stale,
            ForcedShutdown = forcedShutdown,
            Status = flags,
            Variables = variables.ToImmutable(),
            VariableInfo = info.ToImmutable(),
            Commands = commands.Length > 0 ? [.. commands] : ["load.off.delay", "shutdown.return"],
        };
    }

    public static UpsSnapshot With(UpsSnapshot s, bool? forcedShutdown = null, bool? available = null)
    {
        bool fsd = forcedShutdown ?? s.ForcedShutdown;
        bool avail = available ?? s.IsAvailable;
        UpsStatusFlags flags = fsd ? s.Status | UpsStatusFlags.ForcedShutdown : s.Status & ~UpsStatusFlags.ForcedShutdown;
        return new UpsSnapshot
        {
            Name = s.Name,
            DriverId = s.DriverId,
            DriverState = avail ? DriverState.Connected : DriverState.Disconnected,
            Availability = avail ? DataAvailability.Available : DataAvailability.Stale,
            ForcedShutdown = fsd,
            Status = flags,
            Variables = s.Variables.SetItem("ups.status", UpsStatus.Format(flags)),
            VariableInfo = s.VariableInfo,
            Commands = s.Commands,
        };
    }
}

/// <summary>
/// A <see cref="FakeTimeProvider"/> whose wall clock can jump (an NTP correction, an operator changing the time)
/// while the monotonic clock keeps counting normally, as on a real machine.
/// </summary>
internal sealed class JumpingTimeProvider(FakeTimeProvider inner) : TimeProvider
{
    /// <summary>Added to the wall clock only.</summary>
    public TimeSpan Offset { get; set; }

    public override DateTimeOffset GetUtcNow() => inner.GetUtcNow() + Offset;

    public override long GetTimestamp() => inner.GetTimestamp();

    public override long TimestampFrequency => inner.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        inner.CreateTimer(callback, state, dueTime, period);
}

internal static class Wait
{
    /// <summary>
    /// Polls a condition in real time (for work that completes on the thread pool). The limit is generous because it
    /// only matters on failure: a loaded CI runner can need seconds for a first HTTP request or a cold start.
    /// </summary>
    public static async Task UntilAsync(Func<bool> condition, string what, int timeoutMs = 30000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Timed out waiting for " + what);
            }

            await Task.Delay(10);
        }
    }
}
