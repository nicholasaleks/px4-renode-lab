#!/usr/bin/env bash
# Everything from a fresh clone to runnable firmware (each step is skipped once done):
#   1. ./setup.sh: ArduPilot board tooling, Python env, Renode (what ./run.sh needs)
#   2. PX4-Autopilot checkout (PX4_REF, default v1.17.0) with its submodules
#   3. the lab's patches applied, firmware built in Docker -> firmware/<target>.elf
#   ./build.sh                      cubepilot_cubeorange_console (NuttX shell on UART7)
#   ./build.sh cubepilot_cubeorange_default
# Output: firmware/<target>.elf (full debug symbols; Renode and GDB both use it)
set -euo pipefail
LAB="$(cd "$(dirname "$0")" && pwd)"
TARGET="${1:-cubepilot_cubeorange_console}"
PX4="$LAB/PX4-Autopilot"
PX4_REF="${PX4_REF:-v1.17.0}"
PX4_URL="https://github.com/PX4/PX4-Autopilot.git"

if ! command -v docker >/dev/null; then
    echo "docker not found. Install Docker (Debian/Kali: sudo apt install docker.io && sudo usermod -aG docker \$USER, then log in again)" >&2
    exit 1
fi
if ! docker info >/dev/null 2>&1; then
    echo "cannot reach the Docker daemon: start it (sudo systemctl start docker) and check you are in the docker group" >&2
    exit 1
fi

"$LAB/setup.sh"

if [ ! -d "$PX4/.git" ]; then
    echo "fetching PX4-Autopilot $PX4_REF with submodules into PX4-Autopilot/ (a few minutes)"
    rm -rf "$PX4.partial"
    git -c advice.detachedHead=false clone --quiet --depth 1 --branch "$PX4_REF" \
        --recurse-submodules --shallow-submodules "$PX4_URL" "$PX4.partial"
    mv "$PX4.partial" "$PX4"
fi

# patches/<name>.patch applies to the NuttX tree; patches/px4-<name>.patch to PX4 itself
apply() {  # dir patch
    if git -C "$1" apply --reverse --check "$2" 2>/dev/null; then return; fi
    git -C "$1" apply "$2" && echo "applied $(basename "$2")"
}
for p in "$LAB"/patches/*.patch; do
    case "$(basename "$p")" in
        px4-*) apply "$PX4" "$p" ;;
        *)     apply "$PX4/platforms/nuttx/NuttX/nuttx" "$p" ;;
    esac
done

# Image is rebuilt when docker/ changes (tagged by its content hash).
TAG="$(cat "$LAB/docker/Dockerfile" "$LAB/docker/requirements.txt" | git hash-object --stdin | cut -c1-12)"
IMAGE="px4-renode-lab-build:$TAG"
docker image inspect "$IMAGE" >/dev/null 2>&1 || docker build -t "$IMAGE" "$LAB/docker"

# Build as the host user so PX4-Autopilot/build and the compiler cache stay owned by you (matters on Linux).
mkdir -p "$LAB/.ccache"
docker run --rm --user "$(id -u):$(id -g)" -e HOME=/tmp -e CCACHE_DIR=/ccache \
    -v "$PX4":/src -v "$LAB/.ccache":/ccache "$IMAGE" make "$TARGET"

mkdir -p "$LAB/firmware"
cp "$PX4/build/$TARGET/$TARGET.elf" "$LAB/firmware/$TARGET.elf"
echo "firmware/$TARGET.elf   next: ./run.sh"
