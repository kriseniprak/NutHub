using System.Diagnostics.CodeAnalysis;
using NutHub.Core.Catalog;
using NutHub.Core.Model;
using NutHub.Protocol.Parsing;

namespace NutHub.Protocol.Commands;

/// <summary>
/// Name lookups with upsd's rules: variable and command names ignore ASCII case (its state tree compares with
/// strcasecmp), and the name the client typed is the one echoed back in answers.
/// </summary>
internal static class SnapshotLookup
{
    /// <summary>What upsd answers for GET DESC / GET CMDDESC and LIST UPS when it has no description.</summary>
    public const string DescriptionUnavailable = "Description unavailable";

    private static readonly Lazy<Dictionary<string, string>> VariableDescriptionsIgnoringCase = new(() =>
    {
        var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, text) in NutCatalog.VariableDescriptions)
        {
            table.TryAdd(name, text);
        }

        return table;
    });

    public static bool TryGetVariable(UpsSnapshot snapshot, string name, [NotNullWhen(true)] out string? canonical,
                                      [NotNullWhen(true)] out string? value)
    {
        if (snapshot.Variables.TryGetValue(name, out value))
        {
            canonical = name;
            return true;
        }

        foreach (var (key, v) in snapshot.Variables)
        {
            if (NutText.AsciiEqualsIgnoreCase(key, name))
            {
                canonical = key;
                value = v;
                return true;
            }
        }

        canonical = null;
        value = null;
        return false;
    }

    /// <summary>The command as the driver named it, or null when the UPS does not support it.</summary>
    public static string? FindCommand(UpsSnapshot snapshot, string name)
    {
        foreach (string command in snapshot.Commands)
        {
            if (NutText.AsciiEqualsIgnoreCase(command, name))
            {
                return command;
            }
        }

        return null;
    }

    /// <summary>The NUT error for data that cannot be served (upsd ups_available), or null when it can.</summary>
    public static string? AvailabilityError(UpsSnapshot snapshot) => snapshot.Availability switch
    {
        DataAvailability.Available => null,
        DataAvailability.Stale => NutErrors.DataStale,
        _ => NutErrors.DriverNotConnected,
    };

    /// <summary>
    /// The description of a variable. Like upsd (desc.c), a "upstream." prefix used by proxies is ignored for the
    /// lookup and names ignore case.
    /// </summary>
    public static string DescribeVariable(string name)
    {
        string lookup = name.StartsWith("upstream.", StringComparison.Ordinal) ? name[9..] : name;
        return NutCatalog.DescribeVariable(lookup)
               ?? (VariableDescriptionsIgnoringCase.Value.TryGetValue(lookup, out string? text) ? text : null)
               ?? DescriptionUnavailable;
    }

    /// <summary>The description of an instant command (same rules as <see cref="DescribeVariable"/>).</summary>
    public static string DescribeCommand(string name)
    {
        string lookup = name.StartsWith("upstream.", StringComparison.Ordinal) ? name[9..] : name;
        return NutCatalog.DescribeCommand(lookup) ?? DescriptionUnavailable;
    }

    /// <summary>The variables in upsd's listing order.</summary>
    public static IEnumerable<KeyValuePair<string, string>> OrderedVariables(UpsSnapshot snapshot) =>
        snapshot.Variables.OrderBy(kv => kv.Key, NutNameComparer.Instance);
}
