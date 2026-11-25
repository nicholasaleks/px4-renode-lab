#!/usr/bin/env bash
# Interactive NuttX/PX4 shell on the emulated UART7 (Ctrl-] quits; reconnect any time).
LAB="$(cd "$(dirname "$0")" && pwd)"
exec python3 "$LAB/tools/console.py" "$LAB/state/CubeOrange/APM-UDS-serial5" "$@"
