using NutHub.Core;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Protocol.Parsing;

namespace NutHub.Protocol.Commands;

// GET: upsd server/netget.c.
internal sealed partial class NutCommandProcessor
{
    private NutReply Get(NutClientState client, ArraySegment<string> a)
    {
        if (a.Count < 1)
        {
            return InvalidArgument;
        }

        string sub = NutText.AsciiUpper(a[0]);
        if (sub == "TRACKING")
        {
            if (a.Count < 2)
            {
                return NutReply.Line(client.Tracking ? "ON" : "OFF");
            }

            // upsd only answers about ids to clients that enabled tracking themselves.
            return client.Tracking
                ? NutReply.Line(_tracking.Get(a[1]))
                : NutReply.Error(NutErrors.FeatureNotConfigured);
        }

        if (a.Count < 2)
        {
            return InvalidArgument;
        }

        switch (sub)
        {
            case "NUMLOGINS":
                return GetNumLogins(a[1]);
            case "UPSDESC":
                return GetUpsDescription(a[1]);
        }

        if (a.Count < 3)
        {
            return InvalidArgument;
        }

        return sub switch
        {
            "VAR" => GetVariable(a[1], a[2]),
            "TYPE" => GetVariableType(a[1], a[2]),
            "DESC" => GetDescription(a[1], a[2]),
            "CMDDESC" => GetCommandDescription(a[1], a[2]),
            _ => InvalidArgument,
        };
    }

    private NutReply GetNumLogins(string ups)
    {
        if (!TryFind(ups, requireData: true, out UpsUnit? unit, out _, out NutReply error))
        {
            return error;
        }

        return NutReply.Line(Invariant($"NUMLOGINS {ups} {_sessions.GetLoginCount(unit.Name)}"));
    }

    private NutReply GetUpsDescription(string ups)
    {
        if (!TryFind(ups, requireData: false, out _, out UpsSnapshot? snapshot, out NutReply error))
        {
            return error;
        }

        return NutReply.Line($"UPSDESC {ups} {NutText.Quote(snapshot.Description ?? "Unavailable")}");
    }

    private NutReply GetVariable(string ups, string name)
    {
        // Like upsd, server.* variables ignore the UPS name entirely.
        if (NutText.AsciiStartsWithIgnoreCase(name, "server."))
        {
            return GetServerVariable(ups, name);
        }

        if (!TryFind(ups, requireData: true, out _, out UpsSnapshot? snapshot, out NutReply error))
        {
            return error;
        }

        // ups.status already carries FSD in front when a forced shutdown is set (UpsUnit does it).
        return SnapshotLookup.TryGetVariable(snapshot, name, out _, out string? value)
            ? NutReply.Line($"VAR {ups} {name} {NutText.Quote(value)}")
            : NutReply.Error(NutErrors.VarNotSupported);
    }

    private NutReply GetServerVariable(string ups, string name)
    {
        if (NutText.AsciiEqualsIgnoreCase(name, "server.info"))
        {
            return NutReply.Line($"VAR {ups} server.info {NutText.Quote(VersionText())}");
        }

        if (NutText.AsciiEqualsIgnoreCase(name, "server.version"))
        {
            return NutReply.Line($"VAR {ups} server.version {NutText.Quote(NutHubInfo.Version)}");
        }

        return NutReply.Error(NutErrors.VarNotSupported);
    }

    private NutReply GetVariableType(string ups, string name)
    {
        if (!TryFind(ups, requireData: true, out _, out UpsSnapshot? snapshot, out NutReply error))
        {
            return error;
        }

        if (!SnapshotLookup.TryGetVariable(snapshot, name, out string? canonical, out _))
        {
            return NutReply.Error(NutErrors.VarNotSupported);
        }

        VariableInfo info = snapshot.GetInfo(canonical);
        return NutReply.Line($"TYPE {ups} {name} {info.ToNutTypeWords()}");
    }

    private NutReply GetDescription(string ups, string name)
    {
        // upsd does not check that the UPS has the variable: descriptions come from a global table.
        if (!TryFind(ups, requireData: true, out _, out _, out NutReply error))
        {
            return error;
        }

        return NutReply.Line($"DESC {ups} {name} {NutText.Quote(SnapshotLookup.DescribeVariable(name))}");
    }

    private NutReply GetCommandDescription(string ups, string command)
    {
        if (!TryFind(ups, requireData: true, out _, out _, out NutReply error))
        {
            return error;
        }

        return NutReply.Line($"CMDDESC {ups} {command} {NutText.Quote(SnapshotLookup.DescribeCommand(command))}");
    }
}
