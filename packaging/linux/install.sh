#!/bin/sh
# NutHub installer for Linux distributions with systemd.
#
#   sudo ./install.sh               install or upgrade (keeps the configuration and data)
#   sudo ./install.sh --uninstall   remove NutHub, keep the configuration and data
#   sudo ./install.sh --uninstall --purge
#                                   remove NutHub with its configuration, data and service account
#
# Installs the executable in /opt/nuthub, creates the "nuthub" system account, the systemd unit, the udev rules that
# give that account access to USB UPSes, and the polkit rule that lets it power off this machine on a power failure.
# Running it again upgrades in place. Needs: systemd, useradd/groupadd (shadow-utils), install(1).
set -eu

PREFIX=/opt/nuthub
SERVICE=nuthub.service
UNIT_FILE=/etc/systemd/system/$SERVICE
UDEV_RULES=/etc/udev/rules.d/99-nuthub-ups.rules
POLKIT_RULES=/etc/polkit-1/rules.d/50-nuthub-poweroff.rules
POLKIT_PKLA=/etc/polkit-1/localauthority/50-local.d/50-nuthub-poweroff.pkla
DATA_DIR=/var/lib/nuthub
CONFIG_DIR=/etc/nuthub
CACHE_DIR=/var/cache/nuthub
ACCOUNT=nuthub

HERE=$(cd "$(dirname "$0")" && pwd)

usage() {
    cat <<EOF
Usage: $0 [--uninstall [--purge]]

  (no option)    Install or upgrade NutHub and start its service.
  --uninstall    Remove NutHub; the configuration ($CONFIG_DIR) and data ($DATA_DIR) are kept.
  --purge        With --uninstall: also delete the configuration, the data and the "$ACCOUNT" account.
EOF
}

die() {
    echo "install.sh: $*" >&2
    exit 1
}

info() {
    echo "==> $*"
}

# Finds a packaged file next to this script, or in the linux/ folder of a source checkout.
packaged() {
    for candidate in "$HERE/$1" "$HERE/linux/$1" "$HERE/packaging/linux/$1"; do
        if [ -f "$candidate" ]; then
            echo "$candidate"
            return 0
        fi
    done
    die "$1 not found next to install.sh"
}

ACTION=install
PURGE=0
while [ $# -gt 0 ]; do
    case "$1" in
        --uninstall) ACTION=uninstall ;;
        --purge) PURGE=1 ;;
        -h | --help) usage; exit 0 ;;
        *) echo "install.sh: unknown option '$1'" >&2; usage >&2; exit 2 ;;
    esac
    shift
done

if [ "$PURGE" -eq 1 ] && [ "$ACTION" != uninstall ]; then
    echo "install.sh: --purge is only valid with --uninstall" >&2
    exit 2
fi

[ "$(id -u)" -eq 0 ] || die "run this script as root, e.g. sudo $0"
command -v systemctl >/dev/null 2>&1 || die "systemd is required (systemctl not found)"

# The group that owns serial ports: dialout on Debian/Ubuntu/Fedora, uucp on Arch and openSUSE.
serial_group() {
    for group in dialout uucp; do
        if getent group "$group" >/dev/null 2>&1; then
            echo "$group"
            return 0
        fi
    done
    echo ""
}

create_account() {
    if ! getent group "$ACCOUNT" >/dev/null 2>&1; then
        info "Creating the group $ACCOUNT"
        groupadd --system "$ACCOUNT"
    fi
    if ! getent passwd "$ACCOUNT" >/dev/null 2>&1; then
        info "Creating the system account $ACCOUNT"
        nologin=/usr/sbin/nologin
        [ -x "$nologin" ] || nologin=/sbin/nologin
        [ -x "$nologin" ] || nologin=/bin/false
        useradd --system --gid "$ACCOUNT" --home-dir "$DATA_DIR" --no-create-home --shell "$nologin" \
            --comment "NutHub UPS server" "$ACCOUNT"
    fi
}

install_unit() {
    group=$(serial_group)
    info "Installing $UNIT_FILE"
    # Keep the packaged unit, adapted to this system: the serial port group, and LF line endings whatever the
    # archive went through.
    tr -d '\r' <"$(packaged nuthub.service)" | {
        if [ -z "$group" ]; then
            sed '/^SupplementaryGroups=/d'
        else
            sed "s/^SupplementaryGroups=.*/SupplementaryGroups=$group/"
        fi
    } >"$UNIT_FILE.tmp"
    chmod 0644 "$UNIT_FILE.tmp"
    mv -f "$UNIT_FILE.tmp" "$UNIT_FILE"
}

install_rules() {
    info "Installing $UDEV_RULES"
    tr -d '\r' <"$(packaged 99-nuthub-ups.rules)" >"$UDEV_RULES"
    chmod 0644 "$UDEV_RULES"
    if command -v udevadm >/dev/null 2>&1; then
        udevadm control --reload-rules || true
        udevadm trigger --subsystem-match=hidraw || true
    fi

    installed_polkit=0
    if [ -d /etc/polkit-1/rules.d ] || [ -d /usr/share/polkit-1/rules.d ]; then
        info "Installing $POLKIT_RULES"
        mkdir -p /etc/polkit-1/rules.d
        tr -d '\r' <"$(packaged 50-nuthub-poweroff.rules)" >"$POLKIT_RULES"
        chmod 0644 "$POLKIT_RULES"
        installed_polkit=1
    fi
    if [ -d /etc/polkit-1/localauthority/50-local.d ]; then
        info "Installing $POLKIT_PKLA"
        tr -d '\r' <"$(packaged 50-nuthub-poweroff.pkla)" >"$POLKIT_PKLA"
        chmod 0644 "$POLKIT_PKLA"
        installed_polkit=1
    fi
    if [ "$installed_polkit" -eq 0 ]; then
        echo "    polkit was not found: NutHub will not be able to power off this machine unless it runs as root."
    fi
}

primary_address() {
    address=""
    if command -v hostname >/dev/null 2>&1; then
        address=$(hostname -I 2>/dev/null | awk '{print $1}') || address=""
    fi
    if [ -z "$address" ] && command -v ip >/dev/null 2>&1; then
        address=$(ip -4 -o addr show scope global 2>/dev/null | awk '{sub(/\/.*/, "", $4); print $4; exit}') || address=""
    fi
    [ -n "$address" ] || address=localhost
    echo "$address"
}

do_install() {
    binary="$HERE/nuthub"
    [ -f "$binary" ] || die "the nuthub executable was not found next to install.sh"

    create_account

    if systemctl is-active --quiet "$SERVICE" 2>/dev/null; then
        info "Stopping the running service for the upgrade"
        systemctl stop "$SERVICE"
    fi

    info "Installing the executable in $PREFIX"
    install -d -m 0755 "$PREFIX"
    install -m 0755 "$binary" "$PREFIX/nuthub.new"
    mv -f "$PREFIX/nuthub.new" "$PREFIX/nuthub"
    for doc in LICENSE README.md; do
        if [ -f "$HERE/$doc" ]; then
            install -m 0644 "$HERE/$doc" "$PREFIX/$doc"
        fi
    done

    # The configuration and data belong to the service account; files left by a run as root are handed over too.
    for dir in "$CONFIG_DIR" "$DATA_DIR" "$CACHE_DIR"; do
        install -d -m 0750 -o "$ACCOUNT" -g "$ACCOUNT" "$dir"
        chown -R "$ACCOUNT:$ACCOUNT" "$dir"
    done

    install_unit
    install_rules

    info "Starting $SERVICE"
    systemctl daemon-reload
    systemctl enable "$SERVICE" >/dev/null
    systemctl restart "$SERVICE"

    # The first start creates the web administrator; give it a moment.
    password_file="$DATA_DIR/initial-admin-password.txt"
    waited=0
    while [ ! -f "$password_file" ] && [ "$waited" -lt 15 ]; do
        if ! systemctl is-active --quiet "$SERVICE"; then
            break
        fi
        sleep 1
        waited=$((waited + 1))
    done

    echo
    if systemctl is-active --quiet "$SERVICE"; then
        echo "NutHub is running."
    else
        echo "NutHub did not start: see 'journalctl -u $SERVICE' and $DATA_DIR/logs/."
    fi
    echo "  Web panel:      http://$(primary_address):8493/"
    echo "  NUT clients:    port 3493"
    echo "  Configuration:  $CONFIG_DIR/nuthub.json"
    echo "  Data and logs:  $DATA_DIR"
    if [ -f "$password_file" ]; then
        echo
        echo "First sign-in (you will be asked to choose a new password; then delete $password_file):"
        sed 's/^/  /' "$password_file"
    fi
    echo
    echo "If a firewall is active, open TCP ports 3493 (NUT) and 8493 (web panel), e.g.:"
    echo "  ufw allow 3493/tcp && ufw allow 8493/tcp"
    echo "  firewall-cmd --permanent --add-port=3493/tcp --add-port=8493/tcp && firewall-cmd --reload"
}

do_uninstall() {
    if [ -f "$UNIT_FILE" ] || systemctl list-unit-files "$SERVICE" >/dev/null 2>&1; then
        info "Stopping and disabling $SERVICE"
        systemctl disable --now "$SERVICE" >/dev/null 2>&1 || true
    fi
    rm -f "$UNIT_FILE"
    systemctl daemon-reload || true

    info "Removing the udev and polkit rules"
    rm -f "$UDEV_RULES" "$POLKIT_RULES" "$POLKIT_PKLA"
    if command -v udevadm >/dev/null 2>&1; then
        udevadm control --reload-rules || true
    fi

    info "Removing $PREFIX"
    rm -rf "$PREFIX"

    if [ "$PURGE" -eq 1 ]; then
        info "Deleting the configuration, the data and the $ACCOUNT account"
        rm -rf "$CONFIG_DIR" "$DATA_DIR" "$CACHE_DIR"
        if getent passwd "$ACCOUNT" >/dev/null 2>&1; then
            userdel "$ACCOUNT" || true
        fi
        if getent group "$ACCOUNT" >/dev/null 2>&1; then
            groupdel "$ACCOUNT" || true
        fi
        echo "NutHub has been removed completely."
    else
        echo "NutHub has been removed. Kept: $CONFIG_DIR, $DATA_DIR and the $ACCOUNT account"
        echo "(run '$0 --uninstall --purge' to delete them)."
    fi
}

case "$ACTION" in
    install) do_install ;;
    uninstall) do_uninstall ;;
esac
