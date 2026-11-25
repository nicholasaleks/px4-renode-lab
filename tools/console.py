#!/usr/bin/env python3
"""Talk to an emulated UART exposed by Renode as a Unix domain socket.

  tools/console.py state/CubeOrange/APM-UDS-serial5              interactive (Ctrl-] to quit)
  tools/console.py SOCKET --log boot.log --send 'ps' --for 20    scripted: send lines, capture for N seconds
"""
import argparse, os, select, socket, sys, termios, time, tty

ap = argparse.ArgumentParser()
ap.add_argument("socket")
ap.add_argument("--send", action="append", default=[], help="line to send (repeatable), scripted mode")
ap.add_argument("--for", dest="duration", type=float, help="scripted mode: seconds to capture, then exit")
ap.add_argument("--log", help="also append everything received to this file")
ap.add_argument("--wait", type=float, default=60, help="seconds to wait for the socket to appear")
ap.add_argument("--gap", type=float, default=4.0, help="scripted mode: seconds between sent lines")
a = ap.parse_args()

deadline = time.time() + a.wait
while True:
    try:
        s = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        s.connect(a.socket)
        break
    except OSError:
        if time.time() > deadline:
            sys.exit(f"no listener on {a.socket}")
        time.sleep(0.5)

log = open(a.log, "ab") if a.log else None
out = sys.stdout.buffer

def emit(b):
    out.write(b.replace(b"\n", b"\r\n") if interactive else b)
    out.flush()
    if log:
        log.write(b); log.flush()

interactive = a.duration is None and not a.send
if not interactive:
    end = time.time() + (a.duration or 10)
    queue = list(a.send)
    next_send = time.time() + 1.0
    while time.time() < end:
        r, _, _ = select.select([s], [], [], 0.1)
        if r:
            d = s.recv(4096)
            if not d:
                break
            emit(d)
        if queue and time.time() >= next_send:
            s.sendall(queue.pop(0).encode() + b"\r\n")
            next_send = time.time() + a.gap
    sys.exit(0)

fd = sys.stdin.fileno()
old = termios.tcgetattr(fd)
tty.setraw(fd)
try:
    sys.stderr.write(f"connected to {a.socket}; Ctrl-] quits\r\n")
    while True:
        r, _, _ = select.select([s, fd], [], [])
        if s in r:
            d = s.recv(4096)
            if not d:
                break
            emit(d)
        if fd in r:
            k = os.read(fd, 1024)
            if b"\x1d" in k:
                break
            s.sendall(k)
finally:
    termios.tcsetattr(fd, termios.TCSADRAIN, old)
    print()
