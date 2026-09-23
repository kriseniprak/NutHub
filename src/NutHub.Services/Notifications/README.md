# Notifications

NutHub notifies events through three kinds of channels. Each channel has its own event list (`events`); when it is
`null` the global `notifications.events` list applies.

- **E-mail** (SMTP): events for the same recipients within 10 seconds share one message. Messages the server does not
  accept yet (typical during an outage, when the switch or the mail server is down too) wait in
  `<data directory>/notifications/outbox.json` and are retried after 30 s, 1, 2, 5 and then every 10 minutes for up
  to 24 hours, also across restarts. The outbox holds at most 500 messages; the oldest non-critical ones go first.
- **Webhooks**: an HTTP request per event, 10 s timeout, up to 3 attempts for network errors, `408`, `429` and `5xx`.
- **Commands**: a program run per event, like upsmon's `NOTIFYCMD`.

Flood protection: each channel sends at most 20 notifications in 10 minutes; the excess is summarised in one later
notification ("Flood protection held back 37 notification(s)..."). `lowBattery`, `forcedShutdown` and the
`shutdown*` events are never held back.

## Placeholders

Webhook URLs, body templates and header values, and command arguments may contain:

| Placeholder | Value |
|---|---|
| `{ups}` | UPS name (empty for server events) |
| `{type}` | event type as in the API: `onBattery`, `lowBattery`, `shutdownStarted`... (`test` for a test message) |
| `{notifytype}` | upsmon name: `ONBATT`, `ONLINE`, `LOWBATT`, `FSD`, `COMMBAD`, `COMMOK`, `SHUTDOWN`, `REPLBATT`, `NOCOMM`... |
| `{severity}` | `info`, `notice`, `warning`, `critical` |
| `{category}` | `power`, `device`, `communication`, `command`, `shutdown`, `audit`, `system` |
| `{message}` | the English sentence of the event |
| `{timestamp}` | UTC, ISO 8601: `2026-09-22T10:15:00Z` |
| `{server}`, `{location}` | server name and location |
| `{status}`, `{charge}`, `{runtime}`, `{load}` | `ups.status`, `battery.charge`, `battery.runtime` (seconds), `ups.load` |
| `{actor}` | who caused the event (`web:admin@192.168.1.10`...) |

Escaping is automatic: in a body whose content type contains `json` the values are JSON-escaped (write the quotes
around the placeholder in the template); in URLs and `application/x-www-form-urlencoded` bodies they are
percent-encoded; elsewhere they are inserted as they are.

Without a body template, POST and PUT send:

```json
{ "server": "NutHub", "ups": "rack1", "type": "onBattery", "notifyType": "ONBATT", "severity": "warning",
  "category": "power", "message": "rack1 is on battery...", "timestamp": "2026-09-22T10:15:00Z",
  "actor": "system", "data": { "battery.charge": "98" },
  "status": { "ups.status": "OB DISCHRG", "battery.charge": "98", "battery.runtime": "1620", "ups.load": "23" } }
```

Header values are stored encrypted, like passwords: put tokens there rather than in the URL when the service allows it.

## Webhook examples

**ntfy** (`https://ntfy.sh` or your own server)

- URL `https://ntfy.sh/my-ups-topic`, method `POST`, content type `text/plain`
- Body template `{message}`
- Headers: `Title: {server} {ups}`, `Priority: high`, `Tags: electric_plug`, and `Authorization: Bearer tk_...`
  for a protected topic.

**Telegram Bot API**

- URL `https://api.telegram.org/bot<TOKEN>/sendMessage`, method `POST`, content type `application/json`
- Body template `{"chat_id": "123456789", "text": "{server}: {message}"}`

**Slack** (incoming webhook)

- URL `https://hooks.slack.com/services/T000/B000/XXXX`, method `POST`, content type `application/json`
- Body template `{"text": ":zap: *{server}* {message}"}`

**Microsoft Teams** (workflow "Post to a channel when a webhook request is received")

- URL: the workflow URL, method `POST`, content type `application/json`
- Body template:
  `{"type": "message", "attachments": [{"contentType": "application/vnd.microsoft.card.adaptive", "content": {"type": "AdaptiveCard", "version": "1.4", "body": [{"type": "TextBlock", "wrap": true, "text": "{server}: {message}"}]}}]}`

**Discord**

- URL `https://discord.com/api/webhooks/<id>/<token>`, method `POST`, content type `application/json`
- Body template `{"username": "NutHub", "content": "**{server}** {message}"}`

**Gotify**

- URL `https://gotify.example.com/message`, method `POST`, content type `application/json`
- Header `X-Gotify-Key: <application token>`
- Body template `{"title": "{server} {ups}", "message": "{message}", "priority": 8}`

**GET request** (e.g. a home automation trigger)

- URL `https://ha.example.com/api/webhook/ups?event={type}&ups={ups}&charge={charge}`, method `GET`

## Commands

The program is started directly, never through a shell, with the arguments split like a command line (double quotes
group, `\"` is a literal quote) and the placeholders substituted inside each argument, so a value can never add
arguments or commands. After `timeoutSeconds` the program and all its children are killed. The exit code and the
first lines of standard error appear in the delivery list.

Environment variables: `NUTHUB_EVENT` (as `{type}`), `NUTHUB_SEVERITY`, `NUTHUB_CATEGORY`, `NUTHUB_UPS`,
`NUTHUB_MESSAGE`, `NUTHUB_TIMESTAMP`, `NUTHUB_SERVER`, `NUTHUB_STATUS`, `NUTHUB_CHARGE`, `NUTHUB_RUNTIME`,
`NUTHUB_LOAD`, `NUTHUB_ACTOR`, `NUTHUB_TEST` (`1` for a test message), and upsmon's `NOTIFYTYPE` and `UPSNAME`, so a
`NOTIFYCMD` script written for upsmon works unchanged. Events upsmon does not have get their own `NOTIFYTYPE`
(`SHUTDOWN_PENDING`, `SHUTDOWN_CANCELLED`, `LOW_BATTERY_CLEARED`, `DRIVER_FAILED`...); a test message has `TEST`.

Examples:

- Linux: command `/usr/local/bin/ups-notify.sh`, arguments `"{ups}" "{message}"`
- Windows batch file: command `C:\Scripts\ups-notify.cmd`, arguments `{type} "{message}"`
- Windows PowerShell: command `powershell.exe`, arguments
  `-NoProfile -ExecutionPolicy Bypass -File "C:\Scripts\ups-notify.ps1" -Event {type}`
