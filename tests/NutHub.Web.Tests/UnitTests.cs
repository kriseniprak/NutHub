using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using NutHub.Core.Configuration;
using NutHub.Web.Admin;
using NutHub.Web.Api;
using NutHub.Web.Auth;
using NutHub.Web.Hosting;

namespace NutHub.Web.Tests;

public sealed class UnitTests
{
    [Fact]
    public void Rate_limit_slides_with_time_and_success_clears_the_account()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var limiter = new LoginRateLimiter(time);
        for (int i = 0; i < LoginRateLimiter.MaxFailures; i++)
        {
            Assert.Null(limiter.GetRetryAfter("10.0.0.1", "bob"));
            limiter.RecordFailure("10.0.0.1", "bob");
            time.Advance(TimeSpan.FromSeconds(10));
        }

        TimeSpan wait = limiter.GetRetryAfter("10.0.0.2", "bob")!.Value;
        Assert.Equal(LoginRateLimiter.Window - TimeSpan.FromSeconds(100), wait);

        time.Advance(wait);
        Assert.Null(limiter.GetRetryAfter("10.0.0.2", "bob"));
        Assert.Null(limiter.GetRetryAfter("10.0.0.1", "alice"));

        limiter.RecordFailure("10.0.0.3", "carol");
        limiter.RecordSuccess("CAROL");
        for (int i = 0; i < LoginRateLimiter.MaxFailures - 1; i++)
        {
            limiter.RecordFailure("10.0.0.4", "carol");
        }

        Assert.Null(limiter.GetRetryAfter("10.0.0.5", "carol"));
    }

    [Fact]
    public void Attempts_count_while_they_run_and_long_names_are_not_kept_whole()
    {
        var limiter = new LoginRateLimiter(new FakeTimeProvider(DateTimeOffset.UtcNow));
        for (int i = 0; i < LoginRateLimiter.MaxFailures - 1; i++)
        {
            Assert.Null(limiter.TryBeginAttempt($"10.0.1.{i}", "bob"));
        }

        Assert.Null(limiter.TryBeginAttempt("10.0.1.100", "bob"));
        // Ten attempts of bob are being checked: an eleventh waits, whatever its address.
        Assert.Equal(TimeSpan.FromSeconds(1), limiter.TryBeginAttempt("10.0.1.200", "BOB"));
        limiter.EndAttempt("10.0.1.100", "bob", failed: false);
        Assert.Null(limiter.GetRetryAfter("10.0.1.200", "bob"));

        // A failed attempt costs a bounded amount of memory for five minutes, however long the name typed.
        string name = new('x', 1_000_000);
        long before = GC.GetAllocatedBytesForCurrentThread();
        limiter.RecordFailure("10.0.2.1", name);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 64 * 1024);
    }

    [Fact]
    public void Only_listener_settings_restart_the_panel()
    {
        var web = new WebSettings();
        Assert.False(PanelBinding.RequiresRestart(web, Copy(web, w => w.SessionHours = 1)));
        Assert.False(PanelBinding.RequiresRestart(web, Copy(web, w => w.AllowAnonymousRead = true)));
        Assert.False(PanelBinding.RequiresRestart(web, Copy(web, w => w.CertificatePassword = "x")));
        Assert.True(PanelBinding.RequiresRestart(web, Copy(web, w => w.HttpPort = 1234)));
        Assert.True(PanelBinding.RequiresRestart(web, Copy(web, w => w.Enabled = false)));
        var https = Copy(web, w => w.HttpsEnabled = true);
        Assert.True(PanelBinding.RequiresRestart(https, Copy(https, w => w.CertificatePath = "/etc/cert.pfx")));
    }

    [Fact]
    public void Section_merge_keeps_absent_members_and_secret_rules()
    {
        var web = new WebSettings { HttpPort = 8000, CertificatePassword = "enc:v1:stored", AllowedNetworks = ["10.0.0.0/8"] };

        WebSettings kept = SectionJson.Merge(web, Parse("""{"httpPort":8100,"certificatePassword":null,"certificatePasswordSet":false,"unknown":1}"""));
        Assert.Equal(8100, kept.HttpPort);
        Assert.Equal("enc:v1:stored", kept.CertificatePassword);
        Assert.Equal(["10.0.0.0/8"], kept.AllowedNetworks);

        Assert.Null(SectionJson.Merge(web, Parse("""{"certificatePassword":""}""")).CertificatePassword);
        Assert.Equal("new", SectionJson.Merge(web, Parse("""{"certificatePassword":"new"}""")).CertificatePassword);

        var nut = new NutServerSettings();
        nut.Tls.CertificatePath = "/tls.pem";
        NutServerSettings merged = SectionJson.Merge(nut, Parse("""{"tls":{"enabled":true}}"""));
        Assert.True(merged.Tls.Enabled);
        Assert.Equal("/tls.pem", merged.Tls.CertificatePath);

        var error = Assert.Throws<ApiException>(() => SectionJson.Merge(web, Parse("""{"httpPort":"x"}""")));
        Assert.Equal("validation", error.Code);
        Assert.True(error.Fields!.ContainsKey("httpPort"));
    }

    [Fact]
    public void Timestamps_are_utc_with_milliseconds()
    {
        var value = new DateTimeOffset(2026, 9, 21, 16, 3, 12, 345, TimeSpan.FromHours(2));
        Assert.Equal("\"2026-09-21T14:03:12.345Z\"", JsonSerializer.Serialize(value, ApiJson.Options));
        Assert.Equal("null", JsonSerializer.Serialize((DateTimeOffset?)null, ApiJson.Options));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static WebSettings Copy(WebSettings web, Action<WebSettings> change)
    {
        WebSettings copy = NutHubJson.Clone(web);
        change(copy);
        return copy;
    }
}
