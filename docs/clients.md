# Connecting clients

NutHub speaks the network protocol of Network UPS Tools, so the machines it protects connect to it exactly as they
would to `upsd`: `upsmon` on Linux, NutDesk or WinNUT on Windows, Synology and QNAP NAS units, Home Assistant, and
the command-line tools `upsc`, `upscmd` and `upsrw`. This page explains what a client can do with and without an
account, how to set up each kind of client, how NAS units fit in (UGREEN ones included, whose UPS NutHub reads from
the NAS), how to chain several NutHub servers, and how to protect the machine NutHub itself runs on. The examples use
a NutHub server at `192.168.1.10` that serves a UPS named `rack`.

## Contents

- [Reading and logging in](#reading-and-logging-in)
- [NUT accounts](#nut-accounts)
- [Linux machines with upsmon](#linux-machines-with-upsmon)
- [Windows machines: NutDesk and WinNUT](#windows-machines-nutdesk-and-winnut)
- [NAS units](#nas-units)
- [Home Assistant](#home-assistant)
- [upsc, upscmd and upsrw](#upsc-upscmd-and-upsrw)
- [Encrypting connections with STARTTLS](#encrypting-connections-with-starttls)
- [Several NutHub servers](#several-nuthub-servers)
- [Protecting the NutHub machine itself](#protecting-the-nuthub-machine-itself)
- [Testing a client safely](#testing-a-client-safely)
- [When a client does not connect](#when-a-client-does-not-connect)

## Reading and logging in

NutHub handles clients the way `upsd` does.

**Reading needs no account.** Any client that reaches TCP port 3493 can list the UPSes and read their variables,
writable variables and instant commands. That is all `upsc`, Home Assistant or a monitoring tool need. What limits
readers is your firewall and **Allowed networks** under **Settings > NUT server**: empty (the default) lets every
address in, otherwise only the listed addresses and CIDR networks (`192.168.1.0/24`, `10.0.0.5`, `::1`) can connect.

**Everything else needs a NUT account.** The client sends `USERNAME` and `PASSWORD`, then a command that needs
rights. As in `upsd`, the password is checked when that command arrives, so a wrong password shows up as
`ERR ACCESS-DENIED` on the command itself.

| What the client does | Protocol command | What the account needs |
|---|---|---|
| Registers as a machine powered by the UPS (`upsmon`, NutDesk with a login, the `nut` driver of another NutHub with **Log in as a secondary**) | `LOGIN` | Monitor role **Secondary** or **Primary** |
| Declares itself the primary `upsmon` | `PRIMARY` (NUT 2.8) or `MASTER` (NUT 2.7) | Monitor role **Primary** |
| Sets a forced shutdown | `FSD` | Monitor role **Primary**, or the **FSD** action |
| Changes a writable variable (`upsrw -s`) | `SET VAR` | The **SET** action |
| Runs an instant command (`upscmd`) | `INSTCMD` | **All commands**, or that command among the chosen ones |

`LOGIN` is what makes a machine count. Every connection logged in to a UPS is included in its `NUMLOGINS`, and that
number is how a primary `upsmon`, or NutHub's own host protection, knows that machines are still running on the UPS
and waits for them to shut down before the last one goes down. A client that only reads is never waited for.

You can see who is connected in two places: the **NUT clients** tab of a UPS page, shown while at least one client
is logged in, lists the connections logged in to that UPS (address, account, role, TLS, connection time, activity),
and **Connected NUT clients** under **Settings > NUT server** lists every connection, including those that only
read, with a **Disconnect** button. Logins and logouts are also recorded under **Events**; they are informational
events, hidden by default, so choose **All, including sign-ins and details** in the severity filter to see them.

## NUT accounts

NUT accounts are the equivalent of `upsd.users`. Create them under **Settings > NUT accounts > Add account**:

| Field | Meaning |
|---|---|
| **Name** | The user name the client sends. Up to 64 letters, digits, `.`, `_`, `-` and `@`, no spaces. Case matters. |
| **Password** | At least 8 characters. NutHub keeps only a hash. |
| **Monitor role** | **No monitoring** (cannot `LOGIN`), **Secondary** (the default for a new account) or **Primary**. |
| **Actions** | **SET** (change writable variables) and **FSD** (set a forced shutdown). |
| **Instant commands** | **None**, **All commands**, or **Only the chosen commands**. |
| **Allowed UPSes** | None chosen: the account works with every UPS. Otherwise only with the chosen ones (`upsd` has no such limit). |

Several machines can share one account, as with `upsd.users`. A common set:

- `upsmon`, **Secondary**: every machine that shuts down when the UPS runs out.
- `upsprimary`, **Primary**: at most one machine per UPS (see [below](#the-primary-and-the-forced-shutdown-sequence));
  not needed when NutHub's host protection plays that role.
- `operator`, **No monitoring**, with **SET** and the instant commands you want: people and scripts using `upscmd`
  and `upsrw`, and Home Assistant.

The same accounts can be managed from the command line, also while NutHub runs:

```bash
sudo /opt/nuthub/nuthub nut-user add upsmon --monitor secondary
sudo /opt/nuthub/nuthub nut-user add operator --monitor none --actions SET --instcmds ALL
sudo /opt/nuthub/nuthub nut-user list
```

`--ups rack,sim` limits an account to those UPSes. Without `--password` the password is asked twice on the console
(or read from standard input); `--generate` creates a random one and prints it. On Windows run
`& "C:\Program Files\NutHub\nuthub.exe" nut-user ...` in an administrator PowerShell; with Docker,
`docker exec -it nuthub /app/nuthub nut-user ...`.

### Accounts with short fixed passwords

Synology and QNAP units log in with fixed accounts whose passwords (`secret`, `123456`) are shorter than 8 characters,
so the panel and `nuthub nut-user` refuse them. **Settings > Import from NUT** accepts them: paste an `upsd.users`
section in the **upsd.users** box, leave **ups.conf** empty, press **Preview**, then **Apply the import**.

```
[monuser]
  password = secret
  upsmon secondary
```

The import never changes an account that already exists. Afterwards you can open the account under **NUT accounts**
to set **Allowed UPSes**; leaving the password field empty keeps the imported password. These credentials are
public knowledge, so keep such accounts at **Secondary** (which allows `LOGIN` and nothing else) and use
**Allowed networks** if you can.

## Linux machines with upsmon

### A secondary

On Debian or Ubuntu, install the client:

```bash
sudo apt install nut-client
```

Set `MODE=netclient` in `/etc/nut/nut.conf`, then add to `/etc/nut/upsmon.conf` (on Fedora and RHEL the files are in
`/etc/ups`):

```
MONITOR rack@192.168.1.10 1 upsmon <password> secondary
MINSUPPLIES 1
SHUTDOWNCMD "/sbin/shutdown -h +0"
```

The `MONITOR` line names the UPS as `name@host` (add `:port` when NutHub is not on 3493, for example
`rack@192.168.1.10:13493`), the number of this machine's power supplies fed by that UPS, the NUT account, its password
and the role. Restart the client and check that it reads the UPS:

```bash
sudo systemctl restart nut-monitor
upsc rack@192.168.1.10 ups.status
```

Within a few seconds the machine appears on the **NUT clients** tab of the UPS page, with the account and the role
**Secondary**. The **NUT server** settings page also has a folded **Connecting NUT clients** section with a sample
`MONITOR` line for this server, to copy and complete with the account and password.

A secondary shuts its machine down, following `upsmon`'s own rules, when:

- `ups.status` contains `FSD`;
- the UPS is on battery with a low battery (`OB LB`);
- the UPS was last seen on battery and has not been readable for longer than `DEADTIME` (15 s by default): NutHub
  stopped, the network went down, or NutHub answers `DATA-STALE` or `DRIVER-NOT-CONNECTED` because it lost the UPS.

To have every client shut down earlier than the UPS's own low-battery signal, set **Low battery below charge (%)**
or **Low battery below runtime (s)** in the settings of the UPS (**Actions > Edit settings** on its page). NutHub then
adds `LB` itself at that point, and no `upsmon.conf` needs to change.

### The primary and the forced-shutdown sequence

The primary is the machine that shuts down last. It needs an account with the role **Primary**:

```
MONITOR rack@192.168.1.10 1 upsprimary <password> primary
```

When the UPS becomes critical, the sequence runs like this:

1. The UPS is on battery and reaches low battery (`OB LB`), which the primary sees as critical.
2. The primary sends `FSD rack`. NutHub puts `FSD` at the front of `ups.status`, shows **Forced shutdown in progress**
   on the UPS page, and records the event with the account and address that set it.
3. Every secondary sees `FSD`, runs its `SHUTDOWNCMD` and disconnects as its machine goes down. NutHub records each
   logout.
4. The primary waits until it is the only machine still logged in (`NUMLOGINS`), for at most `HOSTSYNC` seconds
   (15 by default), then runs its own `SHUTDOWNCMD`.

Two things differ from a classic NUT installation:

- **The UPS power-off.** There the primary is the machine with the UPS driver, and at the very end of its shutdown NUT
  tells the driver to cut the UPS output. With NutHub the driver runs inside NutHub, so a primary on another machine
  has nothing to cut the power with. If you want the UPS to switch off and restart everything when mains power
  returns, use the power-off of [host protection](#protecting-the-nuthub-machine-itself) on the NutHub machine.
- **FSD does not stay set forever.** `upsd` keeps a forced shutdown until it restarts. NutHub clears it once the UPS
  has been back on line power, without low battery, for **Clear FSD after mains return (s)** under
  **Settings > NUT server** (60 by default; 0 keeps the `upsd` behaviour). An operator can also **Set FSD** or
  **Clear FSD** from the **Actions** menu of the UPS page.

Have at most one primary per UPS. When NutHub's host protection covers the UPS, NutHub plays this role itself (it
sets FSD and waits for the clients to log out), so make every `upsmon` a secondary. Without any primary each
secondary still shuts itself down on `OB LB`; the primary only adds the ordering.

### NUT 2.7: master and slave

NUT 2.7 calls the roles `master` and `slave`, in `upsmon.conf` and in `upsd.users`. NutHub works with both
generations of `upsmon`:

```
MONITOR rack@192.168.1.10 1 upsmon <password> slave
MONITOR rack@192.168.1.10 1 upsprimary <password> master
```

A `slave` needs an account with the role **Secondary**, a `master` one with **Primary**. NutHub answers the `MASTER`
command as `upsd` 2.7 does (`OK MASTER-GRANTED`), and **Import from NUT** reads `upsmon master` and `upsmon slave`.
The whole sequence above, including FSD, has been tested with the `upsmon` of NUT 2.8.5 and 2.7.4.

## Windows machines: NutDesk and WinNUT

[NutDesk](https://github.com/kriseniprak/NutDesk) connects to NutHub like to any NUT server. In NutDesk, open
**Settings** and fill in the **Connection** section:

| Field | Value |
|---|---|
| NUT host | `192.168.1.10` |
| NUT Port | `3493` |
| UPS Name | `rack` |
| Login / Password | A NutHub NUT account with the role **Secondary**, such as `upsmon` |
| Re-establish connection | On |

With a login, NutDesk sends `LOGIN`: it appears on the **NUT clients** tab and NutHub's host protection (or a primary
`upsmon`) waits for it during a forced shutdown. Without a login it only reads, and nobody waits for it.

Under **Settings > Shutdown Options**, turn on **Shutdown on Nut's FSD Signal** so that a forced shutdown reaches the
PC. The battery and runtime limits on the same page work on the values NutHub reports.

WinNUT has the same connection fields (host, port, UPS name, login and password) and is set up the same way. Neither
NutDesk nor WinNUT uses STARTTLS: if they must log in, leave **Require TLS to log in** off (see
[STARTTLS](#encrypting-connections-with-starttls)).

## NAS units

UGREEN UGOS Pro, Synology DSM and QNAP QTS have their own NUT server on port 3493 when their UPS support is on and
the UPS is connected to their USB port. There are two ways to combine a NAS with NutHub:

- **A. The NAS owns the UPS.** The UPS is on the NAS's USB port, the NAS protects itself as usual, and NutHub reads
  the UPS from the NAS's NUT server with the `nut` driver.
- **B. NutHub owns the UPS.** NutHub reads it (SNMP card, another NUT server, or the USB cable goes to the NutHub
  machine), and the NAS is a NUT client of NutHub.

| NAS | UPS name on its own server (A) | As a client of NutHub (B) | What it asks NutHub for (B) |
|---|---|---|---|
| Synology DSM | `ups` | UPS type **Synology UPS server**, with the NutHub address | UPS `ups`, account `monuser` / `secret`, port 3493 only |
| QNAP QTS | `qnapups` | Network UPS slave, with the NutHub address | UPS `qnapups`, account `admin` / `123456` |
| UGREEN UGOS Pro | usually `ups0` | Not covered here: use A | - |

The client-mode settings of layout B are those Synology and QNAP document for their units; they have not been
verified against NutHub on real units yet. Reading a NAS with the `nut` driver (layout A) has been verified with a UGREEN NAS
running UGOS Pro, and NutHub's Docker layout runs on a UGREEN DXP4800 Plus.

### A. The NAS owns the UPS

Keep the NAS's UPS support as it is: the NAS shuts itself down natively, with its own settings. On Synology and QNAP
units, turn on the NAS's network UPS server and add NutHub's address to its list of machines allowed to connect.

In NutHub, **Settings > UPS devices > Add a UPS**, driver **NUT server (upsd)**:

| Option | Value |
|---|---|
| Server | The NAS's IP address |
| Port | `3493` |
| UPS name on that server | `ups` (Synology), `qnapups` (QNAP), usually `ups0` (UGREEN); `upsc -l <NAS address>` lists them |
| Username / Password | Empty: reading needs no account |

Give the UPS any name you like in NutHub (say `nas`); NutHub's clients then monitor `nas@192.168.1.10`.

What to expect during an outage:

- If the NAS sets a forced shutdown on its UPS before going down, NutHub passes `FSD` on in `ups.status`, so NutHub's
  own clients shut down too.
- When the NAS shuts down, its NUT server goes away and NutHub loses the UPS. NutHub's `upsmon` clients last saw the
  UPS on battery, so after `DEADTIME` they treat it as critical and shut down. NutHub's host protection, if it watches
  that UPS, does the same after **Communication lost while on battery (s)**. If the NAS is set to shut down early
  (after some minutes on battery, say), everything on NutHub follows it at that moment.
- **Log in as a secondary** (an advanced option of the driver) makes the NAS count NutHub among the machines it waits
  for. It needs an account of the NAS's NUT server with the upsmon role, and only makes sense when the NutHub machine
  runs on that UPS and protects itself; otherwise leave it off.

**NutHub in a container on that same NAS.** The NAS's NUT server already holds port 3493, so a container that
publishes 3493 does not start (port in use). Publish another port, as the comment in `docker-compose.yml` suggests:

```yaml
    ports:
      - "13493:3493"
      - "8493:8493"
```

Inside NutHub nothing changes: the `nut` driver still reads the NAS at its IP address, port 3493 (not `localhost`,
which inside the container is the container itself). NutHub's clients use port 13493 of the NAS, say
`192.168.1.20`: `MONITOR nas@192.168.1.20:13493 1 upsmon <password> secondary` in `upsmon.conf`, or port `13493` in
NutDesk. Clients that cannot change the port, such as a Synology in client mode, cannot use this NutHub. If the NAS
only lets listed addresses connect, note that a container on the NAS may reach it from the address of its Docker
network rather than from the NAS's own address.

### B. NutHub owns the UPS, the NAS is a client

1. In NutHub, name the UPS the way the NAS expects it: `ups` for a Synology, `qnapups` for a QNAP.
2. Create the account the NAS logs in with, with **Import from NUT** as shown in
   [Accounts with short fixed passwords](#accounts-with-short-fixed-passwords): the `[monuser]` section shown there
   for a Synology, or this one for a QNAP:

   ```
   [admin]
     password = 123456
     upsmon secondary
   ```

   Then limit the account to that UPS with **Allowed UPSes**.
3. Make sure NutHub answers on port 3493 at the address the NAS uses: a Synology asks only for the server's address.
4. On the NAS, choose the client mode (Synology: UPS type **Synology UPS server**; QNAP: network UPS slave) and enter
   NutHub's address.

As a client, the NAS is expected to shut down like any secondary: when NutHub reports the UPS on battery with low
battery, or FSD (or earlier, if its own UPS settings say so). A container cannot shut down its host, so in this layout the NAS always
shuts itself down, as a client.

**NutHub in a container on that same NAS.** This suits a NAS whose UPS NutHub reads itself: through an SNMP card, from
another server, or over USB passed into the container.

- Publish port 3493 as 3493 (`"3493:3493"`, the default of `docker-compose.yml`), because the NAS client cannot use
  another port. This works only while the NAS's own network UPS server is off: the NAS runs it when its UPS support
  is on and the UPS is on its USB port, and then the container does not start.
- On the NAS, enter the NAS's own LAN address as the UPS server.
- If you restrict **Allowed networks**, connections from the NAS to a container on itself may arrive from the Docker
  network's address rather than from the NAS's LAN address.

In this layout the NAS's protection depends on a container it runs itself. A NUT client that loses its server
while the UPS was last seen on line power only reports the lost connection; if the UPS was last seen on battery,
it treats the silence as critical after its dead time and shuts down, as `upsmon` does. A stopped NutHub container
(an image update, a Docker restart, a crash) is such a silence: the NAS cannot tell it from a UPS that went quiet.
Update or restart the container only on mains power, keep `restart: unless-stopped` (as in `docker-compose.yml`),
and run NutHub on a separate machine if that trade-off is not acceptable. Also expect that when the NAS shuts down, NutHub stops with it, and any other machine still monitoring
NutHub on battery shuts down at that moment.

## Home Assistant

The Network UPS Tools integration of Home Assistant speaks the same protocol as `upsc`, so you set it up as for any
NUT server. Add the integration and enter:

| Field | Value |
|---|---|
| Host | `192.168.1.10` |
| Port | `3493` (or the port you published, such as `13493`) |
| Username / Password | Optional: reading needs no account |

When NutHub serves several UPSes, Home Assistant asks which one to monitor; add the integration once per UPS. To let
Home Assistant run instant commands, give it an account with the role **No monitoring** and only the instant
commands it should run. Home Assistant does not log in as `upsmon` and does not shut anything down: protect the
machine it runs on with a client that does.

## upsc, upscmd and upsrw

The NUT command-line tools work against NutHub as against `upsd`. Reading needs no account:

```bash
upsc -l 192.168.1.10                     # the UPS names
upsc rack@192.168.1.10                   # every variable of rack
upsc rack@192.168.1.10 battery.charge    # one variable
upsc -c rack@192.168.1.10                # the clients logged in to rack
upscmd -l rack@192.168.1.10              # the instant commands of rack, with descriptions
upsrw rack@192.168.1.10                  # the writable variables, with their type and allowed values
```

Commands and writes need an account with the matching rights, such as the `operator` account above. Without `-u` and
`-p` the tools ask for them.

```bash
upscmd -u operator -p '<password>' rack@192.168.1.10 beeper.mute
upsrw -s ups.delay.shutdown=120 -u operator -p '<password>' rack@192.168.1.10
```

A command that takes a value, such as `load.off.delay`, takes it after the command name. NutHub answers a command
only after its driver has handled it, so even without tracking the answer is the outcome, not just an
acknowledgement: `OK`, or the usual NUT error (`CMD-NOT-SUPPORTED`, `INVALID-VALUE`, `ACCESS-DENIED`...). Command
and variable names are not case-sensitive.

NUT 2.8 clients can also wait for the result with tracking, `-w`, and set how long to wait with `-t` (seconds):

```bash
upscmd -w -t 30 -u operator -p '<password>' rack@192.168.1.10 test.battery.start.quick
upsrw -w -s ups.delay.shutdown=180 -u operator -p '<password>' rack@192.168.1.10
```

NutHub keeps tracked results for one hour. NUT 2.7 clients have no `-w`; their commands work as shown before.

## Encrypting connections with STARTTLS

The NUT protocol sends passwords in clear text. NutHub supports STARTTLS (TLS 1.2 and 1.3) for the clients that
can use it. Under **Settings > NUT server**:

| Setting | Meaning |
|---|---|
| **TLS enabled** | Clients that support it can switch to an encrypted connection; the others keep working in clear text. |
| **Certificate file (PFX / PKCS#12)** | Your certificate with its private key (a PEM file that contains both also works). Empty: NutHub generates a self-signed certificate for the machine's name and keeps it as `certs/nut-selfsigned.pfx` in the data directory. |
| **Certificate password** | The password of that file, if any. |
| **Require TLS to log in** | `USERNAME` and `PASSWORD` are refused until the client has switched to TLS, so passwords never cross the network in clear text. Clients without TLS can still read. |

On the client side:

- **Another NutHub** (the `nut` driver): turn on **Use TLS (STARTTLS)**. **Certificate check: None** accepts the
  self-signed certificate; **Verify the certificate chain and the host name** needs a certificate issued for the
  name or address entered in **Server**, by an authority the machine running the `nut` driver trusts.
- **upsmon**, when NUT was built with SSL support: `FORCESSL 1` in `upsmon.conf` makes it use an encrypted connection
  and refuse one without TLS. `CERTVERIFY 1` also makes it verify the server's certificate against the certificates
  in `CERTPATH`; use it with a certificate from your own certificate authority, not with the self-signed one.
- **NutDesk and WinNUT** do not use STARTTLS.

The **TLS** column of the NUT clients lists shows which connections are encrypted. Turn on **Require TLS to log in**
only when every client that logs in uses TLS: the others can no longer log in, so a primary and NutHub's host
protection stop waiting for them.

## Several NutHub servers

The `nut` driver reads a UPS from another NUT server, which can be another NutHub. A central NutHub can gather the
UPSes of several sites or rooms on one dashboard, with one history, one event log and one set of notifications.
For each remote UPS, add a UPS with the driver **NUT server (upsd)**:

| Option | Default | Meaning |
|---|---|---|
| Server | | Host name or IP address of the other server. |
| Port | `3493` | |
| UPS name on that server | | The name before the `@`, as `upsc -l` lists it. |
| Username / Password | | An account of that server. Needed only for instant commands, variable writes and login. |
| Use TLS (STARTTLS) | off | Encrypt the connection; the other server must have TLS enabled. |
| Certificate check | None | **None (accept self-signed certificates)** or **Verify the certificate chain and the host name**. |
| Log in as a secondary | off | Advanced. Sends `LOGIN`, so the other server counts this machine among the systems powered by the UPS and waits for it. Needs an account with the role **Secondary** (or **Primary**). |
| Timeout (ms) | `5000` | Advanced. How long to wait for the other server to connect or answer. |

Each remote UPS gets its own name on the central server (`siteb-rack1`, for instance), and the central server serves
it on its own port 3493 like any other UPS. Instant commands and variable writes made on the central server are
forwarded with the account in the driver, which needs the matching rights on the other server; when that server
supports tracking (NUT 2.8 or NutHub), the answer reflects the real result. The other server sees one connection per
repeated UPS. A forced shutdown set on the other server reaches the central server's clients through `ups.status`.

Machines that must shut down are best connected to the NutHub closest to their UPS: every extra server in the chain
is one more thing that can fail while the power is out. Turn on **Log in as a secondary** only when the central
server's machine is powered by that remote UPS and protects itself; otherwise the other server waits for it for
nothing.

## Protecting the NutHub machine itself

The machine NutHub runs on needs protecting too, and it should go down last, after its clients. There are two ways
to do it, and a container is a case of its own.

**Host protection** (recommended) is built in: **Settings > Host protection**. It does the job of a primary
`upsmon` for the NutHub machine: when too few of the **UPSes powering this machine** are healthy, it waits the
**Grace delay (s)**, sets FSD on the critical UPSes so that the clients shut down (**Tell NUT clients to shut down
first**, on by default), waits for them to log out (at most **Maximum wait for NUT clients (s)**, 15 by default),
optionally tells a UPS that is on battery to turn off after a delay and come back when mains power returns (**Tell
the UPS to turn off after the shutdown**, with **Power-off command** `shutdown.return` and **Power-off delay (s)**
120 by default), and runs the shutdown command. Try it first with **Dry run (no real shutdown)**. With host
protection on, every `upsmon` of those UPSes should be a secondary. See
[Host protection](configuration.md#host-protection) for the conditions it watches and every setting.

**upsmon on the NutHub machine** is the alternative on Linux, for example to keep existing `upsmon` or `upssched`
scripts. Install `nut-client` as above and monitor NutHub locally with a primary account:

```
MONITOR rack@localhost 1 upsprimary <password> primary
```

Leave host protection off for that UPS, so that only one of the two sets FSD and shuts the machine down. `upsmon`
cannot turn the UPS off at the end (there is no NUT driver on the machine); host protection can.

**NutHub in a container** cannot shut down its host, so host protection does not apply there. Run `upsmon` on the
Docker host as the primary, against the port the container publishes (`MONITOR rack@localhost 1 upsprimary <password>
primary`), or, on a NAS, let the NAS be a client as in [layout B](#b-nuthub-owns-the-ups-the-nas-is-a-client).

## Testing a client safely

A client that works does shut its machine down, so test on a machine that may go down, or with a harmless
`SHUTDOWNCMD`, such as:

```
SHUTDOWNCMD "/usr/bin/logger -t upsmon 'shutdown requested'"
```

A UPS with the driver **Simulated UPS** (**Settings > UPS devices > Add a UPS**, name it `sim`) lets you exercise a
client without touching a real UPS. Point the client at it (`MONITOR sim@192.168.1.10 1 upsmon <password> secondary`),
then:

- `upscmd -u operator -p '<password>' sim@192.168.1.10 test.failure.start` puts it on battery, `test.failure.stop`
  back on line power.
- On battery it discharges and reports `LB` at 20 % charge or 120 s of runtime.
  `upsrw -s battery.charge.low=90 -u operator -p '<password>' sim@192.168.1.10` makes that happen within a few
  minutes.
- **Set FSD** in the **Actions** menu of its page tests the forced shutdown at once. On line power the FSD is cleared
  by itself after **Clear FSD after mains return (s)**.

Restart the client (`sudo systemctl restart nut-monitor`) after a test that made it run its `SHUTDOWNCMD`.

## When a client does not connect

The log (**Settings > Logs**) records why NutHub refused a connection or a command, for example
`NUT client 192.168.1.30 (account 'upsmon') was refused LOGIN rack: wrong password.`

| What the client reports | Likely cause |
|---|---|
| The connection is refused, closed at once or times out | The client's address is not in **Allowed networks**, the **Maximum connections** of **Settings > NUT server** (256 by default) are in use, **NUT server enabled** is off, or a firewall blocks port 3493. |
| `ERR UNKNOWN-UPS` | The UPS name is wrong. `upsc -l 192.168.1.10` lists the names (they are not case-sensitive). |
| `ERR ACCESS-DENIED` on `LOGIN` | Unknown account or wrong password (account names are case-sensitive), a role of **No monitoring**, or a UPS outside the account's **Allowed UPSes**. |
| `ERR ACCESS-DENIED` on `USERNAME` or `PASSWORD` | **Require TLS to log in** is on and the client did not switch to TLS first. |
| `ERR ACCESS-DENIED` on every privileged command (`LOGIN`, `SET`, `INSTCMD`, `FSD`), although the password is right | 10 failed password checks from the same address (for IPv6, the same /64 network) within 5 minutes block the privileged commands from there for 5 minutes; reading still works. Fix the misconfigured client and wait. |
| `ERR DATA-STALE` or `ERR DRIVER-NOT-CONNECTED` | NutHub has no fresh data from that UPS: the UPS page says why (driver not connected, UPS disabled...). |
| `ERR FEATURE-NOT-CONFIGURED` on `STARTTLS` | **TLS enabled** is off on NutHub, or its certificate cannot be loaded (the log says why). |
| The machine is not waited for during a forced shutdown | The client does not log in: it reads without an account, or NutDesk has no login. |
| An APC Back-UPS shows `OL DISCHRG` with a full battery | That is what the device reports, not an error. The shutdown decisions of `upsmon` depend on `OB`, `LB` and `FSD`, not on `DISCHRG`. |

More symptoms and checks are in [Troubleshooting](troubleshooting.md#nut-clients-get-an-error), and every setting
of the NUT server and of the NUT accounts is in [Configuration](configuration.md#nut-server). For how the NUT server
itself works, see [ARCHITECTURE.md](ARCHITECTURE.md#nut-protocol-server).
