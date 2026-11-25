#!/usr/bin/env bash
# MAVProxy on the emulated TELEM1 (USART2) socket, forwarded to UDP 14550 for QGroundControl / Mission Planner.
#   ./mavlink.sh                              MAVProxy console (status, watch HEARTBEAT, link, ...)
#   ./mavlink.sh --out udp:127.0.0.1:14551    extra output for a second tool
# The socket takes one client at a time; let this be that client and fan out with --out.
LAB="$(cd "$(dirname "$0")" && pwd)"
SOCK="$LAB/state/CubeOrange/telem1.sock"
[ -S "$SOCK" ] || { echo "no TELEM1 socket: start ./run.sh first" >&2; exit 1; }
mkdir -p "$LAB/mavproxy"
exec mavproxy.py --master "uds:$SOCK" --baudrate 57600 --out udp:127.0.0.1:14550 --state-basedir "$LAB/mavproxy" "$@"
