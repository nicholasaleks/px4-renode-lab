# PX4 lab: PX4 + NuttX on an emulated CubeOrange in Renode

Real PX4 v1.17.0 NuttX firmware (the `cubepilot_cubeorange_console` build) running on an emulated
CubePilot CubeOrange: STM32H743 flight MCU plus the STM32F100 IO coprocessor, in Renode. No hardware,
no SITL: the same binary you would flash, executing on emulated silicon, so you can watch NuttX tasks,
drivers, buses and registers.

## Requirements

macOS (Apple silicon or Intel) or Linux (x86_64 or arm64; tested on macOS and Kali) with:

- git, curl, binutils, procps, Python 3.10+ with venv, and on Linux libicu for Renode
  (Debian/Kali: `sudo apt install git curl binutils procps python3 python3-venv libicu-dev`;
  macOS: `brew install arm-none-eabi-binutils`)
- Docker, usable without sudo, to build the firmware (Debian/Kali: `sudo apt install docker.io`,
  `sudo usermod -aG docker $USER`, log in again)
- ~6 GB of disk, mostly the PX4 and ArduPilot checkouts

Everything else is fetched into this directory (all gitignored):

- `ardupilot/`: a shallow checkout of [ArduPilot](https://github.com/ArduPilot/ardupilot) at the commit this lab is
  tested with, plus its ChibiOS submodule. Its `Tools/renode` generates the CubeOrange board model and launches Renode.
  It is used unmodified; everything PX4/NuttX needs differently lives here. To use an existing checkout instead, set
  `ARDUPILOT=/path/to/ardupilot` for `./build.sh`, `./setup.sh` and `./run.sh`.
- `.venv/`: Python for the board tooling, plus MAVProxy.
- `renode/`: the patched Renode build the board tooling is tested against (size and SHA-256 checked).
- `PX4-Autopilot/`: PX4 v1.17.0 with submodules (`PX4_REF` selects another tag or branch).

## Use

```sh
git clone <this repo> px4-renode-lab && cd px4-renode-lab
./build.sh               # once: fetches and sets up everything, builds the firmware (first run ~20-40 min)
./run.sh                 # terminal 1: boots in ~1 min to nsh> (Ctrl-C or ./stop.sh to stop)
./console.sh             # terminal 2: NuttX shell on UART7 (Ctrl-] quits; reconnect any time)
./mavlink.sh             # terminal 3 (optional): MAVProxy on TELEM1, forwarded to UDP 14550 for QGroundControl
telnet localhost 1244    # Renode monitor: cpu PC, peripherals, sysbus ReadDoubleWord 0x..., pause/start
./stop.sh                # stops only this lab (other Renode instances keep running)
```

`./setup.sh` alone installs just the runtime (`ardupilot/`, `.venv/`, `renode/`), for when you already have an ELF:
`./run.sh path/to/firmware.elf`.

Things to try at `nsh>`:

| Command | Shows |
|---|---|
| `ps` | NuttX tasks/threads: priority, policy, state, what each is blocked on, stack use |
| `top` | CPU load per task (any key exits) |
| `work_queue status` | PX4 work queues (one per SPI/I2C bus, rate_ctrl, INS0..2, ...) and their items |
| `uorb top` | uORB topics and publish rates |
| `listener sensor_gyro` / `sensor_accel` / `sensor_baro` / `sensor_mag` | live driver output |
| `icm20602 status`, `ms5611 status`, `px4io status` | per-driver perf counters |
| `free`, `dmesg`, `ver all`, `param show CAL_*` | heap, boot log, versions, parameters |

It runs at about 0.2x real time. Firmware time is virtual, so behaviour is the same as full speed; only
wall-clock tools (typing, QGC timeouts) notice. The console occasionally drops the first characters of a
fast-typed line; retype it.

Where the host time goes (measured with `cpu ExecutedInstructions` per machine and PC sampling):

| Load | Cost | Note |
|---|---|---|
| 3 IMUs, polled SPI | ~0.66x -> 0.2x | ~100 M guest instructions per virtual second of `spi_send` busy-polling: 8-9 kHz FIFOs x 3 chips, every byte is several emulated MMIO accesses. SPI DMA is off (fix 6); getting it back would move that into the model |
| IO coprocessor | fixed | its firmware never sleeps; capped at the real chip's 24 MIPS (fix 13) |
| everything else | small | idle is `WFI` (fix 2) |

For a faster but less complete drone, stop IMUs at `nsh>`: `icm20948 stop` (also removes the compass)
gives ~0.3x.

## What works

- Boot to `nsh>`; all PX4 modules start; airframe 4001 (generic quad X) loaded
- PX4 flashes its own `cubepilot_io-v2` firmware into the emulated F100 through the real IO bootloader
- Sensors: 3 IMUs (ICM20602, ICM20948, ICM20649), 2 barometers (MS5611), compass (AK09916), all calibrated
- ADC: battery voltage/current (from the physics model) and the 5 V rail; ADC3 internal temperature
- SD card mounted at `/fs/microsd`: logger, dataman, parameter backup
- Parameters persist in emulated FRAM (`state/CubeOrange/fram.img`)
- MAVLink on TELEM1 (`./mavlink.sh`): heartbeat, attitude, local position, status
- Preflight: no hardware/sensor failures left. Remaining flags are external inputs (below)

## Not yet working

| Item | State |
|---|---|
| GPS | u-blox model attached on UART4 (PX4 GPS1). PX4's baud-probe/CFG-VALSET handshake never sees its ACK while the model streams NAV data; needs a UART4 RX trace in NuttX. Without it: no position/home, so position modes and missions refuse |
| R/C | emulated SBUS goes into the IO chip (`--rc-channels`), but `input_rc` is never published; not investigated |
| Physics loop | the physics sidecar (`renode-physics`) is not built; the vehicle sits still on a bench. The board tooling wires the IO chip's PWM timers to the physics actuators, so PX4's IO firmware should drive them too (untested) |
| Speed | ~0.2x real time, IMU SPI polling bound (see above) |

## Build

```sh
./build.sh                               # -> firmware/cubepilot_cubeorange_console.elf
./build.sh cubepilot_cubeorange_default  # other PX4 targets
```

The build runs in Docker (`docker/`, Ubuntu 24.04, native arm64 or amd64) as your own user, with the compiler cache
in `.ccache/`. Re-running only rebuilds what changed; delete `PX4-Autopilot/` to start from a clean checkout.

The ELF keeps full debug symbols, so Renode shows function names (`sysbus FindSymbolAt <addr>`).
`./run.sh --gdb` should give a GDB session (needs `arm-none-eabi-gdb`
on the Mac, or `--gdb-bin`); not tried yet in this lab. PX4's official v1.17.0 release
images (no symbols, no lab patches) also boot, but have the problems listed under fixes 2 and 7.

## Fixes, and why each was needed

Each is a place where NuttX/PX4 uses the hardware in a way the Renode board models did not handle.
Most are real fidelity gaps in the emulator.

| # | Symptom | Cause | Fix (where) |
|---|---|---|---|
| 1 | Hang at boot, PC spinning at `0x0802be24` | NuttX USB init sets PWR_CR3.USB33DEN and waits for USB33RDY; the PWR model never sets it | read hook on `sysbus.pwr` (`run.sh`) |
| 2 | Emulation 22x slower than real time | NuttX's generic `up_idle()` has its `WFI` compiled out, so idle spins at full MIPS | `patches/nuttx-idle-wfi.patch` |
| 3 | IRQ storm on USART6, IO coprocessor `bad sync` | the board's synthetic IOMCU model does not speak PX4's IO protocol; PX4 talks to the IO bootloader | `--real-iomcu`: real F100 + bootloader, PX4 flashes its own IO firmware |
| 4 | IMUs never leave reset | PX4 waits for datasheet reset values (PWR_MGMT_1 = 0x41, CONFIG = 0x80) | `peripherals/PX4Lab_ICM20602.cs`, `PX4Lab_ICM20948.cs` |
| 5 | IMU data rejected | PX4 needs a free-running FIFO at 8/9 kHz, records per FIFO_EN (ICM20602's bit 4 = temp + 3 gyro axes), accel held for two records (4 kHz vs 8 kHz) | same model files |
| 6 | Renode crash in `STM32DMA.PerformTransfer` | NuttX writes SPI_CFG1 DMA-enable bits while the SPI is enabled; the SPI model rejects them and SPI/DMA desync on long transfers | `patches/px4-cubeorange-console-spi-nodma.patch` (polled SPI on IMU buses) |
| 7 | `board_adc sample timeout` | PX4 does single software-triggered conversions with a 50 us timeout; the model converts on a 10 kHz background tick | `peripherals/PX4Lab_STM32H7_ADC.cs` (ADC1/2 and ADC3) |
| 8 | SD: card rejected after CMD8 | Renode's SDCard doesn't echo the CMD8 check pattern (0xAA); the SD spec requires it and NuttX checks | read hook on `SDMMC_RESP1` (`run.sh`) |
| 9 | SD: block reads never complete | NuttX starts data transfers with CMDTRANS and leaves DCTRL.DTEN clear; the IDMA pump arms only on DTEN | write hook on `SDMMC_DCTRL` (`run.sh`) |
| 10 | SD: `mount failed: Invalid argument` | the generated board makes a 256 MiB card but formats a 512 MiB FAT32 image into it; NuttX checks FAT size against card capacity | own 256 MiB image `state/CubeOrange/sdcard-px4.img` (`run.sh`) |
| 11 | Preflight: no airframe, uncalibrated sensors | factory-fresh parameters | `SYS_AUTOSTART 4001`, gyro + quick accel calibration, `CAL_MAG0_ID` (stored in FRAM) |
| 12 | Emulation drops to 0.03x after typing on the console (or MAVLink input) | the USART model (`AP_STM32F7_USART_Idle`) raises the USART IRQ on every idle line even with CR1.IDLEIE off; NuttX never enabled IDLE so never clears it, and `up_interrupt` re-enters forever | read hook on USART2/3, UART7/8 ISR acknowledges IDLE when IDLEIE is off (`run.sh`) |
| 13 | Fixed 3 host-seconds per virtual second even with the FMU idle | generated board runs the F100 IO chip at 60 MIPS, flat out (no sleep in IO firmware); real chip is 24 MHz | `cpu PerformanceInMips 24` on `iomcu` (`run.sh`) |
| 14 | SD writes take ~260 ms per block; `param save` blocks the shell for minutes after boot (the logger's multi-block writes hold the card) | after each write NuttX waits for the card to release busy: a rising edge on D0 (PC8) via EXTI line 8 (`CONFIG_MMCSD_SDIOWAIT_WRCOMPLETE`). The model has no D0 line, so every write waits out its timeout | write hook on `EXTI_CPUIMR1`: when line 8 is unmasked, pulse PC8 low-high (`run.sh`) |

Fixes 1, 8, 9, 10, 12 and 14, and the IMU reset values, are worth reporting upstream to the board
models: they are model bugs that other firmware happens not to exercise.

How the run works: `run.sh` calls the board tooling's `Tools/renode/run.py` (board generation, flash and FRAM
state, real IOMCU, GPS/RC devices) and passes `--exec` commands that pause the emulation right after
start, swap in this lab's models, install the hooks, re-attach the analog feeds, then resume.
`PAUSED_EXEC="cmd1;cmd2" ./run.sh` adds your own monitor commands at that point, for example
`PAUSED_EXEC='logLevel -1 sysbus.sdmmc;sysbus LogPeripheralAccess sysbus.sdmmc true'` to trace every
SD controller register access from reset.

## Layout

| Path | What |
|---|---|
| `build.sh` | full setup: runs `setup.sh`, fetches `PX4-Autopilot/`, applies `patches/`, builds in Docker |
| `setup.sh` | runtime only: `ardupilot/` (board tooling), `.venv/` (Python + MAVProxy) and `renode/` (Renode) |
| `run.sh`, `stop.sh`, `console.sh`, `mavlink.sh` | run / stop / NuttX shell / MAVLink |
| `docker/` | firmware build image |
| `patches/` | `nuttx-*.patch` apply to PX4's NuttX tree, `px4-*.patch` to PX4 |
| `peripherals/` | PX4-compatible Renode models (C#, compiled by Renode at load time) |
| `tools/console.py` | UART socket client (interactive, or scripted `--send ... --for N`) |
| `ardupilot/` | ArduPilot checkout for the Renode board tooling (`Tools/renode`), fetched by `setup.sh` |
| `PX4-Autopilot/` | PX4 v1.17.0 checkout, fetched and patched by `build.sh` |
| `firmware/` | built ELFs |
| `state/CubeOrange/` | flash, FRAM (parameters), SD image, sockets: delete to factory-reset |
| `logs/` | Renode and console logs from runs (safe to delete) |
