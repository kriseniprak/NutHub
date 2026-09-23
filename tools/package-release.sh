#!/usr/bin/env bash
# Builds the NutHub release archives: one self-contained, single-file executable per runtime, with the install
# scripts and service files, in dist/:
#   nuthub-<version>-win-x64.zip        nuthub.exe, install.ps1, LICENSE, README.md
#   nuthub-<version>-linux-<arch>.tar.gz  nuthub, install.sh, nuthub.service, udev and polkit rules, LICENSE, README.md
# plus a .sha256 file per archive (sha256sum format). The version comes from Directory.Build.props.
#
# Usage: tools/package-release.sh [--rid RID]... [--output DIR] [--artifacts-path DIR]
# Needs: the .NET 10 SDK, tar, and zip or python3 for the Windows archive.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
OUTPUT="$ROOT/dist"
ARTIFACTS="$ROOT/artifacts/release"
RIDS=()

while [ $# -gt 0 ]; do
    case "$1" in
        --rid) RIDS+=("$2"); shift 2 ;;
        --output) OUTPUT=$2; shift 2 ;;
        --artifacts-path) ARTIFACTS=$2; shift 2 ;;
        -h | --help) sed -n '2,10p' "$0"; exit 0 ;;
        *) echo "package-release.sh: unknown option '$1'" >&2; exit 2 ;;
    esac
done
[ ${#RIDS[@]} -gt 0 ] || RIDS=(win-x64 linux-x64 linux-arm64 linux-arm)

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -n 1)
[ -n "$VERSION" ] || { echo "package-release.sh: no <Version> in Directory.Build.props" >&2; exit 1; }
echo "Packaging NutHub $VERSION for: ${RIDS[*]}"

mkdir -p "$OUTPUT"
OUTPUT=$(cd "$OUTPUT" && pwd)
STAGING=$(mktemp -d)
trap 'rm -rf "$STAGING"' EXIT

make_zip() { # archive, folder (inside the staging directory)
    if command -v zip >/dev/null 2>&1; then
        (cd "$STAGING" && zip -q -r -X "$1" "$2")
    else
        python3 - "$1" "$STAGING" "$2" <<'PY'
import os, sys, zipfile
archive, base, folder = sys.argv[1:4]
with zipfile.ZipFile(archive, "w", zipfile.ZIP_DEFLATED) as z:
    for directory, _, files in os.walk(os.path.join(base, folder)):
        for name in sorted(files):
            path = os.path.join(directory, name)
            z.write(path, os.path.relpath(path, base))
PY
    fi
}

copy_docs() { # destination
    for doc in LICENSE README.md; do
        if [ -f "$ROOT/$doc" ]; then
            cp "$ROOT/$doc" "$1/"
        fi
    done
}

for rid in "${RIDS[@]}"; do
    name="nuthub-$VERSION-$rid"
    publish="$ARTIFACTS/publish/$rid"
    echo "==> $rid"
    rm -rf "$publish"
    dotnet publish "$ROOT/src/NutHub/NutHub.csproj" -c Release -r "$rid" -o "$publish" \
        --artifacts-path "$ARTIFACTS" -p:Version="$VERSION" -p:DebugType=embedded -nologo -v:q

    folder="$STAGING/$name"
    mkdir -p "$folder"
    copy_docs "$folder"
    case "$rid" in
        win-*)
            cp "$publish/nuthub.exe" "$folder/"
            cp "$ROOT/packaging/windows/install.ps1" "$folder/"
            archive="$OUTPUT/$name.zip"
            rm -f "$archive"
            make_zip "$archive" "$name"
            ;;
        *)
            cp "$publish/nuthub" "$folder/"
            for file in install.sh nuthub.service 99-nuthub-ups.rules 50-nuthub-poweroff.rules 50-nuthub-poweroff.pkla; do
                tr -d '\r' <"$ROOT/packaging/linux/$file" >"$folder/$file"
            done
            chmod 0755 "$folder/nuthub" "$folder/install.sh"
            chmod 0644 "$folder/nuthub.service" "$folder"/*.rules "$folder"/*.pkla
            archive="$OUTPUT/$name.tar.gz"
            tar --owner=0 --group=0 -C "$STAGING" -czf "$archive" "$name"
            ;;
    esac

    (cd "$OUTPUT" && sha256sum "$(basename "$archive")" >"$(basename "$archive").sha256")
    echo "    $archive"
done

echo "Done: $OUTPUT"
