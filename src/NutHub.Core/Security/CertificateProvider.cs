using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NutHub.Core.Security;

/// <summary>
/// TLS certificates for the NUT server (STARTTLS) and the web panel (HTTPS): the one configured by the
/// administrator, or a self-signed one generated once and kept in the certificates directory.
/// </summary>
public static class CertificateProvider
{
    /// <summary>
    /// Loads the configured certificate, or creates / reuses a self-signed one.
    /// </summary>
    /// <param name="path">A .pfx/.p12 file, or a PEM file containing the certificate and its private key; null for
    /// self-signed.</param>
    /// <param name="password">The clear-text password of <paramref name="path"/>, if any.</param>
    /// <param name="paths">For the location of the self-signed certificate.</param>
    /// <param name="selfSignedName">File name of the self-signed certificate, e.g. "web-selfsigned.pfx".</param>
    public static X509Certificate2 LoadOrCreate(string? path, string? password, NutHubPaths paths, string selfSignedName)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return Load(path, password);
        }

        string file = Path.Combine(paths.CertificateDirectory, selfSignedName);
        if (File.Exists(file))
        {
            try
            {
                X509Certificate2 existing = X509CertificateLoader.LoadPkcs12FromFile(file, null);
                if (existing.NotAfter > DateTime.UtcNow.AddDays(30) && existing.HasPrivateKey)
                {
                    return existing;
                }

                existing.Dispose();
            }
            catch (CryptographicException)
            {
                // Corrupt or unreadable: make a new one below.
            }
        }

        X509Certificate2 created = CreateSelfSigned(Environment.MachineName);
        Directory.CreateDirectory(paths.CertificateDirectory);
        File.WriteAllBytes(file, created.Export(X509ContentType.Pfx));
        FilePermissions.TryRestrictFile(file);
        created.Dispose();
        return X509CertificateLoader.LoadPkcs12FromFile(file, null);
    }

    /// <summary>Loads a PKCS#12 or PEM certificate with its private key.</summary>
    public static X509Certificate2 Load(string path, string? password)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The certificate file '{path}' does not exist.", path);
        }

        string extension = Path.GetExtension(path).ToLowerInvariant();
        X509Certificate2 certificate;
        if (extension is ".pem" or ".crt" or ".cer")
        {
            string pem = File.ReadAllText(path);
            certificate = string.IsNullOrEmpty(password)
                ? X509Certificate2.CreateFromPem(pem, pem)
                : X509Certificate2.CreateFromEncryptedPem(pem, pem, password);

            // SslStream on Windows needs a key that is not ephemeral.
            if (OperatingSystem.IsWindows())
            {
                byte[] pfx = certificate.Export(X509ContentType.Pfx);
                certificate.Dispose();
                certificate = X509CertificateLoader.LoadPkcs12(pfx, null);
            }
        }
        else
        {
            certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password);
        }

        if (!certificate.HasPrivateKey)
        {
            certificate.Dispose();
            throw new CryptographicException($"The certificate '{path}' has no private key.");
        }

        return certificate;
    }

    /// <summary>A 10-year ECDSA P-256 server certificate for this machine's name, localhost and loopback.</summary>
    public static X509Certificate2 CreateSelfSigned(string hostName)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={hostName}, O=NutHub", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            [new Oid("1.3.6.1.5.5.7.3.1")], false)); // server authentication
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(hostName);
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));

        DateTimeOffset now = DateTimeOffset.UtcNow;
        return request.CreateSelfSigned(now.AddDays(-1), now.AddYears(10));
    }
}
