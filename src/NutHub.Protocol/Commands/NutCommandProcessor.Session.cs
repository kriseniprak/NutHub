using Microsoft.Extensions.Logging;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Protocol.Security;

namespace NutHub.Protocol.Commands;

// Connection-level commands: STARTTLS (server/netssl.c), USERNAME, PASSWORD, LOGIN, PRIMARY / MASTER
// (server/netuser.c) and FSD (server/netmisc.c).
internal sealed partial class NutCommandProcessor
{
    private NutReply StartTls(NutClientState client)
    {
        if (client.Tls)
        {
            return NutReply.Error(NutErrors.AlreadySslMode);
        }

        if (!_config.Current.Nut.Tls.Enabled || _tls.Certificate is not { } certificate)
        {
            return NutReply.Error(NutErrors.FeatureNotConfigured);
        }

        return new NutReply("OK STARTTLS\n", NutReplyAction.StartTls, certificate);
    }

    private NutReply Username(NutClientState client, ArraySegment<string> a)
    {
        if (a.Count != 1)
        {
            return InvalidArgument;
        }

        if (MustUseTlsFirst(client, "USERNAME"))
        {
            return NutReply.Error(NutErrors.AccessDenied);
        }

        if (client.Username is not null)
        {
            _logger.LogInformation("NUT client {Address} tried to set a username twice.", client.AddressText);
            return NutReply.Error(NutErrors.AlreadySetUsername);
        }

        client.Username = a[0];
        return NutReply.Ok;
    }

    private NutReply Password(NutClientState client, ArraySegment<string> a)
    {
        if (a.Count != 1)
        {
            return InvalidArgument;
        }

        if (MustUseTlsFirst(client, "PASSWORD"))
        {
            return NutReply.Error(NutErrors.AccessDenied);
        }

        if (client.Password is not null)
        {
            _logger.LogInformation("NUT client {Address} tried to set a password twice.", client.AddressText);
            return NutReply.Error(NutErrors.AlreadySetPassword);
        }

        client.Password = a[0];
        return NutReply.Ok;
    }

    /// <summary>
    /// NutHub addition: with RequireTlsForAuthentication, credentials are refused on a connection that did not
    /// switch to TLS, so they never cross the network in clear text.
    /// </summary>
    private bool MustUseTlsFirst(NutClientState client, string command)
    {
        if (client.Tls || !_config.Current.Nut.Tls.RequireTlsForAuthentication)
        {
            return false;
        }

        LogLevel level = _warnings.ShouldLog("notls|" + client.AddressText) ? LogLevel.Warning : LogLevel.Debug;
        _logger.Log(level, "NUT client {Address} sent {Command} before STARTTLS; refused because TLS is required for authentication.",
                    client.AddressText, command);
        return true;
    }

    private NutReply Login(NutClientState client, ArraySegment<string> a)
    {
        if (a.Count != 1)
        {
            return InvalidArgument;
        }

        if (client.LoginUps is not null)
        {
            _logger.LogInformation("NUT client {User}@{Address} tried to log in twice.", client.Username,
                                   client.AddressText);
            return NutReply.Error(NutErrors.AlreadyLoggedIn);
        }

        if (!TryFind(a[0], requireData: false, out UpsUnit? unit, out _, out NutReply error))
        {
            return error;
        }

        if (!_authorizer.Authorize(client, NutRight.Login, unit, null, $"LOGIN {unit.Name}"))
        {
            return NutReply.Error(NutErrors.AccessDenied);
        }

        client.LoginUps = unit.Name;
        _sessions.SetLogin(client.Session, unit.Name);
        _logger.LogInformation("NUT client {User}@{Address} logged in to {Ups}{Tls}.", client.Username,
                               client.AddressText, unit.Name, client.Tls ? " (TLS)" : "");
        _hub.Publish(new UpsEventMessage(UpsEvent.Create(
            UpsEventType.NutClientLogin, _time.GetUtcNow(), unit.Name,
            $"NUT client {client.Username}@{client.AddressText} logged in to {unit.Name}.",
            Origin(client).ToString())));
        return NutReply.Ok;
    }

    private NutReply Primary(NutClientState client, ArraySegment<string> a, string command, string answer)
    {
        if (a.Count != 1)
        {
            return InvalidArgument;
        }

        if (!TryFind(a[0], requireData: false, out UpsUnit? unit, out _, out NutReply error))
        {
            return error;
        }

        if (!_authorizer.Authorize(client, NutRight.Primary, unit, null, $"{command} {unit.Name}"))
        {
            return NutReply.Error(NutErrors.AccessDenied);
        }

        if (!client.Session.Primary)
        {
            _sessions.SetPrimary(client.Session);
            _logger.LogInformation("NUT client {User}@{Address} is upsmon primary for {Ups}.", client.Username,
                                   client.AddressText, unit.Name);
        }

        return NutReply.Line(answer);
    }

    private NutReply ForcedShutdown(NutClientState client, ArraySegment<string> a)
    {
        if (a.Count != 1)
        {
            return InvalidArgument;
        }

        if (!TryFind(a[0], requireData: false, out UpsUnit? unit, out _, out NutReply error))
        {
            return error;
        }

        if (!_authorizer.Authorize(client, NutRight.ForcedShutdown, unit, null, $"FSD {unit.Name}"))
        {
            return NutReply.Error(NutErrors.AccessDenied);
        }

        _logger.LogInformation("NUT client {User}@{Address} set FSD on {Ups}.", client.Username, client.AddressText,
                               unit.Name);
        unit.SetForcedShutdown(Origin(client));
        return NutReply.Line("OK FSD-SET");
    }
}
