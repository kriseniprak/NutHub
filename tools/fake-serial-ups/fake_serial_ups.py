#!/usr/bin/env python3
"""A fake serial-line UPS behind a TCP port, like a UPS on a ser2net or NPort serial device server in raw mode.

Emulates either a Megatec Q1 UPS (the NutHub "megatec" driver) or an APC Smart-UPS (the "apcsmart" driver), so both
drivers can be exercised end to end through their "tcp" transport without hardware. Python 3 standard library only.

Change the power situation with commands typed on stdin or written to a control file (see README.md):
    online | battery | low | charge <percent> | silent | talk | status | help | quit
"""

import argparse
import asyncio
import os
import sys
import threading
import time

COMMANDS_HELP = """commands:
  online           mains present, battery charging
  battery          mains failure, the UPS runs on battery (the charge drops over time)
  low              on battery with the low-battery flag set
  charge <0-100>   set the battery charge
  silent / talk    stop / resume answering (a cut cable, a UPS switched off)
  status           print the current state
  quit             stop the server"""


class UpsState:
    """The power situation both emulations read from."""

    def __init__(self, discharge_minutes):
        self.on_battery = False
        self.forced_low = False
        self.charge = 100.0
        self.silent = False
        self.beeper = True
        self.test_until = 0.0
        self.shutdown_at = None
        self.discharge_minutes = discharge_minutes
        self._last = time.monotonic()
        self.listeners = []

    def tick(self):
        now = time.monotonic()
        elapsed = now - self._last
        self._last = now
        rate = 100.0 / (self.discharge_minutes * 60.0)
        if self.on_battery:
            self.charge = max(0.0, self.charge - elapsed * rate)
        else:
            self.charge = min(100.0, self.charge + elapsed * rate / 4)

    @property
    def low_battery(self):
        return self.on_battery and (self.forced_low or self.charge < 20)

    @property
    def testing(self):
        return time.monotonic() < self.test_until

    @property
    def battery_voltage(self):
        """A 24 V battery: 21.0 V empty, 27.3 V full (floating a bit higher on mains)."""
        volts = 21.0 + 6.3 * self.charge / 100
        return volts + (0.3 if not self.on_battery else 0.0)

    def notify(self, event):
        for listener in list(self.listeners):
            listener(event)

    def apply(self, line):
        """Runs one control command; returns a message for the operator."""
        words = line.strip().lower().split()
        if not words:
            return None
        command, args = words[0], words[1:]
        if command == "online":
            was_low = self.low_battery
            self.on_battery = False
            self.forced_low = False
            self.notify("online")
            if was_low:
                self.notify("not-low")
        elif command == "battery":
            self.on_battery = True
            self.forced_low = False
            self.notify("battery")
        elif command == "low":
            self.on_battery = True
            self.forced_low = True
            self.notify("battery")
            self.notify("low")
        elif command == "charge" and len(args) == 1:
            try:
                self.charge = max(0.0, min(100.0, float(args[0])))
            except ValueError:
                return "charge needs a number from 0 to 100"
        elif command == "silent":
            self.silent = True
        elif command == "talk":
            self.silent = False
        elif command == "status":
            pass
        elif command == "help":
            return COMMANDS_HELP
        else:
            return "unknown command '%s'; type help" % line.strip()
        return self.describe()

    def describe(self):
        mode = "on battery" if self.on_battery else "on line"
        flags = []
        if self.low_battery:
            flags.append("low battery")
        if self.silent:
            flags.append("silent")
        if self.testing:
            flags.append("testing")
        if self.shutdown_at is not None:
            flags.append("shutdown pending")
        extra = (", " + ", ".join(flags)) if flags else ""
        return "%s, charge %.0f %%, battery %.2f V%s" % (mode, self.charge, self.battery_voltage, extra)


class MegatecUps:
    """Megatec Q1: queries end with CR; action commands are silent; unknown commands are echoed."""

    def __init__(self, state):
        self.state = state
        self.buffer = bytearray()

    def receive(self, data, send):
        for byte in data:
            if byte != 0x0D:
                self.buffer.append(byte)
                continue
            command = self.buffer.decode("latin-1")
            self.buffer.clear()
            reply = self.handle(command)
            if reply is not None and not self.state.silent:
                send((reply + "\r").encode("latin-1"))

    def handle(self, command):
        s = self.state
        s.tick()
        if command == "Q1":
            return self.status()
        if command == "F":
            return "#230.0 004 024.0 50.0"
        if command == "I":
            return "#%-15s %-10s %-10s" % ("NutHub", "FAKE-Q1", "V1.0")
        if command == "Q":
            s.beeper = not s.beeper
            return None
        if command in ("T", "TL") or (len(command) == 3 and command[0] == "T" and command[1:].isdigit()):
            minutes = int(command[1:]) if command[1:].isdigit() else 0.2
            s.test_until = time.monotonic() + (60 * minutes if command != "TL" else 120)
            return None
        if command == "CT":
            s.test_until = 0.0
            return None
        if command == "C":
            s.shutdown_at = None
            return None
        if command.startswith("S") and len(command) >= 3:
            delay = command[1:3]
            try:
                seconds = float("0" + delay) * 60 if delay.startswith(".") else int(delay) * 60
            except ValueError:
                return command
            s.shutdown_at = time.monotonic() + seconds
            return None
        return command

    def status(self):
        s = self.state
        mains = 0.0 if s.on_battery else 229.8
        output = 230.0 if not (s.shutdown_at is not None and time.monotonic() > s.shutdown_at) else 0.0
        bits = "%d%d0%d1%d%d%d" % (
            1 if s.on_battery else 0,
            1 if s.low_battery else 0,
            0,
            1 if s.testing else 0,
            1 if s.shutdown_at is not None else 0,
            1 if s.beeper else 0,
        )
        return "(%05.1f %05.1f %05.1f %03d %04.1f %04.1f %04.1f %s" % (
            mains, 140.0, output, 23, 50.0, s.battery_voltage, 30.0, bits)


class ApcSmartUps:
    """APC Smart: single-character commands, CR LF replies, alert characters sent on their own."""

    COMMAND_SET = "3.!$%+?=#|.\x01\x0e\x1a@ABCDEFGKLMNOPQRSUVWXYZ^abcefgjklmnopqrsuxyz~"
    CAPABILITIES = "#uI43253264271280lI43196188208204pI43020180300600rI43000060180300"
    ENUMS = {
        "u": ["253", "264", "271", "280"],
        "l": ["196", "188", "208", "204"],
        "p": ["020", "180", "300", "600"],
        "r": ["000", "060", "180", "300"],
    }

    def __init__(self, state):
        self.state = state
        self.values = {
            "b": "652.13.I", "\x01": "Smart-UPS 1000 (fake)", "n": "FAKE0000001", "m": "01/01/26", "x": "01/01/26",
            "c": "UPS_IDEN", "L": "230.4", "O": "230.4", "F": "50.00", "P": "023.4", "C": "031.5", "X": "OK",
            "G": "O", "M": "235.2", "N": "228.8", "E": "336", "e": "00", "g": "024", "k": "0", "o": "230",
            "q": "02", "s": "H", "u": "253", "l": "196", "p": "020", "r": "000", "9": "FF",
        }
        self.last_command = ""
        self.last_read = ""
        self.multi = None
        self.calibrating = False

    def receive(self, data, send):
        for byte in data:
            reply = self.handle(chr(byte))
            if reply is not None and not self.state.silent:
                send((reply + "\r\n").encode("latin-1"))

    def register(self):
        s = self.state
        s.tick()
        value = 0
        if self.calibrating:
            value |= 0x01
        value |= 0x10 if s.on_battery else 0x08
        if s.low_battery:
            value |= 0x40
        return "%02X" % value

    def handle(self, c):
        s = self.state
        if self.multi is not None:
            kind, text, length = self.multi
            text += c
            if len(text) <= length:
                self.multi = (kind, text, length)
                return None
            self.multi = None
            if kind == "-":
                self.values[self.last_read] = text[1:].rstrip("\r")
            else:
                s.shutdown_at = time.monotonic() + int(self.values["p"])
            return "OK"
        if s.silent:
            return None
        previous, self.last_command = self.last_command, c
        if c == "Y":
            return "SM"
        if c == "\x1b":
            return None
        if c == "a":
            return self.COMMAND_SET
        if c == "\x1a":
            return self.CAPABILITIES
        if c == "Q":
            return self.register()
        if c == "@":
            self.multi = ("@", "@", 3)
            return None
        if c == "-":
            if self.last_read in ("c", "x"):
                self.multi = ("-", "-", 8)
                return None
            cycle = self.ENUMS.get(self.last_read)
            if cycle is None:
                return "NO"
            index = cycle.index(self.values[self.last_read]) if self.values[self.last_read] in cycle else -1
            self.values[self.last_read] = cycle[(index + 1) % len(cycle)]
            return "OK"
        if c in ("K", "Z", "\x0e"):
            if previous == c:
                self.last_command = ""
                if c == "K":
                    s.shutdown_at = time.monotonic() + int(self.values["p"])
                    return "OK"
            return None
        if c == "S":
            if not s.on_battery:
                return "NA"
            s.shutdown_at = time.monotonic() + int(self.values["p"])
            return "OK"
        if c == "U":
            s.apply("battery")
            return None
        if c in ("W", "A"):
            return "OK"
        if c == "D":
            self.calibrating = not self.calibrating
            return "OK"
        if c == "^":
            return "BYP"
        self.last_read = c
        if c == "f":
            return "%05.1f" % s.charge
        if c == "j":
            return "%04d:" % max(0, round(s.charge * 0.6))
        if c == "B":
            return "%05.2f" % s.battery_voltage
        return self.values.get(c, "NA")

    @staticmethod
    def alert(event):
        return {"battery": "!", "online": "$", "low": "%", "not-low": "+"}.get(event)


async def serve(args, state):
    clients = set()

    async def handle(reader, writer):
        peer = writer.get_extra_info("peername")
        log(args, "client %s connected" % (peer,))
        device = MegatecUps(state) if args.protocol == "megatec" else ApcSmartUps(state)

        def send(data):
            if not writer.is_closing():
                writer.write(data)

        def on_event(event):
            if isinstance(device, ApcSmartUps) and not state.silent:
                alert = ApcSmartUps.alert(event)
                if alert:
                    send(alert.encode("latin-1"))

        state.listeners.append(on_event)
        clients.add(writer)
        try:
            while True:
                data = await reader.read(256)
                if not data:
                    break
                device.receive(data, send)
                await writer.drain()
        except (ConnectionError, OSError):
            pass
        finally:
            state.listeners.remove(on_event)
            clients.discard(writer)
            writer.close()
            log(args, "client %s disconnected" % (peer,))

    server = await asyncio.start_server(handle, args.host, args.port)
    log(args, "fake %s UPS listening on %s:%d; type help for the commands" % (args.protocol, args.host, args.port), force=True)
    return server


def log(args, text, force=False):
    if force or not args.quiet:
        print(text, flush=True)


async def watch_control_file(path, state, args):
    """Applies the commands of a control file each time it changes (one command per line)."""
    last = None
    while True:
        try:
            stamp = os.stat(path).st_mtime_ns
            if stamp != last:
                last = stamp
                with open(path, encoding="utf-8") as handle:
                    for line in handle:
                        message = state.apply(line)
                        if message:
                            log(args, message, force=True)
        except FileNotFoundError:
            last = None
        await asyncio.sleep(0.5)


def read_stdin(loop, state, args, stop):
    """Reads commands typed on stdin; the end of stdin leaves the server running (stop it with Ctrl+C)."""
    for line in sys.stdin:
        if line.strip().lower() in ("quit", "exit"):
            loop.call_soon_threadsafe(stop.set)
            return
        loop.call_soon_threadsafe(_apply_and_print, state, line, args)


def _apply_and_print(state, line, args):
    message = state.apply(line)
    if message:
        log(args, message, force=True)


async def main():
    parser = argparse.ArgumentParser(description="Fake Megatec Q1 or APC Smart UPS behind a TCP port.")
    parser.add_argument("--protocol", choices=("megatec", "apcsmart"), default="megatec")
    parser.add_argument("--host", default="127.0.0.1", help="address to listen on (default 127.0.0.1)")
    parser.add_argument("--port", type=int, default=2001, help="TCP port (default 2001)")
    parser.add_argument("--control", help="control file: its lines are applied as commands whenever it changes")
    parser.add_argument("--discharge-minutes", type=float, default=10.0,
                        help="minutes for a full battery to empty on battery (default 10)")
    parser.add_argument("--no-stdin", action="store_true", help="do not read commands from stdin (background use)")
    parser.add_argument("--quiet", action="store_true", help="log only the state changes")
    args = parser.parse_args()

    state = UpsState(max(args.discharge_minutes, 0.1))
    server = await serve(args, state)
    stop = asyncio.Event()
    loop = asyncio.get_running_loop()
    tasks = []
    if args.control:
        tasks.append(asyncio.create_task(watch_control_file(args.control, state, args)))
    if not args.no_stdin:
        threading.Thread(target=read_stdin, args=(loop, state, args, stop), daemon=True).start()
    try:
        await stop.wait()
    finally:
        for task in tasks:
            task.cancel()
        server.close()
        await server.wait_closed()


if __name__ == "__main__":
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        pass
