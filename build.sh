#!/usr/bin/env bash
# Build PX4 NuttX firmware for the emulated CubeOrange inside Docker, with the lab's patches applied.
#   ./build.sh                      cubepilot_cubeorange_console (NuttX shell on UART7)
#   ./build.sh cubepilot_cubeorange_default
# Output: firmware/<target>.elf (full debug symbols; Renode and GDB both use it)
set -euo pipefail
LAB="$(cd "$(dirname "$0")" && pwd)"
TARGET="${1:-cubepilot_cubeorange_console}"
PX4="$LAB/PX4-Autopilot"

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

docker image inspect px4-nuttx-build >/dev/null 2>&1 || docker build -t px4-nuttx-build "$LAB/docker"
docker volume create px4-ccache >/dev/null
docker run --rm -v "$PX4":/src -v px4-ccache:/ccache px4-nuttx-build make "$TARGET"

mkdir -p "$LAB/firmware"
cp "$PX4/build/$TARGET/$TARGET.elf" "$LAB/firmware/$TARGET.elf"
echo "firmware/$TARGET.elf"
