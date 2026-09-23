using System.Globalization;
using System.Text.Json;
using NutHub.Core.Model;

namespace NutHub.Services.Notifications;

/// <summary>
/// Limits how many notifications each channel sends in a sliding window, so flapping mains power (dozens of
/// on-battery / on-line transitions a minute) does not bury the recipients. The excess is counted and sent later as
/// one summary. Events that announce an imminent loss of power are never held back.
/// </summary>
internal sealed class FloodGuard : IDisposable
{
    private readonly TimeProvider _time;
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly Action<string, UpsEvent> _summaryReady;
    private readonly Dictionary<string, ChannelState> _channels = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private bool _disposed;

    /// <param name="summaryReady">Called (on a timer thread) with the channel key and the summary to deliver.</param>
    public FloodGuard(TimeProvider time, Action<string, UpsEvent> summaryReady, int limit = 20, TimeSpan? window = null)
    {
        _time = time;
        _summaryReady = summaryReady;
        _limit = Math.Max(1, limit);
        _window = window ?? TimeSpan.FromMinutes(10);
    }

    /// <summary>Whether this event type always gets through: the machine may be about to lose power.</summary>
    public static bool IsExempt(UpsEventType type) =>
        type is UpsEventType.LowBattery or UpsEventType.ForcedShutdown or UpsEventType.ShutdownPending
            or UpsEventType.ShutdownStarted or UpsEventType.ShutdownCancelled;

    /// <summary>
    /// Returns what to deliver now on this channel: nothing (the event was held back), the event, or a summary of
    /// the events held back so far followed by the event.
    /// </summary>
    public IReadOnlyList<UpsEvent> Admit(string channel, UpsEvent e)
    {
        lock (_lock)
        {
            DateTimeOffset now = _time.GetUtcNow();
            ChannelState state = GetState(channel);
            state.Prune(now - _window);

            var result = new List<UpsEvent>(2);
            if (state.Held > 0 && state.Sent.Count < _limit - 1)
            {
                result.Add(state.TakeSummary(now, _window));
                state.Sent.Enqueue(now);
            }

            if (IsExempt(e.Type) || state.Sent.Count < _limit)
            {
                state.Sent.Enqueue(now);
                result.Add(e);
                return result;
            }

            state.Hold(e);
            ScheduleSummary(channel, state, now);
            return result;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            foreach (ChannelState state in _channels.Values)
            {
                state.Timer?.Dispose();
                state.Timer = null;
            }
        }
    }

    private ChannelState GetState(string channel)
    {
        if (!_channels.TryGetValue(channel, out ChannelState? state))
        {
            state = new ChannelState();
            _channels[channel] = state;
        }

        return state;
    }

    private void ScheduleSummary(string channel, ChannelState state, DateTimeOffset now)
    {
        if (state.Timer is not null || _disposed)
        {
            return;
        }

        // The summary goes out as soon as the oldest delivery leaves the window, i.e. when a slot frees up.
        TimeSpan due = state.Sent.Count > 0 ? state.Sent.Peek() + _window - now : TimeSpan.Zero;
        if (due < TimeSpan.FromSeconds(1))
        {
            due = TimeSpan.FromSeconds(1);
        }

        state.Timer = _time.CreateTimer(_ => OnTimer(channel), null, due, Timeout.InfiniteTimeSpan);
    }

    private void OnTimer(string channel)
    {
        UpsEvent? summary = null;
        lock (_lock)
        {
            if (_disposed || !_channels.TryGetValue(channel, out ChannelState? state))
            {
                return;
            }

            state.Timer?.Dispose();
            state.Timer = null;
            if (state.Held == 0)
            {
                return;
            }

            DateTimeOffset now = _time.GetUtcNow();
            state.Prune(now - _window);
            if (state.Sent.Count < _limit)
            {
                summary = state.TakeSummary(now, _window);
                state.Sent.Enqueue(now);
            }
            else
            {
                ScheduleSummary(channel, state, now);
            }
        }

        if (summary is not null)
        {
            _summaryReady(channel, summary);
        }
    }

    private sealed class ChannelState
    {
        public Queue<DateTimeOffset> Sent { get; } = new();

        public ITimer? Timer { get; set; }

        public int Held { get; private set; }

        private readonly Dictionary<UpsEventType, int> _heldTypes = [];
        private readonly HashSet<string?> _heldUps = [];
        private UpsEvent? _last;
        private EventSeverity _maxSeverity;

        public void Prune(DateTimeOffset cutoff)
        {
            while (Sent.Count > 0 && Sent.Peek() <= cutoff)
            {
                Sent.Dequeue();
            }
        }

        public void Hold(UpsEvent e)
        {
            Held++;
            _heldTypes[e.Type] = _heldTypes.GetValueOrDefault(e.Type) + 1;
            _heldUps.Add(e.Ups);
            _last = e;
            if (e.Severity > _maxSeverity)
            {
                _maxSeverity = e.Severity;
            }
        }

        public UpsEvent TakeSummary(DateTimeOffset now, TimeSpan window)
        {
            UpsEvent last = _last!;
            string types = string.Join(", ", _heldTypes.OrderByDescending(p => p.Value)
                .Select(p => $"{JsonNamingPolicy.CamelCase.ConvertName(p.Key.ToString())} x{p.Value}"));
            string? ups = _heldUps.Count == 1 ? _heldUps.First() : null;
            string message = string.Create(CultureInfo.InvariantCulture,
                $"Flood protection held back {Held} notification(s) in the last {window.TotalMinutes:0} minutes " +
                $"({types}). The last one: {last.Message}");
            var data = new Dictionary<string, string>
            {
                ["suppressed"] = Held.ToString(CultureInfo.InvariantCulture),
                ["types"] = types,
            };
            UpsEvent summary = UpsEvent.Create(last.Type, now, ups, message, "flood-protection", data, _maxSeverity);

            Held = 0;
            _heldTypes.Clear();
            _heldUps.Clear();
            _last = null;
            _maxSeverity = EventSeverity.Info;
            return summary;
        }
    }
}
