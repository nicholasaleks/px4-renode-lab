#!/usr/bin/env bash
# Stop only this lab's Renode (matched by its state directory), leaving other Renode labs running.
LAB="$(cd "$(dirname "$0")" && pwd)"
pkill -f "Tools/renode/run.py CubeOrange.*$LAB/state" 2>/dev/null
pkill -f "renode .*$LAB/state" 2>/dev/null
for i in $(seq 1 15); do pgrep -f "renode .*$LAB/state" >/dev/null || exit 0; sleep 1; done
pkill -9 -f "renode .*$LAB/state"
