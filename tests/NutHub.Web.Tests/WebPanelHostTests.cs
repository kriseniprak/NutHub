using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NutHub.Core;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Web.Hosting;

namespace NutHub.Web.Tests;

/// <summary>The panel on real sockets (loopback only): start, rebinding on a settings change, port in use.</summary>
public sealed class WebPanelHostTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "nuthub-web-tests", Guid.NewGuid().ToString("N"));
    private IHost? _host;

    [Fact]
    public async Task Panel_listens_and_moves_to_a_new_port_when_the_settings_change()
    {
        int first = FreePort();
        await StartAsync(first);
        var panel = _host!.Services.GetRequiredService<WebPanelHost>();
        await WaitUntilAsync(() => panel.Active is not null);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://127.0.0.1:{first}/api/auth/state")).StatusCode);

        int second = FreePort();
        var store = _host.Services.GetRequiredService<IConfigStore>();
        await store.UpdateAsync(c => c.Web.HttpPort = second, CommandOrigin.System, "test");

        await WaitUntilAsync(() => panel.Active?.HttpPort == second);
        Assert.Equal(HttpStatusCode.OK, (await http.GetAsync($"http://127.0.0.1:{second}/api/auth/state")).StatusCode);
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync($"http://127.0.0.1:{first}/api/auth/state"));

        // A change that does not touch the listeners keeps the running instance.
        PanelBinding? before = panel.Active;
        await store.UpdateAsync(c => c.Web.AllowAnonymousRead = true, CommandOrigin.System, "test");
        await Task.Delay(WebPanelHost.RestartDelay * 2);
        Assert.Same(before, panel.Active);
    }

    [Fact]
    public async Task A_port_in_use_is_reported_and_nuthub_keeps_running()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        int busy = ((IPEndPoint)blocker.LocalEndpoint).Port;

        await StartAsync(busy);
        var panel = _host!.Services.GetRequiredService<WebPanelHost>();
        await WaitUntilAsync(() => panel.LastError is not null);
        Assert.Null(panel.Active);
        Assert.Contains("in use", panel.LastError);

        // A new port applies at once, without waiting for the retry.
        int free = FreePort();
        await _host.Services.GetRequiredService<IConfigStore>()
            .UpdateAsync(c => c.Web.HttpPort = free, CommandOrigin.System, "test");
        await WaitUntilAsync(() => panel.Active?.HttpPort == free);
        Assert.Null(panel.LastError);
    }

    [Fact]
    public async Task Https_with_a_self_signed_certificate_and_redirect()
    {
        int http = FreePort();
        int https = FreePort();
        await StartAsync(http, web =>
        {
            web.HttpsEnabled = true;
            web.HttpsPort = https;
            web.RedirectHttpToHttps = true;
        });
        var panel = _host!.Services.GetRequiredService<WebPanelHost>();
        await WaitUntilAsync(() => panel.Active is not null || panel.LastError is not null);
        Assert.Null(panel.LastError);
        Assert.True(panel.Active!.HttpsEnabled);

        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };

        HttpResponseMessage secure = await client.GetAsync($"https://127.0.0.1:{https}/api/auth/state");
        Assert.Equal(HttpStatusCode.OK, secure.StatusCode);
        Assert.True(secure.Headers.Contains("Strict-Transport-Security"));

        HttpResponseMessage redirected = await client.GetAsync($"http://127.0.0.1:{http}/api/auth/state?x=1");
        Assert.Equal(HttpStatusCode.PermanentRedirect, redirected.StatusCode);
        Assert.Equal($"https://127.0.0.1:{https}/api/auth/state?x=1", redirected.Headers.Location!.ToString());
        Assert.True(File.Exists(Path.Combine(_directory, "certs", "web-selfsigned.pfx")));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private async Task StartAsync(int port, Action<WebSettings>? web = null)
    {
        var paths = new NutHubPaths(_directory);
        paths.EnsureCreated();
        var config = new NutHubConfig();
        config.Nut.Enabled = false;
        config.Web.BindAddress = "127.0.0.1";
        config.Web.HttpPort = port;
        web?.Invoke(config.Web);
        config.WebUsers.Add(TestSupport.WebTestHost.User("admin", WebRole.Admin));
        await File.WriteAllTextAsync(paths.ConfigFile, JsonSerializer.Serialize(config, NutHubJson.File));

        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Services.AddLogging();
        builder.Services.AddNutHubCore(paths);
        builder.Services.AddNutHubWeb();
        _host = builder.Build();
        await _host.StartAsync();
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 400; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException();
    }
}
