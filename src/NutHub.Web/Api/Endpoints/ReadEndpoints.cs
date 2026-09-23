using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Web.Api.Dto;

namespace NutHub.Web.Api.Endpoints;

/// <summary>Dashboard, UPS details, history and events (viewer, or anonymous when allowed).</summary>
internal static class ReadEndpoints
{
    public const int MaxHistoryPoints = 500;

    private static readonly Dictionary<string, TimeSpan> Ranges = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1h"] = TimeSpan.FromHours(1),
        ["6h"] = TimeSpan.FromHours(6),
        ["24h"] = TimeSpan.FromHours(24),
        ["7d"] = TimeSpan.FromDays(7),
        ["30d"] = TimeSpan.FromDays(30),
        ["90d"] = TimeSpan.FromDays(90),
    };

    public static void Map(RouteGroupBuilder read)
    {
        read.MapGet("/overview", (ApiViews views) => views.Overview());
        read.MapGet("/ups/{name}", (string name, IUpsRegistry registry, ApiViews views) => views.Detail(FindUnit(registry, name)));
        read.MapGet("/ups/{name}/history", HistoryAsync);
        read.MapGet("/events", EventsAsync);
    }

    public static UpsUnit FindUnit(IUpsRegistry registry, string name) =>
        registry.Find(name) ?? throw ApiException.NotFound($"There is no UPS named '{name}'.");

    private static async Task<HistoryDto> HistoryAsync(string name, string? range, string? from, string? to, string? vars,
                                                       IUpsRegistry registry, IConfigStore config, IHistoryStore history,
                                                       TimeProvider time, HttpContext context)
    {
        string ups = registry.Find(name)?.Name
                     ?? config.Current.Ups.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase))?.Name
                     ?? throw ApiException.NotFound($"There is no UPS named '{name}'.");
        (DateTimeOffset start, DateTimeOffset end) = ParseInterval(range, from, to, time.GetUtcNow());

        IReadOnlyList<string> variables = string.IsNullOrWhiteSpace(vars)
            ? config.Current.History.Variables
            : vars.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                  .Distinct(StringComparer.Ordinal).ToList();
        if (variables.Count > 32)
        {
            throw ApiException.Validation("vars", "Ask for at most 32 variables at once.");
        }

        foreach (string variable in variables)
        {
            if (!NutFormat.IsValidVariableName(variable))
            {
                throw ApiException.Validation("vars", $"'{variable}' is not a valid variable name.");
            }
        }

        HistoryResult result = await history.QueryAsync(new HistoryQuery(ups, variables, start, end, MaxHistoryPoints),
                                                         context.RequestAborted).ConfigureAwait(false);
        var series = new Dictionary<string, IReadOnlyList<double[]>>(StringComparer.Ordinal);
        foreach (var (variable, points) in result.Series)
        {
            series[variable] = points
                .Where(p => double.IsFinite(p.Average) && double.IsFinite(p.Min) && double.IsFinite(p.Max))
                .Select(p => new[] { p.Timestamp.ToUnixTimeMilliseconds(), p.Average, p.Min, p.Max })
                .ToList();
        }

        return new HistoryDto(result.From, result.To, result.StepSeconds, series);
    }

    /// <summary>A named range ending now, or an explicit from/to; at most one year.</summary>
    internal static (DateTimeOffset From, DateTimeOffset To) ParseInterval(string? range, string? from, string? to,
                                                                           DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(from) || !string.IsNullOrWhiteSpace(to))
        {
            DateTimeOffset start = ParseTimestamp(from, "from");
            DateTimeOffset end = string.IsNullOrWhiteSpace(to) ? now : ParseTimestamp(to, "to");
            if (end <= start)
            {
                throw ApiException.Validation("to", "The end must be after the start.");
            }

            if (end - start > TimeSpan.FromDays(366))
            {
                throw ApiException.Validation("from", "Ask for at most one year at once.");
            }

            return (start, end);
        }

        string key = string.IsNullOrWhiteSpace(range) ? "24h" : range.Trim();
        if (!Ranges.TryGetValue(key, out TimeSpan length))
        {
            throw ApiException.Validation("range", "Use 1h, 6h, 24h, 7d, 30d or 90d.");
        }

        return (now - length, now);
    }

    private static DateTimeOffset ParseTimestamp(string? text, string field)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                                     DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset value))
        {
            throw ApiException.Validation(field, "Use an ISO 8601 timestamp such as 2026-09-21T14:00:00Z.");
        }

        return value;
    }

    private static async Task<EventPageDto> EventsAsync(string? ups, string? minSeverity, string? category, long? beforeId,
                                                        int? limit, string? search, IEventStore events,
                                                        HttpContext context)
    {
        var query = new EventQuery(
            Ups: string.IsNullOrWhiteSpace(ups) ? null : ups.Trim(),
            MinSeverity: ParseEnum<EventSeverity>(minSeverity, "minSeverity"),
            Category: ParseEnum<EventCategory>(category, "category"),
            BeforeId: beforeId,
            Limit: Math.Clamp(limit ?? 100, 1, 1000),
            Search: string.IsNullOrWhiteSpace(search) ? null : search.Trim());
        EventPage page = await events.QueryAsync(query, context.RequestAborted).ConfigureAwait(false);
        return new EventPageDto(page.Items.Select(ApiViews.Event).ToList(), page.HasMore);
    }

    private static T? ParseEnum<T>(string? text, string field) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!int.TryParse(text, out _) && Enum.TryParse(text.Trim(), ignoreCase: true, out T value))
        {
            return value;
        }

        string allowed = string.Join(", ", Enum.GetNames<T>().Select(n => char.ToLowerInvariant(n[0]) + n[1..]));
        throw ApiException.Validation(field, $"Use one of: {allowed}.");
    }
}
