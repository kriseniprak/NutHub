using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using NutHub.Core.Configuration;
using NutHub.Core.Drivers;
using NutHub.Core.Runtime;
using NutHub.Web.Admin;
using NutHub.Web.Api.Dto;

namespace NutHub.Web.Api.Endpoints;

/// <summary>Drivers, discovery, serial ports and the UPS configuration (admin).</summary>
internal static class AdminUpsEndpoints
{
    public static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(30);

    public static void Map(RouteGroupBuilder admin)
    {
        admin.MapGet("/drivers", (IDriverCatalog catalog) => catalog.All.Select(f => DriverView(f, catalog)).ToList());
        admin.MapPost("/drivers/{id}/discover", DiscoverAsync);
        admin.MapGet("/serial-ports", () => SerialPortLister.List());
        admin.MapGet("/ups", (ConfigWriter writer, UpsConfigMapper mapper) => writer.Current.Ups.Select(mapper.ToDto).ToList());
        admin.MapPost("/ups", CreateAsync);
        admin.MapPut("/ups/{name}", UpdateAsync);
        admin.MapDelete("/ups/{name}", DeleteAsync);
        admin.MapPost("/ups/{name}/restart", RestartAsync);
        admin.MapPut("/ups-order", ReorderAsync);
    }

    public static DriverDto DriverView(IUpsDriverFactory f, IDriverCatalog catalog) =>
        new(f.Id, f.DisplayName, f.Description, Platforms(f.Platforms), catalog.IsSupportedHere(f), f.SupportsDiscovery,
            f.Options.Select(o => new DriverOptionDto(
                o.Key, o.Label, o.Type, o.Required, o.Default, o.Help,
                o.Choices?.Select(c => new DriverOptionChoiceDto(c.Value, c.Label)).ToList(),
                o.Min, o.Max, o.Advanced,
                o.VisibleWhen is { } v ? new DriverOptionConditionDto(v.Key, v.Values) : null)).ToList());

    private static List<string> Platforms(DriverPlatforms platforms)
    {
        var list = new List<string>(3);
        if (platforms.HasFlag(DriverPlatforms.Windows))
        {
            list.Add("windows");
        }

        if (platforms.HasFlag(DriverPlatforms.Linux))
        {
            list.Add("linux");
        }

        if (platforms.HasFlag(DriverPlatforms.MacOS))
        {
            list.Add("macOS");
        }

        return list;
    }

    private static async Task<DiscoveryResultDto> DiscoverAsync(string id, IDriverCatalog catalog, TimeProvider time,
                                                                ILoggerFactory loggers, HttpContext context)
    {
        IUpsDriverFactory factory = catalog.Find(id) ?? throw ApiException.NotFound($"There is no driver '{id}'.");
        if (!factory.SupportsDiscovery)
        {
            return new DiscoveryResultDto([], $"{factory.DisplayName} cannot search for devices.");
        }

        if (!catalog.IsSupportedHere(factory))
        {
            return new DiscoveryResultDto([], $"{factory.DisplayName} does not work on this operating system.");
        }

        ILogger logger = loggers.CreateLogger("NutHub.Web.Discovery");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        cts.CancelAfter(DiscoveryTimeout);
        try
        {
            // WaitAsync also returns on time when a driver ignores the cancellation.
            IReadOnlyList<DiscoveredDevice> devices = await factory.DiscoverAsync(cts.Token)
                .WaitAsync(DiscoveryTimeout, time, context.RequestAborted).ConfigureAwait(false);
            return new DiscoveryResultDto(
                devices.Select(d => new DiscoveredDeviceDto(d.Title, d.Detail, d.Options, d.SuggestedName)).ToList(), null);
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && !context.RequestAborted.IsCancellationRequested))
        {
            logger.LogWarning("Device discovery of {Driver} did not finish within {Seconds} s.", factory.Id,
                              DiscoveryTimeout.TotalSeconds);
            return new DiscoveryResultDto([], $"The search did not finish within {DiscoveryTimeout.TotalSeconds:0} seconds.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Device discovery of {Driver} failed.", factory.Id);
            return new DiscoveryResultDto([], "The search failed: " + ex.Message);
        }
    }

    private static async Task<IResult> CreateAsync(UpsConfigDto body, HttpContext context, ConfigWriter writer,
                                                   UpsConfigMapper mapper)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        UpsConfig ups = mapper.FromDto(body, null, errors);
        if (ups.Name.Length == 0)
        {
            errors.TryAdd("name", "A name is required.");
        }

        ThrowIfAny(errors);
        int index = -1;
        NutHubConfig saved = await writer.UpdateAsync(context, config =>
        {
            if (config.Ups.Any(u => Same(u.Name, ups.Name)))
            {
                throw ApiException.Conflict($"A UPS named '{ups.Name}' already exists.");
            }

            config.Ups.Add(ups);
            index = config.Ups.Count - 1;
        }, $"UPS {ups.Name} added", key => FieldMaps.Item("ups", index)(key)).ConfigureAwait(false);

        UpsConfig stored = saved.Ups.First(u => Same(u.Name, ups.Name));
        return Results.Created($"/api/admin/ups/{Uri.EscapeDataString(stored.Name)}", mapper.ToDto(stored));
    }

    private static async Task<UpsConfigDto> UpdateAsync(string name, UpsConfigDto body, HttpContext context,
                                                        ConfigWriter writer, UpsConfigMapper mapper)
    {
        int index = -1;
        string newName = name;
        NutHubConfig saved = await writer.UpdateAsync(context, config =>
        {
            index = config.Ups.FindIndex(u => Same(u.Name, name));
            if (index < 0)
            {
                throw ApiException.NotFound($"There is no UPS named '{name}'.");
            }

            UpsConfig existing = config.Ups[index];
            var errors = new Dictionary<string, string>(StringComparer.Ordinal);
            UpsConfig updated = mapper.FromDto(body, existing, errors);
            ThrowIfAny(errors);
            if (config.Ups.Any(u => !ReferenceEquals(u, existing) && Same(u.Name, updated.Name)))
            {
                throw ApiException.Conflict($"A UPS named '{updated.Name}' already exists.");
            }

            config.Ups[index] = updated;
            newName = updated.Name;
            if (!string.Equals(existing.Name, updated.Name, StringComparison.Ordinal))
            {
                Rename(config.HostProtection.Ups, existing.Name, updated.Name);
                foreach (NutUserConfig user in config.NutUsers)
                {
                    Rename(user.AllowedUps, existing.Name, updated.Name);
                }
            }
        }, $"UPS {name} changed", key => FieldMaps.Item("ups", index)(key)).ConfigureAwait(false);

        return mapper.ToDto(saved.Ups.First(u => Same(u.Name, newName)));
    }

    private static async Task<IResult> DeleteAsync(string name, HttpContext context, ConfigWriter writer)
    {
        await writer.UpdateAsync(context, config =>
        {
            UpsConfig ups = config.Ups.FirstOrDefault(u => Same(u.Name, name))
                            ?? throw ApiException.NotFound($"There is no UPS named '{name}'.");

            // An empty list means "every UPS": removing the only allowed UPS would widen the account's rights.
            NutUserConfig? restricted = config.NutUsers.FirstOrDefault(
                u => u.AllowedUps.Count > 0 && u.AllowedUps.All(a => Same(a, ups.Name)));
            if (restricted is not null)
            {
                throw ApiException.Conflict(
                    $"The NUT account '{restricted.Name}' may only use {ups.Name}; change or delete that account first.");
            }

            config.Ups.Remove(ups);
            config.HostProtection.Ups.RemoveAll(u => Same(u, ups.Name));
            if (config.HostProtection.Ups.Count > 0)
            {
                config.HostProtection.MinimumSupplies = Math.Min(config.HostProtection.MinimumSupplies,
                                                                 config.HostProtection.Ups.Count);
            }

            foreach (NutUserConfig user in config.NutUsers)
            {
                user.AllowedUps.RemoveAll(u => Same(u, ups.Name));
            }
        }, $"UPS {name} deleted").ConfigureAwait(false);

        return Results.NoContent();
    }

    private static async Task<IResult> RestartAsync(string name, ConfigWriter writer, IDriverManager manager,
                                                    HttpContext context)
    {
        UpsConfig ups = writer.Current.Ups.FirstOrDefault(u => Same(u.Name, name))
                        ?? throw ApiException.NotFound($"There is no UPS named '{name}'.");
        if (!ups.Enabled)
        {
            throw ApiException.BadRequest($"{ups.Name} is disabled; enable it to start its driver.");
        }

        if (!await manager.RestartAsync(ups.Name, context.RequestAborted).ConfigureAwait(false))
        {
            throw ApiException.Conflict($"The driver of {ups.Name} is not running yet; try again in a moment.");
        }

        return Results.NoContent();
    }

    private static async Task<IResult> ReorderAsync(UpsOrderRequest body, HttpContext context, ConfigWriter writer)
    {
        IReadOnlyList<string> names = body.Names ?? throw ApiException.Validation("names", "The list of names is required.");
        await writer.UpdateAsync(context, config =>
        {
            var ordered = new List<UpsConfig>(config.Ups.Count);
            foreach (string n in names)
            {
                UpsConfig ups = config.Ups.FirstOrDefault(u => Same(u.Name, n))
                                ?? throw ApiException.Validation("names", $"There is no UPS named '{n}'.");
                if (ordered.Contains(ups))
                {
                    throw ApiException.Validation("names", $"'{n}' is listed twice.");
                }

                ordered.Add(ups);
            }

            // UPSes the list does not mention keep their relative order, after the listed ones.
            ordered.AddRange(config.Ups.Where(u => !ordered.Contains(u)));
            config.Ups = ordered;
        }, "UPS order changed").ConfigureAwait(false);

        return Results.NoContent();
    }

    internal static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static void Rename(List<string> list, string from, string to)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (Same(list[i], from))
            {
                list[i] = to;
            }
        }
    }

    private static void ThrowIfAny(IReadOnlyDictionary<string, string> errors)
    {
        if (errors.Count > 0)
        {
            throw ApiException.Validation(errors);
        }
    }
}
