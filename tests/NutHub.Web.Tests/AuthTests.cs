using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Core.Security;
using NutHub.Web.Auth;
using NutHub.Web.Tests.TestSupport;

namespace NutHub.Web.Tests;

public sealed class AuthTests
{
    [Fact]
    public async Task State_of_an_anonymous_visitor_is_not_authenticated()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpResponseMessage response = await host.Client().GetAsync("/api/auth/state");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var state = await Json.ReadAsync(response);
        Assert.False(state["authenticated"]!.GetValue<bool>());
        Assert.Null(state["user"]);
        Assert.False(state["anonymousRead"]!.GetValue<bool>());
        Assert.Equal("NutHub", state["serverName"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(state["version"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Login_sets_a_strict_http_only_cookie_and_records_an_event()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient client = host.Client();
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login",
                                                                   new { username = "admin", password = "admin-password" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await Json.ReadAsync(response);
        Assert.Equal("admin", body["user"]!["name"]!.GetValue<string>());
        Assert.Equal("admin", body["user"]!["role"]!.GetValue<string>());
        Assert.False(body["user"]!["mustChangePassword"]!.GetValue<bool>());
        string cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("nuthub_session=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        var state = await Json.ReadAsync(await client.GetAsync("/api/auth/state"));
        Assert.True(state["authenticated"]!.GetValue<bool>());
        Assert.Equal("ADMIN", state["user"]!["displayName"]!.GetValue<string>());

        await AssertEventAsync(host, UpsEventType.UserLogin);
    }

    [Theory]
    [InlineData("admin", "wrong-password")]
    [InlineData("nobody", "whatever-password")]
    public async Task Wrong_credentials_answer_401_and_record_an_event(string user, string password)
    {
        await using var host = await WebTestHost.StartAsync();
        HttpResponseMessage response = await host.Client().PostAsJsonAsync("/api/auth/login", new { username = user, password });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("invalidCredentials", await Json.ErrorAsync(response));
        await AssertEventAsync(host, UpsEventType.UserLoginFailed);
    }

    [Fact]
    public async Task Disabled_account_cannot_sign_in()
    {
        await using var host = await WebTestHost.StartAsync(c => c.WebUsers.Single(u => u.Name == "viewer").Disabled = true);
        HttpResponseMessage response = await host.Client().PostAsJsonAsync(
            "/api/auth/login", new { username = "viewer", password = "viewer-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient client = await host.SignedInAsync("viewer");

        HttpResponseMessage response = await client.PostAsync("/api/auth/logout", null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/overview")).StatusCode);
    }

    [Fact]
    public async Task Too_many_failures_from_one_address_are_rate_limited()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient client = host.Client(remote: "192.0.2.10");
        for (int i = 0; i < LoginRateLimiter.MaxFailures; i++)
        {
            var failed = await client.PostAsJsonAsync("/api/auth/login", new { username = "user" + i, password = "bad-password" });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        // Even the right password is refused now.
        HttpResponseMessage response = await client.PostAsJsonAsync("/api/auth/login",
                                                                   new { username = "admin", password = "admin-password" });

        Assert.Equal((HttpStatusCode)429, response.StatusCode);
        var body = await Json.ReadAsync(response);
        Assert.Equal("rateLimited", body["error"]!.GetValue<string>());
        int retryAfter = body["retryAfterSeconds"]!.GetValue<int>();
        Assert.InRange(retryAfter, 1, 300);
        Assert.Equal(retryAfter.ToString(), Assert.Single(response.Headers.GetValues("Retry-After")));

        // Another address is not affected.
        HttpResponseMessage other = await host.Client(remote: "192.0.2.11").PostAsJsonAsync(
            "/api/auth/login", new { username = "admin", password = "admin-password" });
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task Ipv6_clients_are_rate_limited_by_64_bit_prefix()
    {
        // A host picks any address of its prefix at will: a new address must not mean ten new attempts.
        await using var host = await WebTestHost.StartAsync();
        for (int i = 0; i < LoginRateLimiter.MaxFailures; i++)
        {
            var failed = await host.Client(remote: $"2001:db8:1:2::{i + 1:x}").PostAsJsonAsync(
                "/api/auth/login", new { username = "user" + i, password = "bad-password" });
            Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);
        }

        HttpResponseMessage response = await host.Client(remote: "2001:db8:1:2:ffff::1").PostAsJsonAsync(
            "/api/auth/login", new { username = "admin", password = "admin-password" });
        Assert.Equal((HttpStatusCode)429, response.StatusCode);

        HttpResponseMessage other = await host.Client(remote: "2001:db8:1:3::1").PostAsJsonAsync(
            "/api/auth/login", new { username = "admin", password = "admin-password" });
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
    }

    [Fact]
    public async Task Too_many_failures_for_one_account_are_rate_limited_from_any_address()
    {
        await using var host = await WebTestHost.StartAsync();
        for (int i = 0; i < LoginRateLimiter.MaxFailures; i++)
        {
            await host.Client(remote: $"198.51.100.{i + 1}").PostAsJsonAsync(
                "/api/auth/login", new { username = "operator", password = "bad-password" });
        }

        HttpResponseMessage response = await host.Client(remote: "198.51.100.200").PostAsJsonAsync(
            "/api/auth/login", new { username = "OPERATOR", password = "operator-password" });
        Assert.Equal((HttpStatusCode)429, response.StatusCode);
    }

    [Fact]
    public async Task Attempts_still_being_checked_count_against_the_limit()
    {
        // Overlapping requests: while the first wrong password is being checked, the others arrive.
        var hasher = new ReentrantHasher(WebTestHost.Hasher);
        await using var host = await WebTestHost.StartAsync(services: s => s.AddSingleton<IPasswordHasher>(hasher));
        var statuses = new ConcurrentBag<HttpStatusCode>();
        hasher.DuringFirstCheck = () =>
        {
            for (int i = 0; i < LoginRateLimiter.MaxFailures; i++)
            {
                HttpResponseMessage response = host.Client(remote: $"198.51.100.{i + 1}")
                    .PostAsJsonAsync("/api/auth/login", new { username = "operator", password = "bad-password" })
                    .GetAwaiter().GetResult();
                statuses.Add(response.StatusCode);
            }
        };

        HttpResponseMessage first = await host.Client(remote: "198.51.100.200").PostAsJsonAsync(
            "/api/auth/login", new { username = "operator", password = "bad-password" });
        statuses.Add(first.StatusCode);

        // However the attempts overlap, no more passwords are checked than the limit allows.
        Assert.Equal(LoginRateLimiter.MaxFailures, statuses.Count(s => s == HttpStatusCode.Unauthorized));
        Assert.Equal(1, statuses.Count(s => s == (HttpStatusCode)429));
    }

    [Fact]
    public async Task A_new_session_length_also_applies_to_open_sessions()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var host = await WebTestHost.StartAsync(services: s => s.AddSingleton<TimeProvider>(time));
        HttpClient idle = await host.SignedInAsync("viewer");
        HttpClient active = await host.SignedInAsync("operator");
        HttpClient admin = await host.SignedInAsync("admin");

        // Opened for 12 hours; the administrator shortens sessions to one hour without activity.
        HttpResponseMessage put = await admin.PutAsync("/api/admin/settings/web", Json.Body(new { sessionHours = 1 }));
        Assert.Equal(HttpStatusCode.OK, put.StatusCode);

        time.Advance(TimeSpan.FromMinutes(40));
        Assert.Equal(HttpStatusCode.OK, (await active.GetAsync("/api/overview")).StatusCode);
        time.Advance(TimeSpan.FromMinutes(40));

        Assert.Equal(HttpStatusCode.Unauthorized, (await idle.GetAsync("/api/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await active.GetAsync("/api/overview")).StatusCode);
    }

    [Fact]
    public async Task Must_change_password_blocks_the_api_until_the_password_is_changed()
    {
        await using var host = await WebTestHost.StartAsync(c =>
            c.WebUsers.Add(WebTestHost.User("fresh", WebRole.Admin, mustChangePassword: true)));
        HttpClient client = await host.SignedInAsync("fresh");

        HttpResponseMessage blocked = await client.GetAsync("/api/overview");
        Assert.Equal(HttpStatusCode.Forbidden, blocked.StatusCode);
        Assert.Equal("passwordChangeRequired", await Json.ErrorAsync(blocked));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/auth/state")).StatusCode);

        HttpResponseMessage tooShort = await client.PostAsJsonAsync("/api/auth/password",
            new { currentPassword = "fresh-password", newPassword = "short" });
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.NotNull((await Json.ReadAsync(tooShort))["fields"]!["newPassword"]);

        HttpResponseMessage same = await client.PostAsJsonAsync("/api/auth/password",
            new { currentPassword = "fresh-password", newPassword = "fresh-password" });
        Assert.Equal("validation", await Json.ErrorAsync(same));

        HttpResponseMessage wrong = await client.PostAsJsonAsync("/api/auth/password",
            new { currentPassword = "not-the-password", newPassword = "a-new-password" });
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal("invalidCredentials", await Json.ErrorAsync(wrong));

        HttpResponseMessage changed = await client.PostAsJsonAsync("/api/auth/password",
            new { currentPassword = "fresh-password", newPassword = "a-new-password" });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/overview")).StatusCode);
        var state = await Json.ReadAsync(await client.GetAsync("/api/auth/state"));
        Assert.False(state["user"]!["mustChangePassword"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Changing_the_password_ends_the_other_sessions_of_the_account()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient first = await host.SignedInAsync("operator");
        HttpClient second = await host.SignedInAsync("operator");
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/overview")).StatusCode);

        HttpResponseMessage changed = await first.PostAsJsonAsync("/api/auth/password",
            new { currentPassword = "operator-password", newPassword = "another-password" });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync("/api/overview")).StatusCode);
        HttpResponseMessage ended = await second.GetAsync("/api/overview");
        Assert.Equal(HttpStatusCode.Unauthorized, ended.StatusCode);
        Assert.Equal("unauthorized", await Json.ErrorAsync(ended));
    }

    [Fact]
    public async Task Password_change_needs_a_session()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpResponseMessage response = await host.Client().PostAsJsonAsync("/api/auth/password",
            new { currentPassword = "x", newPassword = "y-long-enough" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static async Task AssertEventAsync(WebTestHost host, UpsEventType type)
    {
        var store = host.Services.GetRequiredService<IEventStore>();
        await host.WaitUntilAsync(() => store.QueryAsync(new EventQuery()).Result.Items.Any(e => e.Type == type),
                                  type.ToString());
        UpsEvent e = (await store.QueryAsync(new EventQuery())).Items.First(x => x.Type == type);
        Assert.StartsWith("web:", e.Actor);
        Assert.Contains("127.0.0.1", e.Actor);
    }

    /// <summary>Runs <see cref="DuringFirstCheck"/> in the middle of the first password check.</summary>
    private sealed class ReentrantHasher(IPasswordHasher inner) : IPasswordHasher
    {
        private int _started;

        public Action? DuringFirstCheck { get; set; }

        public string Hash(string password) => inner.Hash(password);

        public bool Verify(string password, string? hash)
        {
            if (Interlocked.Exchange(ref _started, 1) == 0)
            {
                DuringFirstCheck?.Invoke();
            }

            return inner.Verify(password, hash);
        }
    }
}
