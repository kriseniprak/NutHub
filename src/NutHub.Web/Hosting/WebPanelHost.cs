using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Channels;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NutHub.Core;
using NutHub.Core.Configuration;
using NutHub.Core.Security;

namespace NutHub.Web.Hosting;

/// <summary>
/// Runs the web panel inside NutHub's host and follows the configuration: when the listeners change (address, ports,
/// HTTPS, certificate, redirect, enabled) the running instance is stopped, after the answer that caused the change
/// had time to leave, and a new one starts. A port that cannot be bound is logged and retried every
/// <see cref="RetryDelay"/>; it never stops NutHub.
/// </summary>
internal sealed class WebPanelHost(
    IServiceProvider services,
    IConfigStore config,
    NutHubPaths paths,
    ISecretProtector secrets,
    TimeProvider time,
    ILogger<WebPanelHost> logger) : BackgroundService
{
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    /// <summary>Lets the HTTP answer that changed the settings reach the browser before its listener goes away.</summary>
    public static readonly TimeSpan RestartDelay = TimeSpan.FromMilliseconds(500);

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly Channel<bool> _restart = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private volatile PanelBinding? _active;
    private volatile string? _lastError;

    /// <summary>The listeners of the running instance; null when the panel is disabled or could not start.</summary>
    public PanelBinding? Active => _active;

    /// <summary>The last start problem (port in use, certificate...), or null after a clean start.</summary>
    public string? LastError => _lastError;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield(); // Never delay the start of the other NutHub services.
        config.Changed += OnConfigChanged;
        Running? running = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                WebSettings web = config.Current.Web;
                bool retry = false;
                if (web.Enabled)
                {
                    (running, retry) = await TryStartAsync(web, stoppingToken).ConfigureAwait(false);
                }
                else
                {
                    logger.LogInformation("The web panel is disabled in the configuration.");
                }

                if (!await WaitForRestartAsync(retry ? RetryDelay : Timeout.InfiniteTimeSpan, stoppingToken).ConfigureAwait(false))
                {
                    break;
                }

                if (running is not null)
                {
                    await Task.Delay(RestartDelay, time, stoppingToken).ConfigureAwait(false);
                    logger.LogInformation("Restarting the web panel with its new settings.");
                    await StopAsync(running).ConfigureAwait(false);
                    running = null;
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            config.Changed -= OnConfigChanged;
            if (running is not null)
            {
                await StopAsync(running).ConfigureAwait(false);
            }
        }
    }

    private void OnConfigChanged(object? sender, ConfigChangedEventArgs e)
    {
        if (PanelBinding.RequiresRestart(e.Previous.Web, e.Current.Web))
        {
            _restart.Writer.TryWrite(true);
        }
    }

    /// <returns>False when NutHub is stopping.</returns>
    private async Task<bool> WaitForRestartAsync(TimeSpan timeout, CancellationToken stoppingToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        Task<bool> signal = _restart.Reader.ReadAsync(cts.Token).AsTask();
        try
        {
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                await signal.ConfigureAwait(false);
            }
            else
            {
                await Task.WhenAny(signal, Task.Delay(timeout, time, cts.Token)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        return !stoppingToken.IsCancellationRequested;
    }

    private async Task<(Running? Running, bool Retry)> TryStartAsync(WebSettings web, CancellationToken stoppingToken)
    {
        PanelBinding binding = PanelBinding.From(web);
        X509Certificate2? certificate = null;
        if (binding.HttpsEnabled)
        {
            try
            {
                certificate = CertificateProvider.LoadOrCreate(web.CertificatePath, secrets.Unprotect(web.CertificatePassword),
                                                               paths, "web-selfsigned.pfx");
            }
            catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException
                                           or ArgumentException or NotSupportedException)
            {
                _lastError = $"The HTTPS certificate could not be loaded: {ex.Message}";
                if (!binding.HttpEnabled)
                {
                    logger.LogError(ex, "The web panel cannot start: its HTTPS certificate ({Path}) could not be loaded. Retrying in {Seconds} s.",
                                    web.CertificatePath ?? "self-signed", RetryDelay.TotalSeconds);
                    return (null, true);
                }

                // Stay reachable over HTTP so the certificate can be fixed from the panel.
                logger.LogError(ex, "The HTTPS certificate of the web panel ({Path}) could not be loaded; serving HTTP only until the settings change.",
                                web.CertificatePath ?? "self-signed");
                binding = binding.WithoutHttps();
            }
        }

        WebApplication? app = null;
        try
        {
            app = WebPanelApp.Build(services, binding, certificate);
            await app.StartAsync(stoppingToken).ConfigureAwait(false);
            _active = binding;
            if (certificate is not null || !web.HttpsEnabled)
            {
                _lastError = null;
            }

            logger.LogInformation("Web panel listening on {Binding}.", binding);
            return (new Running(app, certificate), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
        {
            _active = null;
            if (IsAddressInUse(ex))
            {
                _lastError = $"The address is already in use or reserved ({binding}).";
                logger.LogError("The web panel cannot listen on {Binding}: the port is already in use by another program (or reserved). Retrying in {Seconds} s.",
                                binding, RetryDelay.TotalSeconds);
            }
            else
            {
                _lastError = ex.Message;
                logger.LogError(ex, "The web panel could not start on {Binding}. Retrying in {Seconds} s.",
                                binding, RetryDelay.TotalSeconds);
            }

            if (app is not null)
            {
                await DisposeQuietlyAsync(app).ConfigureAwait(false);
            }

            certificate?.Dispose();
            return (null, true);
        }
    }

    private async Task StopAsync(Running running)
    {
        _active = null;
        using var timeout = new CancellationTokenSource(StopTimeout);
        try
        {
            await running.App.StopAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The web panel did not stop cleanly.");
        }

        await DisposeQuietlyAsync(running.App).ConfigureAwait(false);
        running.Certificate?.Dispose();
    }

    private async Task DisposeQuietlyAsync(WebApplication app)
    {
        try
        {
            await app.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Disposing a web panel instance failed.");
        }
    }

    private static bool IsAddressInUse(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (e is AddressInUseException ||
                e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse or SocketError.AccessDenied })
            {
                return true;
            }
        }

        return false;
    }

    private sealed record Running(WebApplication App, X509Certificate2? Certificate);
}
