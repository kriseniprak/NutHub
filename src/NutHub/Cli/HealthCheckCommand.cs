using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using NutHub.Core;
using NutHub.Core.Configuration;

namespace NutHub.Cli;

/// <summary>
/// "nuthub healthcheck": asks the running server on this machine whether it answers, for container health checks
/// (the runtime image has no shell or curl). Checks the web panel (GET /api/auth/state), or the NUT server (VER)
/// when the panel is disabled. Reads the configuration file only; never creates or changes anything.
/// </summary>
internal static class HealthCheckCommand
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task<int> ExecuteAsync(IReadOnlyList<string> args)
    {
        ParsedArguments parsed = ArgumentParser.Parse(args, "healthcheck", [], ["--data-dir", "--config"]);
        parsed.ExpectPositionals(0, "nuthub healthcheck [--data-dir DIR] [--config FILE]");
        NutHubPaths paths = NutHubPaths.Resolve(parsed.Get("--data-dir"), parsed.Get("--config"));

        NutHubConfig config = ReadConfig(paths.ConfigFile);
        using var timeout = new CancellationTokenSource(Timeout);
        try
        {
            WebSettings web = config.Web ?? new WebSettings();
            if (web.Enabled && (web.HttpEnabled || web.HttpsEnabled))
            {
                return await CheckWebAsync(web, timeout.Token).ConfigureAwait(false);
            }

            NutServerSettings nut = config.Nut ?? new NutServerSettings();
            if (nut.Enabled && nut.Listen is { Count: > 0 })
            {
                return await CheckNutAsync(nut.Listen[0], timeout.Token).ConfigureAwait(false);
            }

            Console.WriteLine("Both the web panel and the NUT server are disabled: nothing to check.");
            return ExitCodes.Ok;
        }
        catch (Exception ex) when (ex is HttpRequestException or SocketException or IOException
                                       or OperationCanceledException)
        {
            Console.Error.WriteLine($"unhealthy: {(ex is OperationCanceledException ? "no answer within 5 s" : ex.Message)}");
            return ExitCodes.Error;
        }
    }

    private static NutHubConfig ReadConfig(string file)
    {
        if (!File.Exists(file))
        {
            return new NutHubConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<NutHubConfig>(File.ReadAllBytes(file), NutHubJson.File) ?? new NutHubConfig();
        }
        catch (JsonException ex)
        {
            throw new CommandException($"{file} is not valid JSON: {ex.Message}");
        }
    }

    private static async Task<int> CheckWebAsync(WebSettings web, CancellationToken cancellationToken)
    {
        bool https = !web.HttpEnabled;
        string host = Loopback(web.BindAddress);
        int port = https ? web.HttpsPort : web.HttpPort;
        var handler = new SocketsHttpHandler();
        // The panel's certificate is usually self-signed or issued for another name: this only checks that the
        // local server answers.
        handler.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
        using var client = new HttpClient(handler);
        using HttpResponseMessage response = await client
            .GetAsync(new Uri($"{(https ? "https" : "http")}://{host}:{port}/api/auth/state"), cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            Console.WriteLine("healthy");
            return ExitCodes.Ok;
        }

        Console.Error.WriteLine($"unhealthy: the web panel answered {(int)response.StatusCode}.");
        return ExitCodes.Error;
    }

    private static async Task<int> CheckNutAsync(ListenEndpoint endpoint, CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(Loopback(endpoint.Address).Trim('[', ']'), endpoint.Port, cancellationToken)
            .ConfigureAwait(false);
        NetworkStream stream = tcp.GetStream();
        await stream.WriteAsync("VER\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        byte[] buffer = new byte[256];
        int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        string answer = Encoding.ASCII.GetString(buffer, 0, read).Trim();
        if (read > 0 && !answer.StartsWith("ERR", StringComparison.Ordinal))
        {
            Console.WriteLine("healthy");
            return ExitCodes.Ok;
        }

        Console.Error.WriteLine($"unhealthy: the NUT server answered '{answer}'.");
        return ExitCodes.Error;
    }

    /// <summary>The address to reach a server bound to <paramref name="bind"/> from this machine.</summary>
    private static string Loopback(string? bind)
    {
        if (string.IsNullOrEmpty(bind) || bind == "*" || !IPAddress.TryParse(bind, out IPAddress? address) ||
            address.Equals(IPAddress.Any))
        {
            return "127.0.0.1";
        }

        if (address.Equals(IPAddress.IPv6Any))
        {
            return "[::1]";
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
    }
}
