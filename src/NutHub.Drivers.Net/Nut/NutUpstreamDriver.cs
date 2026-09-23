using Microsoft.Extensions.Logging;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Drivers.Net.Common;

namespace NutHub.Drivers.Net.Nut;

/// <summary>
/// Repeats a UPS of an upstream NUT server. Every poll runs LIST VAR; LIST RW and LIST CMD run at connection and then
/// every <see cref="MetadataRefreshInterval"/>, with GET TYPE / LIST ENUM / LIST RANGE for writable variables not
/// described yet.
/// <para>
/// Commands and variable writes travel on the polling connection rather than on a second one: the upstream server
/// then sees one client per repeated UPS (NAS firmwares limit the number of clients), the credentials and the LOGIN
/// live on a single session, and a command is never sent on a connection the poll loop has not validated. The
/// connection serialises exchanges one request at a time, so a command waits for at most one poll request, never
/// for a whole poll.
/// </para>
/// </summary>
internal sealed partial class NutUpstreamDriver : IUpsDriver
{
    private readonly string _upsName;
    private readonly NutUpstreamSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private NutUpstreamSession? _session;
    private int _disposed;

    public NutUpstreamDriver(string upsName, NutUpstreamSettings settings, TimeProvider time, ILogger<NutUpstreamDriver> logger)
    {
        _upsName = upsName;
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    /// <summary>How often the writable variables and the commands are listed again.</summary>
    internal TimeSpan MetadataRefreshInterval { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The waits between reconnection attempts; null for <see cref="ReconnectBackoff.DefaultDelays"/>.</summary>
    internal IReadOnlyList<TimeSpan>? ReconnectDelays { get; init; }

    /// <summary>"host:port", for messages.</summary>
    private string Upstream => _settings.Client.Endpoint;

    private NutUpstreamSession? CurrentSession => Volatile.Read(ref _session);

    public async Task RunAsync(IDriverContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var reporter = new DriverStateReporter(context);
        var backoff = new ReconnectBackoff(ReconnectDelays);
        reporter.Connecting($"Connecting to the NUT server {Upstream}");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            NutUpstreamSession? session = null;
            try
            {
                session = await OpenSessionAsync(cancellationToken).ConfigureAwait(false);
                Volatile.Write(ref _session, session);
                _logger.LogInformation("{Ups}: connected to {Upstream}{Tls} ({Version}).", _upsName, Upstream,
                                       session.Connection.IsTls ? " with TLS" : string.Empty,
                                       session.ServerVersion ?? "unknown version");
                await PollLoopAsync(session, context, reporter, backoff, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (NetworkErrors.IsTransient(ex) || ex is NutErrorException or OperationCanceledException)
            {
                string reason = session is null
                    ? $"Cannot connect to the NUT server {Upstream}: {NetworkErrors.Describe(ex)}."
                    : $"Lost the connection to the NUT server {Upstream}: {NetworkErrors.Describe(ex)}.";
                reporter.Disconnected(reason);
            }
            finally
            {
                if (session is not null)
                {
                    Interlocked.CompareExchange(ref _session, null, session);
                    await CloseSessionAsync(session).ConfigureAwait(false);
                }
            }

            await Task.Delay(backoff.NextDelay(), _time, cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        NutUpstreamSession? session = Interlocked.Exchange(ref _session, null);
        if (session is not null)
        {
            await CloseSessionAsync(session).ConfigureAwait(false);
        }
    }

    /// <summary>Connects, negotiates TLS, reads the version and authenticates as configured.</summary>
    private async Task<NutUpstreamSession> OpenSessionAsync(CancellationToken cancellationToken)
    {
        NutClientConnection connection =
            await NutClientConnection.ConnectAsync(_settings.Client, _time, cancellationToken).ConfigureAwait(false);
        try
        {
            string? version = null;
            try
            {
                version = ShortenVersion(await connection.RequestRawAsync("VER", cancellationToken).ConfigureAwait(false));
            }
            catch (NutErrorException)
            {
                // Some servers do not implement VER; the version is informational only.
            }

            bool authenticated = false, tracking = false;
            string? authError = null;
            if (_settings.HasCredentials)
            {
                try
                {
                    await connection.RequestAsync(NutLine.Build("USERNAME", _settings.Username!), cancellationToken)
                        .ConfigureAwait(false);
                    await connection.RequestAsync(NutLine.Build("PASSWORD", _settings.Password!), cancellationToken)
                        .ConfigureAwait(false);
                    authenticated = true;
                }
                catch (NutErrorException ex)
                {
                    // Monitoring needs no credentials: keep going, commands will explain the refusal.
                    authError = $"ERR {ex.Code}";
                    _logger.LogWarning("{Ups}: {Upstream} refused the username or password ({Error}); instant commands " +
                                       "and variable writes will not work.", _upsName, Upstream, authError);
                }
            }

            if (authenticated)
            {
                try
                {
                    await connection.RequestAsync("SET TRACKING ON", cancellationToken).ConfigureAwait(false);
                    tracking = true;
                }
                catch (NutErrorException ex)
                {
                    // Servers before NUT 2.8.0 have no tracking; their OK only means "queued".
                    _logger.LogDebug("{Ups}: {Upstream} does not support command tracking ({Error}).",
                                     _upsName, Upstream, ex.Message);
                }

                if (_settings.Login)
                {
                    try
                    {
                        await connection.RequestAsync(NutLine.Build("LOGIN", _settings.RemoteUps), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (NutErrorException ex) when (ex.Code == "ALREADY-LOGGED-IN")
                    {
                        // Harmless.
                    }
                    catch (NutErrorException ex)
                    {
                        _logger.LogWarning("{Ups}: {Upstream} refused LOGIN {Remote} ({Error}); it will not count this " +
                                           "server as a secondary. The user needs the upsmon role.",
                                           _upsName, Upstream, _settings.RemoteUps, ex.Message);
                    }
                }
            }

            return new NutUpstreamSession(connection, version)
            {
                Authenticated = authenticated,
                AuthenticationError = authError,
                Tracking = tracking,
            };
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task CloseSessionAsync(NutUpstreamSession session)
    {
        using var logout = new CancellationTokenSource(TimeSpan.FromSeconds(1), _time);
        await session.Connection.TryLogoutAsync(logout.Token).ConfigureAwait(false);
        await session.Connection.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Polls until the connection fails (throws) or the driver is cancelled.</summary>
    private async Task PollLoopAsync(NutUpstreamSession session, IDriverContext context, DriverStateReporter reporter,
                                     ReconnectBackoff backoff, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(context.PollInterval, _time);
        do
        {
            if (await PollOnceAsync(session, reporter, cancellationToken).ConfigureAwait(false))
            {
                backoff.Reset();
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// One poll. ERR answers (stale data, upstream driver down, unknown UPS) are reported and polling goes on over
    /// the same connection, which is healthy; communication failures propagate so the caller reconnects.
    /// </summary>
    /// <returns>True when fresh data was published.</returns>
    internal async Task<bool> PollOnceAsync(NutUpstreamSession session, DriverStateReporter reporter,
                                            CancellationToken cancellationToken)
    {
        NutClientConnection connection = session.Connection;
        DateTimeOffset now = _time.GetUtcNow();
        if (now >= session.NextMetadataRefresh)
        {
            try
            {
                await RefreshMetadataAsync(session, cancellationToken).ConfigureAwait(false);
                session.NextMetadataRefresh = now + MetadataRefreshInterval;
            }
            catch (NutErrorException ex)
            {
                // Typically DATA-STALE while the upstream driver starts; LIST VAR below reports it, and the
                // metadata is asked again at the next poll.
                _logger.LogDebug("{Ups}: listing the writable variables and commands failed: {Error}", _upsName, ex.Message);
            }
        }

        List<List<string>> items;
        try
        {
            items = await connection.ListAsync("VAR", [_settings.RemoteUps], cancellationToken).ConfigureAwait(false);
        }
        catch (NutErrorException ex)
        {
            reporter.Disconnected(NutErrorCodes.DescribeReadFailure(ex, _settings.RemoteUps, Upstream));
            return false;
        }

        var variables = new Dictionary<string, string>(items.Count + 1, StringComparer.Ordinal);
        foreach (List<string> item in items)
        {
            // item: [ups, name, value]. The upstream driver.* variables describe the upstream driver; NutHub adds
            // its own, so only the summary below is kept.
            if (item.Count >= 3 && !item[1].StartsWith("driver.", StringComparison.Ordinal))
            {
                variables[item[1]] = item[2];
            }
        }

        if (variables.Count == 0)
        {
            reporter.Disconnected($"The NUT server {Upstream} returned no variables for '{_settings.RemoteUps}'.");
            return false;
        }

        variables["driver.version.data"] = session.ServerVersion is null
            ? $"upstream {Upstream}"
            : $"upstream {Upstream} {session.ServerVersion}";

        reporter.Publish(new DriverUpdate
        {
            Variables = variables,
            VariableInfo = session.HasMetadata ? session.VariableInfo : null,
            Commands = session.HasMetadata ? session.Commands : null,
        });
        return true;
    }

    /// <summary>
    /// LIST RW and LIST CMD, then the type, enum values and ranges of writable variables not described yet on this
    /// connection. Descriptions are cached per connection: they rarely change, and a reconnection (for example after
    /// the upstream server restarted with a new configuration) starts from scratch.
    /// </summary>
    private async Task RefreshMetadataAsync(NutUpstreamSession session, CancellationToken cancellationToken)
    {
        NutClientConnection connection = session.Connection;
        string ups = _settings.RemoteUps;
        List<List<string>> rw = await connection.ListAsync("RW", [ups], cancellationToken).ConfigureAwait(false);
        List<List<string>> cmd = await connection.ListAsync("CMD", [ups], cancellationToken).ConfigureAwait(false);

        IReadOnlyDictionary<string, VariableInfo> known = session.VariableInfo;
        var info = new Dictionary<string, VariableInfo>(StringComparer.Ordinal);
        foreach (List<string> item in rw)
        {
            if (item.Count < 2 || info.ContainsKey(item[1]) || item[1].StartsWith("driver.", StringComparison.Ordinal))
            {
                continue;
            }

            string name = item[1];
            info[name] = known.TryGetValue(name, out VariableInfo? cached)
                ? cached
                : await DescribeVariableAsync(connection, name, cancellationToken).ConfigureAwait(false);
        }

        var commands = new SortedSet<string>(StringComparer.Ordinal);
        foreach (List<string> item in cmd)
        {
            if (item.Count >= 2 && NutLine.IsTransmittable(item[1]))
            {
                commands.Add(item[1]);
            }
        }

        session.VariableInfo = info;
        session.Commands = commands.ToArray();
        session.HasMetadata = true;
    }

    private async Task<VariableInfo> DescribeVariableAsync(NutClientConnection connection, string name,
                                                           CancellationToken cancellationToken)
    {
        string ups = _settings.RemoteUps;
        NutTypeDescription type;
        try
        {
            List<string> words = await connection.RequestAsync(NutLine.Build("GET TYPE", ups, name), cancellationToken)
                .ConfigureAwait(false);
            type = words.Count >= 3 && words[0] == "TYPE"
                ? NutTypeParser.ParseTypeWords(words.Skip(3)) with { Writable = true } // listed by LIST RW
                : NutTypeDescription.UnknownWritable;
        }
        catch (NutErrorException)
        {
            type = NutTypeDescription.UnknownWritable;
        }

        List<string> enumValues = [];
        if (type.IsEnum)
        {
            try
            {
                enumValues = NutTypeParser.ParseEnumValues(
                    await connection.ListAsync("ENUM", [ups, name], cancellationToken).ConfigureAwait(false));
            }
            catch (NutErrorException)
            {
                // No values: the upstream server validates the value itself.
            }
        }

        List<ValueRange> ranges = [];
        if (type.IsRange)
        {
            try
            {
                ranges = NutTypeParser.ParseRanges(
                    await connection.ListAsync("RANGE", [ups, name], cancellationToken).ConfigureAwait(false));
            }
            catch (NutErrorException)
            {
                // Servers before NUT 2.7.3 have no LIST RANGE.
            }
        }

        return type.ToVariableInfo(enumValues, ranges);
    }

    /// <summary>"Network UPS Tools upsd 2.8.1 - https://www.networkupstools.org/" becomes "Network UPS Tools upsd 2.8.1".</summary>
    internal static string? ShortenVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        string text = version.Trim();
        int link = text.IndexOf(" - http", StringComparison.OrdinalIgnoreCase);
        if (link > 0)
        {
            text = text[..link].TrimEnd();
        }

        text = new string(text.Where(c => !char.IsControl(c)).ToArray());
        return text.Length > 120 ? text[..120] : text;
    }
}
