# fake-serial-ups

A fake serial-line UPS behind a TCP port, for trying the `megatec` and `apcsmart` drivers without hardware. It
behaves like a UPS connected to a serial device server (ser2net in raw mode, a Moxa NPort in TCP server mode): the
driver's `tcp` transport connects to it and talks the UPS protocol over the socket.

Python 3.8 or later, standard library only.

## Start it

```sh
# A Megatec Q1 UPS on port 2001
python3 tools/fake-serial-ups/fake_serial_ups.py --protocol megatec --port 2001

# An APC Smart-UPS on port 2002, with a control file
python3 tools/fake-serial-ups/fake_serial_ups.py --protocol apcsmart --port 2002 --control /tmp/apc.ctl
```

| Option | Default | Meaning |
|---|---|---|
| `--protocol` | `megatec` | `megatec` (Q1, F, I, the S/C/T/Q commands) or `apcsmart` (APC Smart protocol) |
| `--host` | `127.0.0.1` | Address to listen on; use `0.0.0.0` to reach it from another machine |
| `--port` | `2001` | TCP port |
| `--control` | none | A file whose lines are applied as commands every time it changes |
| `--discharge-minutes` | `10` | How long a full battery lasts on battery |
| `--no-stdin` | off | Do not read commands from stdin (when started in the background) |
| `--quiet` | off | Log only the state changes, not the connections |

Then add a UPS in NutHub with the driver `megatec` (or `apcsmart`), connection "Serial device server over TCP",
host `127.0.0.1` and the TCP port above. For `megatec`, the protocol can stay on automatic detection: the fake answers
like a plain Megatec UPS (it echoes the queries of the other dialects, as the real firmware does).

## Change the power situation

Type a command on the terminal where the fake runs, or write it to the control file
(`echo battery > /tmp/apc.ctl`):

| Command | Effect |
|---|---|
| `online` | Mains present; the battery recharges slowly |
| `battery` | Mains failure; the charge drops over `--discharge-minutes` and the low-battery flag comes below 20 % |
| `low` | On battery with the low-battery flag set at once |
| `charge 35` | Sets the battery charge in percent |
| `silent` / `talk` | Stops / resumes answering, as a cut cable or a UPS that is switched off: the driver reports the data as stale after three polls |
| `status` | Prints the current state |
| `quit` | Stops the server (Ctrl+C works too) |

The APC emulation also sends the alert characters a real Smart-UPS sends on its own (`!` on battery, `$` back on
line, `%` low battery, `+` battery no longer low).

## What is emulated

Megatec: `Q1` status (voltages, load, frequency, battery voltage, temperature and the 8 status bits), `F` ratings,
`I` vendor information; `Q` (beeper toggle), `T`, `TL`, `Tnn`, `CT` (battery tests, shown as test in progress),
`Sn.n` / `SnnRmmmm` (shutdown, shown as shutdown active), `C` (cancel). Anything else is echoed back.

APC Smart: `Y` smart mode, `a` command set, `^Z` capabilities, `Q` status register, the usual variables (`f`, `j`,
`L`, `O`, `F`, `P`, `B`, `C`, `^A` model, `n` serial, `b` firmware, `x`, `m`, `X`, `G`...), `-` cycling of the EEPROM
values listed in the capabilities (`u`, `l`, `p`, `r`), `-` plus 8 characters for `c` (ups.id) and `x`
(battery.date), `@nnn`, `S`, `K` and `Z` (sent twice), `^N`, `U`, `W`, `A`, `D`, `^`.

Shutdown commands are only simulated: nothing is powered off, the fake just shows the shutdown as pending.

The C# tests do not need this script: they use in-process fakes (`tests/NutHub.Drivers.Serial.Tests/Fakes`).
