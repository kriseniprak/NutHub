# Fake SNMP UPS agent

`fake_snmp_agent.py` pretends to be a UPS network management card, so the `snmp` driver can be tried end to end
without hardware: power failures, low battery, replace-battery alarms, instant commands and variable writes.

- Profiles: `ietf` (an RFC 1628 UPS-MIB UPS from an unknown vendor, detected as `ietf`) and `apc` (an APC Smart-UPS
  with a PowerNet card, detected as `apcc` from its sysObjectID; it answers RFC 1628 too, like real APC cards).
- SNMP v1 and v2c: GET, GETNEXT, GETBULK and SET, with v1 `noSuchName` and v2c exception semantics. SNMPv3 is not
  implemented here; the test suite covers it with its in-process agent (`tests/NutHub.Drivers.Snmp.Tests/Fakes`).
- Python 3.8 or later, standard library only. Listens on `127.0.0.1:1161` by default (port 161 needs privileges).

## Run

```sh
python3 tools/fake-snmp-agent/fake_snmp_agent.py --profile apc
```

| Option | Default | Meaning |
|---|---|---|
| `--profile ietf\|apc` | `ietf` | Which card to simulate. |
| `--bind ADDRESS` | `127.0.0.1` | Address to listen on (`0.0.0.0` to reach it from another machine). |
| `--port PORT` | `1161` | UDP port. |
| `--community NAME` | `public` | Read community; requests with another community are dropped, as real cards do. |
| `--write-community NAME` | the read community | Community accepted for SETs. |
| `--on-battery` | off | Start on battery. |
| `--toggle SECONDS` | `0` | Switch between on line and on battery every SECONDS. |
| `--verbose` | off | Print every request. |

While it runs, type on its console:

| Command | Effect |
|---|---|
| `ob` | Power failure: on battery (`OB`), charge and runtime start to fall. |
| `ol` | Mains power back (`OL`); also clears low battery. |
| `lb` | Low battery (`OB LB`). |
| `rb` | Toggles the replace-battery indicator (`RB`; an RFC 1628 alarm-table row in the `ietf` profile). |
| `s` | Prints the state. |
| `q` | Quits. |

Every SET is printed (`SET 1.3.6.1.4.1.318.1.1.1.7.2.2.0 = int:2`), so instant commands can be checked. APC
`test.failure.start` (upsAdvControlSimulatePowerFail) really switches the fake UPS to battery. Settings written
(transfer voltages, delays, `ups.id`...) are kept and read back.

## Use it with NutHub

Add a UPS with the driver `snmp` and the options `host` = `127.0.0.1`, `port` = `1161`, `version` = `2c` (or `1`),
`community` = `public`, `mibs` = `auto`. Then check it with any NUT client:

```sh
upsc myups@localhost
upscmd -u admin myups@localhost test.battery.start
```

Or, with the net-snmp tools, look at what the driver sees:

```sh
snmpwalk -v2c -c public 127.0.0.1:1161 1.3.6.1.2.1.33
```
