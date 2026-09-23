using System.Collections.Frozen;

namespace NutHub.Core.Catalog;

/// <summary>
/// Descriptions of the standard NUT variables and instant commands, read from the "cmdvartab" file of the Network
/// UPS Tools project (GPL-2.0-or-later), embedded unchanged. Used for GET DESC / GET CMDDESC and by the web panel.
/// </summary>
public static class NutCatalog
{
    /// <summary>What upsd answers when it has no description.</summary>
    public const string Unavailable = "Unavailable";

    private static readonly Lazy<(FrozenDictionary<string, string> Vars, FrozenDictionary<string, string> Cmds)> Tables =
        new(Load);

    public static IReadOnlyDictionary<string, string> VariableDescriptions => Tables.Value.Vars;

    public static IReadOnlyDictionary<string, string> CommandDescriptions => Tables.Value.Cmds;

    /// <summary>The description of a variable, or null when the catalog does not know it.</summary>
    public static string? DescribeVariable(string name)
    {
        if (Tables.Value.Vars.TryGetValue(name, out string? text))
        {
            return text;
        }

        // Three-phase and outlet variables are described generically: "input.L1-N.voltage" -> "input.voltage".
        string generic = GenericName(name);
        return generic != name && Tables.Value.Vars.TryGetValue(generic, out text) ? text : null;
    }

    /// <summary>The description of an instant command, or null when the catalog does not know it.</summary>
    public static string? DescribeCommand(string name) =>
        Tables.Value.Cmds.TryGetValue(name, out string? text) ? text : null;

    private static string GenericName(string name)
    {
        var parts = name.Split('.').Where(p => !IsPhaseOrIndex(p)).ToArray();
        return string.Join('.', parts);
    }

    private static bool IsPhaseOrIndex(string part) =>
        part.Length > 0 && (char.IsDigit(part[0]) ||
                            part is "L1" or "L2" or "L3" or "N" or "L1-N" or "L2-N" or "L3-N" or "L1-L2" or "L2-L3"
                                or "L3-L1");

    private static (FrozenDictionary<string, string>, FrozenDictionary<string, string>) Load()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        var cmds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        using Stream? stream = typeof(NutCatalog).Assembly.GetManifestResourceStream("NutHub.Core.Catalog.cmdvartab");
        if (stream is not null)
        {
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                // VARDESC battery.charge "Battery charge (percent of full)"
                line = line.Trim();
                if (line.Length == 0 || line[0] == '#')
                {
                    continue;
                }

                int firstSpace = line.IndexOf(' ');
                if (firstSpace < 0)
                {
                    continue;
                }

                string kind = line[..firstSpace];
                string rest = line[(firstSpace + 1)..].TrimStart();
                int secondSpace = rest.IndexOf(' ');
                if (secondSpace < 0)
                {
                    continue;
                }

                string name = rest[..secondSpace];
                string text = rest[(secondSpace + 1)..].Trim().Trim('"');

                if (kind == "VARDESC")
                {
                    vars[name] = text;
                }
                else if (kind == "CMDDESC")
                {
                    cmds[name] = text;
                }
            }
        }

        return (vars.ToFrozenDictionary(StringComparer.Ordinal), cmds.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
    }
}
