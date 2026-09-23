using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NutHub.Core;
using NutHub.Core.Configuration;
using NutHub.Hosting;

namespace NutHub.Cli;

/// <summary>
/// The services of NutHub for a command-line tool: the same registrations as the server (so the configuration is
/// validated against the same drivers), never started. Configuration changes made through it are saved to the file,
/// which a running server picks up by itself.
/// </summary>
internal sealed class ToolHost : IDisposable
{
    private readonly IHost _host;

    private ToolHost(NutHubPaths paths, IHost host)
    {
        Paths = paths;
        _host = host;
    }

    public NutHubPaths Paths { get; }

    public IServiceProvider Services => _host.Services;

    public static ToolHost Create(ParsedArguments parsed)
    {
        NutHubPaths paths = NutHubPaths.Resolve(parsed.Get("--data-dir"), parsed.Get("--config"));
        return Create(paths);
    }

    public static ToolHost Create(NutHubPaths paths)
    {
        IHost host = NutHubHostFactory.CreateBuilder(paths, HostRunMode.Tool, fileLogger: null).Build();
        return new ToolHost(paths, host);
    }

    /// <summary>Loads (or, on a new installation, creates) the configuration.</summary>
    public JsonConfigStore LoadConfiguration() => Services.GetRequiredService<JsonConfigStore>();

    public void Dispose() => _host.Dispose();
}
