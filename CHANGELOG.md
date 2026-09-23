# Changelog

All notable changes to NutHub are listed here. Versions follow [semantic versioning](https://semver.org/).

## 1.0.0

First release.

- NUT network protocol 1.3 server (the job of `upsd`): every command of NUT 2.8, STARTTLS, accounts with the rights
  of `upsd.users`, tracking of instant commands and variable writes, client network allow-list, live rebinding.
  Checked with the official NUT 2.8.5 and 2.7.4 clients (`upsmon` primary/secondary and master/slave, `upsc`,
  `upscmd`, `upsrw`).
- Any number of UPSes at once, added, changed and removed while running.
- Drivers: `usbhid` (USB HID Power Device, with the subdriver tables of NUT's `usbhid-ups`), `winbattery` (Windows
  battery class), `megatec` (Megatec / Q1 / Voltronic family over serial, TCP or USB-serial bridges), `apcsmart`,
  `snmp` (v1, v2c, v3; IETF, APC, Eaton, MGE, CyberPower, Netvision, Huawei, Delta, XPPC MIBs), `nut` (another NUT
  server), `apcupsd`, and `simulated`.
- A USB UPS that stays plugged in but stops answering is reset (`USBDEVFS_RESET`) after a few failed attempts,
  so it comes back without anyone unplugging it; `usbReset` turns it off.
- On Linux the `usbhid` driver and the megatec USB-serial bridges talk to `/dev/hidraw*` and `/sys` directly, the
  way NUT and hidapi do: no libudev, so a UPS passed into a container with `--device` is found there as well
  (`NUTHUB_HID_BACKEND=hidsharp` uses the portable library instead).
- Web panel: live dashboard, UPS details with every variable, history charts (1 hour to 90 days), instant commands and
  variable writes, event log, full administration (UPSes, NUT and web accounts, NUT server, notifications, host
  protection, history, web panel, logs, configuration export, import from NUT's `ups.conf` / `upsd.users`). English
  and Italian, light and dark theme, phone layout. Viewer, operator and administrator roles.
- Notifications by email (persistent outbox, grouping), webhooks (with presets for ntfy, Telegram, Slack, Teams,
  Discord and Gotify) and commands.
- Host protection: shuts down the NutHub machine on a power failure after the NUT clients, with an optional UPS
  power-off and a dry-run mode.
- SQLite event log and history with 5-minute roll-ups and retention.
- Windows service and systemd unit, installers for both, Docker image, self-contained single-file releases for
  win-x64, linux-x64, linux-arm64 and linux-arm.
