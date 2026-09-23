using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Web.Api.Endpoints;
using NutHub.Web.Tests.TestSupport;

namespace NutHub.Web.Tests;

public sealed class StreamTests
{
    [Fact]
    public async Task Stream_starts_with_retry_and_overview_then_sends_events()
    {
        await using var host = await WebTestHost.StartAsync();
        await host.WaitForUpsAsync("sim1");
        HttpClient client = await host.SignedInAsync("viewer");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        using HttpResponseMessage response = await client.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType!.MediaType);
        await using Stream body = await response.Content.ReadAsStreamAsync(cts.Token);
        var reader = new SseReader(body);

        Assert.Equal("retry: 5000", await reader.ReadLineAsync(cts.Token));
        Assert.Equal("", await reader.ReadLineAsync(cts.Token));
        var (name, data) = await reader.ReadEventAsync(cts.Token);
        Assert.Equal("overview", name);
        Assert.Equal("sim1", data["ups"]![0]!["name"]!.GetValue<string>());

        host.Hub.Publish(new UpsEventMessage(UpsEvent.Create(UpsEventType.ConfigurationChanged, DateTimeOffset.UtcNow, null,
                                                             "A test event.")));
        JsonNode e = await reader.WaitForAsync("event", cts.Token);
        Assert.Equal("A test event.", e["message"]!.GetValue<string>());
        Assert.True(e["id"]!.GetValue<long>() > 0);

        // The simulated UPS publishes every poll: a coalesced summary arrives.
        JsonNode ups = await reader.WaitForAsync("ups", cts.Token);
        Assert.Equal("sim1", ups["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Stream_ends_when_the_session_is_no_longer_valid()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient stream = await host.SignedInAsync("operator");
        HttpClient other = await host.SignedInAsync("operator");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        using HttpResponseMessage response = await stream.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using Stream body = await response.Content.ReadAsStreamAsync(cts.Token);
        var reader = new SseReader(body);
        await reader.WaitForAsync("overview", cts.Token);

        HttpResponseMessage changed = await other.PostAsJsonAsync("/api/auth/password",
            new { currentPassword = "operator-password", newPassword = "brand-new-password" }, cts.Token);
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        // Drain until the server closes the stream.
        while (await reader.ReadLineAsync(cts.Token) is not null)
        {
        }

        Assert.False(cts.IsCancellationRequested);
    }

    [Fact]
    public async Task Stream_pings_every_15_seconds_and_resyncs_every_minute()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = await WebTestHost.StartAsync(services: s => s.AddSingleton<TimeProvider>(time));
        HttpClient client = await host.SignedInAsync("viewer");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        using HttpResponseMessage response = await client.GetAsync("/api/stream", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        await using Stream body = await response.Content.ReadAsStreamAsync(cts.Token);
        var reader = new SseReader(body);
        await reader.WaitForAsync("overview", cts.Token);

        time.Advance(StreamEndpoint.PingInterval);
        JsonNode ping = await reader.WaitForAsync("ping", cts.Token);
        Assert.Equal(time.GetUtcNow(), DateTimeOffset.Parse(ping["time"]!.GetValue<string>()), TimeSpan.FromMilliseconds(1));

        time.Advance(StreamEndpoint.OverviewInterval - StreamEndpoint.PingInterval);
        await reader.WaitForAsync("overview", cts.Token);
    }

    [Fact]
    public async Task Anonymous_stream_needs_anonymous_read()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpResponseMessage response = await host.Client().GetAsync("/api/stream");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    /// <summary>Reads server-sent events line by line.</summary>
    private sealed class SseReader(Stream stream)
    {
        private readonly StreamReader _reader = new(stream, Encoding.UTF8);

        public async Task<string?> ReadLineAsync(CancellationToken ct) => await _reader.ReadLineAsync(ct);

        public async Task<(string Name, JsonNode Data)> ReadEventAsync(CancellationToken ct)
        {
            string? name = null;
            string? data = null;
            while (await ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0 && name is not null)
                {
                    return (name, JsonNode.Parse(data!)!);
                }

                if (line.StartsWith("event: ", StringComparison.Ordinal))
                {
                    name = line[7..];
                }
                else if (line.StartsWith("data: ", StringComparison.Ordinal))
                {
                    data = line[6..];
                }
            }

            throw new EndOfStreamException();
        }

        public async Task<JsonNode> WaitForAsync(string name, CancellationToken ct)
        {
            while (true)
            {
                var (n, data) = await ReadEventAsync(ct);
                if (n == name)
                {
                    return data;
                }
            }
        }
    }
}
