# Configuration

This page describes every setting of NutHub in the order of the **Settings** area of the web panel, together with
the configuration file that holds them. The panel and the file are two views of the same settings: a change made in
one shows in the other, and changes apply while NutHub runs. Installation and the first sign-in are covered in
[installation.md](installation.md), setting up upsmon, NutDesk and other NUT clients in [clients.md](clients.md), and
common problems in [troubleshooting.md](troubleshooting.md); the web API behind the panel is described in
[API.md](API.md).

- [The configuration file](#the-configuration-file)
- [UPS devices](#ups-devices)
- [Driver reference](#driver-reference)
- [Import from NUT](#import-from-nut)
- [NUT server](#nut-server)
- [NUT accounts](#nut-accounts)
- [Web panel](#web-panel)
- [Web accounts](#web-accounts)
- [Server identity](#server-identity)
- [Notifications](#notifications)
- [Host protection](#host-protection)
- [History](#history)
- [System, logs and the event log](#system-logs-and-the-event-log)

The settings pages need an administrator account. The **Settings** page groups them as follows; on the pages that
have them (UPS form, NUT server, Web panel, Host protection), the settings you rarely need are folded under
**Advanced**.

| Group | Pages |
|---|---|
| Devices | UPS devices, Import from NUT |
| Access | Web accounts, NUT accounts |
| Server | NUT server, Web panel, Server identity |
| Alerts | Notifications, Host protection |
| Data | History |
| System | System, Logs |

## The configuration file

### Where it is

| Installation | Configuration file | Data directory |
|---|---|---|
| Windows (service or console) | `%ProgramData%\NutHub\nuthub.json` | `%ProgramData%\NutHub` |
| Linux, systemd service or run as root | `/etc/nuthub/nuthub.json` | `/var/lib/nuthub` |
| Linux, run as an ordinary user | `~/.config/nuthub/nuthub.json` | `~/.local/share/nuthub` |
| Docker (`docker-compose.yml`) | `/etc/nuthub/nuthub.json` (volume `nuthub-config`) | `/var/lib/nuthub` (volume `nuthub-data`) |

`--config FILE` and `--data-dir DIR` on the command line, or the `NUTHUB_CONFIG` and `NUTHUB_DATA_DIR` environment
variables, override these locations (the systemd unit and the Docker image set both variables). With a data
directory and no configuration file given, the file is `nuthub.json` inside the data directory. For an ordinary
Linux user, `XDG_CONFIG_HOME` and `XDG_DATA_HOME` are honoured. **Settings > System** shows the paths the running
server actually uses.

The data directory holds:

| Path | Content |
|---|---|
| `nuthub.db` | The event log and the history (SQLite) |
| `secret.key` | The key that encrypts the secrets of the configuration file |
| `logs/` | The log files |
| `certs/` | The self-signed certificates NutHub generates (`nut-selfsigned.pfx`, `web-selfsigned.pfx`) |
| `notifications/outbox.json` | E-mail messages waiting to be sent |
| `initial-admin-password.txt` | The password of the first administrator; delete it after the first sign-in |

Both directories are restricted to the account NutHub runs as (and to administrators on Windows).

### Format

The file is JSON with camelCase keys; enumerations are written as strings (`"secondary"`, `"sslOnConnect"`). NutHub
rewrites the whole file, indented, every time a setting changes, and leaves out values that are not set. A setting
missing from the file takes its default. Comments and trailing commas are accepted when the file is read, but a
comment disappears at the next save. A UTF-8 byte order mark, as Notepad writes it, is accepted too.

| Section | Page in the panel |
|---|---|
| `server` | [Server identity](#server-identity) |
| `nut` | [NUT server](#nut-server) |
| `web` | [Web panel](#web-panel) |
| `ups` | [UPS devices](#ups-devices) (a list, in display order) |
| `nutUsers` | [NUT accounts](#nut-accounts) |
| `webUsers` | [Web accounts](#web-accounts) |
| `notifications` | [Notifications](#notifications) |
| `hostProtection` | [Host protection](#host-protection) |
| `history` | [History](#history) |

The tables on this page give the key of each setting relative to its section. One entry of the `ups` list looks like
this:

```json
{
  "name": "rack1",
  "description": "Smart-UPS 1500, rack 1",
  "driver": "usbhid",
  "enabled": true,
  "pollIntervalSeconds": 2,
  "options": {
    "vendorId": "051d",
    "subdriver": "auto"
  },
  "overrides": {
    "battery.charge.low": "30"
  },
  "lowBattery": {
    "chargePercent": 25,
    "ignoreDeviceFlag": false
  }
}
```

### Editing by hand

NutHub watches the file. When you save an edit, it is applied about a second later, while NutHub runs, exactly as if
it had been made in the panel: only the drivers whose settings changed restart, and the listeners follow new
addresses and ports. The event log records it as **Configuration changed**, done by `file`.

An edit that is not valid (broken JSON, a value out of range, an unknown driver) is not applied: NutHub keeps the
previous configuration and logs `The edited configuration file was not applied:` followed by the reason. At start-up,
an invalid file stops NutHub with a message that names the field, for example
`ups[0].pollIntervalSeconds: Must be between 0.5 and 300 seconds.`

Check a file before relying on it:

```bash
sudo /opt/nuthub/nuthub check-config                                  # Linux
```

```powershell
& "C:\Program Files\NutHub\nuthub.exe" check-config                   # Windows, administrator PowerShell
```

```bash
docker compose exec nuthub /app/nuthub check-config                   # Docker, from packaging/docker
```

`check-config` loads the file as the server would, without changing it, also checks that its encrypted values can be
decrypted with this installation's key, and exits with code 0 when the file is valid.

The panel checks the options of a driver (type, range, allowed values) before saving. A driver option you set by
hand is checked when the driver starts instead: a wrong value shows on the UPS as **Driver failed** with the reason,
and the driver is not retried until the configuration changes.

Account passwords are stored as hashes (`passwordHash`), never in clear text. Set them in the panel, or with
`nuthub passwd <web-user>` and `nuthub nut-user set-password <name>`, which also work while NutHub runs.

### Secrets

These values are stored encrypted, as `enc:v1:...` (AES-256-GCM), with the key kept in `secret.key` in the data
directory:

- the certificate passwords of the NUT server and of the web panel;
- the SMTP password;
- the values of webhook headers;
- the driver options of type *secret*: SNMP communities and SNMPv3 passwords, the password of the `nut` driver.

You can type a secret in clear text in the file: NutHub encrypts it as soon as it reads the file. The panel never
shows a stored secret: its field says **A value is stored**; leave it empty to keep the value, or use **Clear** to
remove it.

Keep `secret.key` together with the configuration. A `nuthub.json` copied to another installation without its key
loads, but its secrets cannot be decrypted there: `check-config` reports them, and you have to type them again.

### Backups

Every save keeps the previous version of the file as `nuthub.json.bak`, next to it. To go back one step, copy it over
`nuthub.json`; the running server applies it like any other edit.

**Settings > System > Download configuration** gives you the file without secrets, password hashes or webhook header
values, suitable for a support request. It is not meant to be restored as it is, since its accounts have no
password. A complete backup is `nuthub.json` with `secret.key`, plus `nuthub.db` if you want to keep the history and
the events.

## UPS devices

**Settings > UPS devices** lists the UPSes in the order the dashboard shows them and NUT clients list them
(`upsc -l`). Drag a row, or use **Move *name* up** and **Move *name* down** in its menu (···), to change the order.
The same menu has **Show status**, **Edit**, **Enable** / **Disable**, **Restart driver** and **Delete**.

- **Disable** keeps the settings but stops the driver; NUT clients see the UPS as unavailable.
- **Restart driver** closes the connection to the device and opens it again. It is also in the **Actions** menu of
  the UPS page.
- **Delete** removes the UPS from the host protection and from the allowed UPSes of the NUT accounts. It is refused
  while a NUT account is limited to that UPS alone, because the account would then be allowed every UPS: change or
  delete the account first. The history of the UPS stays until it expires.

### Adding a UPS

Press **Add a UPS** and fill in the three parts of the form.

1. **Driver**: one card per driver. A driver that does not work on this operating system is shown but cannot be
   chosen. See the [driver reference](#driver-reference).
2. **Connection**: the options of the chosen driver. Empty fields use the value shown in grey. For the drivers that
   can search (`usbhid`, `winbattery`, `megatec`, `apcsmart`, `apcupsd`), **Search for devices** lists what the
   driver finds within 30 seconds; **Use** fills in the options, and the name and description if they are empty.
3. **General**: the settings below.

| Setting | Key | Default | Notes |
|---|---|---|---|
| Name | `name` | | The NUT name, as in `MONITOR rack1@server`. 1 to 64 letters, digits, `.`, `_` or `-`, starting with a letter or a digit. Unique without regard to case, since NUT clients may use any case. |
| Description | `description` | | Up to 200 characters; what NUT clients get as the UPS description. |
| Poll interval (s) | `pollIntervalSeconds` | `2` | How often the driver reads the UPS: 0.5 to 300. |
| Enabled | `enabled` | `true` | A disabled UPS keeps its settings, but its driver does not run. |
| Driver | `driver` | | The driver id: `usbhid`, `winbattery`, `megatec`, `apcsmart`, `snmp`, `nut`, `apcupsd` or `simulated`. |
| Connection options | `options` | | The driver options, by key; see the [driver reference](#driver-reference). |

On a machine without a browser at hand, `nuthub devices` prints what the drivers find on the USB and serial ports
(`--timeout SECONDS`, default 20).

A change of the driver, of its options, of the poll interval, of the name or of **Enabled** restarts the driver: the
data is stale for a few seconds, and no **Communication lost** event is raised for a restart you asked for. The
description, the overrides and the low-battery settings apply at once, without a restart. When you rename a UPS in
the panel, NutHub renames it in the host protection and in the NUT accounts too; the NUT clients must use the new
name.

Besides the variables of the device, each UPS publishes `driver.name`, `driver.version`,
`driver.parameter.pollinterval` and one `driver.parameter.<option>` for every option that is set and not secret, as
NUT drivers do.

### Low battery

Under **Advanced > Low battery** NutHub can declare "low battery" itself, for UPSes that report it too late, or not
at all.

| Setting | Key | Default | Notes |
|---|---|---|---|
| Low battery below charge (%) | `lowBattery.chargePercent` | not set | 0 to 100. |
| Low battery below runtime (s) | `lowBattery.runtimeSeconds` | not set | 0 to 86400. |
| Ignore the low battery signal of the UPS | `lowBattery.ignoreDeviceFlag` | `false` | Only the thresholds above set `LB`. |

While the UPS reports that it is on battery (`OB`), NutHub adds `LB` to `ups.status` when `battery.charge` is at or
below the charge threshold, or `battery.runtime` at or below the runtime threshold. A threshold on a value the UPS
does not report never triggers. NUT clients shut down on `OB LB`, and so does the [host protection](#host-protection)
with its default condition.

### Variable overrides

**Advanced > Variable overrides** replaces variables the UPS reports wrongly, or adds ones it does not report, like
the `override.*` options of NUT: for example `ups.mfr` = `APC`, or `battery.date` = `2025/03/01`. An overridden
variable becomes read-only.

An override only changes what is reported. Overriding `battery.charge.low` does not make anybody declare low battery
at that charge: use **Low battery below charge (%)** above for that.

## Driver reference

Each driver has its own options, stored under `options` of the UPS by the keys below. In the panel the options marked
*Advanced* are folded under **Advanced options**, and an option marked "Shown when" appears only when the other
option has one of the values given. Options of type *secret* are stored encrypted.

| Driver | Name in the panel | For | Platforms | Search |
|---|---|---|---|---|
| [`usbhid`](#usbhid) | USB HID Power Device | USB UPSes of the HID Power Device class | Windows, Linux | yes |
| [`winbattery`](#winbattery) | Windows battery | Any UPS Windows shows as a battery | Windows | yes |
| [`megatec`](#megatec) | Megatec / Q1 (Voltronic) | Megatec, Q1 and Voltronic protocol UPSes | Windows, Linux | yes |
| [`apcsmart`](#apcsmart) | APC Smart (serial) | APC Smart-UPS with a serial port | Windows, Linux | yes |
| [`snmp`](#snmp) | SNMP network card | UPS network management cards | Windows, Linux | no |
| [`nut`](#nut) | NUT server (upsd) | A UPS of another NUT server | Windows, Linux | no |
| [`apcupsd`](#apcupsd) | apcupsd | A UPS managed by apcupsd | Windows, Linux | yes |
| [`simulated`](#simulated) | Simulated UPS | A virtual UPS for tests | Windows, Linux | no |

How far each driver has been tried: the `nut` driver has read a real NUT 2.8.5 `upsd` and the NUT server of a UGREEN
NAS (UGOS Pro). The USB HID, serial (`megatec`, `apcsmart`) and SNMP drivers were tested against simulators and
recorded dumps of real devices, not yet against the devices themselves, and the battery calls of `winbattery` have
not been tried on real hardware. If you run one of them with a real UPS, an issue with the result is useful either
way.

### usbhid

**USB HID Power Device.** USB UPSes that follow the HID Power Device class: APC, Eaton/MGE, CyberPower, Tripp Lite,
Belkin, Liebert, PowerCOM, Delta, Salicru and many more, like NUT's `usbhid-ups`.

| Key | Label | Type | Default | Notes |
|---|---|---|---|---|
| `vendorId` | USB vendor id | text | not set | Hexadecimal, e.g. `051d` for APC or `0764` for CyberPower. Leave the device fields empty to use the first UPS found. |
| `productId` | USB product id | text | not set | Hexadecimal, e.g. `0002`. |
| `serial` | Serial number | text | not set | Tells identical UPSes apart. Compared without regard to case. |
| `product` | Product name contains | text | not set | Part of the USB product string, e.g. `Back-UPS`. |
| `devicePath` | Device path | text | not set | The exact HID device path (`/dev/hidraw0`, `\\?\hid#vid_...`), only for identical UPSes without serial numbers. It can change when the UPS is reconnected. *Advanced.* |
| `subdriver` | Subdriver | choice | `auto` | The vendor-specific mapping to use. Automatic picks it like NUT does. Values: `auto`, `mge`, `apc`, `arduino`, `belkin`, `cps`, `delta`, `ecoflow`, `ever`, `idowell`, `legrand`, `liebert`, `openups`, `powercom`, `powervar`, `salicru`, `tripplite`, `generic`. |
| `onlineDischarge` | On line and discharging means on battery | true/false | `false` | For models (e.g. CyberPower UT) that report on line and discharging while they run on battery (NUT `onlinedischarge_onbattery`). *Advanced.* |
| `onlineDischargeCalibration` | On line and discharging means calibrating | true/false | `false` | For models (some APC) that report on line and discharging during a runtime calibration (NUT `onlinedischarge_calibration`). *Advanced.* |
| `usbReset` | Reset the USB device when it stops answering | true/false | `true` | Linux only. After three failed attempts in a row NutHub asks the kernel to re-enumerate the device (`USBDEVFS_RESET` on `/dev/bus/usb/BBB/DDD`, what `usbreset` does), at most once every five minutes. It brings back a UPS that stays plugged in but has stopped answering; in a container the usb devices must be visible as well as the hidraw node. *Advanced.* |
| `offDelay` | Shutdown delay (seconds) | integer | subdriver default | Initial `ups.delay.shutdown`: how long the UPS waits before cutting the power after a shutdown command (NUT `offdelay`). Empty keeps the subdriver default (usually 20). 0 to 7200. *Advanced.* |
| `onDelay` | Restart delay (seconds) | integer | subdriver default | Initial `ups.delay.start`: how long the UPS waits before restoring the power once mains returns (NUT `ondelay`). Empty keeps the subdriver default (usually 30). 0 to 7200. *Advanced.* |

- **Choosing the device.** With every device field empty, the driver takes the first UPS it finds. With several
  UPSes, set the vendor and product ids and the serial number (**Search for devices** fills them in). The device
  path is a last resort for identical UPSes without serial numbers.
- **Subdrivers.** `auto` picks the vendor mapping from the USB ids, as NUT does; `generic` uses only the standard HID
  Power Device usages. The subdriver in use is written in the log when the driver connects and published as
  `driver.version.data`.
- **Linux.** The service account needs access to the `hidraw` node: `install.sh` installs the udev rule
  `99-nuthub-ups.rules` for the known UPS vendors. If access is denied, the driver's error says what to do. In
  Docker, pass the `hidraw` node into the container (examples in `docker-compose.yml`).
- **Windows.** If another program (PowerChute, PowerPanel...) or a driver holds the UPS exclusively, the error reads
  "Windows denied access to the UPS". Close the other program, or use the [`winbattery`](#winbattery) driver, which
  reads the UPS through the Windows battery driver.
- **On line and discharging.** APC Back-UPS units often report `OL DISCHRG` while on line power with a full battery.
  That is what the device says, not an error: leave both "On line and discharging" options off for them. The log
  then shows a warning about it at most every 10 minutes. `onlineDischarge` is for models such as CyberPower UT that
  report `OL DISCHRG` while they really run on battery.

### winbattery

**Windows battery.** Any UPS that Windows shows as a battery (the HidBatt driver), on Windows only. Use it when
`usbhid` cannot open the UPS. It publishes `ups.status`, `battery.charge`, `battery.runtime`, `battery.voltage` and
the identification Windows provides; it has no instant commands and no writable variables.

| Key | Label | Type | Default | Notes |
|---|---|---|---|---|
| `battery` | Battery | text | not set | The unique id or part of the name of the battery to read. Empty takes the first UPS battery. |
| `includeSystemBatteries` | Include laptop batteries | true/false | `false` | Also accept batteries that Windows does not flag as short-term (UPS) batteries. *Advanced.* |

**Search for devices** lists every battery Windows reports, UPS batteries first; a laptop battery is marked "System
battery (not a UPS)". The driver itself only reads UPS batteries unless **Include laptop batteries** is on.

### megatec

**Megatec / Q1 (Voltronic).** UPSes that speak the Megatec Q1 protocol and its Voltronic variants (Tecnoware,
Atlantis, Mecer, PowerWalker, Powercool and many OEM brands), like NUT's `nutdrv_qx` and `blazer`.

| Key | Label | Type | Default | Notes |
|---|---|---|---|---|
| `transport` | Connection | choice | `serial` | Values: `serial`: Serial port (COM / ttyS / ttyUSB); `tcp`: Serial device server over TCP (ser2net, NPort...); `usb`: USB cable with a built-in USB-serial bridge (HID). |
| `port` | Serial port | serial port | not set | Required with the serial transport. Shown when `transport` is `serial`. `COM3` on Windows; on Linux prefer `/dev/serial/by-id/...` over `/dev/ttyUSB0`, whose number can change. |
| `baudRate` | Baud rate | integer | `2400` | Shown when `transport` is `serial`. These UPSes talk at 2400 baud, 8 data bits, no parity, 1 stop bit; change it only if the manual says so. 300 to 115200. *Advanced.* |
| `cablePower` | Cable power (DTR / RTS) | choice | `normal` | Shown when `transport` is `serial`. Some cables take their power from the DTR/RTS lines of the port. Values: `normal`: DTR on, RTS off (default); `reverse`: DTR off, RTS on; `both`: DTR and RTS on; `none`: DTR and RTS off. *Advanced.* |
| `host` | Serial server host | host | not set | Required with the TCP transport. Shown when `transport` is `tcp`. Address of the serial device server. It must pass the bytes through unchanged (ser2net `raw` mode, NPort "TCP server" mode) at 2400 baud 8N1. |
| `tcpPort` | Serial server TCP port | port | not set | Required with the TCP transport. Shown when `transport` is `tcp`. |
| `vendorId` | USB vendor id | text | not set | Shown when `transport` is `usb`. Hexadecimal, e.g. `0665`. Empty: the first known Megatec USB bridge found. |
| `productId` | USB product id | text | not set | Shown when `transport` is `usb`. Hexadecimal, e.g. `5161`. |
| `serial` | USB serial number | text | not set | Shown when `transport` is `usb`. Tells apart several identical USB UPSes. *Advanced.* |
| `usbSubdriver` | USB bridge type | choice | `auto` | Shown when `transport` is `usb`. How the USB cable carries the serial data (NUT `subdriver`); automatic from the USB ids. Values: `auto`, `cypress`, `phoenix`, `ippon`, `sgs`. *Advanced.* |
| `protocol` | Protocol | choice | `auto` | Automatic detection tries every protocol and can take half a minute; the detected one is written in the log. Values: `auto`, `megatec`, `megatec/old`, `mustek`, `zinto`, `q1`, `bestups`, `voltronic-qs`, `voltronic-qs-hex`, `voltronic`. |
| `onDelay` | Restart delay after power returns (s) | integer | `180` | Initial `ups.delay.start`, rounded down to whole minutes. Some early firmware needs at least 3 minutes. 0 to 599940. |
| `offDelay` | Shutdown delay (s) | integer | `30` | Initial `ups.delay.shutdown`: multiples of 6 s below one minute, whole minutes above. 12 to 5940. |
| `batteryVoltageHigh` | Battery voltage when full (V) | number | not set | With the empty voltage, lets NutHub estimate `battery.charge` for UPSes that do not report it. 0 to 1000. *Advanced.* |
| `batteryVoltageLow` | Battery voltage when empty (V) | number | not set | 0 to 1000. *Advanced.* |
| `batteryVoltageNominal` | Nominal battery voltage (V) | number | not set | Replaces a wrong value reported by the UPS; the full/empty voltages are guessed from it when not set. 0 to 1000. *Advanced.* |
| `batteryPacks` | Battery packs | number | not set | The number the reported battery voltage must be multiplied by (e.g. 12 for a 24 V battery reported as 2 V per cell). Detected automatically when not set. 0.5 to 200. *Advanced.* |
| `batteryVoltageReportsOnePack` | Publish battery voltage × packs | true/false | `false` | For UPSes that report the voltage of one pack or cell: publish `battery.voltage` multiplied by the packs. *Advanced.* |
| `runtimeCal` | Runtime calibration | text | not set | Enables the `battery.runtime` estimate: runtime at a high load (s), that load (%), runtime at a low load (s), that load (%). Example: `240,100,720,50`. *Advanced.* |
| `chargeTime` | Battery recharge time (s) | integer | `43200` | Used with the runtime calibration. 1 to 604800. *Advanced.* |
| `idleLoad` | Minimum load for the runtime estimate (%) | number | `10` | Used with the runtime calibration. 0.1 to 100. *Advanced.* |
| `ignoreSab` | Ignore the 'Shutdown Active' bit | true/false | `false` | Some UPSes always report a shutdown in progress, which puts `FSD` in the status and shuts every client down. *Advanced.* |
| `noRating` | Do not query ratings (F) | true/false | `false` | *Advanced.* |
| `noVendor` | Do not query vendor information (I) | true/false | `false` | *Advanced.* |
| `timeoutMs` | Reply timeout (ms) | integer | `1500` (`3000` over USB) | How long to wait for each reply: 1500 by default, 3000 over USB. 200 to 30000. *Advanced.* |

- **Serial port** (`transport` = `serial`). A COM port or `/dev/ttyS*`, including the port a USB-serial adapter
  creates (FTDI, Prolific, CH340...). On Linux, `/dev/serial/by-id/...` keeps its name across reconnections; the
  systemd unit gives the service the `dialout` group (`install.sh` uses `uucp` on distributions that have that group
  instead). Some cables draw their power from DTR/RTS: try the other
  **Cable power** values if the UPS does not answer.
- **Serial device server** (`transport` = `tcp`). A ser2net port in raw mode, or a Moxa NPort in TCP server mode,
  set to 2400 baud 8N1. Host and TCP port are required.
- **USB cable** (`transport` = `usb`). For UPSes whose USB cable contains a USB-serial bridge that shows up as a HID
  device, such as `0665:5161` (Voltronic and many OEM models). The bridge type (NUT's `subdriver`: `cypress`,
  `phoenix`, `ippon`, `sgs`) is chosen from the USB ids. Bridges that need raw USB transfers (for example
  `0001:0000`, `0925:1234`, `ffff:0000`) are recognised and reported as not supported. A CH340 (`1a86:7523`) is a
  plain USB-serial converter: use the serial transport with the port it creates. On Linux, the udev rule
  `99-nuthub-ups.rules` gives the service access to the usual bridges (`0665:5161`, the Phoenixtec ids `06da:*` and
  a few more); for a bridge it does not list, add a line with its ids.
- **Protocol.** Automatic detection tries every protocol and can take half a minute; the protocol found is written
  in the log. Set it explicitly to skip the detection at every start.
- **Battery estimates.** For UPSes that report no charge, the full and empty battery voltages let NutHub estimate
  `battery.charge`; **Runtime calibration** enables a `battery.runtime` estimate.
- **Shutdown Active bit.** Some UPSes always report a shutdown in progress, which puts `FSD` in the status and shuts
  every NUT client down: enable **Ignore the 'Shutdown Active' bit** for them.
- **Search for devices** lists the known USB bridges connected and the serial ports of the machine; the ports are
  not probed, so check that the UPS is really on the one you choose.

`tools/fake-serial-ups` in the source repository simulates a Megatec or APC Smart UPS behind a TCP port, for trying
the serial drivers without hardware.

### apcsmart

**APC Smart (serial).** APC Smart-UPS, Matrix-UPS and Back-UPS Pro with a DB-9 serial port and the APC 940-0024
"smart" cable, like NUT's `apcsmart`. For APC UPSes on USB, use [`usbhid`](#usbhid).

| Key | Label | Type | Default | Notes |
|---|---|---|---|---|
| `transport` | Connection | choice | `serial` | Values: `serial`: Serial port (COM / ttyS / ttyUSB); `tcp`: Serial device server over TCP (ser2net, NPort...). |
| `port` | Serial port | serial port | not set | Required with the serial transport. Shown when `transport` is `serial`. `COM3` on Windows; on Linux prefer `/dev/serial/by-id/...` over `/dev/ttyUSB0`, whose number can change. |
| `baudRate` | Baud rate | integer | `2400` | Shown when `transport` is `serial`. These UPSes talk at 2400 baud, 8 data bits, no parity, 1 stop bit; change it only if the manual says so. 300 to 115200. *Advanced.* |
| `host` | Serial server host | host | not set | Required with the TCP transport. Shown when `transport` is `tcp`. Address of the serial device server. It must pass the bytes through unchanged (ser2net `raw` mode, NPort "TCP server" mode) at 2400 baud 8N1. |
| `tcpPort` | Serial server TCP port | port | not set | Required with the TCP transport. Shown when `transport` is `tcp`. |
| `shutdownType` | Shutdown method | choice | `0` | What `shutdown.return` without a parameter sends (NUT `sdtype`). The default powers the load off and back on when the mains returns, whether the UPS is on battery or not. Values: `0`: Soft hibernate (S) on battery, hard hibernate (@) on line power (default); `1`: Soft hibernate (S), hard hibernate (@) if it fails; `2`: Power off now (Z), stays off; `3`: Power off after the grace delay (K), stays off; `4`: Simulated power failure, then soft hibernate (CS, for Back-UPS CS); `5`: Hard hibernate (@). *Advanced.* |
| `wakeUpDelay` | Extra wake-up delay (× 6 min) | integer | `0` | Hard hibernate only (NUT `awd`): how many 6-minute periods to wait after the mains returns before powering the load on again. 0 to 999. *Advanced.* |
| `csDelay` | Delay of the CS method (s) | number | `3.5` | Shown when `shutdownType` is `4`. Pause between the simulated power failure and the soft hibernate command (NUT `cshdelay`). 0 to 9.9. *Advanced.* |
| `timeoutMs` | Reply timeout (ms) | integer | `3000` | How long to wait for each reply; raise it for slow serial device servers. 500 to 30000. *Advanced.* |

- The transports are the same as for `megatec`: a serial port, or a serial device server over TCP at 2400 baud 8N1.
  Raise **Reply timeout (ms)** for slow device servers.
- **Shutdown method** decides what `shutdown.return` without a parameter sends to the UPS, and so what the UPS does
  when the [host protection](#host-protection) powers it off.
- **Search for devices** lists the serial ports of the machine, without probing them.

### snmp

**SNMP network card.** UPS network management cards: RFC 1628 UPS-MIB, APC PowerNet, Eaton/Powerware, MGE,
CyberPower, Socomec, Delta, Huawei, Phoenixtec and Tripp Lite, over SNMP v1, v2c or v3, like NUT's `snmp-ups`.

| Key | Label | Type | Default | Notes |
|---|---|---|---|---|
| `host` | Address of the network card | host | not set | Required. Host name or IP address of the UPS network management card. |
| `port` | UDP port | port | `161` |  |
| `version` | SNMP version | choice | `2c` | Values: `1`, `2c`, `3`. |
| `community` | Community | secret | `public` | Shown when `version` is `1` or `2c`. The read community configured on the card. Stored encrypted. |
| `writeCommunity` | Write community | secret | not set | Shown when `version` is `1` or `2c`. Used for instant commands and variable writes when the card has a separate read-write community (often `private`). Defaults to the read community. Stored encrypted. *Advanced.* |
| `secName` | User name | text | not set | Shown when `version` is `3`. The SNMPv3 user (securityName) configured on the card. |
| `secLevel` | Security level | choice | `noAuthNoPriv` | Shown when `version` is `3`. Values: `noAuthNoPriv`: No authentication, no encryption; `authNoPriv`: Authentication, no encryption; `authPriv`: Authentication and encryption. |
| `authProtocol` | Authentication protocol | choice | `MD5` | Shown when `secLevel` is `authNoPriv` or `authPriv`. SHA-224 is not available. Values: `MD5`, `SHA`, `SHA256`, `SHA384`, `SHA512`. |
| `authPassword` | Authentication password | secret | not set | Shown when `secLevel` is `authNoPriv` or `authPriv`. At least 8 characters. Stored encrypted. |
| `privProtocol` | Privacy (encryption) protocol | choice | `DES` | Shown when `secLevel` is `authPriv`. Values: `DES`, `AES`, `AES192`, `AES256`. |
| `privPassword` | Privacy password | secret | not set | Shown when `secLevel` is `authPriv`. At least 8 characters. Stored encrypted. |
| `mibs` | MIB | choice | `auto` | Which mapping to use. Automatic recognises the card from its sysObjectID and the objects it answers. Values: `auto`, `apcc`, `cyberpower`, `delta_ups`, `pw`, `huawei`, `mge`, `netvision`, `xppc`, `tripplite`, `ietf`. |
| `timeoutMs` | Timeout (ms) | integer | `1000` | How long to wait for each answer before asking again. 100 to 30000. *Advanced.* |
| `retries` | Retries | integer | `3` | How many times a request is sent again after a timeout. 0 to 10. *Advanced.* |

- **Versions.** With v1 and v2c you give the read community and, if the card has a separate one, the write community
  used for instant commands and variable writes. With v3 you give the user name and the security level; the
  authentication fields appear for `authNoPriv` and `authPriv`, the privacy fields for `authPriv` only. SHA-224 is not
  available.
- **MIB detection.** With `auto`, the driver reads the card's sysObjectID and confirms the MIB it announces; failing
  that, it probes every known MIB, vendor MIBs before RFC 1628. The MIB found is written in the log when the driver
  connects. With a MIB chosen explicitly, the driver only checks that the card answers it, and reports an error
  otherwise.
- There is no device search: enter the address of the card. `tools/fake-snmp-agent` in the source repository
  simulates a card for tests.

### nut

**NUT server (upsd).** A UPS of another NUT server (`upsd`, a NAS, another NutHub), repeated here like `dummy-ups`
with `ups@host`. Its variables are read at every poll; its writable variables and commands are listed again every
minute.

| Key | Label | Type | Default | Notes |
|---|---|---|---|---|
| `host` | Server | host | not set | Required. Host name or IP address of the NUT server. |
| `port` | Port | port | `3493` |  |
| `upsName` | UPS name on that server | text | not set | Required. The name before the `@` in `ups@host`, as listed by `upsc -l` on that server. |
| `username` | Username | text | not set | A user of that server's `upsd.users`. Needed only for instant commands, variable writes and login. |
| `password` | Password | secret | not set | Travels in clear text unless TLS is on, as with every NUT client. Stored encrypted. |
| `useTls` | Use TLS (STARTTLS) | true/false | `false` | Encrypts the connection. The server must have a certificate configured (`CERTFILE` in `upsd.conf`). |
| `tlsVerify` | Certificate check | choice | `none` | Shown when `useTls` is `true`. Values: `none`: None (accept self-signed certificates); `chain`: Verify the certificate chain and the host name. |
| `login` | Log in as a secondary | true/false | `false` | Sends `LOGIN` so that server counts this machine among the systems powered by the UPS and waits for it before shutting down. Needs a user with the upsmon role. *Advanced.* |
| `timeoutMs` | Timeout (ms) | integer | `5000` | How long to wait for the server to connect or to answer a request. 500 to 60000. *Advanced.* |

- **Credentials.** Reading needs no account. Instant commands and variable writes sent to this UPS from NutHub are
  forwarded to the other server with the username and password given here, so that account needs the matching rights
  there (`actions`, `instcmds` in its `upsd.users`). **Log in as a secondary** needs an account with an upsmon role.
  Commands travel on the polling connection, so the other server sees a single client per UPS.
- **TLS.** With **Use TLS (STARTTLS)** the connection is encrypted; **Certificate check** `none` accepts a
  self-signed certificate, `chain` verifies the chain and the host name.
- **UGREEN NAS.** When UGOS Pro manages a USB UPS, keep it that way (the NAS goes on protecting itself) and read the
  UPS with this driver: host = the address of the NAS, port 3493, UPS name usually `ups0` (`upsc -l <nas-address>`
  lists it). Synology DSM and QNAP QTS also run a NUT server on port 3493 when they manage a USB UPS; of the three,
  only the UGREEN one has been verified with this driver.

### apcupsd

**apcupsd.** A UPS managed by apcupsd, read through its network information server (NIS). Read-only: no commands,
no writable variables.

| Key | Label | Type | Default | Notes |
|---|---|---|---|---|
| `host` | apcupsd host | host | `127.0.0.1` | The machine where apcupsd runs, with `NETSERVER on` in `apcupsd.conf`. |
| `port` | Port | port | `3551` |  |
| `timeoutMs` | Timeout (ms) | integer | `5000` | How long to wait for apcupsd to connect and send its status. 500 to 60000. *Advanced.* |

apcupsd must have `NETSERVER on` in `apcupsd.conf`. **Search for devices** checks for apcupsd on this machine
(`127.0.0.1:3551`).

### simulated

**Simulated UPS.** A virtual UPS for trying NutHub and testing NUT clients, notifications and the host protection
without pulling a plug. Its battery charges and discharges with the load, and its instant commands work.

| Key | Label | Type | Default | Notes |
|---|---|---|---|---|
| `model` | Model | text | `Virtual UPS 1500` |  |
| `mfr` | Manufacturer | text | `NutHub` |  |
| `serial` | Serial number | text | `SIM-<name>` | Defaults to `SIM-` followed by the UPS name. |
| `scenario` | Scenario | choice | `steady` | Values: `steady`: Always on line power (use `test.failure.start` to simulate an outage); `outages`: Periodic power outages. |
| `onlineMinutes` | Minutes on line power between outages | integer | `10` | Shown when `scenario` is `outages`. 1 to 1440. |
| `outageMinutes` | Minutes of each outage | integer | `3` | Shown when `scenario` is `outages`. 1 to 600. |
| `load` | Load (%) | integer | `35` | 0 to 150. |
| `runtimeMinutes` | Runtime on a full battery at that load (minutes) | integer | `25` | 1 to 600. |
| `nominalPower` | Rated power (VA) | integer | `1500` | 100 to 100000. |
| `voltage` | Nominal voltage (V) | choice | `230` | Values: `230`, `220`, `120`, `100`. |
| `frequency` | Nominal frequency (Hz) | choice | `50` | Values: `50`, `60`. |
| `devFile` | NUT .dev file | file path | not set | Variables read from this file (`battery.charge: 100` lines, as used by dummy-ups) replace the simulated ones. The file is re-read when it changes. *Advanced.* |

- **Power failures.** With the `steady` scenario the UPS stays on line power until you run the instant command
  `test.failure.start` (UPS page, **Actions**), and returns to line power with `test.failure.stop`. With `outages` it
  alternates by itself.
- **Other commands.** `simulate.commlost` simulates a loss of communication for 30 seconds (or the number of seconds
  given as parameter), to test the communication-lost notifications. The beeper commands, the battery tests, the
  calibration, `load.off`, `load.on`, their `.delay` variants, `shutdown.return`, `shutdown.stayoff` and
  `shutdown.stop` are simulated too, and a few variables are writable (`battery.charge.low`, `battery.runtime.low`,
  `ups.delay.shutdown`, `ups.delay.start`, `ups.id`...).
- **NUT `.dev` files.** With **NUT .dev file** set, every `variable: value` line of the file replaces the simulated
  value, and the file is read again when it changes. `TIMER` lines and `driver.*` variables are ignored. The file
  must be readable by the account NutHub runs as; the systemd unit hides home directories from the service, so keep
  the file under `/var/lib/nuthub`, for example.

## Import from NUT

**Settings > Import from NUT** brings the UPSes and accounts of an existing NUT installation into NutHub. Paste the
content of `ups.conf` and `upsd.users`, or load the files (usually in `/etc/nut/`), then press **Preview**. The
preview shows the UPSes and accounts that can be imported, their options, and a warning for everything that cannot be
carried over. Nothing changes until you press **Apply the import**. UPSes and accounts whose name already exists are
skipped, never changed.

From `ups.conf`:

| NUT driver | NutHub driver | Carried over |
|---|---|---|
| `usbhid-ups` | `usbhid` | `vendorid`, `productid`, `serial`, `product`, `subdriver` (by vendor name), `offdelay`, `ondelay`, `onlinedischarge`, `onlinedischarge_calibration` |
| `nutdrv_qx`, `blazer_ser`, `blazer_usb` | `megatec` | `port` (serial transport), or the USB transport for `blazer_usb` and for `nutdrv_qx` with `port = auto`, a `vendorid` or a `subdriver`; `vendorid`, `productid`, `serial`, `subdriver`, `protocol`, `ondelay`, `offdelay`, `cablepower`, `runtimecal`, `chargetime`, `idleload`, `default.battery.voltage.*` and `override.battery.voltage.*` (`high`, `low`, `nominal`), `battery.packs`, `default.battery.packs`, `ignoresab`, `norating`, `novendor` |
| `apcsmart` | `apcsmart` | `port`, `sdtype`, `awd`, `cshdelay` |
| `snmp-ups` | `snmp` | `port` (host and optional port), `community`, `snmp_version`, `mibs`, `secName`, `secLevel`, `authPassword`, `privPassword`, `authProtocol`, `privProtocol`, `snmp_retries`, `snmp_timeout` (seconds, converted to milliseconds) |
| `dummy-ups` with `port = ups@host[:port]` | `nut` | the UPS name, host and port |
| `apcupsd-ups` | `apcupsd` | `port` (host and optional port) |

For every driver, `desc` becomes the description, `override.*` the variable overrides, `ignorelb` the option
**Ignore the low battery signal of the UPS**, and `pollinterval` the poll interval. Options that only concern NUT's
own driver processes (`sdorder`, `maxstartdelay`, `maxretry`, `retrydelay`, `user`, `group`, `pollfreq`...) are
ignored without a warning; any other option is reported and ignored. `dummy-ups` with a simulation file is not
imported: use the [`simulated`](#simulated) driver with the file as **NUT .dev file**. Other NUT drivers are skipped
with a warning.

From `upsd.users`: `password`, `upsmon primary` (or `master`), `upsmon secondary` (or `slave`), `actions` (`SET` and
`FSD`) and `instcmds`. `allowfrom` is obsolete in NUT and ignored; an account without a password is skipped.
Passwords are stored as hashes.

## NUT server

**Settings > NUT server** configures the NUT protocol server on port 3493, used by `upsmon`, `upsc`, `upscmd`,
`upsrw`, NutDesk, WinNUT and the other NUT clients. Changes apply to the running server, listeners included.

| Setting | Key | Default | Notes |
|---|---|---|---|
| NUT server enabled | `enabled` | `true` | When off, no NUT client can reach NutHub; the UPSes are still monitored. |
| Addresses and ports | `listen` | `*` port 3493 | A list of `address` and `port` pairs. |
| Allowed networks | `allowedNetworks` | empty | Addresses or networks in CIDR notation. Empty allows every client. |
| TLS enabled | `tls.enabled` | `false` | Accept STARTTLS. |
| Certificate file (PFX / PKCS#12) | `tls.certificatePath` | not set | Empty: a self-signed certificate. |
| Certificate password | `tls.certificatePassword` | not set | Secret. |
| Require TLS to log in | `tls.requireTlsForAuthentication` | `false` | Refuse credentials sent before STARTTLS. |
| Maximum connections | `maxConnections` | `256` | 1 to 10000; further connections are refused and logged. *Advanced.* |
| Data maximum age (s) | `maxAgeSeconds` | `15` | 1 to 3600 (upsd `MAXAGE`). *Advanced.* |
| Clear FSD after mains return (s) | `fsdClearDelaySeconds` | `60` | 0 to 86400; 0 keeps the upsd behaviour. *Advanced.* |
| Version string | `versionString` | not set | The answer to `VER`. *Advanced.* |

The page also lists the **Connected NUT clients** (address, account, UPS, primary or secondary, TLS, commands), with
a **Disconnect** button, and under **Connecting NUT clients** the `MONITOR` line to put in `upsmon.conf`.

### Addresses and ports

Each listener is an address and a port. The address can be:

- `*`: every interface, IPv6 and IPv4 (falling back to IPv4 only where IPv6 is not available);
- `0.0.0.0`: every IPv4 interface; `::`: every IPv6 interface;
- an IP address of this machine, to listen on one interface only.

Add several listeners to listen on a few chosen addresses, or on a second port. A port that is in use by another
program is logged and tried again every 30 seconds; the dashboard shows the problem meanwhile. Open the port in the
firewall of the machine (on Windows, `install.ps1 -AddFirewallRules` opens 3493 and 8493). A client that must use
another port adds it to the UPS name: `MONITOR rack1@192.168.1.10:13493 1 monuser <password> secondary`.

UGREEN (UGOS Pro), Synology (DSM) and QNAP (QTS) NAS units run their own NUT server on port 3493 when their UPS
support is on and the UPS is connected to their USB port. A NutHub container on such a NAS that publishes port 3493
does not start, because the port is taken. Publish another host port instead (`"13493:3493"` in
`docker-compose.yml`), or turn off the network UPS server of the NAS.

### Allowed networks

Entries are single addresses (`192.168.1.20`, `::1`) or networks (`192.168.1.0/24`, `fd00::/8`). With an empty list
every client may connect, as with `upsd`; reading data needs no account, so use this list and your firewall to decide
who can read the UPSes. The list is checked on every connection and every command.

### TLS

With **TLS enabled**, clients that support it can switch to an encrypted connection with `STARTTLS`. Without a
certificate file, NutHub generates a self-signed certificate once (`certs/nut-selfsigned.pfx` in the data directory,
valid 10 years, for the machine name, `localhost` and the loopback addresses). Your own certificate can be a PKCS#12
file (`.pfx`, `.p12`) or a PEM file (`.pem`, `.crt`, `.cer`) that contains the certificate and its private key. When
the file is replaced, for example by a renewal, NutHub loads the new one without a restart.

**Require TLS to log in** refuses `USERNAME` and `PASSWORD` (`ERR ACCESS-DENIED`) on a connection that has not
switched to TLS, so passwords never cross the network in clear text. Clients without TLS can still read, but can no
longer log in, send commands or set FSD.

### Data maximum age

When a UPS sends no fresh data for longer than **Data maximum age (s)**, NutHub reports it as stale, like `upsd`
with `MAXAGE`: NUT clients get `ERR DATA-STALE`, the panel shows **Data not current**, and a **Communication lost**
event is raised. A UPS polled less often is given two poll intervals instead: with a poll interval of 10 seconds,
data goes stale after 20 seconds even though the maximum age is 15.

### Forced shutdown (FSD)

FSD is set by an upsmon primary, by an account with the `FSD` action, by an operator in the panel (**Actions >
Set FSD** on the UPS page) or by the [host protection](#host-protection). Every NUT client monitoring the UPS then
shuts its system down. As with `upsd`, FSD is not kept across a restart of NutHub.

**Clear FSD after mains return (s)** clears FSD automatically once the UPS has been back on line power, with fresh
data and without low battery, for that many seconds. With `0` FSD stays set, as `upsd` does, until an operator
clears it (**Actions > Clear FSD**) or NutHub restarts.

### Version string

The answer to the `VER` command. Empty gives
`NutHub <version> (Network UPS Tools protocol 1.3 compatible) - https://github.com/kriseniprak/NutHub`.

## NUT accounts

**Settings > NUT accounts** holds the accounts NUT clients log in with, the equivalent of `upsd.users`. Reading data
needs no account; logging in, setting FSD, writing variables and instant commands do.

| Setting | Key | Default | Notes |
|---|---|---|---|
| Name | `name` | | 1 to 64 letters, digits, `.`, `_`, `-` or `@`. Case-sensitive, as in `upsd`. |
| Password | `passwordHash` | | At least 8 characters in the panel and with `nuthub nut-user`. Stored as a hash. |
| Monitor role | `monitor` | `secondary` | `none` (No monitoring), `secondary` or `primary`. A missing value in the file means `none`. |
| Actions | `actions` | empty | `SET` (Change writable variables) and `FSD` (Set forced shutdown). |
| Instant commands | `instantCommands` | empty | None, `ALL`, or a list of command names. |
| Allowed UPSes | `allowedUps` | empty | The UPSes the account may act on; empty means all of them. |

Under **Instant commands**, **Only the chosen commands** shows the commands of the UPSes connected now; **Other
commands** takes more names, separated by commas, for UPSes that are not connected at the moment. Command and action
names are compared without regard to case.

What each protocol command needs:

| Command | Needs |
|---|---|
| `GET`, `LIST` (reading) | Nothing, from an allowed network |
| `LOGIN` | Monitor role `secondary` or `primary` |
| `PRIMARY` (or `MASTER`) | Monitor role `primary` |
| `FSD` | Monitor role `primary`, or the `FSD` action |
| `SET VAR` | The `SET` action |
| `INSTCMD` | The command in the list, or `ALL` |

**Allowed UPSes** is a NutHub addition: the privileged commands above are refused for any other UPS. After 10 wrong
passwords from one address within 5 minutes, privileged commands from that address are refused for 5 minutes
(IPv6 addresses are grouped by /64).

The same account written in `upsd.users` and in NutHub:

| `upsd.users` | NutHub |
|---|---|
| `[monuser]` | Name `monuser` |
| `password = ...` | Password |
| `upsmon secondary` (`slave`) | Monitor role: Secondary |
| `upsmon primary` (`master`) | Monitor role: Primary |
| `actions = SET FSD` | Actions: Change writable variables, Set forced shutdown |
| `instcmds = ALL` | Instant commands: All commands |
| `instcmds = test.battery.start.quick` | Instant commands: Only the chosen commands |

Use the primary role only for the machine that must shut down last and may turn the UPS off; every other machine is
a secondary. A computer powered by the UPS `rack1` of the NutHub at `192.168.1.10` has this line in `upsmon.conf`:

```
MONITOR rack1@192.168.1.10 1 monuser <password> secondary
```

In NutDesk or WinNUT, give the address of NutHub, port 3493, the UPS name and, if the client should log in, a NUT
account.

Accounts can also be managed from the command line, for scripted installations: `sudo /opt/nuthub/nuthub` on Linux,
`nuthub.exe` from an administrator prompt on Windows, `docker compose exec nuthub /app/nuthub` in Docker. The
default role is `secondary`; without `--password` or `--generate`, the password is asked on the console.

```bash
nuthub nut-user add monuser --monitor secondary --generate
nuthub nut-user add admin1 --monitor primary --actions SET,FSD --instcmds ALL --ups rack1
nuthub nut-user list
nuthub nut-user set-password monuser
nuthub nut-user remove monuser
```

### NAS units as NUT clients

Some NAS units can shut themselves down from the data of another NUT server, but use fixed names:

| NAS | Setting on the NAS | UPS name | NUT account |
|---|---|---|---|
| Synology DSM | UPS type "Synology UPS server", pointing at the address of NutHub | `ups` | `monuser` / `secret`, secondary |
| QNAP QTS | Network UPS slave, pointing at the address of NutHub | `qnapups` | `admin` / `123456`, secondary |

Name the UPS accordingly in NutHub. Synology connects to port 3493 only. Both passwords are shorter than the 8
characters the panel and `nuthub nut-user` require, so create these accounts with [Import from NUT](#import-from-nut):
paste this into the `upsd.users` field, then **Preview** and **Apply the import**.

```
[monuser]
    password = secret
    upsmon secondary

[admin]
    password = 123456
    upsmon secondary
```

This client mode of Synology and QNAP has not been tried against NutHub yet.

## Web panel

**Settings > Web panel** decides how the panel is reached.

| Setting | Key | Default | Notes |
|---|---|---|---|
| Web panel enabled | `enabled` | `true` | When off, the panel and the API stop answering; only the configuration file can turn them on again. |
| HTTP | `httpEnabled` | `true` | While the panel is enabled, HTTP, HTTPS or both must be on. |
| HTTP port | `httpPort` | `8493` | |
| HTTPS | `httpsEnabled` | `false` | |
| HTTPS port | `httpsPort` | `8494` | Must differ from the HTTP port and from the NUT ports. |
| Certificate file (PFX / PKCS#12) | `certificatePath` | not set | Empty: a self-signed certificate. |
| Certificate password | `certificatePassword` | not set | Secret. |
| Redirect HTTP to HTTPS | `redirectHttpToHttps` | `false` | Only when both HTTP and HTTPS are on. |
| Read-only access without signing in | `allowAnonymousRead` | `false` | |
| Allowed networks | `allowedNetworks` | empty | Addresses or networks in CIDR notation; empty allows every address. |
| Listen address | `bindAddress` | `*` | `*` for every address, or one IP address of this machine. *Advanced.* |
| Session length (hours) | `sessionHours` | `12` | 1 to 2160. *Advanced.* |

Changes apply without restarting NutHub. Before saving a change of **Web panel enabled**, the address, HTTP or HTTPS,
a port, the certificate file, the redirect or the allowed networks, the panel asks for confirmation. When the
listener itself changes, the panel restarts it half a second after saving and moves your browser to the new address
after a short countdown (**The panel is moving**, with **Stay here** and **Go now**). A port that is in use is logged
and tried again every 30 seconds.

- **HTTPS.** Without a certificate file, NutHub generates a self-signed certificate (`certs/web-selfsigned.pfx`),
  and browsers show a warning for it. Your own certificate can be a PKCS#12 or PEM file with its private key, as for
  the NUT server. If the certificate cannot be loaded while HTTP is on, the panel stays reachable over HTTP so you can
  fix it. The panel reads the certificate when its listener starts: after renewing the file in place, restart
  NutHub.
- **Read-only access without signing in.** Anyone who can reach the panel sees what a viewer sees: the dashboard,
  the UPS details, the history and the events.
- **Sessions** end after **Session length (hours)** without activity. After 10 failed sign-ins within 5 minutes, from
  one address or for one user name, further attempts are refused for a while.
- **Allowed networks** is checked before anything else, static files included. The panel refuses to save a list that
  would leave out your own address.

If a setting leaves you without access to the panel, fix it in the configuration file on the server: `web.enabled`,
the ports, `web.bindAddress` or `web.allowedNetworks`. The running server applies the edit.

## Web accounts

**Settings > Web accounts** lists the people who can use the panel.

| Role | Can |
|---|---|
| Viewer (`viewer`) | See the dashboard, the UPS details, the history and the events |
| Operator (`operator`) | Also run instant commands, write variables, set and clear FSD, cancel a pending host shutdown |
| Administrator (`admin`) | Everything, including the settings and the accounts |

| Setting | Key | Notes |
|---|---|---|
| User name | `name` | 1 to 64 letters, digits, `.`, `_`, `-` or `@`; unique without regard to case. |
| Display name | `displayName` | Optional. |
| Role | `role` | `viewer`, `operator` or `admin`. |
| Password / New password | `passwordHash` | At least 8 characters; leave empty to keep the current one. Stored as a hash. |
| Account disabled | `disabled` | A disabled account cannot sign in; its sessions end. |
| Must change the password at the next sign-in | `mustChangePassword` | Useful after setting a temporary password. |

- At least one enabled administrator must remain: the panel refuses a change that would leave none, and so does the
  validation of the file.
- You cannot delete, disable or demote your own account.
- A change of password, role or **Account disabled** ends the open sessions of that account (except the session of
  an administrator editing their own account).
- When NutHub starts without any enabled administrator (on its first start, typically), it creates the account
  `admin` (or `admin` followed by three digits, if an account named `admin` already exists) with a random password,
  written to `initial-admin-password.txt` in the data directory and to be changed at the first sign-in.

A forgotten password is reset from the command line, also while NutHub runs:

```bash
sudo /opt/nuthub/nuthub passwd admin                                  # Linux
docker compose exec nuthub /app/nuthub passwd admin                   # Docker
```

```powershell
& "C:\Program Files\NutHub\nuthub.exe" passwd admin                   # Windows, administrator PowerShell
```

The password is asked twice on the console, or read from standard input when that is redirected; `--password P` gives
it on the command line, `--generate` prints a random one, and `--must-change` asks for a new password at the next
sign-in.

## Server identity

**Settings > Server identity** names this server.

| Setting | Key | Default | Notes |
|---|---|---|---|
| Name | `name` | `NutHub` | 1 to 100 characters. Shown in the panel, the page title and the notifications (`{server}`). |
| Location | `location` | not set | For example the room or the rack; in the notifications as `{location}`. |

## Notifications

**Settings > Notifications** decides who is told what, and how: by e-mail, by webhook (an HTTP request) or by running
a command. Events are always recorded in the event log; the notifications only send them out.

### Events

**Events to notify** is the general list. Each channel uses it, or a list of its own (**Events sent by this
channel**). **Restore defaults** brings back the default list, marked below. The key is the name used in the file
(`notifications.events`, and `events` of each channel), in the `{type}` placeholder and in `NUTHUB_EVENT`;
`NOTIFYTYPE` is the name a command receives, the same as upsmon's where upsmon has one.

| Group | Key | Label in the panel | Severity | `NOTIFYTYPE` | Default |
|---|---|---|---|---|---|
| Power | `onBattery` | On battery | warning | `ONBATT` | yes |
| Power | `online` | Back on line power | notice | `ONLINE` | yes |
| Power | `lowBattery` | Low battery | critical | `LOWBATT` | yes |
| Power | `lowBatteryCleared` | Battery no longer low | notice | `LOW_BATTERY_CLEARED` |  |
| Power | `forcedShutdown` | Forced shutdown set | critical | `FSD` | yes |
| Power | `forcedShutdownCleared` | Forced shutdown cleared | notice | `FORCED_SHUTDOWN_CLEARED` |  |
| Device | `replaceBattery` | Battery needs replacement | warning | `REPLBATT` | yes |
| Device | `replaceBatteryCleared` | Battery replacement cleared | info | `REPLACE_BATTERY_CLEARED` |  |
| Device | `overload` | Overload | warning | `OVER` | yes |
| Device | `overloadCleared` | Overload ended | info | `NOTOVER` |  |
| Device | `bypass` | On bypass | warning | `BYPASS` |  |
| Device | `bypassCleared` | Bypass ended | info | `NOTBYPASS` |  |
| Device | `off` | Output off | warning | `OFF` |  |
| Device | `offCleared` | Output on again | info | `NOTOFF` |  |
| Device | `calibration` | Calibration started | info | `CAL` |  |
| Device | `calibrationEnded` | Calibration ended | info | `NOTCAL` |  |
| Device | `trim` | Trimming high voltage | info | `TRIM` |  |
| Device | `trimEnded` | Trimming ended | info | `NOTTRIM` |  |
| Device | `boost` | Boosting low voltage | info | `BOOST` |  |
| Device | `boostEnded` | Boosting ended | info | `NOTBOOST` |  |
| Device | `alarm` | Alarm | warning | `ALARM` |  |
| Device | `alarmCleared` | Alarm cleared | info | `NOTALARM` |  |
| Device | `testStarted` | Self test started | info | `TEST_STARTED` |  |
| Device | `testEnded` | Self test ended | info | `TEST_ENDED` |  |
| Communication | `communicationLost` | Communication lost | warning | `COMMBAD` | yes |
| Communication | `communicationRestored` | Communication restored | notice | `COMMOK` | yes |
| Communication | `noCommunication` | No communication | warning | `NOCOMM` | yes |
| Communication | `driverStarted` | Driver started | info | `DRIVER_STARTED` |  |
| Communication | `driverStopped` | Driver stopped | info | `DRIVER_STOPPED` |  |
| Communication | `driverFailed` | Driver failed | warning | `DRIVER_FAILED` |  |
| Commands | `commandExecuted` | Command executed | notice | `COMMAND_EXECUTED` |  |
| Commands | `commandFailed` | Command failed | warning | `COMMAND_FAILED` |  |
| Commands | `variableChanged` | Variable changed | notice | `VARIABLE_CHANGED` |  |
| Shutdown | `shutdownPending` | Host shutdown pending | critical | `SHUTDOWN_PENDING` | yes |
| Shutdown | `shutdownCancelled` | Host shutdown cancelled | notice | `SHUTDOWN_CANCELLED` | yes |
| Shutdown | `shutdownStarted` | Host shutdown started | critical | `SHUTDOWN` | yes |
| Audit | `configurationChanged` | Configuration changed | notice | `CONFIGURATION_CHANGED` |  |
| Audit | `userLogin` | Web sign-in | info | `USER_LOGIN` |  |
| Audit | `userLoginFailed` | Failed web sign-in | warning | `USER_LOGIN_FAILED` |  |
| Audit | `nutClientLogin` | NUT client logged in | info | `NUT_CLIENT_LOGIN` |  |
| Audit | `nutClientLogout` | NUT client logged out | info | `NUT_CLIENT_LOGOUT` |  |
| System | `serverStarted` | Server started | notice | `SERVER_STARTED` |  |
| System | `serverStopping` | Server stopping | notice | `SERVER_STOPPING` |  |

### E-mail

| Setting | Key | Default | Notes |
|---|---|---|---|
| Send e-mail | `email.enabled` | `false` | |
| SMTP server | `email.host` | | Required when e-mail is on. |
| Port | `email.port` | `587` | |
| Security | `email.security` | `auto` | See below. |
| User name | `email.username` | not set | Empty: no authentication. |
| Password | `email.password` | not set | Secret. |
| Sender address | `email.from` | | Required when e-mail is on. |
| Subject prefix | `email.subjectPrefix` | `[NutHub]` | |
| Recipients | `email.to` | empty | At least one. |
| Events sent by this channel | `email.events` | the general list | |

| Security | Key value | Behaviour |
|---|---|---|
| Automatic | `auto` | TLS from the first byte on port 465; elsewhere STARTTLS when the server offers it |
| None (clear text) | `none` | No encryption |
| STARTTLS | `startTls` | STARTTLS, required |
| SSL/TLS on connect | `sslOnConnect` | TLS from the first byte |

Events for the same recipients that come within 10 seconds of the first one share one message, in plain text and
HTML; the subject starts with the prefix, names the UPS (or the server) and the most important event, and ends with
`(+N more)` when there are several. **Host shutdown started** is sent at once, without waiting.

Messages the mail server does not accept yet (during an outage the switch or the mail server is often down too) wait
in `notifications/outbox.json` in the data directory, also across restarts, and are tried again after 30 seconds, 1,
2 and 5 minutes, then every 10 minutes, for up to 24 hours. A definitive refusal of the server (an unknown recipient,
for example) is not retried. The outbox holds at most 500 messages; when it is full, the oldest message without a
critical event goes first.

### Webhooks

A webhook is an HTTP request sent for each event, to a chat or push service or to your own system. Add one with
**Add webhook**; **Apply** in the dialog, then **Save** on the page.

| Setting | Key | Default | Notes |
|---|---|---|---|
| Name | `name` | | Shown in the delivery list instead of the URL. |
| Enabled | `enabled` | `true` | |
| URL | `url` | | An absolute `http://` or `https://` URL. Placeholders are replaced and percent-encoded. |
| Method | `method` | `POST` | `POST`, `PUT` or `GET`. `GET` sends no body. |
| Content type | `contentType` | `application/json` | |
| Body template | `bodyTemplate` | not set | Empty: the standard JSON document below. |
| Headers | `headers` | empty | Name and value pairs; the values are secrets and are never shown again. |
| Events sent by this channel | `events` | the general list | |

Placeholders in the URL, the body template and the header values are replaced by the values of the event. Only these
names are replaced, so the braces of a JSON template stay as they are:

| Placeholder | Value |
|---|---|
| `{ups}` | The UPS name (empty for server events) |
| `{type}` | The event key: `onBattery`, `lowBattery`... (`test` for a test message) |
| `{notifytype}` | The upsmon name: `ONBATT`, `LOWBATT`, `FSD`... (`TEST` for a test message) |
| `{severity}` | `info`, `notice`, `warning` or `critical` |
| `{category}` | `power`, `device`, `communication`, `command`, `shutdown`, `audit` or `system` |
| `{message}` | The English sentence of the event |
| `{timestamp}` | UTC, ISO 8601: `2026-09-22T10:15:00Z` |
| `{server}` | The server name |
| `{location}` | The server location |
| `{status}` | `ups.status` of the UPS (`OL`, `OB DISCHRG`...) |
| `{charge}` | `battery.charge` |
| `{runtime}` | `battery.runtime`, in seconds |
| `{load}` | `ups.load` |
| `{actor}` | Who caused the event: `web:admin@192.168.1.10`, `system`... |

In a body whose content type contains `json`, the values are escaped for a JSON string, so write the quotes around
the placeholder yourself (`"text": "{message}"`). In URLs and `application/x-www-form-urlencoded` bodies they are
percent-encoded; elsewhere they are inserted as they are.

Without a body template, `POST` and `PUT` send this document:

```json
{
  "server": "NutHub",
  "ups": "rack1",
  "type": "onBattery",
  "notifyType": "ONBATT",
  "severity": "warning",
  "category": "power",
  "message": "rack1 is on battery (battery 98%, runtime 27 min).",
  "timestamp": "2026-09-22T10:15:00Z",
  "actor": "system",
  "data": { "ups.status": "OB DISCHRG", "battery.charge": "98", "battery.runtime": "1620", "ups.load": "23" },
  "status": { "ups.status": "OB DISCHRG", "battery.charge": "98", "battery.runtime": "1620", "ups.load": "23" }
}
```

(`data` holds the details recorded with the event: for a UPS event, the readings at that moment; `status` holds the
readings of the UPS when the notification is sent, `input.voltage` included when the UPS reports it. `location` is
added when the server has one, `status` is `null` for server events, and a test message has `"test": true`.)

The **Presets** in the dialog fill in the URL, the content type and a body template for ntfy, Telegram, Slack,
Microsoft Teams, Discord and Gotify. Replace the parts in angle brackets (`<bot-token>`, `<chat-id>`...) with yours;
for ntfy change the topic `nuthub-alerts` in the body, and for Gotify put the application token in the `X-Gotify-Key`
header. Tokens are safer in a header than in the URL, when the service allows it, because header values are stored
encrypted.

Each attempt has 10 seconds to get an answer. Network errors and the answers 408, 429 and 5xx are retried, up to three
attempts in all (after 2 and 10 seconds, or after the delay the server asks for, up to 30 seconds). HTTPS
certificates are always verified.

### Commands

A command runs a program on the NutHub machine for each event, like upsmon's `NOTIFYCMD`, with the account NutHub
runs as.

| Setting | Key | Default | Notes |
|---|---|---|---|
| Name | `name` | | |
| Enabled | `enabled` | `true` | |
| Program | `command` | | The full path of the program or script. |
| Arguments | `arguments` | not set | Split like a command line (double quotes group); placeholders are replaced inside each argument. |
| Time limit (s) | `timeoutSeconds` | `30` | 1 to 3600. After it, the program and its children are stopped. |
| Events sent by this channel | `events` | the general list | |

The program is started directly, not through a shell, so a value can never add arguments or commands; on Windows,
`.bat` and `.cmd` files run through `cmd.exe` with their arguments escaped. The event is also passed in environment
variables:

| Variable | Value |
|---|---|
| `NUTHUB_EVENT` | The event key, as `{type}` |
| `NUTHUB_SEVERITY`, `NUTHUB_CATEGORY` | As `{severity}` and `{category}` |
| `NUTHUB_UPS`, `NUTHUB_MESSAGE`, `NUTHUB_TIMESTAMP`, `NUTHUB_SERVER` | As `{ups}`, `{message}`, `{timestamp}`, `{server}` |
| `NUTHUB_STATUS`, `NUTHUB_CHARGE`, `NUTHUB_RUNTIME`, `NUTHUB_LOAD` | As `{status}`, `{charge}`, `{runtime}`, `{load}` |
| `NUTHUB_ACTOR` | As `{actor}` |
| `NUTHUB_TEST` | `1` for a test message, otherwise `0` |
| `NOTIFYTYPE`, `UPSNAME` | As upsmon sets them, so a `NOTIFYCMD` script written for upsmon works unchanged |

Examples:

| Program | Arguments |
|---|---|
| `/usr/local/bin/ups-notify.sh` | `"{ups}" "{message}"` |
| `C:\Scripts\ups-notify.cmd` | `{type} "{message}"` |
| `powershell.exe` | `-NoProfile -ExecutionPolicy Bypass -File "C:\Scripts\ups-notify.ps1" -Event {type}` |

### Testing and deliveries

Each channel has a **Send test** button that sends a test message with the saved settings (save your changes first).
A webhook test makes a single attempt, and an e-mail test goes to the server directly, so the result you see is the
real one. **Recent deliveries** lists the last 200 notifications sent since NutHub started, with their result and the
error when one failed (for a command, its exit code and the first lines it wrote to standard error).

### Flood protection

Each channel sends at most 20 notifications in 10 minutes, so mains power that comes and goes does not bury the
recipients. The notifications held back are counted and sent later as one summary ("Flood protection held back 37
notification(s) in the last 10 minutes..."). **Low battery**, **Forced shutdown set** and the three **Host
shutdown** events are never held back.

## Host protection

**Settings > Host protection** shuts down the machine NutHub runs on when its UPSes can no longer power it: the job
`upsmon` does in primary mode. A wrong setting can turn off a working server, so start with **Dry run**.

| Setting | Key | Default | Notes |
|---|---|---|---|
| Protect this machine | `enabled` | `false` | When off, NutHub never shuts down this machine. |
| Dry run (no real shutdown) | `dryRun` | `false` | Runs the sequence and records events; neither the machine nor the UPS is turned off. |
| UPSes powering this machine | `ups` | empty | Names of UPSes. At least one when enabled. |
| Minimum healthy supplies | `minimumSupplies` | `1` | From 1 to the number of UPSes chosen (upsmon `MINSUPPLIES`). |
| On battery with low battery | `onLowBattery` | `true` | The usual NUT condition, `OB` and `LB`. |
| On battery with charge below (%) | `batteryChargeBelow` | not set | 0 to 100; empty ignores it. |
| On battery with runtime below (s) | `runtimeBelowSeconds` | not set | Empty ignores it. |
| On battery for longer than (s) | `onBatteryLongerThanSeconds` | not set | Empty ignores it. |
| Forced shutdown (FSD) set on the UPS | `onForcedShutdown` | `true` | Also react to an FSD set by an operator or by another NUT server. |
| Communication lost while on battery (s) | `communicationLostOnBatterySeconds` | `15` | 0 to 3600 (upsmon `DEADTIME`). |
| Grace delay (s) | `shutdownDelaySeconds` | `0` | 0 to 3600. *Advanced.* |
| Tell NUT clients to shut down first | `notifySecondaries` | `true` | *Advanced.* |
| Maximum wait for NUT clients (s) | `secondariesTimeoutSeconds` | `15` | 0 to 3600 (upsmon `HOSTSYNC`). *Advanced.* |
| Shutdown command | `shutdownCommand` | not set | Empty: the operating system default. *Advanced.* |
| Tell the UPS to turn off after the shutdown | `powerOffUps` | `false` | *Advanced.* |
| Power-off command | `powerOffCommand` | `shutdown.return` | *Advanced.* |
| Power-off delay (s) | `powerOffDelaySeconds` | `120` | 30 to 3600 when the power-off is on. *Advanced.* |

When you turn the protection on with the dry run off, the panel asks for a confirmation. The top of the page shows
the current state: **Disabled**, **Monitoring**, **Shutdown pending**, **Waiting for NUT clients**, **Shutting down**
or **Dry run completed**, with the reason and the UPSes in a critical state.

### When a UPS is critical

NutHub checks every second. A UPS is critical when any of the enabled conditions is true:

- it is on battery with low battery (`OB LB`);
- it is on battery and its charge, or its runtime, is at or below the threshold you set;
- it has been on battery for longer than the time you set;
- FSD is set on it (this counts even when its data is stale);
- it was on battery and has not sent fresh data for longer than **Communication lost while on battery (s)**; within
  that time its last known state stands.

As with upsmon, a UPS that is calibrating its battery is not critical for its charge, runtime, low battery or time on
battery, and a UPS that stops answering while on line power, or that was never seen, counts as healthy. The shutdown
starts when fewer UPSes than **Minimum healthy supplies** are healthy: with one UPS, when it is critical; for a
machine with two power supplies on two UPSes and a minimum of 1, when both are.

### The shutdown sequence

1. **Shutdown pending.** The event **Host shutdown pending** is raised and every page of the panel shows a banner
   with the time left and, for operators and administrators, a **Cancel shutdown** button. During the **Grace
   delay** the shutdown is cancelled if the UPSes recover (**Host shutdown cancelled**). If you cancel it, it is not
   scheduled again until the UPSes are back to normal. With a grace delay of 0 the sequence goes on at once.
2. **Waiting for NUT clients.** With **Tell NUT clients to shut down first**, NutHub sets FSD on the critical UPSes,
   so that the NUT clients monitoring them shut down, and waits until none is logged in to those UPSes any more, or
   until **Maximum wait for NUT clients (s)** has passed.
3. **Shutting down.** The event **Host shutdown started** is raised (its e-mail leaves at once), the UPS power-off
   command is sent if you enabled it (see below), and the shutdown command runs.

**Cancel shutdown** works during step 1 only. Turning the protection off during steps 1 and 2 also stops the
sequence; an FSD already set stays set until it is cleared.

### Dry run and safety switches

With **Dry run** the whole sequence runs and every event is recorded, but the shutdown command and the UPS power-off
are only written to the log. The state ends in **Dry run completed**, and the protection arms itself again once the
UPSes are back to normal.

A dry run does not set FSD either: NUT clients react to FSD for real and their systems would shut down. Step 2 is
only written to the log ("FSD would now be set on ..."), and the sequence goes straight on without waiting for the
clients.

Two switches prevent the real shutdown and the UPS power-off whatever the settings say; the page then shows the
protection as a dry run:

- the environment variable `NUTHUB_DISABLE_SHUTDOWN` set to `1` or `true`, meant for test machines;
- a debug build of NutHub, such as `dotnet run` from the source.

On Linux, set the variable with `sudo systemctl edit nuthub.service`:

```ini
[Service]
Environment=NUTHUB_DISABLE_SHUTDOWN=1
```

On Windows, give it to the service and restart it (administrator PowerShell):

```powershell
New-ItemProperty -Path HKLM:\SYSTEM\CurrentControlSet\Services\NutHub -Name Environment -PropertyType MultiString -Value 'NUTHUB_DISABLE_SHUTDOWN=1' -Force
Restart-Service NutHub
```

### Shutdown command

| System | Default command | Tried next if it fails |
|---|---|---|
| Windows | `shutdown.exe /s /f /t 0 /d 6:12 /c "NutHub: UPS power critical"` | `shutdown.exe /s /f /t 0` |
| Linux | `systemctl poweroff` | `shutdown -h now` |

The panel shows the default of the running system under the field. Your own command is a program with its
arguments, split like a command line and started without a shell; if it fails, the defaults are tried after it,
because the machine must go down. On Linux the service account `nuthub` may power the machine off thanks to the
polkit rule `50-nuthub-poweroff.rules` (or `50-nuthub-poweroff.pkla` for older polkit versions) that `install.sh`
installs; without it, `systemctl poweroff` is refused.

### UPS power-off

With **Tell the UPS to turn off after the shutdown**, NutHub sends the power-off command to the critical UPSes that
are on battery, right before the shutdown command. A UPS on line power is left alone, because it would not come back
by itself. When the UPS has a writable `ups.delay.shutdown`, NutHub first sets it to **Power-off delay (s)**.

| Command | Effect |
|---|---|
| `shutdown.return` | Turn off the load and return when power is back (default) |
| `shutdown.stayoff` | Turn off the load and remain off |
| `load.off.delay` | Turn off the load with a delay; NutHub passes the power-off delay as parameter |
| `load.off` | Turn off the load immediately, without waiting for the machine |

Every device on the UPS loses power when the delay expires, so the delay must be longer than this machine needs to
shut down: the settings refuse less than 30 seconds. `shutdown.return` lets everything start again by itself when
mains power returns. Avoid `load.off` here: it cuts the power before the machine has shut down.

### Containers and NAS units

A container cannot shut down its host: the shutdown command would run inside the container. With NutHub in Docker,
leave the host protection off and protect the machines another way:

- install NutHub on the machine to protect instead of in a container;
- run `upsmon` as a secondary on each machine to protect, pointed at NutHub (see [NUT accounts](#nut-accounts));
- let the NAS shut itself down as a NUT client of NutHub: Synology DSM and QNAP QTS, see
  [NAS units as NUT clients](#nas-units-as-nut-clients);
- on a UGREEN NAS whose UGOS Pro manages the USB UPS, keep it that way: UGOS protects the NAS, and NutHub reads the
  UPS with the [`nut`](#nut) driver.

## History

**Settings > History** decides what is recorded for the charts of the UPS pages, and for how long.

| Setting | Key | Default | Notes |
|---|---|---|---|
| Record history | `enabled` | `true` | Events are recorded in any case. |
| Sample every (s) | `sampleIntervalSeconds` | `30` | 5 to 3600. |
| Keep history (days) | `retentionDays` | `90` | 1 to 3650. |
| Keep events (days) | `eventRetentionDays` | `365` | 1 to 3650. |
| Recorded variables | `variables` | see below | Numeric NUT variables, for every UPS that reports them. |

The default variables are `battery.charge`, `battery.runtime`, `battery.voltage`, `ups.load`, `ups.realpower`,
`ups.power`, `ups.temperature`, `input.voltage`, `input.frequency` and `output.voltage`.

Every sample interval NutHub records the current value of each variable of each UPS, when the data is fresh. Raw
samples are kept for 7 days (or less, if the retention is shorter); beyond that the history keeps 5-minute
roll-ups, with the average, minimum and maximum, up to **Keep history (days)**. Expired samples and events are
deleted every hour. Everything is in `nuthub.db` in the data directory. The charts cover 1 hour to 90 days.

## System, logs and the event log

### System

**Settings > System** shows the version, the operating system, the runtime, whether NutHub runs as a service, the
uptime, the paths of the data directory, the configuration file and the database, and which drivers work on this
system. **Download configuration** is described under [Backups](#backups).

### Logs

**Settings > Logs** shows the last messages of the server log, newest first: the last 2000 since NutHub started.
Choose the **Minimum level** (Information by default), the **Number of lines**, search the text, or let it
**Refresh automatically**.

The log is also written to:

- files in `logs/` in the data directory: `nuthub-YYYYMMDD.log`, continued in `nuthub-YYYYMMDD.1.log`... when a file
  reaches 10 MB, kept 14 days;
- the console, or the systemd journal for the service (`journalctl -u nuthub`);
- on Windows, for the service, the Application event log (source `NutHub`), warnings and errors only.

The detail follows the standard .NET logging settings. For more detail while you look into a problem, set the
environment variable `Logging__LogLevel__Default=Debug` (with `systemctl edit nuthub.service`, or
in the `environment` section of `docker-compose.yml`) or put an `appsettings.json` next to the executable, then
restart NutHub:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Debug"
    }
  }
}
```

### Event log

**Events**, in the top bar, is the event log: everything that happened to the UPSes and to the server, grouped by
day. **Filters** narrows it by UPS, severity and category, and the search box by text.

The default view shows the events of severity **Notice and above**. The informational ones stay hidden: web
sign-ins, NUT clients logging in and out, drivers starting and stopping, self tests, calibrations, voltage trimming
and boosting, and device conditions that ended. Choose **All, including sign-ins and details** in the severity
filter to see them. The [events table](#events) gives the severity of each event.

Every change of the configuration is recorded as **Configuration changed**, with who made it (**Done by**):
`web:<account>@<address>` for the panel, `file` for an edit by hand, `cli:<user>` for the command line. Events are
kept for **Keep events (days)** of the [history settings](#history).
