using System.Net;
using System.Net.Http.Json;
using NutHub.Web.Middleware;
using NutHub.Web.Tests.TestSupport;

namespace NutHub.Web.Tests;

public sealed class SecurityTests
{
    [Fact]
    public async Task Requests_that_change_something_need_the_anti_forgery_header()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient client = host.Client(csrfHeader: false);

        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login",
                                                                   new { username = "admin", password = "admin-password" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("csrf", await Json.ErrorAsync(response));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/state")).StatusCode);

        var wrongValue = new HttpRequestMessage(HttpMethod.Post, "/api/auth/logout");
        wrongValue.Headers.Add(WebTestHost.CsrfHeader, "yes");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(wrongValue)).StatusCode);
    }

    [Theory]
    [InlineData("/api/auth/state")]
    [InlineData("/api/overview")]
    [InlineData("/api/no-such-thing")]
    [InlineData("/")]
    public async Task Every_answer_carries_the_security_headers(string path)
    {
        await using var host = await WebTestHost.StartAsync();
        HttpResponseMessage response = await host.Client().GetAsync(path);

        Assert.Equal(SecurityHeadersMiddleware.ContentSecurityPolicy,
                     Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
        Assert.Equal("nosniff", Assert.Single(response.Headers.GetValues("X-Content-Type-Options")));
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.Equal("DENY", Assert.Single(response.Headers.GetValues("X-Frame-Options")));
        Assert.True(response.Headers.Contains("Permissions-Policy"));
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
        Assert.False(response.Headers.Contains("Server"));
    }

    [Fact]
    public async Task Index_is_served_at_the_root_and_never_cached_without_revalidation()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpResponseMessage response = await host.Client().GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal("no-cache", response.Headers.CacheControl!.ToString());
        Assert.True(response.Headers.ETag is not null || response.Content.Headers.LastModified is not null);
    }

    [Fact]
    public async Task Unknown_paths_answer_404()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient client = host.Client();

        HttpResponseMessage api = await client.GetAsync("/api/does/not/exist");
        Assert.Equal(HttpStatusCode.NotFound, api.StatusCode);
        Assert.Equal("notFound", await Json.ErrorAsync(api));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/some/page")).StatusCode);
    }

    [Fact]
    public async Task Addresses_outside_the_allowed_networks_are_refused_before_anything_else()
    {
        await using var host = await WebTestHost.StartAsync(c => c.Web.AllowedNetworks = ["10.0.0.0/8"]);

        HttpResponseMessage refused = await host.Client(remote: "192.168.1.5").GetAsync("/");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("forbidden", await Json.ErrorAsync(refused));
        Assert.True(refused.Headers.Contains("Content-Security-Policy"));

        Assert.Equal(HttpStatusCode.OK, (await host.Client(remote: "10.1.2.3").GetAsync("/api/auth/state")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await host.Client(remote: "::ffff:10.1.2.3").GetAsync("/api/auth/state")).StatusCode);
    }

    [Fact]
    public async Task Anonymous_visitors_are_refused_unless_anonymous_read_is_on()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient anonymous = host.Client();

        HttpResponseMessage refused = await anonymous.GetAsync("/api/overview");
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        Assert.Equal("unauthorized", await Json.ErrorAsync(refused));

        HttpClient admin = await host.SignedInAsync("admin");
        HttpResponseMessage put = await admin.PutAsync("/api/admin/settings/web", Json.Body(new { allowAnonymousRead = true }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync("/api/events")).StatusCode);
        Assert.True((await Json.ReadAsync(await anonymous.GetAsync("/api/auth/state")))["anonymousRead"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.Unauthorized,
                     (await anonymous.PostAsync("/api/ups/sim1/fsd", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/admin/ups")).StatusCode);
    }

    [Theory]
    [InlineData("viewer", HttpStatusCode.OK, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden)]
    [InlineData("operator", HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.Forbidden)]
    [InlineData("admin", HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK)]
    public async Task Roles_open_their_endpoint_groups(string user, HttpStatusCode read, HttpStatusCode operate,
                                                       HttpStatusCode admin)
    {
        await using var host = await WebTestHost.StartAsync();
        await host.WaitForUpsAsync("sim1");
        HttpClient client = await host.SignedInAsync(user);

        Assert.Equal(read, (await client.GetAsync("/api/overview")).StatusCode);
        Assert.Equal(read, (await client.GetAsync("/api/ups/sim1")).StatusCode);
        Assert.Equal(read, (await client.GetAsync("/api/events")).StatusCode);

        HttpResponseMessage command = await client.PostAsync("/api/ups/sim1/commands",
                                                             Json.Body(new { command = "beeper.mute" }));
        Assert.Equal(operate, command.StatusCode);
        if (operate == HttpStatusCode.Forbidden)
        {
            Assert.Equal("forbidden", await Json.ErrorAsync(command));
        }

        Assert.Equal(operate, (await client.PostAsync("/api/host-protection/cancel", null)).StatusCode);

        foreach (string path in new[] { "/api/admin/ups", "/api/admin/settings", "/api/admin/web-users", "/api/admin/logs",
                                        "/api/admin/system", "/api/admin/notifications", "/api/admin/clients",
                                        "/api/admin/drivers", "/api/admin/host-protection" })
        {
            Assert.Equal(admin, (await client.GetAsync(path)).StatusCode);
        }
    }

    [Fact]
    public async Task Malformed_json_is_a_bad_request()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient admin = await host.SignedInAsync("admin");

        HttpResponseMessage response = await admin.PostAsync("/api/admin/ups", Json.Raw("{ not json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("badRequest", await Json.ErrorAsync(response));
    }

    [Fact]
    public async Task Https_requests_get_hsts_only_when_http_redirects_to_https()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpResponseMessage response = await host.Client(https: true).GetAsync("/api/auth/state");
        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }
}
