using System.Globalization;
using System.Net;
using System.Text;
using MimeKit;
using NutHub.Core;
using NutHub.Core.Configuration;
using NutHub.Core.Model;

namespace NutHub.Services.Notifications.Email;

/// <summary>
/// Turns one or more events into an e-mail: a subject naming the most important event, and a plain-text and an
/// HTML body with every event, the time in UTC and in the server's time zone, and the readings of each UPS.
/// </summary>
internal sealed class EmailComposer(TimeProvider time)
{
    private const int MaxSubjectMessage = 110;

    public OutboxMessage Compose(IReadOnlyList<NotificationContext> items, EmailSettings settings, ServerSettings server,
                                 Func<string, UpsReadings?> readings)
    {
        if (items.Count == 0)
        {
            throw new ArgumentException("At least one event is required.", nameof(items));
        }

        // The headline is the most severe event; among equals, the latest (the current situation).
        NotificationContext headline = items
            .Select((item, index) => (item, index))
            .OrderByDescending(p => p.item.Event.Severity)
            .ThenByDescending(p => p.index)
            .First().item;

        bool test = items.Any(i => i.IsTest);
        var upsNames = items.Select(i => i.Event.Ups).Where(u => u is not null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        string subjectSubject = upsNames.Count == 1 && items.All(i => i.Event.Ups is not null) ? upsNames[0]! : server.Name;
        string subject = $"{settings.SubjectPrefix} {(test ? "TEST: " : "")}{subjectSubject}: {Shorten(headline.Event.Message)}";
        if (items.Count > 1)
        {
            subject += $" (+{items.Count - 1} more)";
        }

        // Readings at the time the message is written, so the e-mail shows the situation it reports.
        var upsReadings = upsNames
            .Select(name => readings(name!) ?? items.FirstOrDefault(i => string.Equals(i.Event.Ups, name, StringComparison.OrdinalIgnoreCase))?.Readings)
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList();

        return new OutboxMessage
        {
            CreatedAt = time.GetUtcNow(),
            From = settings.From,
            To = [.. settings.To],
            Subject = subject.ReplaceLineEndings(" ").Trim(),
            TextBody = BuildText(items, server, upsReadings, test),
            HtmlBody = BuildHtml(items, server, upsReadings, test),
            Critical = items.Any(i => i.Event.Severity == EventSeverity.Critical),
            EventType = headline.Event.Type,
            Ups = headline.Event.Ups,
            NextAttemptAt = time.GetUtcNow(),
        };
    }

    /// <summary>Builds the MIME message; throws <see cref="ParseException"/> for an invalid address.</summary>
    public static MimeMessage ToMime(OutboxMessage message)
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(message.From));
        foreach (string to in message.To)
        {
            mime.To.Add(MailboxAddress.Parse(to));
        }

        mime.Subject = message.Subject;
        mime.Date = message.CreatedAt;
        // RFC 3834: tells mail servers and vacation responders not to answer an automatic message.
        mime.Headers.Add("Auto-Submitted", "auto-generated");
        mime.Priority = message.Critical ? MessagePriority.Urgent : MessagePriority.Normal;
        var body = new BodyBuilder { TextBody = message.TextBody, HtmlBody = message.HtmlBody };
        mime.Body = body.ToMessageBody();
        return mime;
    }

    private string BuildText(IReadOnlyList<NotificationContext> items, ServerSettings server,
                             IReadOnlyList<UpsReadings> readings, bool test)
    {
        var sb = new StringBuilder();
        if (test)
        {
            sb.AppendLine("*** TEST MESSAGE - no action is required ***").AppendLine();
        }

        foreach (NotificationContext item in items)
        {
            UpsEvent e = item.Event;
            sb.Append('[').Append(e.Severity.ToString().ToUpperInvariant()).Append("] ").AppendLine(e.Message);
            sb.Append("  Time:  ").AppendLine(FormatTimes(e.Timestamp));
            sb.Append("  Event: ").Append(item.TypeName);
            if (e.Ups is not null)
            {
                sb.Append("  UPS: ").Append(e.Ups);
            }

            if (e.Actor is not null)
            {
                sb.Append("  By: ").Append(e.Actor);
            }

            sb.AppendLine().AppendLine();
        }

        foreach (UpsReadings r in readings)
        {
            sb.Append("UPS ").Append(r.Name).AppendLine(r.Available ? "" : " (no fresh data)");
            foreach ((string label, string value) in Rows(r))
            {
                sb.Append("  ").Append(label.PadRight(15)).AppendLine(value);
            }

            sb.AppendLine();
        }

        sb.Append("Server: ").Append(server.Name);
        if (!string.IsNullOrWhiteSpace(server.Location))
        {
            sb.Append(" (").Append(server.Location).Append(')');
        }

        sb.AppendLine().Append("Sent by NutHub ").AppendLine(NutHubInfo.Version);
        return sb.ToString();
    }

    private string BuildHtml(IReadOnlyList<NotificationContext> items, ServerSettings server,
                             IReadOnlyList<UpsReadings> readings, bool test)
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><body style=\"font-family:Segoe UI,Helvetica,Arial,sans-serif;font-size:14px;color:#222\">");
        if (test)
        {
            sb.Append("<p style=\"padding:8px;background:#fff3cd;border:1px solid #e0c36c\"><b>TEST MESSAGE</b> - no action is required.</p>");
        }

        foreach (NotificationContext item in items)
        {
            UpsEvent e = item.Event;
            string color = e.Severity switch
            {
                EventSeverity.Critical => "#b00020",
                EventSeverity.Warning => "#b36b00",
                EventSeverity.Notice => "#1a5fb4",
                _ => "#555",
            };
            sb.Append("<div style=\"margin:0 0 12px;padding-left:8px;border-left:4px solid ").Append(color).Append("\">")
              .Append("<div><b>").Append(H(e.Message)).Append("</b></div>")
              .Append("<div style=\"color:#555\">").Append(H(FormatTimes(e.Timestamp))).Append(" &middot; ")
              .Append(H(item.TypeName)).Append(" &middot; ").Append(H(e.Severity.ToString().ToLowerInvariant()));
            if (e.Actor is not null)
            {
                sb.Append(" &middot; by ").Append(H(e.Actor));
            }

            sb.Append("</div></div>");
        }

        foreach (UpsReadings r in readings)
        {
            sb.Append("<table style=\"border-collapse:collapse;margin:8px 0\"><tr><th colspan=\"2\" style=\"text-align:left;padding:4px 0\">UPS ")
              .Append(H(r.Name)).Append(r.Available ? "" : " (no fresh data)").Append("</th></tr>");
            foreach ((string label, string value) in Rows(r))
            {
                sb.Append("<tr><td style=\"padding:2px 16px 2px 0;color:#555\">").Append(H(label))
                  .Append("</td><td>").Append(H(value)).Append("</td></tr>");
            }

            sb.Append("</table>");
        }

        sb.Append("<p style=\"color:#777;font-size:12px\">Server ").Append(H(server.Name));
        if (!string.IsNullOrWhiteSpace(server.Location))
        {
            sb.Append(" (").Append(H(server.Location)).Append(')');
        }

        sb.Append(" &middot; NutHub ").Append(H(NutHubInfo.Version)).Append("</p></body></html>");
        return sb.ToString();
    }

    private static IEnumerable<(string Label, string Value)> Rows(UpsReadings r)
    {
        if (r.Status is not null) yield return ("Status", r.Status);
        if (r.Charge is not null) yield return ("Battery charge", r.Charge + " %");
        if (r.Runtime is not null) yield return ("Runtime", FormatRuntime(r.Runtime));
        if (r.Load is not null) yield return ("Load", r.Load + " %");
        if (r.InputVoltage is not null) yield return ("Input voltage", r.InputVoltage + " V");
    }

    private static string FormatRuntime(string seconds)
    {
        if (!NutFormat.TryParseNumber(seconds, out double s) || s < 0)
        {
            return seconds + " s";
        }

        var span = TimeSpan.FromSeconds(Math.Round(s));
        return span.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours} h {span.Minutes} min")
            : string.Create(CultureInfo.InvariantCulture, $"{span.Minutes} min {span.Seconds} s");
    }

    private string FormatTimes(DateTimeOffset timestamp)
    {
        DateTimeOffset utc = timestamp.ToUniversalTime();
        TimeZoneInfo zone = time.LocalTimeZone;
        DateTimeOffset local = TimeZoneInfo.ConvertTime(utc, zone);
        string text = utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
        if (local.Offset != TimeSpan.Zero || zone.Id != TimeZoneInfo.Utc.Id)
        {
            string offset = local.ToString("zzz", CultureInfo.InvariantCulture);
            text += $" / {local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} server time (UTC{offset})";
        }

        return text;
    }

    private static string Shorten(string message)
    {
        string single = message.ReplaceLineEndings(" ").Trim();
        return single.Length <= MaxSubjectMessage ? single : single[..(MaxSubjectMessage - 3)].TrimEnd() + "...";
    }

    private static string H(string? text) => WebUtility.HtmlEncode(text ?? "");
}
