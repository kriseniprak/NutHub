using System.Text;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Protocol.Parsing;

namespace NutHub.Protocol.Commands;

// LIST: upsd server/netlist.c. Each list is built completely and sent in one write.
internal sealed partial class NutCommandProcessor
{
    private NutReply List(ArraySegment<string> a)
    {
        if (a.Count < 1)
        {
            return InvalidArgument;
        }

        string sub = NutText.AsciiUpper(a[0]);
        if (sub == "UPS")
        {
            return ListUps();
        }

        if (a.Count < 2)
        {
            return InvalidArgument;
        }

        switch (sub)
        {
            case "VAR":
                return ListVariables(a[1], writableOnly: false);
            case "RW":
                return ListVariables(a[1], writableOnly: true);
            case "CMD":
                return ListCommands(a[1]);
            case "CLIENT":
                return ListClients(a[1]);
        }

        if (a.Count < 3)
        {
            return InvalidArgument;
        }

        return sub switch
        {
            "ENUM" => ListEnum(a[1], a[2]),
            "RANGE" => ListRange(a[1], a[2]),
            _ => InvalidArgument,
        };
    }

    private NutReply ListUps()
    {
        var sb = new StringBuilder("BEGIN LIST UPS\n");
        foreach (UpsUnit unit in _registry.Units)
        {
            UpsSnapshot snapshot = unit.Snapshot;
            sb.Append("UPS ").Append(snapshot.Name).Append(' ')
              .Append(NutText.Quote(snapshot.Description ?? SnapshotLookup.DescriptionUnavailable)).Append('\n');
        }

        sb.Append("END LIST UPS\n");
        return new NutReply(sb.ToString());
    }

    private NutReply ListVariables(string ups, bool writableOnly)
    {
        if (!TryFind(ups, requireData: true, out _, out UpsSnapshot? snapshot, out NutReply error))
        {
            return error;
        }

        string kind = writableOnly ? "RW" : "VAR";
        var sb = new StringBuilder(64 + snapshot.Variables.Count * 48);
        sb.Append("BEGIN LIST ").Append(kind).Append(' ').Append(ups).Append('\n');
        foreach (var (name, value) in SnapshotLookup.OrderedVariables(snapshot))
        {
            if (writableOnly && !snapshot.GetInfo(name).Writable)
            {
                continue;
            }

            sb.Append(kind).Append(' ').Append(ups).Append(' ').Append(name).Append(' ')
              .Append(NutText.Quote(value)).Append('\n');
        }

        sb.Append("END LIST ").Append(kind).Append(' ').Append(ups).Append('\n');
        return new NutReply(sb.ToString());
    }

    private NutReply ListCommands(string ups)
    {
        if (!TryFind(ups, requireData: true, out _, out UpsSnapshot? snapshot, out NutReply error))
        {
            return error;
        }

        var sb = new StringBuilder();
        sb.Append("BEGIN LIST CMD ").Append(ups).Append('\n');
        foreach (string command in snapshot.Commands.Order(NutNameComparer.Instance))
        {
            sb.Append("CMD ").Append(ups).Append(' ').Append(command).Append('\n');
        }

        sb.Append("END LIST CMD ").Append(ups).Append('\n');
        return new NutReply(sb.ToString());
    }

    private NutReply ListClients(string ups)
    {
        // upsd does not check the data of the UPS here: the list of logged-in clients is its own.
        if (!TryFind(ups, requireData: false, out UpsUnit? unit, out _, out NutReply error))
        {
            return error;
        }

        var sb = new StringBuilder();
        sb.Append("BEGIN LIST CLIENT ").Append(ups).Append('\n');
        foreach (NutSession session in _sessions.GetLogins(unit.Name))
        {
            sb.Append("CLIENT ").Append(session.LoginUps).Append(' ').Append(session.Address).Append('\n');
        }

        sb.Append("END LIST CLIENT ").Append(ups).Append('\n');
        return new NutReply(sb.ToString());
    }

    private NutReply ListEnum(string ups, string name)
    {
        if (!TryFind(ups, requireData: true, out _, out UpsSnapshot? snapshot, out NutReply error))
        {
            return error;
        }

        if (!SnapshotLookup.TryGetVariable(snapshot, name, out string? canonical, out _))
        {
            return NutReply.Error(NutErrors.VarNotSupported);
        }

        var sb = new StringBuilder();
        sb.Append("BEGIN LIST ENUM ").Append(ups).Append(' ').Append(name).Append('\n');
        foreach (string value in snapshot.GetInfo(canonical).EnumValues)
        {
            sb.Append("ENUM ").Append(ups).Append(' ').Append(name).Append(' ').Append(NutText.Quote(value)).Append('\n');
        }

        sb.Append("END LIST ENUM ").Append(ups).Append(' ').Append(name).Append('\n');
        return new NutReply(sb.ToString());
    }

    private NutReply ListRange(string ups, string name)
    {
        if (!TryFind(ups, requireData: true, out _, out UpsSnapshot? snapshot, out NutReply error))
        {
            return error;
        }

        if (!SnapshotLookup.TryGetVariable(snapshot, name, out string? canonical, out _))
        {
            return NutReply.Error(NutErrors.VarNotSupported);
        }

        var sb = new StringBuilder();
        sb.Append("BEGIN LIST RANGE ").Append(ups).Append(' ').Append(name).Append('\n');
        foreach (ValueRange range in snapshot.GetInfo(canonical).Ranges)
        {
            sb.Append("RANGE ").Append(ups).Append(' ').Append(name).Append(' ')
              .Append(NutText.Quote(range.Min)).Append(' ').Append(NutText.Quote(range.Max)).Append('\n');
        }

        sb.Append("END LIST RANGE ").Append(ups).Append(' ').Append(name).Append('\n');
        return new NutReply(sb.ToString());
    }
}
