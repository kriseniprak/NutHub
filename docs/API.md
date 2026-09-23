# NutHub web API

The web panel is a client of this API; other tools can use it too. Base path `/api`, JSON bodies, camelCase names,
enums as camelCase strings, timestamps as ISO 8601 UTC strings (`"2026-09-21T14:03:12.345Z"`), numbers as JSON
numbers (`null` when unknown).

## Conventions

**Authentication.** A session cookie (`nuthub_session`, HttpOnly, SameSite=Strict, Secure over HTTPS) set by
`POST /api/auth/login`. Sessions slide: they expire after `web.sessionHours` without activity. A change of the
account's password, role or state ends its other sessions.

**Anti-forgery.** Every request other than GET/HEAD must carry the header `X-NutHub-Request: 1`; otherwise the
answer is `400 {"error":"csrf"}`. Browsers cannot add that header cross-site without a CORS preflight, which the
server never allows.

**Roles.** `viewer` < `operator` < `admin`. Read endpoints need `viewer`, or nothing when
`web.allowAnonymousRead` is on. While an account has `mustChangePassword`, everything except `/api/auth/*` answers
`403 {"error":"passwordChangeRequired"}`.

**Errors.** `{"error": "<code>", "message": "<English text>", "fields": {"<field>": "<message>"}}` where `fields`
is present only for validation errors. Codes and status:

| Status | `error` | When |
|---|---|---|
| 400 | `validation` | Invalid body; `fields` names the problems (paths like `name`, `options.port`, `listen[0].port`) |
| 400 | `badRequest` | Malformed request |
| 400 | `csrf` | Missing `X-NutHub-Request` header |
| 401 | `unauthorized` | Not signed in |
| 401 | `invalidCredentials` | Wrong user name or password at login |
| 403 | `forbidden` | Role too low, or client address not allowed |
| 403 | `passwordChangeRequired` | See above |
| 404 | `notFound` | Unknown UPS, account, driver... |
| 409 | `conflict` | Name already used |
| 409 | `lastAdmin` | Would leave no enabled administrator |
| 409 | `self` | Deleting, disabling or demoting your own account |
| 413 / 415 | `badRequest` | Body larger than 1 MB, or not JSON |
| 429 | `rateLimited` | Too many login attempts; `retryAfterSeconds` in the body and `Retry-After` header |
| 500 | `internal` | Unexpected error (details in the server log only) |

**Command results.** Instant commands, variable writes and notification tests answer `200` with
`{"ok": true|false, "status": "<CommandStatus>", "message": "..."}` where status is one of `success`, `unknownUps`,
`notSupported`, `readOnly`, `invalidValue`, `tooLong`, `invalidArgument`, `driverNotConnected`, `accessDenied`,
`failed`. An unknown UPS name answers `404 notFound` instead.

## Shared shapes

### UpsSummary

```json
{
  "name": "rack1",
  "description": "Rack A",
  "driver": "usbhid",
  "driverName": "USB HID Power Device",
  "enabled": true,
  "driverState": "connected",
  "driverMessage": null,
  "availability": "available",
  "status": "OL CHRG",
  "flags": ["OL", "CHRG"],
  "forcedShutdown": false,
  "severity": "ok",
  "mfr": "APC",
  "model": "Back-UPS ES 700",
  "serial": "4B1234P56789",
  "battery": { "charge": 100, "runtime": 1680, "voltage": 13.5, "chargeLow": 10, "runtimeLow": 120 },
  "load": 34,
  "inputVoltage": 231.2,
  "outputVoltage": 230.1,
  "inputFrequency": 50.0,
  "temperature": 30.1,
  "power": 510,
  "powerNominal": 1500,
  "realPower": 306,
  "realPowerNominal": 900,
  "lastUpdate": "2026-09-21T14:03:12.345Z",
  "clients": 2,
  "sequence": 42
}
```

- `driverState`: `disabled`, `starting`, `connecting`, `connected`, `disconnected`, `failed`.
- `availability`: `available`, `stale`, `driverNotConnected`.
- `severity` (for colours), first match wins: `offline` (disabled, or no driver connected), `critical` (FSD, or
  on battery with low battery, or `OFF`), `warning` (on battery, `RB`, `OVER`, `BYPASS`, `ALARM`, stale data),
  `info` (`CAL`, `TEST`, `TRIM`, `BOOST`), `ok`.
- `battery.runtime` in seconds. Any value the UPS does not report is `null`. When data is stale the last known
  values are still returned.
- `clients`: NUT connections logged in to this UPS.

### EventDto

```json
{
  "id": 1234,
  "timestamp": "2026-09-21T14:03:12.345Z",
  "ups": "rack1",
  "type": "onBattery",
  "severity": "warning",
  "category": "power",
  "message": "rack1 is on battery (battery 98%, runtime 27 min).",
  "actor": "system",
  "data": { "battery.charge": "98" }
}
```

`type` is a `UpsEventType` in camelCase (`onBattery`, `online`, `lowBattery`, `lowBatteryCleared`,
`forcedShutdown`, `forcedShutdownCleared`, `replaceBattery`, ..., `communicationLost`, `communicationRestored`,
`noCommunication`, `driverFailed`, `commandExecuted`, `commandFailed`, `variableChanged`, `shutdownPending`,
`shutdownCancelled`, `shutdownStarted`, `serverStarted`, `serverStopping`, `configurationChanged`, `userLogin`,
`userLoginFailed`, `nutClientLogin`, `nutClientLogout`, `notificationTest`). `severity`: `info`, `notice`, `warning`, `critical`.
`category`: `power`, `device`, `communication`, `command`, `shutdown`, `audit`, `system`. `ups` is null for
server-wide events.

### NutClientDto

```json
{ "id": 7, "address": "192.168.1.20", "port": 50122, "username": "upsmon", "tls": false,
  "loginUps": "rack1", "primary": false, "connectedAt": "...", "lastActivity": "...", "commands": 1234 }
```

### HostProtectionDto

```json
{ "enabled": true, "state": "monitoring", "reason": null, "shutdownAt": null, "dryRun": false,
  "criticalUps": [], "ups": ["rack1"] }
```

`state`: `disabled`, `monitoring`, `pending`, `waitingForSecondaries`, `shuttingDown`, `dryRunCompleted`.

## Authentication

| Method | Path | Body | Answer |
|---|---|---|---|
| GET | `/api/auth/state` | | `{ "authenticated": bool, "user": {"name","displayName","role","mustChangePassword"} \| null, "anonymousRead": bool, "serverName": "NutHub", "version": "1.0.0" }` (never 401) |
| POST | `/api/auth/login` | `{ "username", "password" }` | `200 { "user": {...} }`, `401 invalidCredentials`, `429 rateLimited` |
| POST | `/api/auth/logout` | | `204` |
| POST | `/api/auth/password` | `{ "currentPassword", "newPassword" }` | `204`; `400 validation` (new password at least 8 characters and different), `401 invalidCredentials` (wrong current password) |

## Reading (viewer, or anonymous when allowed)

| Method | Path | Answer |
|---|---|---|
| GET | `/api/overview` | `{ "server": ServerInfo, "ups": [UpsSummary] }` |
| GET | `/api/ups/{name}` | `UpsDetail` |
| GET | `/api/ups/{name}/history?range=1h&vars=battery.charge,ups.load` | `History` |
| GET | `/api/events?ups=&minSeverity=&category=&beforeId=&limit=100&search=` | `{ "items": [EventDto], "hasMore": bool }` |
| GET | `/api/stream` | Server-sent events, see below |

ServerInfo:

```json
{
  "name": "NutHub", "location": "Server room", "version": "1.0.0",
  "startedAt": "...", "uptimeSeconds": 12345, "time": "...",
  "nut": { "enabled": true, "endpoints": ["[::]:3493"], "lastError": null, "tls": false, "clients": 3 },
  "hostProtection": HostProtectionDto
}
```

UpsDetail:

```json
{
  "summary": UpsSummary,
  "variables": [
    { "name": "battery.charge", "value": "100", "description": "Battery charge (percent of full)",
      "writable": false, "type": "number", "maxLength": 0, "enumValues": [], "ranges": [], "overridden": false }
  ],
  "commands": [ { "name": "test.battery.start.quick", "description": "Start a quick battery test", "dangerous": false } ],
  "clients": [NutClientDto],
  "pollIntervalSeconds": 2
}
```

`ranges` items are `{ "min": "5", "max": "90" }`, with the values as strings, as NUT sends them.

`dangerous` is true for commands that can cut the power of the load or change the battery state:
`load.off*`, `shutdown.*` (except `shutdown.stop`), `calibrate.start`, `test.failure.start`, `bypass.start`,
`reset.*`, `outlet.*.off*`, `outlet.*.shutdown*`. The panel asks for confirmation before sending them.

History: `range` is one of `1h`, `6h`, `24h`, `7d`, `30d`, `90d` (default `24h`), or `from` and `to` as ISO
timestamps. `vars` defaults to the configured history variables. At most ~500 points per series.

```json
{
  "from": "...", "to": "...", "stepSeconds": 30,
  "series": { "battery.charge": [[1726927392000, 99.5, 99, 100], [1726927422000, 99.6, 99, 100]] }
}
```

Each point is `[unix milliseconds, average, minimum, maximum]`.

### Server-sent events: `GET /api/stream`

`Content-Type: text/event-stream`, starting with `retry: 5000`. Events:

| `event:` | `data:` | When |
|---|---|---|
| `overview` | the same object as `GET /api/overview` | on connection, then every 60 s as a resync |
| `ups` | `UpsSummary` | a UPS changed (at most one per UPS per second, the latest wins) |
| `upsRemoved` | `{ "name": "rack1" }` | a UPS was deleted |
| `event` | `EventDto` | an event was recorded |
| `clients` | `{ "total": 3, "byUps": { "rack1": 2 } }` | NUT clients connected, logged in or left |
| `hostProtection` | `HostProtectionDto` | the host protection changed state |
| `ping` | `{ "time": "..." }` | every 15 s, keeps proxies from closing the stream |

The stream ends (and the browser reconnects) when the session expires; the panel then checks `/api/auth/state`.

## Operating (operator)

| Method | Path | Body | Answer |
|---|---|---|---|
| POST | `/api/ups/{name}/commands` | `{ "command": "test.battery.start.quick", "parameter": null }` | command result |
| PUT | `/api/ups/{name}/variables/{variable}` | `{ "value": "20" }` | command result |
| POST | `/api/ups/{name}/fsd` | | command result |
| DELETE | `/api/ups/{name}/fsd` | | command result (success also when no FSD was set; the message says so) |
| POST | `/api/host-protection/cancel` | | `{ "ok": bool }` |

## Administration (admin)

### Drivers and UPSes

| Method | Path | Body | Answer |
|---|---|---|---|
| GET | `/api/admin/drivers` | | `[DriverDto]` |
| POST | `/api/admin/drivers/{id}/discover` | | `{ "devices": [ { "title", "detail", "options": {}, "suggestedName" } ], "message": null }` (up to 30 s; `message` explains a timeout, a failure or a driver without discovery) |
| GET | `/api/admin/serial-ports` | | `["COM3", "/dev/ttyUSB0", "/dev/serial/by-id/usb-..."]` |
| GET | `/api/admin/ups` | | `[UpsConfigDto]` |
| POST | `/api/admin/ups` | `UpsConfigDto` | `201 UpsConfigDto` |
| PUT | `/api/admin/ups/{name}` | `UpsConfigDto` (may rename) | `200 UpsConfigDto` |
| DELETE | `/api/admin/ups/{name}` | | `204`; `409 conflict` when a NUT account is restricted to this UPS only |
| POST | `/api/admin/ups/{name}/restart` | | `204`; `400 badRequest` when the UPS is disabled |
| PUT | `/api/admin/ups-order` | `{ "names": ["rack1", "rack2"] }` | `204` |
| POST | `/api/admin/import/nut` | `{ "upsConf": "...", "upsdUsers": "...", "apply": false }` | `{ "ups": [UpsConfigDto], "nutUsers": [NutUserDto], "warnings": ["..."], "applied": bool }` |

DriverDto:

```json
{
  "id": "snmp", "displayName": "SNMP network card", "description": "...",
  "platforms": ["windows", "linux", "macOS"], "supported": true, "supportsDiscovery": false,
  "options": [
    { "key": "host", "label": "Host", "type": "host", "required": true, "default": null, "help": null,
      "choices": null, "min": null, "max": null, "advanced": false, "visibleWhen": null },
    { "key": "authPassword", "label": "Authentication password", "type": "secret", "required": false,
      "visibleWhen": { "key": "version", "values": ["3"] } }
  ]
}
```

Option `type`: `string`, `integer`, `decimal`, `boolean`, `choice`, `secret`, `host`, `port`, `serialPort`,
`filePath`.

UpsConfigDto:

```json
{
  "name": "rack1", "description": "Rack A", "driver": "usbhid", "enabled": true, "pollIntervalSeconds": 2,
  "options": { "vendorId": "051d" },
  "secretsSet": ["community"],
  "overrides": { "battery.charge.low": "30" },
  "lowBattery": { "chargePercent": null, "runtimeSeconds": 180, "ignoreDeviceFlag": false }
}
```

Secret options are never returned: `secretsSet` lists those that have a value. On create/update, a secret option
absent from `options` keeps its value, `""` clears it, any other value replaces it. Missing required options give
`400 validation` with `fields["options.<key>"]`. Renaming a UPS also renames it in the host protection and in NUT
accounts; deleting removes it from both.

`import/nut` reads NUT's `ups.conf` and `upsd.users`, maps the drivers NutHub has (`usbhid-ups` → `usbhid`,
`nutdrv_qx`/`blazer_ser`/`blazer_usb` → `megatec`, `apcsmart` → `apcsmart`, `snmp-ups` → `snmp`, `dummy-ups` with
`port = ups@host` → `nut`, others reported in `warnings`) and, with `apply: true`, adds them (existing names are
skipped with a warning). Passwords from `upsd.users` are hashed.

### Accounts

| Method | Path | Body | Answer |
|---|---|---|---|
| GET | `/api/admin/nut-users` | | `[NutUserDto]` |
| POST | `/api/admin/nut-users` | `NutUserDto` with `password` | `201 NutUserDto` |
| PUT | `/api/admin/nut-users/{name}` | `NutUserDto` (`password` empty or absent keeps it) | `200 NutUserDto` |
| DELETE | `/api/admin/nut-users/{name}` | | `204` |
| GET | `/api/admin/web-users` | | `[WebUserDto]` |
| POST | `/api/admin/web-users` | `WebUserDto` with `password` | `201 WebUserDto` |
| PUT | `/api/admin/web-users/{name}` | `WebUserDto` (`password` optional) | `200 WebUserDto` |
| DELETE | `/api/admin/web-users/{name}` | | `204` |

NutUserDto: `{ "name", "password"?, "monitor": "none"|"secondary"|"primary", "actions": ["SET","FSD"],
"instantCommands": ["ALL"], "allowedUps": [] }`.
WebUserDto: `{ "name", "displayName", "role": "viewer"|"operator"|"admin", "disabled", "mustChangePassword",
"password"? }`. Passwords: at least 8 characters.

### Settings

| Method | Path | Body | Answer |
|---|---|---|---|
| GET | `/api/admin/settings` | | `{ "server": {...}, "nut": {...}, "web": {...}, "history": {...} }` |
| PUT | `/api/admin/settings/server` | `{ "name", "location" }` | `200 { "settings": {...} }` (that section only) |
| PUT | `/api/admin/settings/nut` | the `nut` object | `200 { "settings": {...} }` |
| PUT | `/api/admin/settings/web` | the `web` object | `200 { "settings": {...}, "notice": "The panel moves to https://host:8494/" }` |
| PUT | `/api/admin/settings/history` | the `history` object | `200 { "settings": {...} }` |
| GET | `/api/admin/notifications` | | notification settings, see below |
| PUT | `/api/admin/notifications` | notification settings | `200` notification settings |
| POST | `/api/admin/notifications/test` | `{ "channel": "email"\|"webhook"\|"command", "id": null }` | command result |
| GET | `/api/admin/notifications/deliveries` | | `[{ "timestamp", "channel", "target", "eventType", "ups", "success", "error" }]` |
| GET | `/api/admin/host-protection` | | `{ "settings": HostProtectionSettings, "status": HostProtectionDto, "defaultShutdownCommand": "..." }` |
| PUT | `/api/admin/host-protection` | `HostProtectionSettings` | `200` same as GET |

A PUT body is merged onto the current section: members left out keep their values. The settings objects mirror `NutHubConfig` (see `NutHubConfig.cs`) with secrets replaced: `certificatePassword`
is never returned, `certificatePasswordSet: bool` is; on PUT an absent `certificatePassword` keeps it, `""` clears
it. Web settings are applied after the answer is sent: when the port, address or protocol changes the panel
follows the `notice`.

Notification settings: `{ "events": [...], "email": {..., "password" never returned, "passwordSet": bool},
"webhooks": [ {..., "headers": { "Authorization": null }} ], "commands": [...] }`. Header values are secrets:
returned as `null`; on PUT `null` keeps the stored value, a string replaces it, a header missing from the object is
removed.

### Server

| Method | Path | Answer |
|---|---|---|
| GET | `/api/admin/clients` | `[NutClientDto]` |
| DELETE | `/api/admin/clients/{id}` | `204` (closes the connection) |
| GET | `/api/admin/logs?minLevel=information&limit=500&search=` | `[{ "id", "timestamp", "level", "category", "message", "exception" }]` newest first; `level`: `trace`...`critical` |
| GET | `/api/admin/system` | `{ "version", "os", "architecture", "framework", "processId", "machineName", "isService", "startedAt", "uptimeSeconds", "workingSetBytes", "dataDirectory", "configFile", "databaseFile", "drivers": [{"id","supported"}] }` |
| GET | `/api/admin/config/export` | the configuration file without secrets, as an attachment `nuthub-config.json` |
