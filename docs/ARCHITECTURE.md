# NutHub architecture

NutHub is a UPS server compatible with the [Network UPS Tools](https://networkupstools.org/) network protocol
(what `upsd` does), written in C# for .NET 10. It runs on Windows and Linux (x64, arm64, arm), serves any number of
UPSes at once through built-in drivers, and has a web panel for monitoring and administration.

Existing NUT clients (`upsmon`, `upsc`, `upscmd`, `upsrw`, NutDesk, WinNUT, Home Assistant, Synology/QNAP NAS...)
connect to it on TCP port 3493 exactly as they would to `upsd`.

## Solution layout

| Project | Role |
|---|---|
| `src/NutHub.Core` | Model, driver contracts and runtime (registry, driver manager), configuration, security primitives, event hub, the simulated driver. Every other project depends on it; it depends on none. |
| `src/NutHub.Protocol` | The NUT protocol server: TCP listener(s), STARTTLS, authentication against NUT accounts, every protocol command. |
| `src/NutHub.Drivers.Hid` | `usbhid` (USB HID Power Device class, like NUT `usbhid-ups`) through HidSharp; `winbattery` (Windows battery class, for UPSes Windows already claims). |
| `src/NutHub.Drivers.Serial` | `megatec` (Q1/Voltronic family, like NUT `nutdrv_qx`/`blazer`) and `apcsmart`, over a serial port, a TCP serial server or a USB-serial HID bridge. |
| `src/NutHub.Drivers.Snmp` | `snmp` (UPS network cards: RFC 1628 UPS-MIB, APC PowerNet, Eaton, MGE, CyberPower...), SNMP v1/v2c/v3. |
| `src/NutHub.Drivers.Net` | `nut` (a UPS of another NUT server, like `dummy-ups` in repeater mode) and `apcupsd` (the apcupsd NIS protocol). |
| `src/NutHub.Storage` | SQLite event log and history (time series) with retention and roll-ups. |
| `src/NutHub.Services` | Notifications (email, webhooks, commands) and host protection (shutting down the NutHub machine, like `upsmon` primary). |
| `src/NutHub.Web` | The web panel: its own Kestrel server (HTTP/HTTPS, restarted on configuration changes), REST API, server-sent events, authentication, and the single-page application embedded as resources. |
| `src/NutHub` | The executable `nuthub`: host, command line, Windows service / systemd integration, logging. |
| `tests/NutHub.*.Tests` | xUnit tests, one project per source project. |
| `packaging/` | systemd unit, udev rules, install scripts, Dockerfile. |
| `tools/` | Test doubles (fake serial UPS, fake SNMP agent...) and release scripts. |

Each project exposes one registration method, called by `src/NutHub/Program.cs`:

```csharp
services.AddNutHubCore(paths)        // NutHub.Core
        .AddNutHubProtocol()         // NutHub.Protocol
        .AddNutHubHidDrivers()       // NutHub.Drivers.Hid
        .AddNutHubSerialDrivers()    // NutHub.Drivers.Serial
        .AddNutHubSnmpDrivers()      // NutHub.Drivers.Snmp
        .AddNutHubNetworkDrivers()   // NutHub.Drivers.Net
        .AddNutHubStorage()          // NutHub.Storage
        .AddNutHubServices()         // NutHub.Services
        .AddNutHubWeb();             // NutHub.Web
```

The whole application uses the ASP.NET Core shared framework (`Microsoft.AspNetCore.App`, referenced by Core), so
hosting, logging, dependency injection and options come from one place. Package versions are central in
`Directory.Packages.props`; the product version is set once in `Directory.Build.props`.

## Data flow

```
 device ──driver──► IDriverContext.Publish(DriverUpdate)
                         │
                         ▼
                    UpsUnit (one per UPS)  ── overrides, low-battery thresholds, FSD, driver.* ──► UpsSnapshot
                         │                                                                       (immutable)
                         ├─ EventHub: SnapshotChangedMessage, UpsEventMessage (on battery, low battery...)
                         ▼
     ┌──────────────┬──────────────┬───────────────┬────────────────┬──────────────┐
  Protocol (3493)  Web API / SSE   EventRecorder →  History recorder  Notifications  Host protection
                                   IEventStore       (Storage)         (Services)     (Services)
```

- A **driver** (`IUpsDriver`) talks to one device and calls `IDriverContext.Publish` every poll with the complete
  set of variables in NUT naming (`battery.charge`, `ups.status`...), the metadata of writable variables and the list
  of instant commands. It reports connection problems with `ReportConnecting` / `ReportDisconnected` and keeps
  retrying on its own; it throws only for unrecoverable errors (`DriverConfigurationException` for bad options).
- The **driver manager** (`DriverManager`, a hosted service) starts one driver per enabled UPS, restarts failed
  drivers with a back-off (2, 5, 10, 30, 60 s), and applies configuration changes live: added, removed and modified
  UPSes need no restart of NutHub.
- A **`UpsUnit`** turns driver data into an immutable **`UpsSnapshot`**: applies overrides, adds `device.*` /
  `driver.*` variables, adds `LB` from the configured thresholds, puts `FSD` in front of `ups.status` when a forced
  shutdown is set, and computes the data availability (fresh / stale / driver not connected, with NUT `MAXAGE`
  semantics, at least two poll intervals for a UPS polled less often; durations use the monotonic clock). It raises
  events on status transitions and exposes the operations: `InstantCommandAsync`, `SetVariableAsync` (with enum /
  range / length validation), `SetForcedShutdown`, `ClearForcedShutdown`.
  Snapshots are replaced atomically; consumers compare `Sequence` to detect changes.
- The **event hub** (`EventHub`) is the in-process publish/subscribe. Synchronous handlers for quick work, bounded
  channels (`SubscribeChannel`) for slow consumers.
- **`IUpsRegistry`** lists the units in configuration order; `Find` ignores case like `upsd`.
- **`NutSessionRegistry`** tracks the connections of NUT clients (who is logged in to which UPS, who is primary):
  the protocol server maintains it; the host protection counts secondaries from it; the web panel lists them.

## Configuration

One JSON file (`nuthub.json`, camelCase, enums as camelCase strings), modelled by `NutHubConfig`. Everything is
changed through `IConfigStore.UpdateAsync(mutate, origin, description)`, which clones the current configuration,
applies the change, validates the result (`ConfigValidator`, errors keyed by field path such as `ups[0].name`),
saves it atomically (temporary file + rename, previous version kept as `.bak`) and raises `Changed`. Hand edits of
the file are picked up while running. Consumers read `IConfigStore.Current` and treat it as immutable.

Secrets (certificate passwords, SMTP password, webhook header values, driver options of type `Secret`) are stored
encrypted (`enc:v1:...`, AES-256-GCM, key in `secret.key` in the data directory) through `ISecretProtector`.
Values typed in clear text by hand are encrypted at the next save. The web API never returns secrets.

| | Windows | Linux (root / service) | Linux (user) |
|---|---|---|---|
| Configuration | `%ProgramData%\NutHub\nuthub.json` | `/etc/nuthub/nuthub.json` | `~/.config/nuthub/nuthub.json` |
| Data (database, logs, keys, certificates) | `%ProgramData%\NutHub` | `/var/lib/nuthub` | `~/.local/share/nuthub` |

`--data-dir` / `--config` and the `NUTHUB_DATA_DIR` / `NUTHUB_CONFIG` environment variables override these. The
directories are restricted to the service account (and SYSTEM / Administrators on Windows).

On first start without an administrator, NutHub creates the web account `admin` with a random password, written to
`initial-admin-password.txt` in the data directory (and printed on an interactive console); the password must be
changed at the first sign-in.

## Drivers

A driver is an `IUpsDriverFactory` registered as a singleton plus the `IUpsDriver` it creates. The factory
describes its options (`DriverOption`: key, label, type, default, help, choices, visibility condition), from
which the web panel builds its forms, and can discover devices (`DiscoverAsync`) to pre-fill them.

Rules every driver follows:

- Variable names and values follow NUT conventions (see `docs/nut-names.txt` upstream); numbers are formatted with
  `NutFormat.Number` / `NutFormat.Fixed` (dot decimal separator, no exponent). `ups.status` uses the standard tokens
  (`OL`, `OB`, `LB`, `CHRG`, `DISCHRG`, `RB`, `BYPASS`, `CAL`, `OFF`, `OVER`, `TRIM`, `BOOST`, `ALARM`, `TEST`...).
- Publish `ups.mfr`, `ups.model`, `ups.serial` when known (the `device.*` twins are added automatically), and
  `driver.version.data` / `driver.version.internal` if useful; never other `driver.*` variables.
- Publish every poll, even when nothing changed: it is the heartbeat that keeps the data fresh.
- Commands and variable writes may run concurrently with the poll loop: serialise access to the device.
- Respect the cancellation token; release the device in `DisposeAsync`.
- Never block a thread pool thread for long; use async I/O or a dedicated thread for blocking device APIs.

| Id | Devices | Transport |
|---|---|---|
| `usbhid` | USB UPSes implementing the HID Power Device class: APC, Eaton/MGE, CyberPower, Tripp Lite, Belkin, Liebert, Powercom, Delta, Salicru and many more | USB (HidSharp: Windows HID API, Linux hidraw) |
| `winbattery` | Any UPS Windows shows as a battery (Windows only) | Windows battery class |
| `megatec` | Q1 / Megatec / Voltronic protocol UPSes (Tecnoware, Atlantis, Mecer, Powercool, many OEM) | Serial, TCP serial server, USB-serial HID bridges |
| `apcsmart` | APC Smart-UPS with a serial port | Serial, TCP serial server |
| `snmp` | UPS network cards | SNMP v1 / v2c / v3 |
| `nut` | A UPS of another NUT server | NUT protocol |
| `apcupsd` | A UPS managed by apcupsd | apcupsd NIS (TCP 3551) |
| `simulated` | A virtual UPS for testing | none |

## NUT protocol server

Implements the NUT network protocol 1.3 (NUT 2.8): `VER`, `NETVER`/`PROTVER`, `HELP`, `STARTTLS`, `USERNAME`,
`PASSWORD`, `LOGIN`, `LOGOUT`, `PRIMARY`/`MASTER`, `FSD`, `GET` (`VAR`, `TYPE`, `DESC`, `CMDDESC`, `UPSDESC`,
`NUMLOGINS`, `TRACKING`), `LIST` (`UPS`, `VAR`, `RW`, `CMD`, `ENUM`, `RANGE`, `CLIENT`), `SET` (`VAR`,
`TRACKING`), `INSTCMD`, with upsd's quoting rules and error codes. Reading is anonymous, as with upsd; `LOGIN`,
`PRIMARY`, `FSD`, `SET` and `INSTCMD` need a NUT account (`NutUserConfig`, the equivalent of `upsd.users`) with the
right role, actions and instant commands. Client networks can be restricted (CIDR allow-list). Listeners follow
configuration changes without a restart. Descriptions come from NUT's `cmdvartab`, embedded in Core.

A forced shutdown (`FSD`) behaves as in upsd, with one addition: it can be cleared by an operator, and is cleared
automatically after the UPS has been back on line power for `nut.fsdClearDelaySeconds` (0 keeps upsd's latch
behaviour).

## Web panel

`NutHub.Web` runs its own Kestrel server inside the host (so ports and certificates change without restarting
NutHub), serving:

- the REST API and the server-sent event stream described in [API.md](API.md);
- the single-page application in `src/NutHub.Web/wwwroot` (plain HTML, CSS and JavaScript modules, no build step,
  no external resources), embedded in the assembly.

Roles: **viewer** (dashboard, details, history, events), **operator** (plus instant commands, variable writes, FSD),
**admin** (plus configuration and accounts). Optionally the read-only views are open without signing in.

## Storage

SQLite (`nuthub.db`, WAL mode). Events are kept `history.eventRetentionDays`; samples of the numeric variables in
`history.variables` every `history.sampleIntervalSeconds`, raw for 7 days and as 5-minute roll-ups (average,
minimum, maximum) for `history.retentionDays`.

## Services

- **Notifications**: email (SMTP with STARTTLS/SSL and authentication, messages queued on disk while the server is
  unreachable), webhooks (JSON or templated body, custom headers, retries), and commands (a program run with the
  event in environment variables, compatible with upsmon's `NOTIFYTYPE` / `UPSNAME`;
  `.bat` / `.cmd` scripts run through cmd.exe with escaped arguments). Each channel has its own
  event filter.
- **Host protection**: the job of upsmon in primary mode for the NutHub machine itself. When fewer than
  `minimumSupplies` of the configured UPSes are healthy (critical = on battery with low battery, except during a calibration, as upsmon; or thresholds on
  charge, runtime or time on battery, or FSD, or communication lost while on battery), it waits the grace period,
  sets FSD so NUT secondaries shut down, waits for them to log out, optionally tells the UPS to power off after a
  delay and return when mains power returns, and shuts the machine down. A dry-run mode does everything but the
  shutdown. Debug builds never execute the shutdown or the UPS power-off.

## Conventions

- C# latest, nullable enabled, file-scoped namespaces, `sealed` by default, `async` all the way, `TimeProvider`
  for time (tests use `FakeTimeProvider`), `ILogger<T>` for logs. Comments explain why, not what.
- User-facing messages (events, errors) are English sentences; the web panel localises by event type and error
  code.
- Tests: xUnit, one test project per source project, no hardware or network access outside the loopback interface.
- No new dependencies without a clear need; every package is in `Directory.Packages.props`.
