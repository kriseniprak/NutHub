# Troubleshooting and FAQ

This page is organised by symptom: what you see, what to check, and how to fix it. Most answers start in one of
three places. First, the driver's own message, shown on the dashboard card and on the UPS page whenever a driver
cannot reach its device. Second, the **Events** page. Third, the log (see [Logs](#logs)). Installation is covered in
[installation.md](installation.md), every setting and driver option in [configuration.md](configuration.md), the
client side in [clients.md](clients.md), and the internals in [ARCHITECTURE.md](ARCHITECTURE.md).

## Contents

- [Paths and commands used on this page](#paths-and-commands-used-on-this-page)
- [The web panel does not open](#the-web-panel-does-not-open)
- [Forgotten password](#forgotten-password)
- [The UPS is not found](#the-ups-is-not-found)
- [Import from NUT left something out](#import-from-nut-left-something-out)
- [Data not current, communication lost](#data-not-current-communication-lost)
- [NUT clients get an error](#nut-clients-get-an-error)
- [upsmon does not shut down, or shuts down too early](#upsmon-does-not-shut-down-or-shuts-down-too-early)
- [The forced shutdown (FSD) stays set](#the-forced-shutdown-fsd-stays-set)
- [The status shows OL DISCHRG](#the-status-shows-ol-dischrg)
- [Notifications do not arrive](#notifications-do-not-arrive)
- [Host protection did not shut down the machine](#host-protection-did-not-shut-down-the-machine)
- [Logs](#logs)
- [Resetting to defaults](#resetting-to-defaults)
- [Reporting a bug](#reporting-a-bug)

## Paths and commands used on this page

| | Windows | Linux | Docker |
|---|---|---|---|
| Executable | `C:\Program Files\NutHub\nuthub.exe` | `/opt/nuthub/nuthub` | `/app/nuthub` in the container `nuthub` |
| Configuration | `%ProgramData%\NutHub\nuthub.json` | `/etc/nuthub/nuthub.json` | `/etc/nuthub/nuthub.json` (volume `nuthub-config`) |
| Data (database, keys, certificates) | `%ProgramData%\NutHub` | `/var/lib/nuthub` | `/var/lib/nuthub` (volume `nuthub-data`) |
| Log files | `%ProgramData%\NutHub\logs` | `/var/lib/nuthub/logs` | `/var/lib/nuthub/logs` |
| Service | Windows service `NutHub` | systemd unit `nuthub.service` | container `nuthub` |

**Settings > System** shows the paths the running server really uses. Docker Compose adds the project name in front
of the volume names; `docker volume ls` shows the full names.

The executable is not on the `PATH`. Run its commands like this:

```powershell
# Windows, in an administrator PowerShell (the data directory is restricted to administrators)
& "C:\Program Files\NutHub\nuthub.exe" check-config
```

```bash
# Linux
sudo /opt/nuthub/nuthub check-config

# Docker
docker exec nuthub /app/nuthub check-config
```

On Linux the `sudo` matters. If you run the command as an ordinary user, NutHub looks for its files under your home
directory (`~/.config/nuthub`, `~/.local/share/nuthub`), not in `/etc/nuthub` and `/var/lib/nuthub`.

## The web panel does not open

Go through these checks in order.

### Is NutHub running?

```powershell
# Windows
Get-Service NutHub
& "C:\Program Files\NutHub\nuthub.exe" healthcheck
```

```bash
# Linux
systemctl status nuthub
sudo /opt/nuthub/nuthub healthcheck
sudo journalctl -u nuthub -n 50
```

`healthcheck` asks the server running on this machine whether it answers, and prints `healthy` or
`unhealthy: <reason>`. If it reports `the web panel answered 403`, the panel's allowed networks exclude this
machine too (see [Allowed networks](#allowed-networks)).

If NutHub is not running, the reason is at the end of the journal (Linux) or in the Windows Application event log
under the source `NutHub` (see [Logs](#logs)). NutHub refuses to start if the configuration file is not valid. The
message names the field, or the line if the JSON itself is broken. Fix the file, run `nuthub check-config` until it
reports no errors, then start the service again.

If the panel opens on the server itself (`http://localhost:8493/`) but not from other machines, check the firewall
and the allowed networks.

### The port is in use

The panel listens on TCP 8493 (HTTP), and on 8494 (HTTPS) once HTTPS is enabled. If another program already holds
the port, NutHub keeps running (the NUT server too), tries again every 30 seconds, and logs:

> The web panel cannot listen on ...: the port is already in use by another program (or reserved). Retrying in 30 s.

Find out what holds the port:

```powershell
# Windows: the last column is the process id
netstat -ano | findstr :8493
# Port ranges Windows reserves (Hyper-V, WSL, Docker Desktop); a port inside one cannot be used
netsh interface ipv4 show excludedportrange protocol=tcp
```

```bash
# Linux
sudo ss -ltnp | grep 8493
```

Either stop that program or move NutHub to another port with **Settings > Web panel > HTTP port**. If you cannot
reach the panel to do that, set `"httpPort"` in the file instead (see
[Fixing the panel settings in the file](#fixing-the-panel-settings-in-the-file)).

### Firewall

- **Windows**: `install.ps1` opens TCP 3493 and 8493 only when you run it with `-AddFirewallRules`. You can run it
  again with that switch. HTTPS on 8494 needs its own rule:

  ```powershell
  New-NetFirewallRule -DisplayName "NutHub web panel HTTPS (TCP 8494)" -Direction Inbound -Protocol TCP -LocalPort 8494 -Action Allow
  ```

- **Linux**: `install.sh` does not change the firewall. Open the ports yourself:

  ```bash
  sudo ufw allow 3493/tcp && sudo ufw allow 8493/tcp
  # or
  sudo firewall-cmd --permanent --add-port=3493/tcp --add-port=8493/tcp && sudo firewall-cmd --reload
  ```

- **Docker**: `docker-compose.yml` publishes 3493 and 8493. The line for 8494 is commented out; uncomment it before
  you enable HTTPS.

### Docker: the container did not start

```bash
docker ps -a --filter name=nuthub
docker logs nuthub
```

| What you see | Meaning |
|---|---|
| Status `Up ... (healthy)` | The container runs and the panel answers. Check the firewall and the address you open. |
| Status `Up ... (unhealthy)` | NutHub runs, but the panel does not answer its health check. `docker logs nuthub` says why, and `docker inspect --format '{{json .State.Health}}' nuthub` shows the answers of the last checks. |
| Status `Exited` | NutHub stopped. The last lines of `docker logs nuthub` give the reason, for example an invalid configuration file. |
| Status `Created`, and `docker logs` is empty | The container never started, so NutHub never ran. Docker could not publish a port. |

If a port could not be published, the reason is in the output of `docker compose up -d`, or in the message of your
NAS's container app. It mentions that the port `is already allocated` or `address already in use`.

On a NAS this is usually port 3493. UGREEN UGOS Pro, Synology DSM and QNAP QTS run their own NUT server on 3493 when
their UPS support is on and the UPS is connected to their USB port. There are two fixes:

- Publish NutHub's NUT port on another host port. In `docker-compose.yml`, change `"3493:3493"` to `"13493:3493"`
  and run `docker compose up -d` again. NUT clients then connect to port 13493 of the NAS (`upsc -l nas:13493`).
- Or turn off the NAS's network UPS server, if you do not need it.

Keep in mind that a Synology acting as a NUT client of NutHub expects port 3493 (see
[NUT clients get an error](#nut-clients-get-an-error)).

### Allowed networks

When **Settings > Web panel > Allowed networks** is not empty, requests from any other address get an HTTP 403
answer, `Your address is not allowed to use this panel.`, and the log shows (at most once a minute):

> Refused a web request from 192.168.5.20: not in the allowed networks.

The address in that line is the one NutHub sees. Behind a reverse proxy, that is the proxy's address, because
NutHub does not read `X-Forwarded-For`. With some Docker network setups it can be an address of the Docker network
rather than the browser's. Add the address or network that the log shows. If you are locked out, empty the list in
the file.

`nuthub healthcheck`, and with it the health check of the Docker container, connects to the panel from `127.0.0.1`
(inside the container, in Docker). If the list does not include `127.0.0.1`, the check gets the 403 answer and the
container is reported `unhealthy`, although the panel works for the listed networks.

### HTTPS certificate warning

If you enable HTTPS without choosing a certificate, NutHub generates a self-signed one (`certs/web-selfsigned.pfx`
in the data directory, valid for 10 years). Browsers warn about it because nobody vouches for it. That is expected.
Either accept the warning for this address, or set your own certificate in **Settings > Web panel > Certificate
file (PFX / PKCS#12)**, with its password. A PEM file (`.pem`, `.crt` or `.cer`) that holds both the certificate and
its private key is accepted too. The path is on the server; in Docker it is a path inside the container, such as a
file in the data volume.

If the certificate cannot be loaded and HTTP is also enabled, the panel keeps serving HTTP and logs `The HTTPS
certificate of the web panel (...) could not be loaded; serving HTTP only until the settings change.` If HTTPS is
the only protocol, the panel stays down and retries every 30 seconds until you fix the file.

The self-signed certificate carries the machine name it had when the certificate was created. After renaming the
machine, stop NutHub, delete `certs/web-selfsigned.pfx` and start again to get a new one.

### Fixing the panel settings in the file

A wrong port, listen address, certificate or network list can make the panel unreachable. So can turning the panel
off. In that case, edit the `"web"` section of `nuthub.json` on the server. These values bring back plain HTTP on
every address, open to every network:

```json
"web": {
  "enabled": true,
  "bindAddress": "*",
  "httpEnabled": true,
  "httpPort": 8493,
  "allowedNetworks": []
}
```

Change only these keys and leave the rest of the section as it is. A running NutHub applies edits to the file
within a second or two. If an edit is not valid, NutHub keeps the previous settings and logs `The edited
configuration file was not applied: ...`. Run `nuthub check-config` to see the exact problem.

```powershell
# Windows, administrator PowerShell
notepad "$env:ProgramData\NutHub\nuthub.json"
```

```bash
# Linux
sudo nano /etc/nuthub/nuthub.json
```

In Docker the file is in the configuration volume. `docker volume inspect <name>` shows where that volume lives on
the host.

## Forgotten password

### Web accounts

- **First sign-in**: the password of the generated `admin` account is in `initial-admin-password.txt` in the data
  directory (see [First sign-in](installation.md#first-sign-in)).
- **Another administrator** can set a new password in **Settings > Web accounts**, and can turn on **Must change the
  password at the next sign-in**.
- **From the command line on the server** (this works while NutHub is running):

  ```powershell
  & "C:\Program Files\NutHub\nuthub.exe" passwd admin
  ```

  ```bash
  sudo /opt/nuthub/nuthub passwd admin
  ```

  ```bash
  # Docker: --generate creates a random password and prints it
  docker exec nuthub /app/nuthub passwd admin --generate
  # or type it yourself (-it gives the command a terminal)
  docker exec -it nuthub /app/nuthub passwd admin
  ```

  The command asks for the new password twice (at least 8 characters). Add `--must-change` to force a change at the
  next sign-in. A running NutHub applies the change within a few seconds and signs that account out of every
  browser. If the account name is wrong, the command lists the accounts that exist.

- **No enabled administrator left** (the only one was deleted or disabled by hand in the file): restart NutHub. At
  start it creates a new administrator, named `admin` (or `admin` plus three digits if that name is taken), and
  writes its password to `initial-admin-password.txt`.

After 10 failed sign-ins within 5 minutes from one address or for one user name, the panel refuses further attempts
for a while and shows `Too many attempts. Try again in N s.`

### NUT accounts

NutHub stores only hashes of NUT account passwords, so a password cannot be read back. Set a new one in
**Settings > NUT accounts**, or on the server:

```bash
sudo /opt/nuthub/nuthub nut-user set-password monuser
```

Then update the password in `upsmon.conf` (or the client's settings) on every machine that uses the account.

## The UPS is not found

When a driver cannot reach its UPS, it says why: on the dashboard card, on the UPS page and in the log. It keeps
retrying on its own. If it has had no contact at all within 60 seconds of starting, the event **No communication**
is recorded. Read the driver's message first; most of the messages quoted below come with the fix.

There are two ways to search for devices:

- **Settings > UPS devices > Add a UPS**: choose a driver marked **Can search for devices**, then press **Search for
  devices**. The search runs on the server, so in Docker it only sees devices passed into the container.
- On the server: `nuthub devices` runs the search of every driver that has one (USB HID, Windows battery, serial
  ports, apcupsd) and prints what it finds, with the options to use. `--timeout SECONDS` changes how long each
  driver may search (20 by default).

```bash
sudo /opt/nuthub/nuthub devices
docker exec nuthub /app/nuthub devices
```

The USB HID, serial and SNMP drivers have been tested against simulators and dumps of real devices, not against
every model. If your UPS is found but a value is missing or wrong, please report it (see
[Reporting a bug](#reporting-a-bug)).

### USB on Linux

The service runs as the `nuthub` account. It can open a USB UPS because `install.sh` installs
`/etc/udev/rules.d/99-nuthub-ups.rules`, which gives the `nuthub` group access to the UPS's `/dev/hidraw*` node.
Without the rule, the driver reports `Permission denied on /dev/hidrawN (USB vvvv:pppp)` and suggests installing it.
When the UPS options name no device, the message can also be `No USB UPS was found.` followed by `N HID device(s)
could not be read for lack of permission`: the cause is the same.

Check what is connected and who may open it:

```bash
lsusb                                                   # the UPS and its vendor:product id
grep -H HID_NAME /sys/class/hidraw/hidraw*/device/uevent   # which hidraw node is the UPS
ls -l /dev/hidraw*                                      # the UPS node should belong to the group nuthub
```

- **The node belongs to `root`, not `nuthub`**: the rule is missing, or the UPS's vendor id is not in it. The rule
  lists the vendors and products NUT knows. For another UPS, add your own rule in a separate file (`install.sh`
  rewrites `99-nuthub-ups.rules` on every upgrade). Replace `vvvv` and `pppp` with the two halves of the id `lsusb`
  shows, for example `051d` and `0002`:

  ```bash
  echo 'SUBSYSTEM=="hidraw", ATTRS{idVendor}=="vvvv", ATTRS{idProduct}=="pppp", MODE="0660", GROUP="nuthub"' \
    | sudo tee /etc/udev/rules.d/99-nuthub-local.rules
  sudo udevadm control --reload-rules
  sudo udevadm trigger --subsystem-match=hidraw
  ```

  If the node still belongs to `root`, unplug the USB cable and plug it back in.

- **The UPS answered for days and then stopped, without being unplugged**: some models (and some USB
  controllers) hang until the device is re-enumerated. NutHub does it by itself after three failed attempts in a
  row, writing `the USB device was reset (/dev/bus/usb/...)` in the log; if instead it writes that the device could
  not be reset, the process may not open that node (install the udev rules, or in a container give it the usb
  devices as well as the hidraw node). The option `usbReset` turns the behaviour off.
- **`lsusb` shows the UPS but no hidraw node matches it**: another program has taken the device away from the
  kernel's HID driver. NUT's own `usbhid-ups` driver does this. Stop it and keep it from starting at boot
  (`systemctl list-units 'nut*'` lists NUT's services), then unplug and replug the UPS so that `/dev/hidraw*` comes
  back. NutHub replaces NUT's driver and server; the two should not share the same UPS.
- **No HID device is visible at all**: the driver says so and suggests checking the cable and that the `hidraw` and
  `usbhid` kernel modules are loaded. Some UPSes are not HID devices at all; they have a USB-serial chip (they show
  up as `/dev/ttyUSB0` or `/dev/ttyACM0`) and need the `megatec` or `apcsmart` driver.
- **`does not look like a UPS`**: the device was found, but its report descriptor has no power device collection
  and no subdriver knows its ids. Choose a subdriver by hand in the UPS's **Subdriver** option.
- **Docker**: the hidraw node must be passed into the container, and the container's user needs access to it.
  `docker-compose.yml` has commented examples (`devices`, `group_add`, and a variant that follows the UPS when its
  hidraw number changes). `docker exec nuthub /app/nuthub devices` shows what the container sees. A node the
  container cannot open is listed with the error: `EACCES` means the file permissions (`group_add` with the node's
  group, or a udev rule on the host; the container's user can also be root), `EPERM` means the container was not
  given the device (`devices`, or `device_cgroup_rules` for the variant that passes `/dev`) and running as root does
  not help. The node must keep a `hidraw` name inside the container: NutHub looks at `/dev/hidraw*`.
- **On a NAS whose own UPS support manages the USB UPS**: leave it that way. Let NutHub read the UPS from the NAS's
  NUT server with the `nut` driver (host: the NAS address, port 3493). On UGREEN UGOS Pro the UPS name is usually
  `ups0`. This setup has been checked on a UGREEN NAS.
- **The HID layer**: on Linux NutHub opens the `/dev/hidraw*` nodes itself and identifies them with the kernel's
  hidraw requests, adding what `/sys` tells when it is mounted. It needs neither libudev nor the udev database
  (`/run/udev`), so it works in containers and minimal systems. The HidSharp library, which Windows uses and which
  enumerates through libudev on Linux, can be brought back with the environment variable
  `NUTHUB_HID_BACKEND=hidsharp` (in the systemd unit with `Environment=`, in Docker with `-e` or `environment:`), for
  instance to compare the two; remove it to return to the default. It applies to the `usbhid` driver and to
  `megatec` UPSes on a USB cable.

### USB on Windows

If another program holds the UPS, the driver reports:

> Windows denied access to the UPS (USB vvvv:pppp): another program or driver is using it exclusively.

Close the vendor's UPS software (PowerChute, PowerPanel...) and anything else that talks to the UPS. If access is
still denied, use the `winbattery` driver instead. It reads the UPS through the Windows battery driver: charge,
runtime and power state, with no instant commands. Its **Battery** option chooses one battery when there are
several; left empty, it takes the first UPS battery. The driver's use of the Windows battery interface has not been
checked on real UPSes yet, so reports are welcome.

### Serial UPS (megatec, apcsmart)

- **Port name**: `COM3` on Windows (see **Ports (COM & LPT)** in Device Manager). On Linux, prefer the stable
  `/dev/serial/by-id/...` names to `/dev/ttyUSB0`, whose number can change after a reboot. `The serial port ... does
  not exist.` means the name is wrong, or the USB adapter is unplugged or has no driver.
- **Permission denied (Linux)**: the port belongs to the `dialout` group (`uucp` on Arch and openSUSE). The systemd
  unit already adds that group to the service (`SupplementaryGroups=`). If you run NutHub another way, add its
  account to the group. In Docker, add the group with `group_add` (see `docker-compose.yml`).
- **In use by another program**: another UPS service, a terminal program, or a second UPS in NutHub configured on
  the same port. Two programs talking to the same port disturb each other, so only one may use it.
- **Baud rate**: 2400 baud, 8 data bits, no parity, 1 stop bit, for almost all these UPSes. Change it (in the
  advanced options) only if the manual says so.
- **megatec protocol detection**: with **Protocol** on **Automatic detection**, the driver tries every protocol,
  which can take half a minute. It then logs the one that answered: `the UPS on ... speaks the '...' protocol; set it
  explicitly in the UPS options to skip the detection.` If nothing answers, the message is `No valid answer from a
  Megatec/Q1 UPS on ... in any known protocol`. Check the cable (a straight RS-232 cable or the one supplied with the
  UPS), the port and the baud rate. A UPS that is switched off does not answer. Some cables take their power from
  the port: try the **Cable power (DTR / RTS)** option.
- **apcsmart**: APC serial ports do not use the standard pinout; use the APC 940-0024 "smart" cable. UPSes that
  are on USB need the `usbhid` driver instead.
- **TCP serial server** (ser2net, Moxa NPort...): the server must pass the bytes through unchanged (ser2net `raw`
  mode, NPort `TCP server` mode), with its own serial side set to 2400 8N1. If the driver reports that the server
  `closed the connection`, the server restarted or accepts only one client at a time. If it `refused the
  connection`, the TCP port is wrong or the service is not running. For slow servers, raise **Reply timeout (ms)**.
- **megatec over a USB cable**: many of these UPSes have a USB-serial bridge that shows up as a HID device. Use the
  connection **USB cable with a built-in USB-serial bridge (HID)**. On Linux, the udev rule covers the known
  bridges (for example `0665:5161`); for others, add a local rule as described for USB above.

### SNMP card

- `No answer from ... (check address, community / credentials, and that SNMP is enabled on the card)`: with SNMP v1
  and v2c, a card that receives the wrong community does not answer at all, so a wrong community looks exactly like
  a network problem. Check, in this order:
  - SNMP is enabled on the card, for the version you chose.
  - **Community** matches the card's read community (NutHub's default is `public`).
  - UDP port 161 is reachable from the NutHub machine; firewalls and VLANs often block UDP.
  - Many cards answer only the managers listed in their SNMP access settings. Add the NutHub machine's address there.

  If the net-snmp tools are installed, `snmpget -v2c -c public <card> 1.3.6.1.2.1.1.2.0` tests the same path.
- **SNMP v3**: the messages name the problem. `does not know the user`: the user name (securityName) is wrong.
  `wrong authentication password, or the card does not use ...`: check the password and the authentication
  protocol. `privacy failed`: check the privacy password and protocol. `does not accept the security level`: choose
  the level configured on the card. Both passwords need at least 8 characters. SHA-224 is not available.
- **MIB**: **Automatic** recognises the card from its sysObjectID and the objects it answers. `answers SNMP but
  implements none of the supported UPS MIBs` means the device is probably not a UPS card, or it uses a MIB NutHub
  does not have. If you chose a MIB by hand and the card does not implement it, the message says so; go back to
  **Automatic**.
- **Instant commands fail**: many cards have a separate read-write community (often `private`). Set it in **Write
  community**.
- **Slow cards**: raise **Timeout (ms)** (1000 by default) or **Retries** (3).

### Another NUT server (nut driver)

| Driver message | Check |
|---|---|
| `Cannot connect to the NUT server ...: connection refused (is the server running and listening on this address?)` | `upsd` listens only on `127.0.0.1` and `::1` unless `upsd.conf` has a `LISTEN` line for another address. Add one (for example `LISTEN 0.0.0.0 3493`) and restart `upsd`. On a NAS, turn on its network UPS server. Check the firewall on that machine. |
| `... has no UPS named '...' (ERR UNKNOWN-UPS)` | **UPS name on that server** is wrong. `upsc -l <server>` lists the names. UGREEN usually uses `ups0`, Synology `ups`, QNAP `qnapups`. |
| `... does not allow this machine to read '...' (ERR ACCESS-DENIED)` | That server restricts which clients may read. Some NAS UPS servers answer only the addresses listed in their network UPS settings; add the NutHub machine. |
| `... reports stale data ...` or `The driver of '...' on ... is not connected to the UPS` | The problem is on that server, between its driver and the UPS. |

Reading needs no account. Instant commands and variable writes do: set **Username** and **Password** to an account
of that server's `upsd.users` that has the right `actions` and `instcmds`. Without them, a command answers `The NUT
server ... needs a username and a password for this`. If the account lacks the right, the answer is `The upstream
server refused the credentials; check the username, the password and the actions and instcmds granted in its
upsd.users`. **Log in as a secondary** also needs the `upsmon` role on that server.

With **Use TLS (STARTTLS)**, the other server must have a certificate (`CERTFILE` in `upsd.conf`). **Certificate
check: None** accepts self-signed certificates.

### apcupsd

The `apcupsd` driver reads apcupsd's network information server on TCP 3551. In `apcupsd.conf`, `NETSERVER` must be
`on`, and `NISIP` must be an address NutHub can reach: with `127.0.0.1`, apcupsd answers only on its own machine.
If apcupsd itself has lost the UPS, the driver reports `apcupsd at ... has lost communication with the UPS (STATUS
COMMLOST)`; look at apcupsd, not NutHub. This driver is read-only: it has no instant commands.

## Import from NUT left something out

**Settings > Import from NUT** lists under **Warnings** everything it could not carry over; read that list in the
preview before you press **Apply the import**. The usual cases:

| Warning | Meaning |
|---|---|
| `A UPS named '...' already exists; not imported.` (or `A NUT account named ...`) | The import never changes what exists. Delete the existing one first if you want the imported version. |
| `the NUT driver ... has no NutHub equivalent; skipped.` | Only `usbhid-ups`, `nutdrv_qx`, `blazer_ser`, `blazer_usb`, `apcsmart`, `snmp-ups`, `dummy-ups` with `port = ups@host` and `apcupsd-ups` are mapped. Add that UPS by hand with the closest driver. |
| `dummy-ups with a simulation file is not supported` | Use the **Simulated UPS** driver with the file as **NUT .dev file**. |
| `the option ... is not supported; ignored.` | That `ups.conf` option has no counterpart; the UPS is imported without it. |
| `... is required; set it after the import.` | The UPS is imported, but its driver needs a value `ups.conf` did not give. Its driver fails until you set it. |

The full mapping is in [configuration.md](configuration.md#import-from-nut).

## Data not current, communication lost

A UPS shows **Data not current** when its driver runs but has sent no fresh data for too long. The limit is the
larger of:

- **Data maximum age (s)** in **Settings > NUT server** (NUT's `MAXAGE`, 15 seconds by default);
- two poll intervals of that UPS (**Poll interval (s)** in the UPS's settings, 2 seconds by default).

When a UPS goes from fresh to stale, the event **Communication lost** is recorded with the reason, for example
`Communication with rack lost (no data for more than 15 s).`, followed by **Communication restored** when data comes
back. The dashboard and the UPS page keep showing the last known values, marked as not current. NUT clients get
`ERR DATA-STALE`.

What to check:

- **The driver's message** on the UPS page, which usually names the cause (device unplugged, host unreachable,
  timeout).
- **The link to the device**: the USB cable (try another port or cable), the serial cable, the network path to an
  SNMP card or another server (`ping` it from the NutHub machine).
- **Slow devices**: if an SNMP card or a serial device server sometimes needs longer than the limit for one poll,
  raise **Data maximum age (s)**. A longer poll interval also raises the limit, because the limit is at least two
  poll intervals.
- **Restart driver** (UPS page, **Actions** menu, administrators) closes the connection to the device and opens it
  again. A driver that fails is restarted by NutHub on its own, after 2, 5, 10, 30 and then 60 seconds. The exception
  is a driver that refuses its options (a missing or invalid value): it stays in **Driver failed**, with the reason on
  the UPS page and in the log, until you correct the UPS's settings.

A UPS that stops answering while it is **on battery** is treated as critical, both by `upsmon` (after its
`DEADTIME`) and by NutHub's host protection (after **Communication lost while on battery (s)**, 15 by default). A
communication problem during an outage can therefore start a shutdown. That is intentional.

## NUT clients get an error

NutHub answers with the error words `upsd` uses, so `upsc`, `upsmon`, NutDesk and other clients report them as
usual.

| Error | `upsc` prints | In NutHub it means |
|---|---|---|
| `ERR ACCESS-DENIED` | `Error: Access denied` | A command that needs an account was refused. See the list below. |
| `ERR UNKNOWN-UPS` | `Error: Unknown UPS` | No UPS has that name. Names ignore case, as in `upsd`. `upsc -l <server>` lists them. |
| `ERR DATA-STALE` | `Error: Data stale` | The UPS's driver runs, but has no fresh data: it is still connecting, it lost the device, or the data is older than the maximum age. See [Data not current](#data-not-current-communication-lost). |
| `ERR DRIVER-NOT-CONNECTED` | `Error: Driver not connected` | No driver runs for that UPS. It is disabled in **Settings > UPS devices**, its driver is still starting (the first seconds after a start or a settings change), or its driver failed, usually because of an invalid option (the log names it). |

`ERR ACCESS-DENIED` is logged as a warning with the reason, for example:

> NUT client 192.168.1.20 (account 'monuser') was refused LOGIN rack: the account lacks the right (upsmon secondary or primary).

| Reason in the log | Fix |
|---|---|
| `unknown account` | The user name in the client does not exist in **Settings > NUT accounts**. Names are case-sensitive. |
| `wrong password` | Set the password again in the account and in the client. |
| `the account lacks the right (...)` | `LOGIN` needs the monitor role **Secondary** or **Primary**. `PRIMARY` (or `MASTER`) needs **Primary**. `FSD` needs **Primary** or the **Set forced shutdown** action. `SET` needs **Change writable variables**. `INSTCMD` needs the command, or **All commands**. |
| `the account may not use the UPS ...` | The account's **Allowed UPSes** does not include this UPS. |
| `too many failed password checks from this address` | 10 failed checks within 5 minutes lock the address out of commands that need an account for 5 minutes. Fix the password, then wait. |

If **Require TLS to log in** is on, a client that sends its user name without switching to TLS first gets `ERR
ACCESS-DENIED` too. Clients without TLS can then only read.

Other errors you may see:

- `ERR FEATURE-NOT-CONFIGURED` in answer to `STARTTLS`: TLS is off in **Settings > NUT server**.
- `ERR READONLY`: the variable cannot be written. This includes variables you override in the UPS's settings, since
  an override makes the variable read-only.
- `ERR CMD-NOT-SUPPORTED`, `ERR VAR-NOT-SUPPORTED`: the UPS does not have that command or variable. Its **Variables**
  tab and **Actions** menu show what it has.

### The client cannot connect at all

- **Another NUT server on the same machine**: if `upsd` (or anything else) already listens on 3493, NutHub keeps
  running without its NUT server. **Settings > NUT server** shows **NUT server problem**, the status summary in the
  top bar shows the error, and the log records `The NUT server cannot listen on ...: the port is already in use by
  another program. Retrying every 30 s.` Stop the other server, or change NutHub's port in **Settings > NUT server >
  Addresses and ports**.
- **Firewall**: TCP 3493 must be open on the NutHub machine (see [Firewall](#firewall)).
- **Allowed networks** in **Settings > NUT server**: a client outside the list is disconnected as soon as it
  connects, with no error message. The log records `Refused a NUT connection from ...: not in the allowed networks.`
- **Docker on a NAS with the port published as 13493**: clients must use that port (`upsc rack@nas:13493`).

To test from a client machine:

```bash
upsc -l 192.168.1.10
upsc rack@192.168.1.10 ups.status
```

```powershell
# Windows, without NUT installed: is the port reachable?
Test-NetConnection 192.168.1.10 -Port 3493
```

**Settings > NUT server > Connected NUT clients** lists every connection, with its address, account and role. The
**NUT clients** tab of a UPS page, shown while at least one client is logged in, lists the clients logged in to that
UPS.

### NAS units as NUT clients

When a NAS acts as a NUT client, it uses fixed values. A Synology in **Synology UPS server** mode pointing at NutHub
connects to port 3493 and expects a UPS named `ups` and the NUT account `monuser` with the password `secret`, role
Secondary. A QNAP acting as a network UPS slave expects the UPS `qnapups` and the account `admin` / `123456`. Give
the UPS that name in NutHub and create that account. These passwords are shorter than 8 characters, so **Settings >
NUT accounts** and `nuthub nut-user` refuse them; create the account with **Settings > Import from NUT** instead, as
described in [Accounts with short fixed passwords](clients.md#accounts-with-short-fixed-passwords). Both modes
follow NUT's protocol but have not been tested against NutHub yet.

## upsmon does not shut down, or shuts down too early

Start with the **Events** page, filtered on the UPS. It shows what happened and when: on battery, low battery,
communication lost, forced shutdown. The details of a forced shutdown event show who set it (**Done by**).

### It does not shut down

1. **Is upsmon logged in?** It should appear in **Connected NUT clients** with its account and the role Secondary
   or Primary. If it does not, check the `MONITOR` line (UPS name, NutHub address, port, account, password) and the
   `ERR ACCESS-DENIED` reasons above. A `secondary` line needs an account with the monitor role Secondary (or
   Primary); a `primary` line needs Primary. A typical secondary:

   ```
   MONITOR rack@192.168.1.10 1 monuser <password> secondary
   ```

   NUT 2.7.4 clients write `slave` and `master` instead of `secondary` and `primary`; NutHub accepts both.
2. **Does the UPS become critical?** upsmon shuts down when the UPS is on battery with a low battery (`OB LB`), or
   when a forced shutdown (`FSD`) is set. Many UPSes report `LB` late, and some never do. Let NutHub declare it:
   in the UPS's settings, under **Low battery**, set **Low battery below charge (%)** or **Low battery below runtime
   (s)**. Once the UPS is on battery and below that level, NutHub adds `LB` itself, and every client sees it.
3. **Does upsmon's shutdown command work?** Check `SHUTDOWNCMD` in `upsmon.conf` and upsmon's own log on that
   machine.

To test the whole chain without cutting real power, add a UPS with the **Simulated UPS** driver and a short
**Runtime on a full battery at that load (minutes)**, such as 3. Point a test machine's upsmon at it, then run the
instant command `test.failure.start` from its **Actions** menu. The simulated UPS goes on battery, reaches `LB`
within about a minute, and the client should shut down. `test.failure.stop` ends the outage.

### It shuts down too early

- **Low battery arrives too early**: your thresholds are too high, or the UPS signals `LB` early. Turn on **Ignore
  the low battery signal of the UPS** and set your own threshold.
- **A forced shutdown was set**, by one of these:
  - an operator in the panel;
  - NutHub's host protection with **Tell NUT clients to shut down first**;
  - a upsmon primary on another machine;
  - a `megatec` UPS that always reports "Shutdown Active" (turn on **Ignore the 'Shutdown Active' bit**);
  - the other server, for a UPS read with the `nut` driver.
- **Communication was lost while on battery**: see
  [Data not current](#data-not-current-communication-lost).
- **A UPS that reports `OL DISCHRG` with mains present**, with **On line and discharging means on battery** turned
  on: see [The status shows OL DISCHRG](#the-status-shows-ol-dischrg).
- **The machine shuts down again right after it boots**: the forced shutdown is still set. See the next section.

Only one machine per UPS should be the primary: the one that shuts down last and may turn the UPS off. Every other
machine is a secondary.

## The forced shutdown (FSD) stays set

A forced shutdown tells every NUT client of the UPS to shut down now. As in `upsd`, it stays set once set. A client
that starts while it is still set shuts down again right away. NutHub therefore clears it by itself once the UPS
has been back on line power, with fresh data and no low battery, for **Clear FSD after mains return (s)** in
**Settings > NUT server** (60 seconds by default). With 0, it stays until an operator clears it or NutHub restarts,
which is upsd's behaviour.

If it does not clear:

- **Check the status.** Clearing waits for `OL` without `OB` and without `LB`. Some UPSes keep `LB` for a while
  after an outage, until the battery has recharged a little.
- **Clear it by hand**: on the UPS page, **Actions > Clear FSD** (operators and administrators). Machines that
  already shut down must be started by hand.
- **The FSD comes from the device**: some `megatec` UPSes report "Shutdown Active" all the time. Turn on **Ignore
  the 'Shutdown Active' bit** in the UPS's advanced options.
- **The FSD comes from another server**: a UPS read with the `nut` driver shows the status of the other server,
  including its `FSD`. **Clear FSD** only appears for a forced shutdown set in NutHub; clear the other one on the
  server that set it.

## The status shows OL DISCHRG

`OL DISCHRG` means the UPS reports being on line power and discharging at the same time. NutHub publishes what the
device says. With the `usbhid` driver, it logs this warning at most every 10 minutes:

> ... reports being on line and discharging at the same time. If it is calibrating, enable the
> 'onlineDischargeCalibration' option; some models (e.g. CyberPower UT) report this when they actually run on
> battery: then enable 'onlineDischarge'.

APC Back-UPS units often report `OL DISCHRG` on line power with a full battery. That is what the device says, not an
error, and it is harmless. `OL` stays in the status, so no **On battery** event is recorded, and neither upsmon nor
the host protection reacts: they need `OB`. Leave both options off for these units.

The two options are in the UPS's advanced options (`usbhid` driver):

| Option | Use it when |
|---|---|
| **On line and discharging means on battery** | The UPS really runs on battery but reports `OL DISCHRG` (some CyberPower UT models). NutHub then reports it on battery (`OB`). Test it by unplugging the UPS from the mains. |
| **On line and discharging means calibrating** | The UPS reports `OL DISCHRG` during a runtime calibration (some APC models). NutHub then reports a calibration (`CAL`). |

Never enable the first option on a UPS that shows `OL DISCHRG` with mains present. Every client would see it as on
battery, and rules based on time on battery (such as the host protection's **On battery for longer than (s)**)
would start a shutdown.

## Notifications do not arrive

Start in **Settings > Notifications**.

1. **Is the event notified?** **Events to notify** is the general list. Each channel uses it, unless the channel's
   **Events sent by this channel** is set to **A list of its own**. By default the list covers on battery, back on
   line, low battery, forced shutdown, replace battery, overload, communication lost and restored, no
   communication, and the host shutdown events. **Restore defaults** brings it back.
2. **Is the channel on?** **Send e-mail** for e-mail, the enabled switch for each webhook and command.
3. **Press Send test.** Tests use the saved settings, so save first. A test is sent even when the channel is
   switched off, so a working test and no real messages point to steps 1 and 2. An e-mail test skips the outbox and
   shows the mail server's answer at once. A webhook test makes a single attempt.
4. **Read Recent deliveries.** It lists the last notifications of every channel with their result, and the error
   when one failed.

### E-mail

- **Grouping**: events that happen within 10 seconds of each other go out as one message, so a short delay is
  normal.
- **Outbox**: a message that cannot be sent waits on disk (`notifications/outbox.json` in the data directory) and
  survives restarts. It is retried after 30 seconds, then 1, 2, 5 and 10 minutes, then every 10 minutes, for up to
  24 hours. **Recent deliveries** shows the first failure with `(queued, retrying for up to 24 hours)`. A message
  still unsent after 24 hours is dropped with `Given up after N attempts in 24 hours`. The outbox holds 500 messages
  at most. Turning e-mail off discards the messages waiting in it.
- **Errors**:

| Error | Check |
|---|---|
| `SMTP authentication failed: ...` | User name and password. Many providers need an app password when the account uses two-factor authentication. |
| `TLS negotiation with the SMTP server failed: ...` | NutHub checks the server's certificate. A relay with a self-signed certificate fails this check. Also make sure **Security** matches the port: **SSL/TLS on connect** on a STARTTLS port (587, 25) fails here. |
| `Cannot reach the SMTP server: ...` | Host name, port, firewall. Many networks block outgoing port 25. |
| `The SMTP server did not answer in time.` | Often **STARTTLS** or **None (clear text)** on port 465, where the server waits for TLS from the first byte. |
| `The SMTP server requires authentication: ...` | Fill in **User name** and **Password**. |
| `SMTP error 5xx: ...` for the sender or a recipient | The server refuses the address. These messages are not retried. |

**Security: Automatic** uses SSL/TLS on connect on port 465, and STARTTLS on other ports when the server offers it.
The usual combinations are port 587 with STARTTLS and port 465 with SSL/TLS on connect. **None (clear text)** is
only for a trusted relay on your own network.

### Webhooks

**Recent deliveries** shows the service's answer: `HTTP 401 Unauthorized: ...` (a token or header is wrong), `HTTP
400 ...` (the body does not suit the service, check the template and its placeholders), `No answer within 10 s.`, or
a network or TLS error. Timeouts, network errors, HTTP 408, 429 and 5xx answers are retried twice (after 2 and 10
seconds, or after the `Retry-After` delay the service asks for, up to 30 seconds). Other 4xx answers are not retried,
because retrying would not help. Certificates are always checked.

### Commands

The program runs on the NutHub machine, as the service account, with a time limit (30 seconds by default) after
which it is stopped. Give its full path. The event is also passed in environment variables (`NOTIFYTYPE`, `UPSNAME`
and `NUTHUB_*`). On Linux, the service runs in a sandbox: the file system is read-only except NutHub's own
directories, `/home` cannot be reached, `/tmp` is private and privileges cannot be raised. A script that writes to
other places, or calls `sudo`, fails there.

### Flood protection

Each channel sends at most 20 notifications in 10 minutes. The events held back are counted and sent later as a
single summary: `Flood protection held back N notification(s) in the last 10 minutes (...)`. Low battery, forced
shutdown and the host shutdown events always go out at once. If mains power flaps, expect the summary, not one
message per transition.

## Host protection did not shut down the machine

Look at **Settings > Host protection > Current status** and at the events **Host shutdown pending**, **Host shutdown
started** and **Host shutdown cancelled**.

1. **Is it on, and does it know the UPS?** **Protect this machine** must be on, and the UPS selected in **UPSes
   powering this machine**. With no UPS selected, the log warns `Host protection is enabled but no UPS is selected:
   this machine is not protected.`
2. **Did the UPS become critical?** By default, a UPS is critical only when it is on battery with a low battery
   (`OB LB`), when a forced shutdown is set on it, or when it has been silent for 15 seconds after being seen on
   battery. A UPS on battery with plenty of charge is not critical, and a runtime calibration never counts. For an
   earlier shutdown, set **On battery with charge below (%)**, **On battery with runtime below (s)** or **On battery
   for longer than (s)**. The shutdown starts when fewer than **Minimum healthy supplies** of the selected UPSes are
   healthy.
3. **Was it a dry run?** With **Dry run (no real shutdown)** on, the sequence ends in **Dry run completed**. The log
   records `Dry run (dry run enabled in the settings): this machine would now be shut down with: ...`. It arms again
   once the UPSes are back to normal.
4. **Does the panel show "dry run" while the switch is off?** Then something outside the settings prevents the
   shutdown, and the same log line names it:
   - `NUTHUB_DISABLE_SHUTDOWN is set`: the environment variable `NUTHUB_DISABLE_SHUTDOWN` is `1` or `true` for the
     service. Look in `systemctl edit nuthub.service`, the system environment variables on Windows, or the
     `environment` of the compose file.
   - `debug build`: this executable was built in the Debug configuration (for example with `dotnet run` from the
     sources), which never shuts anything down. The release archives and the Docker image are release builds.
5. **Was it cancelled?** If the power came back during **Grace delay (s)**, the shutdown is cancelled. After someone
   presses **Cancel shutdown**, the protection waits until the UPSes recover before it arms again.
6. **Did the shutdown command fail?** The log shows `The shutdown command failed (...); trying the next one.` and,
   when every command failed, `The shutdown of this machine failed: ... Shut it down by hand.`
   - **Linux**: the service runs as `nuthub` and powers off through `systemctl poweroff`, which polkit must allow.
     `install.sh` installs `/etc/polkit-1/rules.d/50-nuthub-poweroff.rules`, or
     `/etc/polkit-1/localauthority/50-local.d/50-nuthub-poweroff.pkla` on older polkit versions. When polkit is
     not installed, it skips this step and prints `polkit was not found`. Install polkit and run `install.sh`
     again. The alternative is to run the service as root: `User=root` with `sudo systemctl edit nuthub.service`.
   - A custom **Shutdown command** runs as the service account, in the sandbox described under
     [Commands](#commands), so it cannot use `sudo`. If it fails, NutHub tries the system default next.
   - **Windows**: the service runs as LocalSystem and uses `shutdown.exe /s /f /t 0 /d 6:12 /c "NutHub: UPS power
     critical"`.
7. **NutHub runs in a container.** A container cannot shut down its host. Install NutHub on the host, or run
   `upsmon` as a secondary of NutHub on each machine to protect. For a NAS, make the NAS a NUT client of NutHub (see
   [NAS units as NUT clients](#nas-units-as-nut-clients)). If the NAS itself manages the USB UPS, as UGREEN UGOS does,
   keep it that way: the NAS protects itself, and NutHub reads the UPS with the `nut` driver.

The UPS power-off (**Tell the UPS to turn off after the shutdown**) is sent only if the UPS is still on battery at
that point. A UPS on line power would not turn itself back on. The UPS must also support the chosen **Power-off
command**; its **Actions** menu lists the commands it has.

## Logs

| Where | What |
|---|---|
| **Settings > Logs** | The last 2000 messages at level Information and above since NutHub started, with **Minimum level**, **Number of lines**, **Search the log** and **Refresh automatically**. Kept in memory only. |
| Log files | One file per day in the `logs` folder of the data directory: `nuthub-YYYYMMDD.log`, continued in `nuthub-YYYYMMDD.1.log` and so on at 10 MB. Kept 14 days. |
| Linux | The journal: `sudo journalctl -u nuthub -f` (follow), `sudo journalctl -u nuthub --since "1 hour ago"` |
| Windows | Event Viewer, **Windows Logs > Application**, source `NutHub`: warnings and errors only, including why the service could not start. |
| Docker | `docker logs nuthub` (or `docker compose logs nuthub`), plus the files in the data volume. |

```powershell
# Windows: the last NutHub entries of the Application log, and today's log file
Get-WinEvent -FilterHashtable @{ LogName = 'Application'; ProviderName = 'NutHub' } -MaxEvents 20
Get-Content "$env:ProgramData\NutHub\logs\nuthub-$(Get-Date -Format yyyyMMdd).log" -Tail 50 -Wait
```

The **Events** page is not the log. It is the history of the UPSes and the server (on battery, commands, sign-ins,
configuration changes), kept 365 days by default. The log is the technical record of what NutHub did.

### More detail

The default level is Information. For a problem that is hard to see, raise it to Debug with the setting
`Logging__LogLevel__Default=Debug`:

- **Linux**: run `sudo systemctl edit nuthub.service`, add the lines below, then `sudo systemctl restart nuthub`.

  ```ini
  [Service]
  Environment=Logging__LogLevel__Default=Debug
  ```

- **Windows**: create `C:\Program Files\NutHub\appsettings.json` next to `nuthub.exe`, then restart the service
  (`Restart-Service NutHub`).

  ```json
  { "Logging": { "LogLevel": { "Default": "Debug" } } }
  ```

- **Docker**: uncomment `Logging__LogLevel__Default: "Debug"` in `docker-compose.yml`, then `docker compose up -d`.

Debug messages go to the log files and to the journal or `docker logs`. The **Logs** page still shows Information
and above. Debug output is verbose, so switch back once you have what you need: remove the line, or delete the
file, and restart.

## Resetting to defaults

Stop NutHub before you touch its files.

| Service | Stop | Start |
|---|---|---|
| Windows | `Stop-Service NutHub` | `Start-Service NutHub` |
| Linux | `sudo systemctl stop nuthub` | `sudo systemctl start nuthub` |
| Docker | `docker stop nuthub` | `docker start nuthub` |

- **Undo the last change**: every save keeps the previous file as `nuthub.json.bak`, next to `nuthub.json`. Stop
  NutHub, copy `nuthub.json.bak` over `nuthub.json`, and start it again. The backup is replaced at every save, so it
  only goes back one step.
- **Start over with a new configuration**: stop NutHub, rename `nuthub.json` (keep it in case you need something from
  it), and start NutHub. It creates a configuration with the default settings and a new `admin` account, whose
  password is in `initial-admin-password.txt`. The history and the events are kept.

  ```bash
  sudo systemctl stop nuthub
  sudo mv /etc/nuthub/nuthub.json /etc/nuthub/nuthub.json.old
  sudo systemctl start nuthub
  sudo cat /var/lib/nuthub/initial-admin-password.txt
  ```

  ```powershell
  Stop-Service NutHub
  Rename-Item "$env:ProgramData\NutHub\nuthub.json" nuthub.json.old
  Start-Service NutHub
  Get-Content "$env:ProgramData\NutHub\initial-admin-password.txt"
  ```

  With Docker, `docker exec` needs the container running: rename the file, then restart the container at once.

  ```bash
  docker exec nuthub mv /etc/nuthub/nuthub.json /etc/nuthub/nuthub.json.old
  docker restart nuthub
  docker exec nuthub cat /var/lib/nuthub/initial-admin-password.txt
  ```

- **Empty history and events**: stop NutHub and delete `nuthub.db` from the data directory, together with
  `nuthub.db-wal` and `nuthub.db-shm` if they exist.
- **Everything**: remove NutHub with its data (`sudo ./install.sh --uninstall --purge` on Linux,
  `install.ps1 -Uninstall -Purge` on Windows) and install it again. With Docker, `docker compose down -v` in
  `packaging/docker` deletes the container and both volumes; `docker compose up -d` starts from scratch.

Secrets in `nuthub.json` (SMTP password, SNMP communities, webhook header values...) are encrypted with the key in
`secret.key` in the data directory. When you move a configuration to another machine, copy `secret.key` with it.
`nuthub check-config` reports any value that cannot be decrypted with the key it finds.

## Reporting a bug

Open an issue at <https://github.com/kriseniprak/NutHub/issues>. Include:

- the NutHub version (`nuthub version`, or **Settings > System**), the operating system and architecture, and how
  NutHub runs (Windows service, systemd, Docker, and on which NAS);
- what you did, what you expected, and what happened instead;
- the relevant log lines, at Debug level if the problem is not visible otherwise (see [Logs](#logs));
- for a UPS: the model and firmware, the driver and its options, the driver's message, the output of `nuthub
  devices`, the USB vendor and product id (`lsusb`) or the SNMP card model, and the UPS's **Variables** tab (or the
  output of `upsc <ups>@<server>`);
- for a NUT client: the client and its version, the `MONITOR` line without the password, and the exact `ERR`
  answer.

For the configuration, use **Settings > System > Download configuration**. The downloaded file leaves out passwords,
password hashes, certificate passwords, webhook header values, secret driver options (SNMP communities and
passwords, NUT passwords) and every encrypted value. It still contains host names and addresses, e-mail addresses,
account names and webhook URLs. Some webhook URLs carry a token (Telegram, Slack and Discord URLs do), so read the
file and remove what you would not publish.

Never post `nuthub.json`, `nuthub.json.bak` or `secret.key`. The configuration file holds password hashes and
encrypted secrets, and `secret.key` decrypts them.

Reports about real hardware are especially useful. The USB HID, serial and SNMP drivers have been tested against
simulators and dumps of real devices. The `winbattery` driver's use of the Windows battery interface, and Synology
and QNAP units as NUT clients of NutHub, have not been checked on real hardware yet.
