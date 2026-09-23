namespace NutHub.Core.Drivers;

/// <summary>The registered driver factories, by id.</summary>
public interface IDriverCatalog
{
    IReadOnlyList<IUpsDriverFactory> All { get; }

    IUpsDriverFactory? Find(string id);

    /// <summary>Whether the factory works on the operating system NutHub runs on.</summary>
    bool IsSupportedHere(IUpsDriverFactory factory);
}

public sealed class DriverCatalog : IDriverCatalog
{
    private readonly Dictionary<string, IUpsDriverFactory> _byId;

    public DriverCatalog(IEnumerable<IUpsDriverFactory> factories)
    {
        All = factories.OrderBy(f => f.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
        _byId = new Dictionary<string, IUpsDriverFactory>(StringComparer.OrdinalIgnoreCase);
        foreach (var factory in All)
        {
            if (!_byId.TryAdd(factory.Id, factory))
            {
                throw new InvalidOperationException($"Two drivers are registered with the id '{factory.Id}'.");
            }
        }
    }

    public IReadOnlyList<IUpsDriverFactory> All { get; }

    public IUpsDriverFactory? Find(string id) => _byId.TryGetValue(id, out var f) ? f : null;

    public bool IsSupportedHere(IUpsDriverFactory factory) => (factory.Platforms & Current) != 0;

    public static DriverPlatforms Current =>
        OperatingSystem.IsWindows() ? DriverPlatforms.Windows
        : OperatingSystem.IsLinux() ? DriverPlatforms.Linux
        : OperatingSystem.IsMacOS() ? DriverPlatforms.MacOS
        : DriverPlatforms.None;

    /// <summary>The keys of the secret options of a driver (encrypted in the configuration).</summary>
    public static IEnumerable<string> SecretKeys(IUpsDriverFactory? factory) =>
        factory?.Options.Where(o => o.IsSecret).Select(o => o.Key) ?? [];
}
