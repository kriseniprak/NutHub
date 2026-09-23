using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Logging;
using NutHub.Core;
using NutHub.Core.Configuration;
using NutHub.Core.Security;

namespace NutHub.Protocol.Security;

/// <summary>
/// The certificate STARTTLS presents: the configured one, or a self-signed one generated once in the certificates
/// directory ("nut-selfsigned.pfx"). Loaded when TLS is enabled or its settings change, not at every handshake.
/// A certificate that failed to load, or a configured file that was replaced (a renewal), is loaded again at most
/// every <see cref="NutProtocolOptions.BindRetryInterval"/>; a renewal that fails keeps the previous certificate.
/// </summary>
internal sealed class NutTlsCertificates
{
    public const string SelfSignedFileName = "nut-selfsigned.pfx";

    private readonly ISecretProtector _secrets;
    private readonly NutHubPaths _paths;
    private readonly TimeProvider _time;
    private readonly NutProtocolOptions _options;
    private readonly ILogger _logger;
    private readonly object _lock = new();

    private (string? Path, string? Password)? _loadedFor;
    private X509Certificate2? _certificate;
    private DateTime? _loadedStamp;
    private string? _error;
    private DateTimeOffset _lastAttempt;

    public NutTlsCertificates(ISecretProtector secrets, NutHubPaths paths, TimeProvider time,
                              NutProtocolOptions options, ILogger logger)
    {
        _secrets = secrets;
        _paths = paths;
        _time = time;
        _options = options;
        _logger = logger;
    }

    /// <summary>The certificate for STARTTLS, or null when TLS is off or the certificate cannot be loaded.</summary>
    public X509Certificate2? Certificate
    {
        get
        {
            lock (_lock)
            {
                return _certificate;
            }
        }
    }

    /// <summary>Why the certificate could not be loaded, or null.</summary>
    public string? Error
    {
        get
        {
            lock (_lock)
            {
                return _error;
            }
        }
    }

    /// <summary>
    /// Brings the certificate in line with the settings. Callers serialise calls (the server does it under its
    /// reconciliation lock); reading <see cref="Certificate"/> is safe at any time.
    /// </summary>
    public void Apply(NutTlsSettings tls)
    {
        if (!tls.Enabled)
        {
            lock (_lock)
            {
                // Not disposed: a handshake in progress may still use it; the finalizer releases it.
                _certificate = null;
                _error = null;
                _loadedFor = null;
            }

            return;
        }

        var wanted = (tls.CertificatePath, tls.CertificatePassword);
        DateTime? stamp = FileStamp(tls.CertificatePath);
        DateTimeOffset now = _time.GetUtcNow();
        bool same;
        lock (_lock)
        {
            same = _loadedFor == wanted;

            // Loaded and unchanged: nothing to do. Otherwise (failure, or a renewed file) try again, but not more
            // often than the retry interval.
            if (same && _certificate is not null && stamp == _loadedStamp)
            {
                return;
            }

            if (same && now - _lastAttempt < _options.BindRetryInterval)
            {
                return;
            }

            _lastAttempt = now;
        }

        X509Certificate2? certificate = null;
        string? error = null;
        try
        {
            string? password = _secrets.Unprotect(tls.CertificatePassword);
            certificate = CertificateProvider.LoadOrCreate(tls.CertificatePath, password, _paths, SelfSignedFileName);
            _logger.LogInformation("NUT STARTTLS certificate: {Subject}, valid until {NotAfter:yyyy-MM-dd} ({Source}).",
                                   certificate.Subject, certificate.NotAfter,
                                   string.IsNullOrWhiteSpace(tls.CertificatePath) ? "self-signed" : tls.CertificatePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException
                                       or FormatException or ArgumentException or NotSupportedException)
        {
            error = $"The STARTTLS certificate cannot be loaded: {ex.Message}";
            _logger.LogError("{Error}", error);
        }

        lock (_lock)
        {
            if (certificate is not null)
            {
                _certificate = certificate;
                _loadedStamp = stamp;
            }
            else if (!same)
            {
                _certificate = null; // settings changed: never keep presenting the old certificate
            }

            _error = error;
            _loadedFor = wanted;
        }
    }

    private static DateTime? FileStamp(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException)
        {
            return null;
        }
    }
}
