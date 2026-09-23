# Installation and upgrades

This page explains how to install NutHub on Windows, on Linux and with Docker (including on a NAS), how to sign in
for the first time and recover a lost password, how to upgrade, what to back up, and how to build NutHub from
source. NutHub is a single self-contained executable: no .NET runtime needs to be installed first.
Every installation keeps the program apart from its configuration and data, so an upgrade replaces the program and
keeps everything else. For what to do once NutHub runs, see the [README](../README.md#using-it),
[Configuration](configuration.md) for the settings and drivers, and [Connecting clients](clients.md) for `upsmon`,
NutDesk and NAS units.

## Contents

1. [Requirements](#requirements)
2. [Windows](#windows)
3. [Linux](#linux)
4. [Docker](#docker)
5. [On a NAS](#on-a-nas)
6. [First sign-in and lost passwords](#first-sign-in-and-lost-passwords)
7. [Upgrading](#upgrading)
8. [Backup and restore](#backup-and-restore)
9. [Running without installing](#running-without-installing)
10. [Building from source](#building-from-source)

## Requirements

| | Windows | Linux | Docker |
|---|---|---|---|
| Processor | x64 | x64, arm64, 32-bit arm (ARMv7) | amd64, arm64, arm/v7 |
| System | 64-bit Windows, an administrator account to install the service | A glibc-based distribution (Debian, Ubuntu, Raspberry Pi OS, Fedora...) with systemd for the installer | Docker Engine with the Compose plugin, internet access to build the image |
| Download | `nuthub-<version>-win-x64.zip` | `nuthub-<version>-linux-x64.tar.gz`, `-linux-arm64.tar.gz` or `-linux-arm.tar.gz` | the source code (the image is built from it) |

Each release archive has a `.sha256` file next to it on the [Releases](https://github.com/kriseniprak/NutHub/releases)
page. On a Raspberry Pi use `linux-arm64` with the 64-bit Raspberry Pi OS and `linux-arm` with the 32-bit one; on
Debian-based systems `dpkg --print-architecture` prints `amd64`, `arm64` or `armhf`. .NET needs ARMv7 or later, so
the first Raspberry Pi models and the original Pi Zero (ARMv6) cannot run NutHub.

NutHub listens on these TCP ports, which you can change in **Settings > NUT server** and **Settings > Web panel**:

| Port | Used by |
|---|---|
| 3493 | The NUT protocol: `upsmon`, `upsc`, NutDesk, Home Assistant, NAS units |
| 8493 | The web panel over HTTP |
| 8494 | The web panel over HTTPS, once you enable it |

NutHub does the job of NUT's `upsd`, so do not run both on 3493 on the same machine. If the port is taken, NutHub
logs "The NUT server cannot listen on ..." and tries again every 30 seconds; the web panel works meanwhile. On Debian
and Ubuntu, NUT's server stops with `sudo systemctl disable --now nut-server`.
[Troubleshooting](troubleshooting.md#the-port-is-in-use) shows how to find the program that holds a port.

Devices: the `nut` driver has been used with a real NUT 2.8.5 `upsd` and with the NUT server of a UGREEN NAS. The
USB HID, serial and SNMP drivers have been tested against simulators and recordings of real devices, and the battery
queries of the Windows `winbattery` driver have not been tried on real hardware yet. Reports from real UPSes are
welcome.

## Windows

### Installing

1. Download `nuthub-<version>-win-x64.zip` and `nuthub-<version>-win-x64.zip.sha256`. To check the download, in
   PowerShell (the second line prints `True`):

   ```powershell
   $expected = (Get-Content .\nuthub-<version>-win-x64.zip.sha256).Split(' ')[0]
   (Get-FileHash .\nuthub-<version>-win-x64.zip -Algorithm SHA256).Hash -eq $expected
   ```

2. Extract the archive. The folder `nuthub-<version>-win-x64` contains `nuthub.exe`, `install.ps1`, `LICENSE` and
   `README.md`.
3. Open PowerShell as administrator (**Run as administrator**), go to that folder and run:

   ```powershell
   powershell -ExecutionPolicy Bypass -File .\install.ps1 -AddFirewallRules
   ```

At the end the script prints the web panel address, where the configuration and logs are, and the user name and
password for the [first sign-in](#first-sign-in-and-lost-passwords).

### install.ps1

| Switch | Effect |
|---|---|
| (none) | Install, or upgrade an existing installation, and start the service. The firewall is not touched. |
| `-AddFirewallRules` | Also allow inbound TCP 3493 and 8493 for `nuthub.exe` in Windows Firewall. |
| `-InstallDir <path>` | Install the executable elsewhere. Default: `C:\Program Files\NutHub`. |
| `-Uninstall` | Remove the service, the firewall rules and the program folder. The configuration and data are kept. |
| `-Purge` | With `-Uninstall`: also delete `%ProgramData%\NutHub` (configuration, database, logs, keys). |

When you install, the script:

1. stops the NutHub service if it is running;
2. copies `nuthub.exe`, `LICENSE` and `README.md` to the installation folder;
3. registers the service with `nuthub service install --no-start` (or updates it, when it already exists);
4. adds the firewall rules, with `-AddFirewallRules`;
5. starts the service and waits up to 15 seconds for the first start to create the administrator password.

### The service

| | |
|---|---|
| Name | `NutHub` (display name "NutHub UPS server") |
| Account | LocalSystem |
| Start | Automatic |
| Command | `"C:\Program Files\NutHub\nuthub.exe" run --data-dir "C:\ProgramData\NutHub"` |
| On failure | Restarted after 5, 10 and 30 seconds (the count resets after a day), also when it stops with an error |
| Logs | Warnings and errors in the Windows Application log (source `NutHub`); the full log in `%ProgramData%\NutHub\logs` |

Manage it with the Services console, `Get-Service NutHub` / `Restart-Service NutHub`, or
`nuthub service start|stop` from an elevated prompt. `nuthub service status` needs no elevation and exits with 0 when
the service runs, 3 when it does not.
The log folder has one file per day (`nuthub-YYYYMMDD.log`), kept for 14 days; the recent log is also shown in
**Settings > Logs**.

### Firewall

`-AddFirewallRules` creates two inbound rules in the group `NutHub`, "NutHub NUT server (TCP 3493)" and
"NutHub web panel (TCP 8493)", which only apply to `nuthub.exe`. If you enable HTTPS or change a port, add a rule
the same way; in the `NutHub` group it is removed together with the others on uninstall:

```powershell
New-NetFirewallRule -Group NutHub -DisplayName 'NutHub web panel (TCP 8494)' -Direction Inbound `
    -Protocol TCP -LocalPort 8494 -Program "$env:ProgramFiles\NutHub\nuthub.exe" -Action Allow
```

Running `install.ps1 -AddFirewallRules` again first deletes every rule of the `NutHub` group, including one you added
this way, and then creates the two default rules. Upgrade without the switch to keep your rules.

### Files

| Path | Content |
|---|---|
| `C:\Program Files\NutHub\nuthub.exe` | The program (with `LICENSE` and `README.md`) |
| `%ProgramData%\NutHub\nuthub.json` | The configuration; `nuthub.json.bak` is the version before the last change |
| `%ProgramData%\NutHub\secret.key` | The key that encrypts the secrets stored in `nuthub.json` |
| `%ProgramData%\NutHub\nuthub.db` | Event log and history (SQLite) |
| `%ProgramData%\NutHub\keys\` | Keys of the web panel sessions |
| `%ProgramData%\NutHub\certs\` | Self-signed certificates for HTTPS and STARTTLS, created when first needed |
| `%ProgramData%\NutHub\logs\` | Log files |
| `%ProgramData%\NutHub\notifications\` | Email messages waiting to be sent |
| `%ProgramData%\NutHub\initial-admin-password.txt` | The first administrator password, until you delete it |

`%ProgramData%\NutHub` is restricted to SYSTEM and the Administrators group: open files there from an elevated
prompt. **Settings > System** shows the paths in use.

### Uninstalling

From an elevated PowerShell, in a folder with `install.ps1`:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall          # keeps %ProgramData%\NutHub
powershell -ExecutionPolicy Bypass -File .\install.ps1 -Uninstall -Purge   # deletes it too
```

This stops and removes the service and its Event Log source, removes the firewall rules and the program folder.
If you installed with `-InstallDir`, pass the same folder.

### Other locations

`nuthub service install` registers the service for the executable where it is, with the data directory you choose:

```powershell
& "C:\Program Files\NutHub\nuthub.exe" service install --data-dir D:\NutHub
```

Add `--dry-run` to see the commands without running them. `install.ps1` always registers the default data
directory, so do not use it to upgrade such an installation: stop the service, replace `nuthub.exe` and start it
again. Pass the same `--data-dir` to the other commands (`passwd`, `nut-user`, `check-config`, `devices`), or they
use `%ProgramData%\NutHub`.

## Linux

### Installing

```bash
curl -LO https://github.com/kriseniprak/NutHub/releases/download/v<version>/nuthub-<version>-linux-x64.tar.gz
curl -LO https://github.com/kriseniprak/NutHub/releases/download/v<version>/nuthub-<version>-linux-x64.tar.gz.sha256
sha256sum -c nuthub-<version>-linux-x64.tar.gz.sha256
tar xzf nuthub-<version>-linux-x64.tar.gz
cd nuthub-<version>-linux-x64
sudo ./install.sh
```

Replace `linux-x64` with `linux-arm64` or `linux-arm` as needed. The archive contains `nuthub`, `install.sh`,
`nuthub.service`, `99-nuthub-ups.rules`, `50-nuthub-poweroff.rules`, `50-nuthub-poweroff.pkla`, `LICENSE` and
`README.md`. At the end the installer prints the web panel address, the paths and the first sign-in password.

If a firewall is active, open the ports:

```bash
sudo ufw allow 3493/tcp && sudo ufw allow 8493/tcp
# or, with firewalld:
sudo firewall-cmd --permanent --add-port=3493/tcp --add-port=8493/tcp && sudo firewall-cmd --reload
```

### install.sh

| Option | Effect |
|---|---|
| (none) | Install, or upgrade an existing installation, and start the service |
| `--uninstall` | Remove NutHub; keep `/etc/nuthub`, `/var/lib/nuthub` and the `nuthub` account |
| `--uninstall --purge` | Also delete the configuration, the data, `/var/cache/nuthub` and the `nuthub` account and group |
| `--help` | Print the usage |

It must run as root and needs systemd, `useradd` / `groupadd` and `install`. When you install, it:

1. creates the system group and account `nuthub` (home `/var/lib/nuthub`, not created; no login shell);
2. stops the service if it is running;
3. installs the executable as `/opt/nuthub/nuthub`, with `LICENSE` and `README.md`;
4. creates `/etc/nuthub`, `/var/lib/nuthub` and `/var/cache/nuthub` (mode 0750, owned by `nuthub`) and gives any
   file already in them to `nuthub`, for example files left by running NutHub as root;
5. installs the systemd unit, with the group that owns serial ports on this system (`dialout`, or `uucp` on Arch
   and openSUSE);
6. installs the udev rule for USB UPSes and applies it to the devices already connected;
7. installs the polkit rule that lets `nuthub` power the machine off, or warns that polkit was not found;
8. enables and starts the service, and waits up to 15 seconds for the first administrator password.

`nuthub service install` (as root) sets up the same account, unit and rules for an executable that is already in
place, without copying it. It refuses executables under `/home`, `/root`, `/run/user`, `/tmp` or `/var/tmp`, which
the service cannot see, and data or configuration directories whose name does not contain `nuthub`, because it gives
them to the `nuthub` account.

### Files

| Path | Content |
|---|---|
| `/opt/nuthub/nuthub` | The program |
| `/etc/nuthub/nuthub.json` | The configuration (and `nuthub.json.bak`, the version before the last change) |
| `/var/lib/nuthub/` | Data: `nuthub.db`, `secret.key`, `keys/`, `certs/`, `logs/`, `notifications/`, `initial-admin-password.txt` |
| `/var/cache/nuthub/` | Native libraries the executable unpacks when it starts |
| `/etc/systemd/system/nuthub.service` | The systemd unit |
| `/etc/udev/rules.d/99-nuthub-ups.rules` | Access to USB UPSes |
| `/etc/polkit-1/rules.d/50-nuthub-poweroff.rules` | Permission to power off (polkit with JavaScript rules) |
| `/etc/polkit-1/localauthority/50-local.d/50-nuthub-poweroff.pkla` | The same for polkit 0.105 (for example Ubuntu 22.04), when that folder exists |

NutHub keeps the files that hold secrets readable by `nuthub` only, so use `sudo` to read them.

### The systemd unit

The service runs `/opt/nuthub/nuthub run` as `nuthub`, with `NUTHUB_DATA_DIR=/var/lib/nuthub` and
`NUTHUB_CONFIG=/etc/nuthub/nuthub.json`. It is a `Type=notify` unit: `systemctl start` returns once NutHub reports
that everything has started. It restarts 5 seconds after a failure and gets 30 seconds to stop cleanly.

```bash
systemctl status nuthub
sudo systemctl restart nuthub
journalctl -u nuthub -f          # the log; also in /var/lib/nuthub/logs and in Settings > Logs
```

The unit is hardened:

| Setting | Effect |
|---|---|
| `User=nuthub`, `NoNewPrivileges=yes`, empty `CapabilityBoundingSet=` and `AmbientCapabilities=` | No root, no capabilities, no `sudo` or setuid programs. The ports are above 1024 and need none. |
| `ProtectSystem=strict`, `ReadWritePaths=/var/lib/nuthub /etc/nuthub` | The file system is read-only except for NutHub's own directories (and its cache directory). |
| `ProtectHome=yes`, `PrivateTmp=yes` | `/home`, `/root` and `/run/user` are hidden; `/tmp` is private. |
| `DevicePolicy=closed` with `DeviceAllow=` | Only the device nodes UPS drivers use: `hidraw`, `ttyS`, `ttyUSB`, `ttyACM` and `usb_device`, all read-write (the USB device node carries the string descriptors and the reset of a UPS that stops answering). |
| `RestrictAddressFamilies=AF_UNIX AF_INET AF_INET6 AF_NETLINK` | IP networking and local sockets (the shutdown goes through D-Bus). |
| `ProtectKernelTunables`, `ProtectKernelModules`, `ProtectKernelLogs`, `ProtectControlGroups`, `ProtectClock`, `ProtectHostname`, `RestrictNamespaces`, `RestrictRealtime`, `RestrictSUIDSGID`, `LockPersonality`, `SystemCallArchitectures=native` | The usual kernel and system protections. |
| `UMask=0027` | New files are not readable by other users. |

In practice:

- Notification commands, a custom shutdown command and the `.dev` file of the `simulated` driver run or are read
  inside the same sandbox, as `nuthub`: keep them outside `/home` and `/tmp` (for example in `/usr/local/bin`), and do
  not rely on `sudo` in scripts.
- A serial port of another kind, such as a Raspberry Pi UART, needs its own `DeviceAllow=char-<name> rw` line in a
  drop-in (see the next point); `grep tty /proc/devices` shows the names.
- Make local changes with `sudo systemctl edit nuthub.service`. The drop-in it creates in
  `/etc/systemd/system/nuthub.service.d/` survives upgrades, while `install.sh` replaces the unit itself. For
  example, to get debug logs:

  ```ini
  [Service]
  Environment=Logging__LogLevel__Default=Debug
  ```

### USB UPSes: the udev rule

`99-nuthub-ups.rules` gives the `nuthub` group read-write access to the `hidraw` nodes of USB UPSes: by vendor id for
vendors that only make power equipment (APC, Eaton/MGE, CyberPower, Tripp Lite, Liebert, PowerCOM...) and by
vendor and product id for the others (HP, Dell, Belkin, the Megatec USB-serial bridges...). `ls -l /dev/hidraw*`
shows the group `nuthub` on a UPS it recognises.

For a UPS that is not in the list, add your own rule in a separate file (`install.sh` overwrites
`99-nuthub-ups.rules` on every upgrade). `lsusb` shows the vendor and product ids, for example `ID 1234:5678`:

```bash
echo 'SUBSYSTEM=="hidraw", ATTRS{idVendor}=="1234", ATTRS{idProduct}=="5678", MODE="0660", GROUP="nuthub"' \
    | sudo tee /etc/udev/rules.d/99-nuthub-local.rules
sudo udevadm control --reload-rules && sudo udevadm trigger --subsystem-match=hidraw
```

To check what the drivers see, `sudo /opt/nuthub/nuthub devices` lists the USB and serial UPSes found. It runs as
root; **Search for devices** in the UPS form of the web panel searches with the permissions of the service.

### Serial UPSes

The unit adds the serial port group (`dialout`, or `uucp`) to the service, so the `nuthub` account does not need to
be a member of it. Prefer the stable names in `/dev/serial/by-id/` to `/dev/ttyUSB0`, whose number can change.

### Host protection and polkit

When host protection shuts the machine down, NutHub runs `systemctl poweroff`, which asks logind, which asks polkit
whether `nuthub` may do it. `50-nuthub-poweroff.rules` (or the `.pkla` file on polkit 0.105) lets the `nuthub`
account power the machine off, also when other users are logged in or a program holds an inhibitor lock, and reboot
it. Without the rule the shutdown is refused.

**Dry run (no real shutdown)** in **Settings > Host protection** runs the sequence without the shutdown command and
the UPS power-off, so it does not test this rule. It does not set the forced shutdown on the UPS either, so the NUT
clients connected to it are not affected.

### Running as root

Where polkit is not available, the service can run as root instead, which needs no polkit rule, udev rule or serial
group, at the cost of the isolation of a dedicated account. The packaged unit removes every
capability, even from root, so give back the two this needs, reading files that belong to `nuthub` and shutting the
machine down:

```bash
sudo systemctl edit nuthub.service
```

```ini
[Service]
User=root
CapabilityBoundingSet=CAP_DAC_OVERRIDE CAP_SYS_BOOT
```

Then `sudo systemctl restart nuthub`. The rest of the hardening still applies. To go back, remove those lines with
`sudo systemctl edit nuthub.service`, then run `sudo ./install.sh` again, which gives the files back to `nuthub`.

### Uninstalling

From the extracted folder:

```bash
sudo ./install.sh --uninstall            # keeps /etc/nuthub, /var/lib/nuthub and the nuthub account
sudo ./install.sh --uninstall --purge    # removes them too
```

It stops and disables the service and removes the unit, the udev and polkit rules and `/opt/nuthub`. A drop-in you
made with `systemctl edit` stays in `/etc/systemd/system/nuthub.service.d/`; delete that folder if you no longer
want it.

## Docker

### Starting

The image is built from the source code:

```bash
git clone https://github.com/kriseniprak/NutHub.git
cd NutHub/packaging/docker
docker compose up -d --build
docker compose logs nuthub
```

To build a specific release, clone its tag: `git clone --branch v<version> https://github.com/kriseniprak/NutHub.git`.
Then open `http://<docker host>:8493/`; the password for the first sign-in is shown by:

```bash
docker exec nuthub cat /var/lib/nuthub/initial-admin-password.txt
```

### The compose file

`packaging/docker/docker-compose.yml`:

| Setting | Value |
|---|---|
| `build` | The repository root as context, `packaging/docker/Dockerfile` |
| `image`, `container_name` | `nuthub:latest`, `nuthub` |
| `restart` | `unless-stopped` |
| `stop_grace_period` | `30s`: NutHub stops on SIGTERM and gives its drivers 20 seconds to close their devices |
| `ports` | `3493:3493` (NUT) and `8493:8493` (web panel); `8494:8494` (HTTPS) is commented out |
| `volumes` | `nuthub-data` on `/var/lib/nuthub` (database, logs, keys, certificates), `nuthub-config` on `/etc/nuthub` (`nuthub.json`) |
| `environment` | `TZ: "UTC"`; a commented `Logging__LogLevel__Default: "Debug"` |

- **TZ** sets the time zone of the log files and of the server times shown in the panel. Put your own, for
  example `TZ: "Europe/Rome"`.
- **Volumes** keep the configuration and data when the container is recreated. Compose names them after the
  project, which is the folder name by default (`docker_nuthub-data`, `docker_nuthub-config`; see
  `docker volume ls`). `docker compose down` keeps them; `docker compose down -v` deletes them with all the data.
- **Logs** go to the container output (`docker compose logs -f nuthub`) and to rolling files in the `logs` folder of
  the data volume.
- **Network drivers** (`snmp`, `nut`, `apcupsd`) work without any change.
- **Host protection** cannot work in a container: a container cannot shut down its host. Install NutHub on the host
  for that, or run `upsmon` on the machines to protect.

### The image

The image is based on `mcr.microsoft.com/dotnet/runtime-deps` and holds NutHub in `/app`, with `libudev1` for the
optional HidSharp HID layer (used only with `NUTHUB_HID_BACKEND=hidsharp`, see
[Troubleshooting](troubleshooting.md#usb-on-linux)). It runs as the unprivileged `app` account of the .NET images
(user id 1654; `docker exec nuthub id` shows it), which owns `/var/lib/nuthub` and `/etc/nuthub`. `NUTHUB_DATA_DIR`
and `NUTHUB_CONFIG` point there, so `/app/nuthub` commands run with `docker exec` use the same files as the server.

The health check runs `/app/nuthub healthcheck` every 30 seconds (10-second timeout, 30-second start period, 3
retries): it asks the local web panel for `/api/auth/state`, or sends `VER` to the NUT port when the panel is
disabled. `docker ps` shows the result.

Without Compose, the equivalent is:

```bash
docker build -f packaging/docker/Dockerfile -t nuthub .          # from the repository root
docker run -d --name nuthub --restart unless-stopped --stop-timeout 30 \
    -p 3493:3493 -p 8493:8493 -e TZ=Europe/Rome \
    -v nuthub-data:/var/lib/nuthub -v nuthub-config:/etc/nuthub nuthub
```

### USB and serial UPSes

NutHub talks to USB UPSes through the kernel's `hidraw` interface directly: it needs neither libudev nor `/run/udev`
(the udev database of the host). `/sys`, which Docker mounts read-only anyway, is optional; it adds the USB
manufacturer, product and serial strings.

The container needs the device node and the right to open it as the `app` account. For a USB UPS (and for a
`megatec` UPS on a USB cable, which also uses `hidraw`), find its `hidraw` node on the host (`ls -l /dev/hidraw*`)
and its group id (`stat -c %g /dev/hidraw0`), then uncomment and adapt in the compose file. The group must have
read-write access (`crw-rw----`); a node that only root can open (`crw------- root root`) needs a udev rule first, as
described below.

```yaml
    devices:
      - /dev/hidraw0:/dev/hidraw0
    group_add:
      - "1001"          # the group id of the device on the host
```

Keep a `hidraw` name inside the container: NutHub looks at `/dev/hidraw*`, so a node mapped under another name
(`- /dev/hidraw3:/dev/ups`) is not found. Mapping it to a different number (`- /dev/hidraw3:/dev/hidraw0`) works, but
`/sys` then describes the host's `hidraw0`, another device: NutHub notices the mismatch and leaves the USB
manufacturer, product and serial strings out rather than showing those of the wrong device.

On a Linux host you can instead install `packaging/linux/99-nuthub-ups.rules` with `GROUP` set to a group of your
choice and add that group's id. The `hidraw` number can change when the UPS is reconnected; to follow it, pass `/dev`
and allow the `hidraw` major number (`grep hidraw /proc/devices` on the host) and the USB devices (major 189):

```yaml
    volumes:
      - /dev:/dev
    device_cgroup_rules:
      - "c 243:* rmw"   # replace 243 with the hidraw major number of the host
      - "c 189:* rmw"   # the USB devices: string descriptors, and the reset below
```

**A UPS that stops answering.** Some models, and some USB controllers, hang while the UPS stays plugged in. NutHub
then asks the kernel to re-enumerate the device (the `usbReset` option, on by default) and reopens it, which is what
`usbreset` does by hand. It needs `/dev/bus/usb` inside the container, so give it the USB devices as above; with a
fixed `devices:` entry the reset still works, but if the device comes back under another `hidraw` number the
container will not see it until it is recreated. In the log the driver writes which node it reset, or why it could
not.

For a serial UPS (`megatec`, `apcsmart`):

```yaml
    devices:
      - /dev/ttyUSB0:/dev/ttyUSB0
    group_add:
      - "dialout"       # or the group id of the device on the host (stat -c %g /dev/ttyUSB0)
```

After changing the file, `docker compose up -d` recreates the container, and `docker exec nuthub /app/nuthub devices`
lists what the drivers find inside it. The USB HID and serial drivers have been tested against simulators and device
recordings rather than real hardware, in a container or elsewhere.

## On a NAS

NutHub runs in the container app of a NAS, such as UGREEN UGOS Pro, Synology DSM or QNAP QTS, with the same image;
its Docker layout has been run on a UGREEN DXP4800 Plus. Three things differ from an ordinary Docker host: the
NUT port is often taken, the NAS may already manage the UPS, and folders of the NAS are often used instead of
volumes. The commands below run over SSH; prefix them with `sudo` if your NAS user cannot use Docker directly.

### Getting the image onto the NAS

Build it on the NAS: copy the repository there (`git clone`, or the source archive of a release) and run
`docker build -f packaging/docker/Dockerfile -t nuthub:latest .` in it. The Dockerfile needs BuildKit, the default
builder since Docker 23; with an older Docker, put `DOCKER_BUILDKIT=1` before the command. Or build it on another
machine with the same architecture (most NAS units are x86-64; `uname -m` on the NAS tells) and copy it:

```bash
docker build -f packaging/docker/Dockerfile -t nuthub:latest .     # on the build machine, repository root
docker save nuthub:latest | gzip > nuthub-image.tar.gz
docker load -i nuthub-image.tar.gz                                 # on the NAS
```

To build on a machine of another architecture, use Docker Buildx with one platform and `--load`, for example
`docker buildx build -f packaging/docker/Dockerfile --platform linux/amd64 -t nuthub:latest --load .`. The last
steps of the image run for the target architecture, which needs QEMU emulation on the build machine (Docker Desktop
includes it).

### A compose file for a NAS

This version uses the image built above, publishes the NUT port as 13493 and keeps the files in folders of the NAS.
Create those folders first ([Volumes, folders and permissions](#volumes-folders-and-permissions)), save the file as
`docker-compose.yml` in a folder of its own and start it there with `docker compose up -d`, or create a project with
it in the container app of the NAS. Adjust the paths to your volume:

```yaml
services:
  nuthub:
    image: nuthub:latest
    container_name: nuthub
    restart: unless-stopped
    stop_grace_period: 30s
    ports:
      - "13493:3493"    # NUT protocol, on port 13493 of the NAS
      - "8493:8493"     # web panel
    volumes:
      - /volume1/docker/nuthub/data:/var/lib/nuthub
      - /volume1/docker/nuthub/config:/etc/nuthub
    environment:
      TZ: "Europe/Rome"
```

### Port 3493 is often taken

UGREEN UGOS Pro, Synology DSM and QNAP QTS run their own NUT server on port 3493 when their UPS support is on and the
UPS is connected to their USB port. A container that publishes 3493 on such a NAS does not start at all, web panel
included, because the port is in use. Publish `13493:3493` as above, or turn off the network UPS server of the NAS.

NUT clients then connect to port 13493 of the NAS. In `upsmon.conf`:

```
MONITOR rack@192.168.1.20:13493 1 monuser <password> secondary
```

In NutDesk and Home Assistant, set the port to 13493. A Synology acting as a NUT client always uses port 3493
([below](#letting-a-nas-shut-down-from-nuthub)).

### Reading the UPS of the NAS

When the NAS manages a USB UPS, keep it that way: the NAS shuts itself down from it, and NutHub reads the UPS from
the NAS's NUT server with the `nut` driver. In **Settings > UPS devices**, add a UPS with:

| Field | Value |
|---|---|
| Driver | NUT server (upsd) |
| Server | The IP address of the NAS (not `localhost`, which inside the container is the container itself) |
| Port | 3493 |
| UPS name on that server | Usually `ups0` on UGREEN; `upsc -l <NAS address>` lists the names |
| Username, Password | Empty: reading needs no account |

This is how NutHub reads the UPS of a UGREEN NAS running UGOS Pro. Synology DSM and QNAP QTS only answer the
addresses listed in their network UPS server settings, so add the address NutHub connects from; a container on the
NAS itself may connect from the address of its Docker network rather than from the NAS's own address.
[Connecting clients](clients.md#nas-units) describes both ways of combining a NAS with NutHub in detail.

To use the USB UPS directly in NutHub instead, turn the UPS support of the NAS off first (it usually keeps the device
busy) and pass the device into the container as described in [USB and serial UPSes](#usb-and-serial-upses). The
`hidraw` node is all the container needs: NutHub does not use libudev or the NAS's `/run/udev`.

### Letting a NAS shut down from NutHub

The container cannot shut down the NAS. A NAS shuts down from NutHub's data by being a NUT client of NutHub, with the
names these systems expect:

| NAS | UPS setting on the NAS | UPS name in NutHub | NUT account in NutHub (secondary) |
|---|---|---|---|
| Synology DSM | UPS type "Synology UPS server", pointing at the NutHub address | `ups` | `monuser` / `secret` |
| QNAP QTS | Network UPS slave, pointing at the NutHub address | `qnapups` | `admin` / `123456` |

Synology DSM connects to port 3493 only, so NutHub must be reachable on 3493 at that address: on another machine, or
in a container that publishes 3493 on a NAS whose own network UPS server is off.

The two passwords are shorter than the 8 characters that **Settings > NUT accounts** and `nuthub nut-user` require.
Create these accounts with **Settings > Import from NUT** instead: paste only this as `upsd.users`, then press
**Preview** and **Apply the import**:

```
[monuser]
    password = secret
    upsmon secondary

[admin]
    password = 123456
    upsmon secondary
```

This client mode of Synology and QNAP has not been verified against NutHub yet. On UGREEN, keep UGOS managing its own
USB UPS as described above. The steps on the NAS, and what to expect when NutHub runs in a container on that same
NAS, are in [Connecting clients](clients.md#b-nuthub-owns-the-ups-the-nas-is-a-client).

### Volumes, folders and permissions

- **Named volumes** (the default compose file) need no preparation: Docker gives a new volume the owner of the
  image's directories. The files live in Docker's own storage, not in a shared folder.
- **Folders of the NAS** (bind mounts, as in the compose file above) are easier to see and back up, but the
  container writes as user id 1654, which must own them:

  ```bash
  sudo mkdir -p /volume1/docker/nuthub/data /volume1/docker/nuthub/config
  sudo chown -R 1654:1654 /volume1/docker/nuthub/data /volume1/docker/nuthub/config
  ```

  Otherwise the container exits at once (and keeps restarting) and its log says "Cannot create or access
  /var/lib/nuthub".
- NutHub makes its files readable by its own account only, so the file manager of the NAS may not open them; read
  them with `docker exec` or with `sudo` over SSH.
- Adding `user: "0:0"` to the service runs the container as root: it can then write to any folder and open device
  nodes without `group_add`, but gives up the unprivileged account.

### The password on a NAS

The first sign-in password is in `initial-admin-password.txt` in the data folder. Read it, or set your own password
without it (at least 8 characters):

```bash
docker exec nuthub cat /var/lib/nuthub/initial-admin-password.txt
docker exec nuthub /app/nuthub passwd admin --password 'a-long-password'
docker exec -it nuthub /app/nuthub passwd admin      # asks for it, so it stays out of the shell history
```

## First sign-in and lost passwords

### First sign-in

On its first start NutHub creates the web account `admin` with a random password and writes both to
`initial-admin-password.txt` in the data directory. The installers print it; `nuthub run` in a terminal shows it in a
box; the log only says where the file is.

| Installation | Read the password with |
|---|---|
| Windows (elevated PowerShell) | `Get-Content "$env:ProgramData\NutHub\initial-admin-password.txt"` |
| Linux | `sudo cat /var/lib/nuthub/initial-admin-password.txt` |
| Docker | `docker exec nuthub cat /var/lib/nuthub/initial-admin-password.txt` |

Open `http://<server>:8493/` (`http://localhost:8493/` on the machine itself) and sign in as `admin`. NutHub then
asks you to choose a new password of at least 8 characters. Delete the file afterwards. Later you change your
password from the user menu, **Account and password**.

NutHub creates such an account whenever the configuration has no enabled administrator when it starts: on the first
start, or after every administrator was removed or disabled by editing `nuthub.json`. It is named `admin`, or
`admin` followed by three digits when an account named `admin` already exists.

### Resetting a forgotten password

`nuthub passwd` sets the password of a web account from the command line, also while NutHub runs (it applies the
change within a few seconds). Run it where it uses the server's files:

| Installation | Command |
|---|---|
| Windows | `& "C:\Program Files\NutHub\nuthub.exe" passwd admin`, from an elevated PowerShell |
| Linux | `sudo /opt/nuthub/nuthub passwd admin` |
| Docker | `docker exec -it nuthub /app/nuthub passwd admin` |

On Linux, `sudo` matters: as root the command uses `/etc/nuthub` and `/var/lib/nuthub` and gives the changed files
back to `nuthub`; as an ordinary user it would use a separate configuration in your home directory.

| Option | Effect |
|---|---|
| (none) | Asks for the new password twice (or reads it from standard input) |
| `--password P` | Sets `P` (at least 8 characters) |
| `--generate` | Generates a random password and prints it |
| `--must-change` | Asks for a new password at the next sign-in |
| `--data-dir DIR`, `--config FILE` | For an installation with its own data directory or configuration file |

The command ends the open sessions of that account and deletes `initial-admin-password.txt` if it names that
account. If the name is wrong, it lists the web accounts that exist. After 10 failed sign-ins in 5 minutes from one
address or for one user name, the panel refuses further attempts for a while; wait a few minutes.

## Upgrading

Upgrading replaces the program and keeps the configuration and data. NutHub updates its database on the first start
of the new version. Take a [backup](#backup-and-restore) first: to go back to an older version you restore it. An
older NutHub refuses a configuration file in a newer format, and moves a database in a newer format aside
(`nuthub.db.newer-...`) to start an empty one.

| Installation | Upgrade |
|---|---|
| Windows | Extract the new zip and run `install.ps1` from it, from an elevated PowerShell. It stops the service, replaces `nuthub.exe`, updates the service and starts it. The firewall rules stay; `-AddFirewallRules` is not needed again. |
| Linux | Extract the new archive and run `sudo ./install.sh` from it. It stops the service, replaces `/opt/nuthub/nuthub`, the unit and the udev and polkit rules, and starts it again. Your `systemctl edit` drop-ins stay. |
| Docker | Update the source and rebuild: `git pull` in the repository (or `git checkout v<version>`), then `docker compose up -d --build` in `packaging/docker`. The volumes stay; `docker image prune` removes the old image afterwards. |

On a NAS that uses the [compose file for a NAS](#a-compose-file-for-a-nas), build (or build and load) the new image
as [above](#getting-the-image-onto-the-nas), then `docker compose up -d` recreates the container with it. If you
push the image to a registry of your own, set that image in the compose file and upgrade with
`docker compose pull && docker compose up -d`.

**Settings > System** shows the version that runs; `nuthub version` prints the version of the executable.

## Backup and restore

### What to keep

| File | Content | In a backup |
|---|---|---|
| `nuthub.json` | The configuration: UPSes, NUT and web accounts (password hashes), every setting | Yes |
| `secret.key` | The key that decrypts the secrets in `nuthub.json`: the SMTP password, webhook header values, SNMP communities and passwords, the `nut` driver password, certificate passwords | Yes, always with `nuthub.json`: without it those values must be typed again |
| `nuthub.db` | The event log and the history | To keep them |
| `keys/` | Keys of the web panel sessions | Optional: without it everyone signs in again |
| `certs/` | The self-signed certificates for HTTPS and STARTTLS | Optional: new ones are generated, and clients that trusted the old ones see a different certificate |
| `notifications/` | Email messages waiting to be sent | Optional |
| `logs/` | Log files | No |

Also keep your own certificate files, notification scripts and anything else the configuration points to. The backup
contains password hashes and the secret key: store it as carefully as the server.

**Settings > System > Download configuration** saves the configuration without passwords, hashes and secrets. It is
a record of the settings and safe to share, but not a backup to restore from.

Stop NutHub for the backup, so the database is complete in `nuthub.db`.

### Windows

```powershell
Stop-Service NutHub
robocopy "$env:ProgramData\NutHub" "D:\Backup\NutHub" /E
Start-Service NutHub
```

To restore, install NutHub (on a new machine), then:

```powershell
Stop-Service NutHub
robocopy "D:\Backup\NutHub" "$env:ProgramData\NutHub" /E
Start-Service NutHub
```

On another machine, add `/XD keys` to the restore command: Windows encrypts the session keys for the machine that
created them.

### Linux

```bash
sudo systemctl stop nuthub
sudo tar czf nuthub-backup.tar.gz -C / etc/nuthub var/lib/nuthub
sudo systemctl start nuthub
```

To restore, install NutHub (on a new machine), then:

```bash
sudo systemctl stop nuthub
sudo tar xzf nuthub-backup.tar.gz -C /
sudo /opt/nuthub/nuthub check-config
sudo chown -R nuthub:nuthub /etc/nuthub /var/lib/nuthub
sudo systemctl start nuthub
```

`check-config` validates the configuration and checks that every encrypted value can be decrypted with the restored
`secret.key`.

### Docker

`--volumes-from` mounts the volumes of the `nuthub` container, whatever their names:

```bash
docker stop nuthub
docker run --rm --volumes-from nuthub -v "$PWD":/backup busybox \
    tar czf /backup/nuthub-backup.tar.gz /var/lib/nuthub /etc/nuthub
docker start nuthub
```

To restore into the stopped container:

```bash
docker stop nuthub
docker run --rm --volumes-from nuthub -v "$PWD":/backup busybox sh -c '
    rm -rf /var/lib/nuthub/* /etc/nuthub/* &&
    tar xzf /backup/nuthub-backup.tar.gz -C / &&
    chown -R 1654:1654 /var/lib/nuthub /etc/nuthub'
docker start nuthub
```

With folders of a NAS, stop the container and copy the two folders.

### Moving to another machine

Copy `nuthub.json` and `secret.key` (and `nuthub.db` for the history) into the new installation, in the paths shown
above, whatever the operating system; on Linux, give them to `nuthub` as in the restore commands. Then check what
depends on the old machine: serial port names (`COM3` or `/dev/ttyUSB0`), file paths, and listen addresses. Delete
the `initial-admin-password.txt` the new installation created: its password no longer applies.

## Running without installing

To try NutHub, run it in a terminal from the extracted folder, with its files in a folder of your choice:

```powershell
.\nuthub.exe run --data-dir .\data       # Windows
```

```bash
./nuthub run --data-dir ./data           # Linux
```

On the first start in that folder it prints the first sign-in password in a box; it serves the panel on
`http://localhost:8493/`, and Ctrl+C stops it. With `--data-dir` alone, the configuration is `nuthub.json` in that
folder; `--config FILE` puts it elsewhere, and the environment variables `NUTHUB_DATA_DIR` and `NUTHUB_CONFIG` do the
same as the two options.

Without `--data-dir`, NutHub uses the files of the service: `%ProgramData%\NutHub` on Windows (restricted to
administrators once the service has created it), `/var/lib/nuthub` and `/etc/nuthub/nuthub.json` for root on Linux.
An ordinary Linux user gets `~/.local/share/nuthub` and `~/.config/nuthub/nuthub.json` instead.

On a test machine, set the environment variable `NUTHUB_DISABLE_SHUTDOWN=1`: host protection then only logs the
shutdown and the UPS power-off, whatever the settings say.

## Building from source

You need the .NET 10 SDK (10.0.100 or a later feature band, as `global.json` asks).

```bash
dotnet build NutHub.sln
dotnet test NutHub.sln
dotnet run --project src/NutHub -- run --data-dir ./data
```

A Debug build never runs the shutdown command or the UPS power-off; use a Release build to try host protection for
real.

A self-contained, single-file executable for one platform:

```bash
dotnet publish src/NutHub/NutHub.csproj -c Release -r linux-arm64 -o publish/linux-arm64
```

The runtimes are `win-x64`, `linux-x64`, `linux-arm64` and `linux-arm`; `-p:PublishSingleFile=false` gives a plain
folder instead, as the Docker image uses.

The release archives, with a `.sha256` file each, come from the packaging scripts. They write to `dist/` and take
the version from `Directory.Build.props`:

```bash
tools/package-release.sh                                    # all four runtimes
tools/package-release.sh --rid linux-x64 --rid linux-arm64  # some of them; --output DIR for another folder
```

```powershell
pwsh tools/package-release.ps1 -Runtimes win-x64,linux-x64
```

The shell script needs bash, tar, and zip or python3. Linux archives made with Windows PowerShell 5.1 lose the
executable bits; run their installer with `sudo sh install.sh`.

The Docker image, from the repository root:

```bash
docker build -f packaging/docker/Dockerfile -t nuthub .
docker buildx build -f packaging/docker/Dockerfile --platform linux/amd64,linux/arm64,linux/arm/v7 -t nuthub .
```

A multi-architecture build needs somewhere to put the result: add `--push` with an image name of your registry, or
build one platform at a time with `--load` to get it in the local image store.

`tools/fake-serial-ups` and `tools/fake-snmp-agent` (Python 3.8 or later) simulate serial and SNMP UPSes for tests
without hardware. The design is described in [ARCHITECTURE.md](ARCHITECTURE.md).
