#!/usr/bin/env python3
"""A fake SNMP UPS network card for manual tests of NutHub's "snmp" driver.

Serves an RFC 1628 UPS-MIB UPS ("ietf" profile) or an APC PowerNet UPS ("apc" profile, which also answers
RFC 1628 like real APC cards) over SNMP v1 and v2c on UDP: GET, GETNEXT, GETBULK and SET.
Python 3 standard library only. Type commands on the console to change the UPS state (see --help).
"""

import argparse
import select
import socket
import sys
import threading
import time

# ----- BER encoding --------------------------------------------------------------------------------------------

INTEGER, OCTET_STRING, NULL, OID, SEQUENCE = 0x02, 0x04, 0x05, 0x06, 0x30
COUNTER32, GAUGE32, TIMETICKS = 0x41, 0x42, 0x43
NO_SUCH_OBJECT, NO_SUCH_INSTANCE, END_OF_MIB_VIEW = 0x80, 0x81, 0x82
GET, GETNEXT, RESPONSE, SET, GETBULK = 0xA0, 0xA1, 0xA2, 0xA3, 0xA5
NO_ERROR, TOO_BIG, NO_SUCH_NAME, BAD_VALUE, GEN_ERR, NOT_WRITABLE = 0, 1, 2, 3, 5, 17


def enc_len(n):
    if n < 0x80:
        return bytes([n])
    b = n.to_bytes((n.bit_length() + 7) // 8, "big")
    return bytes([0x80 | len(b)]) + b


def tlv(tag, payload):
    return bytes([tag]) + enc_len(len(payload)) + payload


def enc_int(v, tag=INTEGER):
    length = max(1, (v.bit_length() + 8) // 8) if v >= 0 else max(1, ((-v - 1).bit_length() + 8) // 8)
    return tlv(tag, v.to_bytes(length, "big", signed=True))


def enc_unsigned(v, tag):
    b = v.to_bytes(max(1, (v.bit_length() + 7) // 8), "big")
    if b[0] & 0x80:
        b = b"\x00" + b
    return tlv(tag, b)


def enc_oid(oid):
    arcs = oid_tuple(oid)
    out = bytearray([arcs[0] * 40 + arcs[1]])
    for arc in arcs[2:]:
        chunk = [arc & 0x7F]
        arc >>= 7
        while arc:
            chunk.insert(0, 0x80 | (arc & 0x7F))
            arc >>= 7
        out += bytes(chunk)
    return tlv(OID, bytes(out))


def oid_tuple(oid):
    return tuple(int(a) for a in oid.strip(".").split("."))


def oid_text(arcs):
    return ".".join(str(a) for a in arcs)


def dec_tlv(data, pos):
    tag = data[pos]
    pos += 1
    length = data[pos]
    pos += 1
    if length & 0x80:
        count = length & 0x7F
        length = int.from_bytes(data[pos:pos + count], "big")
        pos += count
    if pos + length > len(data):
        raise ValueError("truncated")
    return tag, data[pos:pos + length], pos + length


def dec_seq(payload):
    items, pos = [], 0
    while pos < len(payload):
        tag, value, pos = dec_tlv(payload, pos)
        items.append((tag, value))
    return items


def dec_oid(payload):
    arcs = [payload[0] // 40, payload[0] % 40]
    arc = 0
    for b in payload[1:]:
        arc = (arc << 7) | (b & 0x7F)
        if not b & 0x80:
            arcs.append(arc)
            arc = 0
    return tuple(arcs)


# ----- Values --------------------------------------------------------------------------------------------------

class V:
    """A typed SNMP value: ("int", 5), ("str", "x"), ("oid", "1.3..."), ("ticks", 100), ("gauge", 7)."""

    def __init__(self, kind, value):
        self.kind, self.value = kind, value

    def encode(self):
        if self.kind == "int":
            return enc_int(int(self.value))
        if self.kind == "str":
            return tlv(OCTET_STRING, str(self.value).encode("utf-8"))
        if self.kind == "oid":
            return enc_oid(self.value)
        if self.kind == "ticks":
            return enc_unsigned(int(self.value), TIMETICKS)
        if self.kind == "gauge":
            return enc_unsigned(int(self.value), GAUGE32)
        raise ValueError(self.kind)

    @staticmethod
    def decode(tag, payload):
        if tag == INTEGER:
            return V("int", int.from_bytes(payload, "big", signed=True))
        if tag == OCTET_STRING:
            return V("str", payload.decode("utf-8", "replace"))
        if tag == OID:
            return V("oid", oid_text(dec_oid(payload)))
        if tag in (TIMETICKS, GAUGE32, COUNTER32):
            return V({TIMETICKS: "ticks"}.get(tag, "gauge"), int.from_bytes(payload, "big"))
        return None

    def __repr__(self):
        return f"{self.kind}:{self.value}"


# ----- The UPS -------------------------------------------------------------------------------------------------

IETF = "1.3.6.1.2.1.33.1."
APC = "1.3.6.1.4.1.318.1.1.1."


class Ups:
    """The simulated UPS state and the MIB objects it gives."""

    def __init__(self, profile):
        self.profile = profile
        self.on_battery = False
        self.low_battery = False
        self.replace_battery = False
        self.since = time.monotonic()
        self.started = time.monotonic()
        self.lock = threading.Lock()
        self.written = {}  # objects changed by SETs

    def state(self):
        return ("OB" if self.on_battery else "OL") + (" LB" if self.low_battery else "") + (" RB" if self.replace_battery else "")

    def objects(self):
        seconds_on_battery = int(time.monotonic() - self.since) if self.on_battery else 0
        charge = 12 if self.low_battery else (max(30, 100 - seconds_on_battery // 6) if self.on_battery else 100)
        runtime_min = max(1, charge * 30 // 100)
        uptime = int((time.monotonic() - self.started) * 100)
        sys_oid = "1.3.6.1.4.1.318.1.3.27" if self.profile == "apc" else "1.3.6.1.4.1.99999.1"
        o = {
            "1.3.6.1.2.1.1.1.0": V("str", "NutHub fake SNMP agent (%s profile)" % self.profile),
            "1.3.6.1.2.1.1.2.0": V("oid", sys_oid),
            "1.3.6.1.2.1.1.3.0": V("ticks", uptime),
            "1.3.6.1.2.1.1.4.0": V("str", "admin@example.com"),
            "1.3.6.1.2.1.1.5.0": V("str", "fake-ups"),
            "1.3.6.1.2.1.1.6.0": V("str", "Lab"),
            # RFC 1628 UPS-MIB
            IETF + "1.1.0": V("str", "APC" if self.profile == "apc" else "ACME"),
            IETF + "1.2.0": V("str", "Smart-UPS 1500" if self.profile == "apc" else "ACME Line-Interactive 1500"),
            IETF + "1.3.0": V("str", "UPS 09.3"),
            IETF + "1.4.0": V("str", "Agent 1.0"),
            IETF + "2.1.0": V("int", 3 if self.low_battery else 2),
            IETF + "2.2.0": V("int", seconds_on_battery),
            IETF + "2.3.0": V("int", runtime_min),
            IETF + "2.4.0": V("int", charge),
            IETF + "2.5.0": V("int", 262 if self.on_battery else 272),
            IETF + "2.7.0": V("int", 29),
            IETF + "3.2.0": V("int", 1),
            IETF + "3.3.1.2.1": V("int", 0 if self.on_battery else 500),
            IETF + "3.3.1.3.1": V("int", 0 if self.on_battery else 230),
            IETF + "4.1.0": V("int", 5 if self.on_battery else 3),
            IETF + "4.2.0": V("int", 500),
            IETF + "4.3.0": V("int", 1),
            IETF + "4.4.1.2.1": V("int", 230),
            IETF + "4.4.1.3.1": V("int", 21),
            IETF + "4.4.1.4.1": V("int", 420),
            IETF + "4.4.1.5.1": V("int", 32),
            IETF + "6.1.0": V("int", 1 if self.replace_battery else 0),
            IETF + "7.1.0": V("oid", IETF + "7.7.1"),
            IETF + "7.3.0": V("int", 6),
            IETF + "8.2.0": V("int", -1),
            IETF + "8.3.0": V("int", -1),
            IETF + "8.5.0": V("int", 1),
            IETF + "9.1.0": V("int", 230),
            IETF + "9.2.0": V("int", 500),
            IETF + "9.3.0": V("int", 230),
            IETF + "9.4.0": V("int", 500),
            IETF + "9.5.0": V("int", 1500),
            IETF + "9.6.0": V("int", 1000),
            IETF + "9.7.0": V("int", 2),
            IETF + "9.8.0": V("int", 2),
        }
        if self.replace_battery:
            o[IETF + "6.2.1.2.1"] = V("oid", IETF + "6.3.1")  # upsAlarmBatteryBad
        if self.profile == "apc":
            o.update({
                APC + "1.1.1.0": V("str", "Smart-UPS 1500"),
                APC + "1.1.2.0": V("str", "UPS_IDEN"),
                APC + "1.2.1.0": V("str", "UPS 09.3"),
                APC + "1.2.2.0": V("str", "01/02/2019"),
                APC + "1.2.3.0": V("str", "AS1234567890"),
                APC + "2.1.1.0": V("int", 3 if self.low_battery else 2),
                APC + "2.1.3.0": V("str", "01/02/2023"),
                APC + "2.2.1.0": V("gauge", charge),
                APC + "2.2.2.0": V("gauge", 29),
                APC + "2.2.3.0": V("ticks", runtime_min * 6000),
                APC + "2.2.4.0": V("int", 2 if self.replace_battery else 1),
                APC + "2.2.8.0": V("int", 27),
                APC + "2.3.1.0": V("gauge", charge * 10),
                APC + "2.3.4.0": V("int", 262 if self.on_battery else 272),
                APC + "3.2.1.0": V("gauge", 0 if self.on_battery else 230),
                APC + "3.2.4.0": V("gauge", 0 if self.on_battery else 50),
                APC + "3.2.5.0": V("int", 4 if self.on_battery else 1),
                APC + "3.3.1.0": V("gauge", 0 if self.on_battery else 2301),
                APC + "4.1.1.0": V("int", 3 if self.on_battery else 2),
                APC + "4.2.1.0": V("gauge", 230),
                APC + "4.2.3.0": V("gauge", 32),
                APC + "4.3.1.0": V("gauge", 2300),
                APC + "4.3.3.0": V("gauge", 321),
                APC + "5.2.2.0": V("int", 253),
                APC + "5.2.3.0": V("int", 196),
                APC + "5.2.7.0": V("int", 4),
                APC + "5.2.8.0": V("ticks", 12000),
                APC + "5.2.9.0": V("ticks", 0),
                APC + "5.2.10.0": V("ticks", 9000),
                APC + "6.1.1.0": V("int", 1),
                APC + "6.2.1.0": V("int", 1),
                APC + "6.2.2.0": V("int", 1),
                APC + "6.2.4.0": V("int", 1),
                APC + "6.2.5.0": V("int", 1),
                APC + "6.2.6.0": V("int", 1),
                APC + "7.2.2.0": V("int", 1),
                APC + "7.2.3.0": V("int", 1),
                APC + "7.2.4.0": V("str", "03/04/2024"),
                APC + "7.2.5.0": V("int", 1),
                APC + "7.2.6.0": V("int", 1),
            })
        o.update(self.written)
        return dict(sorted(((oid_tuple(k), v) for k, v in o.items())))

    def set_state(self, on_battery=None, low_battery=None, replace_battery=None):
        with self.lock:
            if on_battery is not None and on_battery != self.on_battery:
                self.on_battery = on_battery
                self.since = time.monotonic()
                if not on_battery:
                    self.low_battery = False
            if low_battery is not None:
                self.low_battery = low_battery
                if low_battery:
                    self.on_battery = True
            if replace_battery is not None:
                self.replace_battery = replace_battery


# ----- The agent -----------------------------------------------------------------------------------------------

class Agent:
    def __init__(self, ups, community, write_community, verbose):
        self.ups, self.community, self.write_community, self.verbose = ups, community, write_community, verbose

    def handle(self, datagram):
        tag, body, _ = dec_tlv(datagram, 0)
        if tag != SEQUENCE:
            return None
        items = dec_seq(body)
        version = int.from_bytes(items[0][1], "big", signed=True)
        community = items[1][1].decode("latin-1")
        pdu_tag, pdu_body = items[2]
        if version not in (0, 1):
            return None  # SNMPv3 is not implemented here
        allowed = community == self.write_community if pdu_tag == SET else community in (self.community, self.write_community)
        if not allowed:
            return None  # real agents stay silent (and count snmpInBadCommunityNames)
        fields = dec_seq(pdu_body)
        request_id = int.from_bytes(fields[0][1], "big", signed=True)
        a = int.from_bytes(fields[1][1], "big", signed=True)
        b = int.from_bytes(fields[2][1], "big", signed=True)
        binds = []
        for _, vb in dec_seq(fields[3][1]):
            (otag, oval), (vtag, vval) = dec_seq(vb)
            binds.append((dec_oid(oval), vtag, vval))

        with self.ups.lock:
            objects = self.ups.objects()
            status, index, answer = self.process(pdu_tag, version, binds, objects, a, b)

        varbinds = b"".join(tlv(SEQUENCE, enc_oid(oid_text(oid)) + val) for oid, val in answer)
        pdu = enc_int(request_id) + enc_int(status) + enc_int(index) + tlv(SEQUENCE, varbinds)
        return tlv(SEQUENCE, enc_int(version) + tlv(OCTET_STRING, items[1][1]) + tlv(RESPONSE, pdu))

    def process(self, pdu_tag, version, binds, objects, non_repeaters, max_repetitions):
        v1 = version == 0
        keys = list(objects.keys())
        echo = [(oid, tlv(NULL, b"")) for oid, _, _ in binds]

        def next_of(oid):
            for k in keys:
                if k > oid:
                    return k
            return None

        if pdu_tag == GET:
            answer = []
            for i, (oid, _, _) in enumerate(binds):
                if oid in objects:
                    answer.append((oid, objects[oid].encode()))
                elif v1:
                    return NO_SUCH_NAME, i + 1, echo
                else:
                    answer.append((oid, tlv(NO_SUCH_OBJECT, b"")))
            return NO_ERROR, 0, answer

        if pdu_tag == GETNEXT or (pdu_tag == GETBULK and not v1):
            if pdu_tag == GETNEXT:
                non_repeaters, max_repetitions = len(binds), 0
            answer = []
            for i, (oid, _, _) in enumerate(binds[:non_repeaters]):
                k = next_of(oid)
                if k is None and v1:
                    return NO_SUCH_NAME, i + 1, echo
                answer.append((k, objects[k].encode()) if k else (oid, tlv(END_OF_MIB_VIEW, b"")))
            cursors = [oid for oid, _, _ in binds[non_repeaters:]]
            for _ in range(max(0, min(max_repetitions, 50))):
                for j, cur in enumerate(cursors):
                    k = next_of(cur) if cur else None
                    answer.append((k, objects[k].encode()) if k else (cur or binds[-1][0], tlv(END_OF_MIB_VIEW, b"")))
                    cursors[j] = k
            return NO_ERROR, 0, answer

        if pdu_tag == SET:
            decoded = []
            for i, (oid, vtag, vval) in enumerate(binds):
                value = V.decode(vtag, vval)
                if oid not in objects:
                    return (NO_SUCH_NAME if v1 else NOT_WRITABLE), i + 1, echo
                if value is None:
                    return BAD_VALUE, i + 1, echo
                decoded.append((oid, value))
            for oid, value in decoded:
                text = oid_text(oid)
                print(f"SET {text} = {value}", flush=True)
                self.apply(text, value)
            return NO_ERROR, 0, [(oid, value.encode()) for oid, value in decoded]

        return GEN_ERR, 0, echo

    def apply(self, oid, value):
        # Control objects "happen" and read back as "no action"; settings are remembered.
        controls = {APC + "6.2.4.0": "simulated power failure", APC + "6.2.1.0": "UPS off",
                    APC + "6.1.1.0": "UPS put to sleep", APC + "7.2.2.0": "self test", IETF + "8.2.0": "shutdown after delay"}
        if oid in controls:
            print(f"  -> {controls[oid]} requested", flush=True)
            if oid == APC + "6.2.4.0":
                self.ups.on_battery = True
                self.ups.since = time.monotonic()
            return
        self.ups.written[oid] = value


def console(ups):
    help_text = "Commands: ob (on battery), ol (on line), lb (low battery), rb (toggle replace battery), s (status), q (quit)"
    print(help_text, flush=True)
    for line in sys.stdin:
        cmd = line.strip().lower()
        if cmd == "ob":
            ups.set_state(on_battery=True)
        elif cmd == "ol":
            ups.set_state(on_battery=False)
        elif cmd == "lb":
            ups.set_state(low_battery=True)
        elif cmd == "rb":
            ups.set_state(replace_battery=not ups.replace_battery)
        elif cmd in ("q", "quit", "exit"):
            return
        elif cmd not in ("s", "status", ""):
            print(help_text, flush=True)
            continue
        print(f"UPS status: {ups.state()}", flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("--profile", choices=["ietf", "apc"], default="ietf", help="which card to simulate (default ietf)")
    parser.add_argument("--bind", default="127.0.0.1", help="address to listen on (default 127.0.0.1)")
    parser.add_argument("--port", type=int, default=1161, help="UDP port (default 1161; 161 needs privileges)")
    parser.add_argument("--community", default="public", help="read community (default public)")
    parser.add_argument("--write-community", help="write community (default: the read community)")
    parser.add_argument("--on-battery", action="store_true", help="start on battery")
    parser.add_argument("--toggle", type=float, default=0, metavar="SECONDS",
                        help="switch between on line and on battery every SECONDS (0 = never)")
    parser.add_argument("--verbose", action="store_true", help="print every request")
    parser.add_argument("--no-stdin", action="store_true",
                        help="do not read commands from the console (background use; stop it with Ctrl+C or kill)")
    args = parser.parse_args()

    ups = Ups(args.profile)
    ups.set_state(on_battery=args.on_battery)
    agent = Agent(ups, args.community, args.write_community or args.community, args.verbose)
    sock = socket.socket(socket.AF_INET6 if ":" in args.bind else socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind((args.bind, args.port))
    print(f"Fake {args.profile} UPS on udp://{args.bind}:{args.port}, community '{args.community}'. {ups.state()}", flush=True)

    stop = threading.Event()
    # Without a usable console (started in the background) the agent keeps serving until it is killed.
    if not args.no_stdin and sys.stdin is not None and not sys.stdin.closed:
        threading.Thread(target=lambda: (console(ups), stop.set()), daemon=True).start()
    next_toggle = time.monotonic() + args.toggle if args.toggle > 0 else None
    while not stop.is_set():
        if next_toggle and time.monotonic() >= next_toggle:
            ups.set_state(on_battery=not ups.on_battery)
            print(f"UPS status: {ups.state()}", flush=True)
            next_toggle += args.toggle
        ready, _, _ = select.select([sock], [], [], 0.2)
        if not ready:
            continue
        try:
            data, peer = sock.recvfrom(65535)
        except OSError:
            continue  # ICMP errors from clients that went away
        try:
            reply = agent.handle(data)
        except (ValueError, IndexError) as ex:
            if args.verbose:
                print(f"Ignoring a malformed datagram from {peer}: {ex}", flush=True)
            continue
        if args.verbose:
            print(f"{peer[0]}:{peer[1]} -> {'answered' if reply else 'dropped'}", flush=True)
        if reply:
            sock.sendto(reply, peer)
    sock.close()


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
