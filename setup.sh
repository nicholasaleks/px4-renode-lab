#!/usr/bin/env bash
# One-time setup, all inside this directory: ArduPilot's Renode board tooling (ardupilot/), the lab's own Python
# environment (.venv/) and Renode (renode/).
#   ./setup.sh               install whatever is missing
#   ./setup.sh --force       reinstall .venv/ and renode/
# ardupilot/ is a shallow checkout of the ArduPilot commit this lab is tested with (ARDUPILOT_REF overrides it).
# ARDUPILOT=/path/to/checkout uses an existing ArduPilot tree instead (pass the same variable to ./run.sh).
# Renode is the patched build the board tooling is tested against, from its published packages
# (latest.json, size and SHA-256 checked). RENODE_SOURCE_REVISION=<40-hex> refuses any other build.
set -euo pipefail
# Host tools: git/curl fetch, objcopy (the board tooling makes the flash image), pgrep (run.sh/stop.sh).
MISSING=()
for tool in git:git curl:curl pgrep:procps; do
    command -v "${tool%%:*}" >/dev/null || MISSING+=("${tool#*:}")
done
command -v arm-none-eabi-objcopy >/dev/null || command -v objcopy >/dev/null || MISSING+=(binutils)
if [ "$(uname -s)" = Darwin ]; then
    [ ${#MISSING[@]} -eq 0 ] || { echo "missing objcopy; macOS: brew install arm-none-eabi-binutils" >&2; exit 1; }
else
    # Renode (.NET) needs ICU on Linux
    { ldconfig -p 2>/dev/null || true; } | grep -q 'libicuuc\.so' || MISSING+=(libicu-dev)
    [ ${#MISSING[@]} -eq 0 ] || { echo "missing host packages; Debian/Kali/Ubuntu: sudo apt install ${MISSING[*]}" >&2; exit 1; }
fi
LAB="$(cd "$(dirname "$0")" && pwd)"
AP="${ARDUPILOT:-$LAB/ardupilot}"
AP_REF="${ARDUPILOT_REF:-437a3301cc335742e5807e6f10e1b819e0a5afa9}"
AP_URL="https://github.com/ArduPilot/ardupilot.git"
BASE_URL="${RENODE_PACKAGE_BASE_URL:-https://firmware.ardupilot.org/Tools/Renode}"
VENV="$LAB/.venv"
RENODE_DIR="$LAB/renode"

if [ "${1:-}" = "--force" ]; then rm -rf "$VENV" "$RENODE_DIR"; fi

if [ ! -f "$AP/Tools/renode/run.py" ]; then
    if [ -n "${ARDUPILOT:-}" ]; then
        echo "board tooling not found at $AP/Tools/renode (ARDUPILOT must point at an ArduPilot checkout)" >&2
        exit 1
    fi
    # Only the tree is needed (board hwdefs, IO bootloader, Tools/renode), no history.
    echo "fetching ArduPilot ${AP_REF:0:10} into ardupilot/ (board tooling)"
    rm -rf "$AP.partial"
    git init --quiet "$AP.partial"
    git -C "$AP.partial" fetch --quiet --depth 1 "$AP_URL" "$AP_REF"
    git -C "$AP.partial" -c advice.detachedHead=false checkout --quiet FETCH_HEAD
    mv "$AP.partial" "$AP"
fi
# Board generation reads the STM32 DMAMUX headers from the ChibiOS submodule; no other submodule is needed.
if [ ! -f "$AP/modules/ChibiOS/os/hal/ports/STM32/STM32H7xx/stm32_dmamux.h" ]; then
    echo "fetching ArduPilot's ChibiOS submodule (board generation)"
    git -C "$AP" submodule update --quiet --init --depth 1 modules/ChibiOS
fi

# Python: run.py needs pyfatfs 1.1.0 (SD images; its 'fs' dependency imports pkg_resources, gone in
# setuptools 81), intelhex and pymavlink; ./mavlink.sh needs MAVProxy. Prefer 3.12, which this lab is tested with.
PIP_PACKAGES=("pyfatfs==1.1.0" intelhex pymavlink MAVProxy "setuptools<81")
[ "$(uname -s)" = Darwin ] && PIP_PACKAGES+=(gnureadline)  # MAVProxy's console imports it on macOS
if [ ! -x "$VENV/bin/python" ]; then
    PY="${PYTHON:-$(command -v python3.12 || command -v python3 || true)}"
    [ -n "$PY" ] || { echo "python3 not found (Debian/Kali: sudo apt install python3 python3-venv)" >&2; exit 1; }
    echo "creating .venv with $PY ($("$PY" --version))"
    "$PY" -m venv "$VENV" || { rm -rf "$VENV"; echo "venv creation failed (Debian/Kali: sudo apt install python3-venv)" >&2; exit 1; }
    "$VENV/bin/pip" install --quiet --upgrade pip
fi
if [ "$(cat "$VENV/.lab-packages" 2>/dev/null)" != "${PIP_PACKAGES[*]}" ]; then
    echo "installing Python packages into .venv"
    "$VENV/bin/pip" install --quiet "${PIP_PACKAGES[@]}"
    echo "${PIP_PACKAGES[*]}" > "$VENV/.lab-packages"
fi

case "$(uname -s)-$(uname -m)" in
    Darwin-arm64)  ARTIFACT=renode-macos-arm64;   EXE="$RENODE_DIR/Renode.app/Contents/MacOS/renode" ;;
    Darwin-x86_64) ARTIFACT=renode-macos-x86_64;  EXE="$RENODE_DIR/Renode.app/Contents/MacOS/renode" ;;
    Linux-x86_64)  ARTIFACT=renode-linux-x86_64;  EXE="$RENODE_DIR/renode" ;;
    Linux-aarch64) ARTIFACT=renode-linux-aarch64; EXE="$RENODE_DIR/renode" ;;
    *) echo "no published Renode package for $(uname -s) $(uname -m)" >&2; exit 1 ;;
esac

if [ ! -x "$EXE" ]; then
    TMP="$(mktemp -d "$LAB/.renode-download.XXXXXX")"
    trap 'hdiutil detach -quiet "$TMP/mnt" 2>/dev/null || true; rm -rf -- "$TMP"' EXIT
    curl --fail --location --silent --show-error --retry 5 --output "$TMP/latest.json" "$BASE_URL/latest.json"
    # filename, sha256, size, revision of this platform's single package
    { read -r FILE; read -r SHA; read -r SIZE; read -r REV; } < <("$VENV/bin/python" -I - "$TMP/latest.json" "$ARTIFACT" \
        "${RENODE_SOURCE_REVISION:-}" <<'PY'
import json, re, sys
m = json.load(open(sys.argv[1]))
if m.get("schema_version") != 1: sys.exit("unsupported Renode manifest schema")
(entry,) = [a for a in m["artifacts"] if a["artifact"] == sys.argv[2]]
(pkg,) = [p for p in entry["packages"] if p["filename"].endswith((".dmg", ".tar.gz"))]
rev = m["source"]["revision"]
if not re.fullmatch(r"[A-Za-z0-9._+-]+", pkg["filename"]): sys.exit("unsafe package filename")
if not re.fullmatch(r"[0-9a-f]{64}", pkg["sha256"]): sys.exit("invalid package SHA-256")
if sys.argv[3] and rev != sys.argv[3]: sys.exit(f"Renode revision is {rev}, expected {sys.argv[3]}")
print(pkg["filename"], pkg["sha256"], pkg["size"], rev, sep="\n")
PY
)
    echo "downloading $FILE (Renode ${REV:0:9})"
    curl --fail --location --progress-bar --retry 5 --output "$TMP/$FILE" "$BASE_URL/$FILE"
    [ "$(wc -c < "$TMP/$FILE" | tr -d ' ')" = "$SIZE" ] || { echo "size mismatch for $FILE" >&2; exit 1; }
    GOT="$("$VENV/bin/python" -I -c 'import hashlib, sys; h = hashlib.sha256(); f = open(sys.argv[1], "rb"); [h.update(b) for b in iter(lambda: f.read(1 << 20), b"")]; print(h.hexdigest())' "$TMP/$FILE")"
    [ "$GOT" = "$SHA" ] || { echo "SHA-256 mismatch for $FILE" >&2; exit 1; }
    mkdir -p "$RENODE_DIR"
    case "$FILE" in
        *.dmg)
            hdiutil attach -quiet -nobrowse -readonly -mountpoint "$TMP/mnt" "$TMP/$FILE"
            APP="$(find "$TMP/mnt" -maxdepth 1 -name '*.app' | head -1)"
            [ -n "$APP" ] || { echo "no .app in $FILE" >&2; exit 1; }
            ditto "$APP" "$RENODE_DIR/Renode.app"
            ;;
        *.tar.gz) tar -xzf "$TMP/$FILE" --strip-components=1 -C "$RENODE_DIR" ;;
    esac
    echo "$REV" > "$RENODE_DIR/REVISION"
fi

# Model data the board tooling would otherwise download on every ./run.sh (STM32H743 register map, SHA-256 checked),
# kept in renode-data/ so running needs no network.
"$VENV/bin/python" -I -c 'import sys; sys.path.insert(0, sys.argv[1]); import renode_data; renode_data.ensure_mcu_data("STM32H743xx", sys.argv[2])' \
    "$AP/Tools/renode" "$LAB/renode-data"

echo "python: $("$VENV/bin/python" --version)   renode: $("$EXE" --version 2>/dev/null | head -1) ($(cut -c1-9 "$RENODE_DIR/REVISION" 2>/dev/null || echo '?'))"
echo "ready: ./run.sh"
