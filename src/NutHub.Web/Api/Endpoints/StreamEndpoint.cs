using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using NutHub.Core.Configuration;
using NutHub.Core.Runtime;
using NutHub.Core.Security;
using NutHub.Web.Api.Dto;
using NutHub.Web.Auth;

namespace NutHub.Web.Api.Endpoints;

/// <summary>
/// <c>GET /api/stream</c>: server-sent events with the live state. UPS updates are coalesced to one per UPS per
/// second (the latest wins), the overview is resent every minute as a resync, and a ping every 15 s keeps proxies
/// from closing the connection. The stream ends when the session is no longer valid (expired, signed out elsewhere,
/// account changed) or anonymous read is turned off; the browser then reconnects and finds out.
/// </summary>
internal static class StreamEndpoint
{
    public static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan OverviewInterval = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan UpsInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ClientsInterval = TimeSpan.FromSeconds(1);

    public static void Map(RouteGroupBuilder read) => read.MapGet("/stream", StreamAsync);

    private static async Task StreamAsync(HttpContext context, EventHub hub, ApiViews views, IConfigStore config,
                                          IUpsRegistry registry, TimeProvider time, IHostApplicationLifetime lifetime)
    {
        var session = new SessionCheck(context, config);

        // Subscribe before the first overview so that no change falls between the two.
        using ChannelSubscription subscription = hub.SubscribeChannel(2048, m =>
            m is SnapshotChangedMessage or UpsRemovedMessage or EventRecordedMessage or NutClientsChangedMessage
                or HostProtectionChangedMessage or ConfigChangedMessage);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, lifetime.ApplicationStopping);
        CancellationToken ct = cts.Token;

        HttpResponse response = context.Response;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache, no-store";
        response.Headers["X-Accel-Buffering"] = "no";
        context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

        var writer = new SseWriter(response.Body);
        try
        {
            await writer.WriteRawAsync("retry: 5000\n\n", ct).ConfigureAwait(false);
            await writer.SendAsync("overview", views.Overview(), ct).ConfigureAwait(false);
            await PumpAsync(writer, subscription.Reader, session, views, registry, time, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            // The client closed the connection while we were writing.
        }
    }

    private static async Task PumpAsync(SseWriter writer, ChannelReader<HubMessage> reader, SessionCheck session,
                                        ApiViews views, IUpsRegistry registry, TimeProvider time, CancellationToken ct)
    {
        var pendingUps = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        var lastUps = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        DateTimeOffset now = time.GetUtcNow();
        DateTimeOffset nextPing = now + PingInterval;
        DateTimeOffset nextOverview = now + OverviewInterval;
        DateTimeOffset lastClients = DateTimeOffset.MinValue;
        bool clientsPending = false;
        Task<bool>? readTask = null;

        while (!ct.IsCancellationRequested)
        {
            now = time.GetUtcNow();
            if (!session.IsValid(now))
            {
                return;
            }

            if (now >= nextOverview)
            {
                await writer.SendAsync("overview", views.Overview(), ct).ConfigureAwait(false);
                nextOverview = now + OverviewInterval;
                foreach (string name in pendingUps.Keys)
                {
                    lastUps[name] = now;
                }

                pendingUps.Clear();
            }

            foreach (var (name, due) in pendingUps.ToList())
            {
                if (due <= now)
                {
                    pendingUps.Remove(name);
                    lastUps[name] = now;
                    if (registry.Find(name) is { } unit)
                    {
                        await writer.SendAsync("ups", views.Summary(unit), ct).ConfigureAwait(false);
                    }
                }
            }

            if (clientsPending && now >= lastClients + ClientsInterval)
            {
                clientsPending = false;
                lastClients = now;
                await writer.SendAsync("clients", views.ClientsSummary(), ct).ConfigureAwait(false);
            }

            if (now >= nextPing)
            {
                nextPing = now + PingInterval;
                await writer.SendAsync("ping", new PingDto(now), ct).ConfigureAwait(false);
            }

            // Sleep until the next due item or the next message, whichever comes first.
            DateTimeOffset deadline = Min(nextPing, nextOverview);
            foreach (DateTimeOffset due in pendingUps.Values)
            {
                deadline = Min(deadline, due);
            }

            if (clientsPending)
            {
                deadline = Min(deadline, lastClients + ClientsInterval);
            }

            if (session.ExpiresAt is { } expires)
            {
                deadline = Min(deadline, expires);
            }

            readTask ??= reader.WaitToReadAsync(ct).AsTask();
            TimeSpan wait = deadline - time.GetUtcNow();
            if (wait > TimeSpan.Zero && !readTask.IsCompleted)
            {
                using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task delay = Task.Delay(wait, time, delayCts.Token);
                await Task.WhenAny(readTask, delay).ConfigureAwait(false);
                await delayCts.CancelAsync().ConfigureAwait(false);
            }

            if (!readTask.IsCompleted)
            {
                continue;
            }

            if (!await readTask.ConfigureAwait(false))
            {
                return; // The hub subscription was closed.
            }

            readTask = null;
            now = time.GetUtcNow();
            while (reader.TryRead(out HubMessage? message))
            {
                switch (message)
                {
                    case SnapshotChangedMessage changed:
                        string name = changed.Current.Name;
                        if (!pendingUps.ContainsKey(name))
                        {
                            DateTimeOffset due = lastUps.TryGetValue(name, out DateTimeOffset last) && last + UpsInterval > now
                                ? last + UpsInterval
                                : now;
                            pendingUps[name] = due;
                        }

                        break;
                    case UpsRemovedMessage removed:
                        pendingUps.Remove(removed.Name);
                        lastUps.Remove(removed.Name);
                        await writer.SendAsync("upsRemoved", new UpsRemovedDto(removed.Name), ct).ConfigureAwait(false);
                        break;
                    case EventRecordedMessage recorded:
                        await writer.SendAsync("event", ApiViews.Event(recorded.Event), ct).ConfigureAwait(false);
                        break;
                    case NutClientsChangedMessage:
                        clientsPending = true;
                        break;
                    case HostProtectionChangedMessage:
                        await writer.SendAsync("hostProtection", views.HostProtection(), ct).ConfigureAwait(false);
                        break;
                    case ConfigChangedMessage:
                        // Checked at the top of the loop: accounts, anonymous read and address filter may have changed.
                        break;
                }
            }
        }
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    /// <summary>Whether the connection may continue: same account state as when it opened, or anonymous read.</summary>
    private sealed class SessionCheck
    {
        private readonly HttpContext _context;
        private readonly IConfigStore _config;
        private readonly bool _authenticated;
        private readonly IPAddress? _address;
        private NutHubConfig? _checked;
        private bool _valid = true;

        public SessionCheck(HttpContext context, IConfigStore config)
        {
            _context = context;
            _config = config;
            _authenticated = WebIdentity.CurrentUser(context) is not null;
            _address = WebIdentity.ClientAddress(context);
            ExpiresAt = _authenticated
                ? context.Features.Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Properties?.ExpiresUtc
                : null;
        }

        /// <summary>When the session cookie of this connection expires (renewals by other requests do not count).</summary>
        public DateTimeOffset? ExpiresAt { get; }

        public bool IsValid(DateTimeOffset now)
        {
            if (ExpiresAt is { } expires && now >= expires)
            {
                return false;
            }

            NutHubConfig current = _config.Current;
            if (!ReferenceEquals(current, _checked))
            {
                _checked = current;
                _valid = Evaluate(current);
            }

            return _valid;
        }

        private bool Evaluate(NutHubConfig config)
        {
            try
            {
                if (!AddressFilter.Parse(config.Web.AllowedNetworks).IsAllowed(_address))
                {
                    return false;
                }
            }
            catch (FormatException)
            {
                return false;
            }

            if (!_authenticated)
            {
                return config.Web.AllowAnonymousRead;
            }

            WebUserConfig? user = WebIdentity.FindValidUser(config, _context.User);
            return user is { MustChangePassword: false };
        }
    }

    /// <summary>Writes "event:" / "data:" frames; one JSON line per event, flushed at once.</summary>
    private sealed class SseWriter(Stream body)
    {
        public async Task SendAsync<T>(string eventName, T data, CancellationToken ct)
        {
            string json = JsonSerializer.Serialize(data, ApiJson.Options);
            await WriteRawAsync($"event: {eventName}\ndata: {json}\n\n", ct).ConfigureAwait(false);
        }

        public async Task WriteRawAsync(string text, CancellationToken ct)
        {
            await body.WriteAsync(Encoding.UTF8.GetBytes(text), ct).ConfigureAwait(false);
            await body.FlushAsync(ct).ConfigureAwait(false);
        }
    }
}
