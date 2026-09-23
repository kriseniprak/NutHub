using System.Security.Cryptography.X509Certificates;
using NutHub.Core.Security;
using NutHub.Protocol.Security;
using NutHub.Protocol.Tests.Infrastructure;
using Xunit.Abstractions;

namespace NutHub.Protocol.Tests.Server;

public sealed class TlsTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Starttls_is_refused_when_not_configured()
    {
        await using var host = await NutTestHost.StartAsync(output);
        Assert.False(host.Server.TlsAvailable);
        await using var client = await host.ConnectAsync();
        Assert.Equal("ERR FEATURE-NOT-CONFIGURED", await client.CommandAsync("STARTTLS"));
        Assert.Equal("1.3", await client.CommandAsync("NETVER"));
    }

    [Fact]
    public async Task Starttls_with_the_self_signed_certificate()
    {
        await using var host = await NutTestHost.StartAsync(output, c => c.Nut.Tls.Enabled = true);
        await NutTestHost.WaitUntilAsync(() => host.Server.TlsAvailable, "the certificate");
        Assert.True(File.Exists(Path.Combine(host.Paths.CertificateDirectory, NutTlsCertificates.SelfSignedFileName)));

        await using var client = await host.ConnectAsync();
        await client.StartTlsAsync();
        Assert.NotNull(client.TlsProtocol);
        Assert.Contains("O=NutHub", client.RemoteCertificate!.Subject);

        Assert.Equal("1.3", await client.CommandAsync("NETVER"));
        Assert.Equal("ERR ALREADY-SSL-MODE", await client.CommandAsync("STARTTLS"));
        Assert.True(Assert.Single(host.Sessions.Sessions).Tls);

        Assert.Equal("OK", await client.CommandAsync("USERNAME monsecondary"));
        Assert.Equal("OK", await client.CommandAsync("PASSWORD " + NutTestClient.Quote(NutTestHost.Password)));
        Assert.Equal("OK", await client.CommandAsync("LOGIN ups1"));
        List<string> vars = await client.ListAsync("LIST VAR ups1");
        Assert.Equal("END LIST VAR ups1", vars[^1]);

        Assert.Equal("OK Goodbye", await client.CommandAsync("LOGOUT"));
        Assert.True(await client.WaitForCloseAsync());
    }

    [Fact]
    public async Task Starttls_with_a_configured_certificate()
    {
        string pfx = Path.Combine(Path.GetTempPath(), "nuthub-protocol-tests", Guid.NewGuid().ToString("N") + ".pfx");
        Directory.CreateDirectory(Path.GetDirectoryName(pfx)!);
        using (X509Certificate2 created = CertificateProvider.CreateSelfSigned("nut-test-host"))
        {
            File.WriteAllBytes(pfx, created.Export(X509ContentType.Pfx, "pfx-pass"));
        }

        try
        {
            await using var host = await NutTestHost.StartAsync(output, c =>
            {
                c.Nut.Tls.Enabled = true;
                c.Nut.Tls.CertificatePath = pfx;
                c.Nut.Tls.CertificatePassword = "pfx-pass";
            });
            await NutTestHost.WaitUntilAsync(() => host.Server.TlsAvailable, "the certificate");

            await using var client = await host.ConnectAsync();
            await client.StartTlsAsync();
            Assert.Contains("CN=nut-test-host", client.RemoteCertificate!.Subject);
            Assert.Equal("1.3", await client.CommandAsync("PROTVER"));
        }
        finally
        {
            File.Delete(pfx);
        }
    }

    [Fact]
    public async Task A_missing_certificate_is_reported_and_starttls_refused()
    {
        await using var host = await NutTestHost.StartAsync(output, c =>
        {
            c.Nut.Tls.Enabled = true;
            c.Nut.Tls.CertificatePath = Path.Combine(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + ".pfx");
        });
        await NutTestHost.WaitUntilAsync(() => host.Server.LastError is not null, "the error");
        Assert.Contains("certificate", host.Server.LastError);
        Assert.False(host.Server.TlsAvailable);

        await using var client = await host.ConnectAsync();
        Assert.Equal("ERR FEATURE-NOT-CONFIGURED", await client.CommandAsync("STARTTLS"));
    }

    [Fact]
    public async Task Enabling_tls_applies_to_existing_connections()
    {
        await using var host = await NutTestHost.StartAsync(output);
        await using var client = await host.ConnectAsync();
        Assert.Equal("ERR FEATURE-NOT-CONFIGURED", await client.CommandAsync("STARTTLS"));

        await host.Config.UpdateAsync(c => c.Nut.Tls.Enabled = true);
        await NutTestHost.WaitUntilAsync(() => host.Server.TlsAvailable, "the certificate");

        await client.StartTlsAsync();
        Assert.Equal("1.3", await client.CommandAsync("NETVER"));
    }

    [Fact]
    public async Task A_failed_handshake_closes_only_that_connection()
    {
        await using var host = await NutTestHost.StartAsync(output, c => c.Nut.Tls.Enabled = true);
        await NutTestHost.WaitUntilAsync(() => host.Server.TlsAvailable, "the certificate");

        await using var broken = await host.ConnectAsync();
        Assert.Equal("OK STARTTLS", await broken.CommandAsync("STARTTLS"));
        await broken.SendAsync("this is not a TLS client hello");
        Assert.True(await broken.WaitForCloseAsync());

        await using var fine = await host.ConnectAsync();
        Assert.Equal("1.3", await fine.CommandAsync("NETVER"));
    }
}
