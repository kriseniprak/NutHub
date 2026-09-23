using System.Text.Json;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Services.Tests.Support;

namespace NutHub.Services.Tests.Notifications;

public sealed class WebhookTests : NotificationServiceTestBase, IAsyncLifetime
{
    private HttpTestServer _http = null!;

    public async Task InitializeAsync() => _http = await HttpTestServer.StartAsync();

    public async Task DisposeAsync() => await _http.DisposeAsync();

    [Fact]
    public async Task Default_document_is_posted_as_json()
    {
        AddWebhook(new WebhookSettings { Id = "w1", Name = "Home", Url = _http.BaseUrl + "/hook" });
        var service = CreateService();

        service.Dispatch(Event(UpsEventType.OnBattery, message: "rack1 is on \"battery\"."));
        await service.WhenIdleAsync();

        ReceivedRequest request = Assert.Single(_http.Requests);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/hook", request.PathAndQuery);
        Assert.StartsWith("application/json", request.Headers["Content-Type"]);
        Assert.StartsWith("NutHub/", request.Headers["User-Agent"]);
        using JsonDocument doc = JsonDocument.Parse(request.Body);
        JsonElement root = doc.RootElement;
        Assert.Equal("lab", root.GetProperty("server").GetString());
        Assert.Equal("rack1", root.GetProperty("ups").GetString());
        Assert.Equal("onBattery", root.GetProperty("type").GetString());
        Assert.Equal("warning", root.GetProperty("severity").GetString());
        Assert.Equal("power", root.GetProperty("category").GetString());
        Assert.Equal("rack1 is on \"battery\".", root.GetProperty("message").GetString());
        Assert.Equal("2026-09-22T10:00:00Z", root.GetProperty("timestamp").GetString());
        Assert.Equal("system", root.GetProperty("actor").GetString());
        Assert.Equal(JsonValueKind.Object, root.GetProperty("data").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("status").ValueKind); // not in the (empty) registry

        NotificationDelivery delivery = Assert.Single(service.RecentDeliveries);
        Assert.True(delivery.Success);
        Assert.Equal("Home", delivery.Target);
        Assert.Equal(NotificationChannelKind.Webhook, delivery.Channel);
    }

    [Fact]
    public async Task Template_headers_and_secrets_are_applied()
    {
        AddWebhook(new WebhookSettings
        {
            Id = "w1",
            Url = _http.BaseUrl + "/topic/{ups}",
            Method = "PUT",
            ContentType = "application/json",
            BodyTemplate = "{\"text\": \"{server}: {message}\", \"sev\": \"{severity}\"}",
            Headers = new Dictionary<string, string>
            {
                ["Authorization"] = Secrets.Protect("Bearer tk_123")!,
                ["Title"] = "UPS {ups}",
            },
        });
        var service = CreateService();

        service.Dispatch(Event(UpsEventType.LowBattery, ups: "rack 1", message: "Low\nbattery \\ \"now\""));
        await service.WhenIdleAsync();

        ReceivedRequest request = Assert.Single(_http.Requests);
        Assert.Equal("PUT", request.Method);
        Assert.Equal("/topic/rack%201", request.PathAndQuery);
        Assert.Equal("Bearer tk_123", request.Headers["Authorization"]);
        Assert.Equal("UPS rack 1", request.Headers["Title"]);
        using JsonDocument doc = JsonDocument.Parse(request.Body);
        Assert.Equal("lab: Low\nbattery \\ \"now\"", doc.RootElement.GetProperty("text").GetString());
        Assert.Equal("critical", doc.RootElement.GetProperty("sev").GetString());
    }

    [Fact]
    public async Task Get_requests_url_encode_the_values()
    {
        AddWebhook(new WebhookSettings
        {
            Id = "w1",
            Method = "GET",
            Url = _http.BaseUrl + "/trigger?event={type}&msg={message}",
        });
        var service = CreateService();

        service.Dispatch(Event(UpsEventType.Online, message: "back & fine?"));
        await service.WhenIdleAsync();

        // A machine slow enough to time out the first attempt has the webhook retried; what this test is about is
        // the encoding of the values, which every attempt must show. Delivering once is checked on its own.
        Assert.NotEmpty(_http.Requests);
        Assert.All(_http.Requests, request =>
        {
            Assert.Equal("GET", request.Method);
            Assert.Equal("/trigger?event=online&msg=back%20%26%20fine%3F", request.PathAndQuery);
            Assert.Equal("", request.Body);
        });
    }

    [Fact]
    public async Task Server_errors_are_retried_up_to_three_times()
    {
        _http.Statuses.Enqueue(503);
        _http.Statuses.Enqueue(500);
        AddWebhook(new WebhookSettings { Id = "w1", Url = _http.BaseUrl + "/hook" });
        var service = CreateService();

        service.Dispatch(Event(UpsEventType.OnBattery));
        await service.WhenIdleAsync();

        Assert.Equal(3, _http.Requests.Count);
        Assert.True(Assert.Single(service.RecentDeliveries).Success);
    }

    [Fact]
    public async Task Client_errors_are_not_retried_and_are_reported()
    {
        _http.Statuses.Enqueue(404);
        AddWebhook(new WebhookSettings { Id = "w1", Url = _http.BaseUrl + "/missing" });
        var service = CreateService();

        service.Dispatch(Event(UpsEventType.OnBattery));
        await service.WhenIdleAsync();

        Assert.Single(_http.Requests);
        NotificationDelivery delivery = Assert.Single(service.RecentDeliveries);
        Assert.False(delivery.Success);
        Assert.StartsWith("HTTP 404", delivery.Error);
        Assert.Contains("not here", delivery.Error);
    }

    [Fact]
    public async Task Send_test_uses_one_attempt_and_returns_the_error()
    {
        _http.Statuses.Enqueue(500);
        AddWebhook(new WebhookSettings { Id = "w1", Url = _http.BaseUrl + "/hook" });
        var service = CreateService();

        CommandResult failed = await service.SendTestAsync(NotificationChannelKind.Webhook, "w1");
        CommandResult ok = await service.SendTestAsync(NotificationChannelKind.Webhook, "w1");
        CommandResult unknown = await service.SendTestAsync(NotificationChannelKind.Webhook, "nope");

        Assert.False(failed.IsSuccess);
        Assert.StartsWith("HTTP 500", failed.Message);
        Assert.True(ok.IsSuccess);
        Assert.Equal(CommandStatus.InvalidArgument, unknown.Status);
        Assert.Equal(2, _http.Requests.Count);
        using JsonDocument doc = JsonDocument.Parse(_http.Requests.Last().Body);
        Assert.Equal("test", doc.RootElement.GetProperty("type").GetString());
        Assert.True(doc.RootElement.GetProperty("test").GetBoolean());
    }

    [Fact]
    public async Task Event_filters_and_disabled_webhooks_are_respected()
    {
        Config.Update(c => c.Notifications.Events = [UpsEventType.OnBattery]);
        AddWebhook(new WebhookSettings { Id = "all", Url = _http.BaseUrl + "/all" });
        AddWebhook(new WebhookSettings { Id = "own", Url = _http.BaseUrl + "/own", Events = [UpsEventType.Online] });
        AddWebhook(new WebhookSettings { Id = "off", Url = _http.BaseUrl + "/off", Enabled = false });
        var service = CreateService();

        service.Dispatch(Event(UpsEventType.OnBattery));
        service.Dispatch(Event(UpsEventType.Online));
        service.Dispatch(Event(UpsEventType.VariableChanged));
        await service.WhenIdleAsync();

        Assert.Equal(new[] { "/all", "/own" }, _http.Requests.Select(r => r.PathAndQuery).Order());
    }

    [Fact]
    public async Task Flapping_power_is_capped_per_webhook()
    {
        AddWebhook(new WebhookSettings { Id = "w1", Url = _http.BaseUrl + "/hook" });
        var service = CreateService();

        for (int i = 0; i < 30; i++)
        {
            service.Dispatch(Event(i % 2 == 0 ? UpsEventType.OnBattery : UpsEventType.Online));
            Time.Advance(TimeSpan.FromSeconds(2));
        }

        service.Dispatch(Event(UpsEventType.LowBattery));
        await service.WhenIdleAsync();
        Assert.Equal(21, _http.Requests.Count);

        Time.Advance(TimeSpan.FromMinutes(10));
        await Wait.UntilAsync(() => _http.Requests.Count == 22, "the summary");
        await service.WhenIdleAsync();
        using JsonDocument doc = JsonDocument.Parse(_http.Requests.Last().Body);
        Assert.Contains("held back 10 notification(s)", doc.RootElement.GetProperty("message").GetString());
        Assert.Equal("flood-protection", doc.RootElement.GetProperty("actor").GetString());
    }

    [Fact]
    public async Task A_full_queue_for_a_slow_webhook_still_takes_the_shutdown()
    {
        AddWebhook(new WebhookSettings { Id = "w1", Url = _http.BaseUrl + "/hook" });
        var service = CreateService();
        var slow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _http.Hold = slow.Task;

        // A low-battery flag flapping during the outage: never held back by the flood protection.
        for (int i = 0; i < 100; i++)
        {
            service.Dispatch(Event(UpsEventType.LowBattery));
        }

        service.Dispatch(Event(UpsEventType.ShutdownStarted, ups: null));
        slow.SetResult();
        await service.WhenIdleAsync();

        Assert.True(service.RecentDeliveries.Single(d => d.EventType == UpsEventType.ShutdownStarted).Success);
        Assert.Equal(101, _http.Requests.Count);
    }

    [Fact]
    public async Task Events_from_the_hub_are_delivered_once_started()
    {
        AddWebhook(new WebhookSettings { Id = "w1", Url = _http.BaseUrl + "/hook" });
        var service = CreateService();
        Hub.Publish(new Core.Runtime.UpsEventMessage(Event(UpsEventType.OnBattery)));

        await service.StartAsync(CancellationToken.None);
        try
        {
            await Wait.UntilAsync(() => !_http.Requests.IsEmpty, "the webhook call");
        }
        finally
        {
            await service.StopAsync(CancellationToken.None);
        }

        Assert.Single(_http.Requests);
    }

    [Theory]
    [InlineData("Expires")] // a content header: refused on a request
    [InlineData("X Token")] // not a header name
    public async Task A_header_that_cannot_be_sent_is_reported(string header)
    {
        AddWebhook(new WebhookSettings
        {
            Id = "w1",
            Name = "Home",
            Url = _http.BaseUrl + "/hook",
            Headers = new Dictionary<string, string> { [header] = "0" },
        });
        var service = CreateService();

        service.Dispatch(Event(UpsEventType.LowBattery));
        await service.WhenIdleAsync();

        NotificationDelivery delivery = Assert.Single(service.RecentDeliveries);
        Assert.False(delivery.Success);
        Assert.Contains(header, delivery.Error);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("application/json")]
    public async Task Text_cut_in_the_middle_of_a_character_is_still_delivered(string? contentType)
    {
        AddWebhook(new WebhookSettings
        {
            Id = "w1",
            Url = _http.BaseUrl + "/hook",
            ContentType = contentType ?? "application/json",
            BodyTemplate = contentType is null ? null : "{\"text\": \"{message}\"}",
            Events = [UpsEventType.UserLoginFailed],
        });
        var service = CreateService();

        // A user name shortened to 64 characters can end with half of an emoji.
        service.Dispatch(Event(UpsEventType.UserLoginFailed, ups: null, message: "Failed sign-in as 'abc\uD83D' from 10.0.0.1."));
        await service.WhenIdleAsync();

        Assert.True(Assert.Single(service.RecentDeliveries).Success);
        using JsonDocument doc = JsonDocument.Parse(Assert.Single(_http.Requests).Body);
        string? text = doc.RootElement.GetProperty(contentType is null ? "message" : "text").GetString();
        Assert.Equal("Failed sign-in as 'abc�' from 10.0.0.1.", text);
    }

    private void AddWebhook(WebhookSettings hook) =>
        Config.Update(c =>
        {
            c.Server.Name = "lab";
            c.Notifications.Webhooks.Add(hook);
        });
}
