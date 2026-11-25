#!/usr/bin/env bash
# One-time setup: the lab's own Python environment (.venv/) and Renode (renode/), both inside this directory.
#   ./setup.sh               install whatever is missing
#   ./setup.sh --force       reinstall both
# Renode is the patched build the board tooling is tested against, from its published packages
# (latest.json, size and SHA-256 checked). RENODE_SOURCE_REVISION=<40-hex> refuses any other build.
set -euo pipefail
LAB="$(cd "$(dirname "$0")" && pwd)"
AP="${ARDUPILOT:-$HOME/Projects/ardupilot}"
BASE_URL="${RENODE_PACKAGE_BASE_URL:-https://firmware.ardupilot.org/Tools/Renode}"
VENV="$LAB/.venv"
RENODE_DIR="$LAB/renode"

if [ "${1:-}" = "--force" ]; then rm -rf "$VENV" "$RENODE_DIR"; fi

if [ ! -f "$AP/Tools/renode/run.py" ]; then
    echo "board tooling not found at $AP/Tools/renode (set ARDUPILOT to your checkout, or clone it):" >&2
    echo "  git clone --depth 1 https://github.com/ArduPilot/ardupilot.git $AP" >&2
    exit 1
fi

# Python: run.py needs pyfatfs 1.1.0 (SD images; its 'fs' dependency imports pkg_resources, gone in
# setuptools 81), intelhex and pymavlink. Prefer 3.12, which this lab is tested with.
if [ ! -x "$VENV/bin/python" ]; then
    PY="${PYTHON:-$(command -v python3.12 || command -v python3)}"
    echo "creating .venv with $PY ($("$PY" --version))"
    "$PY" -m venv "$VENV"
    "$VENV/bin/pip" install --quiet --upgrade pip
    "$VENV/bin/pip" install --quiet "pyfatfs==1.1.0" intelhex pymavlink "setuptools<81"
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
    echo "$SHA  $TMP/$FILE" | shasum -a 256 --check --status || { echo "SHA-256 mismatch for $FILE" >&2; exit 1; }
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

echo "python: $("$VENV/bin/python" --version)   renode: $("$EXE" --version 2>/dev/null | head -1) ($(cut -c1-9 "$RENODE_DIR/REVISION" 2>/dev/null || echo '?'))"
echo "ready: ./run.sh"
