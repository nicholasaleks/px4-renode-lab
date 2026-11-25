# PX4 NuttX firmware build environment (arm64 or amd64 host).
# PX4's own Tools/setup/ubuntu.sh skips gcc-arm-none-eabi on aarch64; Ubuntu 24.04 ships 13.2.rel1 for both arches,
# which is the version PX4 v1.17 expects.
#   docker build -t px4-nuttx-build docker/
FROM ubuntu:24.04

ENV DEBIAN_FRONTEND=noninteractive LANG=C.UTF-8 LC_ALL=C.UTF-8
RUN apt-get update -qq && apt-get install -y --no-install-recommends \
      build-essential cmake ninja-build ccache git ca-certificates curl file zip unzip \
      gcc-arm-none-eabi libnewlib-arm-none-eabi libstdc++-arm-none-eabi-newlib binutils-arm-none-eabi gdb-multiarch \
      genromfs kconfig-frontends gperf flex bison gettext texinfo libncurses-dev libtool automake pkg-config \
      python3 python3-pip python3-dev python3-venv rsync xxd \
 && rm -rf /var/lib/apt/lists/*

COPY requirements.txt /tmp/requirements.txt
RUN pip3 install --no-cache-dir --break-system-packages -r /tmp/requirements.txt

RUN git config --system --add safe.directory '*'
ENV CCACHE_DIR=/ccache
WORKDIR /src
CMD ["/bin/bash"]
