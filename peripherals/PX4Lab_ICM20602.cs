//
// ICM-20602 on SPI, modeled from the datasheet (DS-000176) closely enough for PX4's
// src/drivers/imu/invensense/icm20602 driver. Derived from ArduPilot's AP_ICM20689
// Renode model (Tools/renode/peripherals/sensors, GPLv3); the differences are what
// PX4's driver relies on:
//
//  * DEVICE_RESET restores datasheet reset values: PWR_MGMT_1 = 0x41, CONFIG = 0x80.
//    PX4 waits for exactly these before configuring, and otherwise resets forever.
//  * The FIFO fills continuously at the 8 kHz gyro rate from Renode virtual time,
//    holds 1008 bytes, and reports overflow as a full FIFO. The original model
//    regenerates its FIFO on every FIFO_COUNTH read; PX4 reads FIFO_COUNTH twice
//    per cycle (count poll, then count+data burst) and needs the samples to persist.
//  * FIFO records follow the ICM-20602 FIFO_EN layout (accel; temp + gyro X/Y/Z), in chip order.
//  * Accel and gyro scaling follow ACCEL_CONFIG / GYRO_CONFIG full-scale bits.
//
// Samples are generated lazily when the firmware reads FIFO_COUNTH, so a fast
// virtual FIFO costs nothing between reads. Physics, orientation and thermal state
// come from the shared AP_PhysicsState, so the physics sidecar drives it.
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.SPI;
using Antmicro.Renode.Peripherals.Miscellaneous;

namespace Antmicro.Renode.Peripherals.Sensors
{
    public class PX4Lab_ICM20602 : ISPIPeripheral, IGPIOReceiver
    {
        public PX4Lab_ICM20602(IMachine machine, byte whoAmI = 0x12, byte rotation = 0)
        {
            this.machine = machine;
            this.whoAmI = whoAmI;
            this.rotation = rotation;
            physics = AP_PhysicsState.ForMachine(machine);
            thermal = AP_BoardThermalState.ForMachine(machine);
            fifo = new Queue<byte>();
            registers = new byte[128];
            Reset();
        }

        public void Reset()
        {
            DeviceReset();
            transferByte = 0;
            reading = false;
            currentReg = 0;
        }

        public byte Transmit(byte value)
        {
            byte response = 0;
            if(transferByte == 0)
            {
                reading = (value & 0x80) != 0;
                currentReg = (byte)(value & 0x7F);
                capturing = reading && currentReg == FIFO_COUNTH;
                if(capturing)
                {
                    capture.Clear();
                    bursts++;
                }
            }
            else if(reading)
            {
                response = ReadRegister(currentReg);
                if(capturing && capture.Count < 64)
                {
                    capture.Add(response);
                }
                if(currentReg != FIFO_R_W)
                {
                    currentReg = (byte)((currentReg + 1) & 0x7F);
                }
            }
            else
            {
                WriteRegister(currentReg, value);
                if(currentReg != FIFO_R_W)
                {
                    currentReg = (byte)((currentReg + 1) & 0x7F);
                }
            }
            transferByte++;
            return response;
        }

        public void FinishTransmission()
        {
            transferByte = 0;
        }

        public void OnGPIO(int number, bool value)
        {
            if(value)
            {
                transferByte = 0; // chip select deasserted: transaction over
            }
        }

        // monitor: sysbus.spi4.spi4Mux.imu0 DebugInfo
        public string DebugInfo
        {
            get
            {
                return string.Format("bursts={0} resets={1} fifoBytes={2} USER_CTRL=0x{3:X2} FIFO_EN=0x{4:X2} CONFIG=0x{5:X2} temp={6:F1}C last=[{7}]",
                    bursts, resets, fifo.Count, registers[USER_CTRL], registers[FIFO_EN], registers[CONFIG],
                    thermal.TemperatureC, BitConverter.ToString(capture.ToArray()));
            }
        }

        private void DeviceReset()
        {
            Array.Clear(registers, 0, registers.Length);
            fifo.Clear();
            resets++;
            registers[WHOAMI] = whoAmI;
            registers[PWR_MGMT_1] = 0x41;   // SLEEP | CLKSEL=1
            registers[CONFIG] = 0x80;       // reserved bit 7 reads 1 after reset
            previousAcceleration = null;
            previousGyro = null;
            lastSampleUs = NowUs();
        }

        private byte ReadRegister(byte reg)
        {
            switch(reg)
            {
            case FIFO_COUNTH:
                Generate();
                return (byte)(fifo.Count >> 8);
            case FIFO_COUNTL:
                return (byte)(fifo.Count & 0xFF);
            case FIFO_R_W:
                return fifo.Count > 0 ? fifo.Dequeue() : (byte)0;
            case INT_STATUS:
                return 0x01; // DATA_RDY_INT
            case TEMP_OUT_H:
                return (byte)(TemperatureRaw() >> 8);
            case TEMP_OUT_L:
                return (byte)TemperatureRaw();
            default:
                return registers[reg];
            }
        }

        private void WriteRegister(byte reg, byte value)
        {
            switch(reg)
            {
            case PWR_MGMT_1:
                if((value & 0x80) != 0)
                {
                    DeviceReset();
                    return;
                }
                registers[reg] = value;
                break;
            case SIGNAL_PATH_RESET:
                registers[reg] = 0; // self-clearing
                break;
            case USER_CTRL:
                if((value & USER_CTRL_FIFO_RST) != 0)
                {
                    fifo.Clear();
                    lastSampleUs = NowUs();
                }
                if((value & USER_CTRL_FIFO_EN) != 0 && (registers[USER_CTRL] & USER_CTRL_FIFO_EN) == 0)
                {
                    lastSampleUs = NowUs();
                }
                registers[reg] = (byte)(value & ~(USER_CTRL_FIFO_RST | USER_CTRL_SIG_COND_RST)); // self-clearing
                break;
            case FIFO_EN:
                if(value != registers[reg])
                {
                    lastSampleUs = NowUs();
                }
                registers[reg] = value;
                break;
            case FIFO_R_W:
                break;
            default:
                registers[reg] = value;
                break;
            }
        }

        private void Generate()
        {
            var now = NowUs();
            var enables = registers[FIFO_EN];
            if((registers[USER_CTRL] & USER_CTRL_FIFO_EN) == 0 || enables == 0 || now <= lastSampleUs)
            {
                if((registers[USER_CTRL] & USER_CTRL_FIFO_EN) == 0 || enables == 0)
                {
                    lastSampleUs = now;
                }
                return;
            }
            // ICM-20602 FIFO_EN: bit 4 GYRO_FIFO_EN writes TEMP and GYRO X/Y/Z, bit 3 ACCEL_FIFO_EN
            // (unlike the MPU-6000 family's per-axis bits)
            var recordSize = ((enables & ACCEL_FIFO_EN) != 0 ? 6 : 0) + ((enables & GYRO_FIFO_EN) != 0 ? 8 : 0);
            var due = (now - lastSampleUs) / SamplePeriodUs;
            if(recordSize == 0 || due < 1)
            {
                return;
            }
            var room = (FifoSize - fifo.Count) / recordSize;
            var count = (int)Math.Min(due, (double)room);
            var overflow = due > room;
            lastSampleUs = overflow ? now : lastSampleUs + count * SamplePeriodUs;

            var truth = physics.Current;
            var acceleration = AP_SensorOrientation.BodyToSensor(truth.SpecificForceMS2, rotation);
            var gyro = AP_SensorOrientation.BodyToSensor(truth.GyroRadS, rotation);
            if(previousAcceleration == null)
            {
                previousAcceleration = acceleration;
                previousGyro = gyro;
            }
            var accelScale = Gravity / (16384 >> ((registers[ACCEL_CONFIG] >> 3) & 3));
            var gyroScale = Math.PI / 180.0 / (131.0 / (1 << ((registers[GYRO_CONFIG] >> 3) & 3)));
            for(var i = 0; i < count; i++)
            {
                var alpha = (i + 1.0f) / count;
                // accel runs at 4 kHz against the 8 kHz gyro: each accel value fills two FIFO records,
                // which PX4 checks (A0==A1, A2==A3) to detect corrupted transfers
                if(heldAcceleration == null || (sampleCounter++ & 1) == 0)
                {
                    heldAcceleration = Interpolate(previousAcceleration, acceleration, alpha);
                }
                var a = heldAcceleration;
                var g = Interpolate(previousGyro, gyro, alpha);
                // chip axes: the same raw words the original model produces for this mounting
                if((enables & ACCEL_FIFO_EN) != 0)
                {
                    PushWord(ScaleWord(a[1], accelScale));
                    PushWord(ScaleWord(a[0], accelScale));
                    PushWord(ScaleWord(-a[2], accelScale));
                }
                if((enables & GYRO_FIFO_EN) != 0)
                {
                    PushWord(TemperatureRaw());
                    PushWord(ScaleWord(g[1], gyroScale));
                    PushWord(ScaleWord(g[0], gyroScale));
                    PushWord(ScaleWord(-g[2], gyroScale));
                }
            }
            if(overflow)
            {
                while(fifo.Count < FifoSize)
                {
                    fifo.Enqueue(0); // a full FIFO is how the driver detects overflow
                }
            }
            previousAcceleration = acceleration;
            previousGyro = gyro;
        }

        private double NowUs()
        {
            return machine.ElapsedVirtualTime.TimeElapsed.TotalMicroseconds;
        }

        private short TemperatureRaw()
        {
            return ScaleWord((float)(thermal.TemperatureC - 25.0), 1.0 / 326.8);
        }

        private static float[] Interpolate(float[] from, float[] to, float alpha)
        {
            return new float[] {
                from[0] + (to[0] - from[0]) * alpha,
                from[1] + (to[1] - from[1]) * alpha,
                from[2] + (to[2] - from[2]) * alpha,
            };
        }

        private static short ScaleWord(float value, double scale)
        {
            return (short)Math.Max(Int16.MinValue, Math.Min(Int16.MaxValue, Math.Round(value / scale)));
        }

        private void PushWord(short value)
        {
            fifo.Enqueue((byte)((value >> 8) & 0xFF));
            fifo.Enqueue((byte)(value & 0xFF));
        }

        private readonly IMachine machine;
        private readonly Queue<byte> fifo;
        private readonly byte[] registers;
        private readonly byte whoAmI;
        private readonly byte rotation;
        private readonly AP_PhysicsState physics;
        private readonly AP_BoardThermalState thermal;
        private float[] previousAcceleration;
        private float[] previousGyro;
        private double lastSampleUs;
        private float[] heldAcceleration;
        private ulong sampleCounter;
        private readonly List<byte> capture = new List<byte>();
        private bool capturing;
        private ulong bursts;
        private ulong resets;
        private int transferByte;
        private bool reading;
        private byte currentReg;

        private const double SamplePeriodUs = 125.0; // 8 kHz gyro output data rate
        private const int FifoSize = 1008;

        private const byte CONFIG = 0x1A;
        private const byte GYRO_CONFIG = 0x1B;
        private const byte ACCEL_CONFIG = 0x1C;
        private const byte FIFO_EN = 0x23;
        private const byte INT_STATUS = 0x3A;
        private const byte TEMP_OUT_H = 0x41;
        private const byte TEMP_OUT_L = 0x42;
        private const byte SIGNAL_PATH_RESET = 0x68;
        private const byte USER_CTRL = 0x6A;
        private const byte PWR_MGMT_1 = 0x6B;
        private const byte FIFO_COUNTH = 0x72;
        private const byte FIFO_COUNTL = 0x73;
        private const byte FIFO_R_W = 0x74;
        private const byte WHOAMI = 0x75;

        private const byte GYRO_FIFO_EN = 0x10;
        private const byte ACCEL_FIFO_EN = 0x08;
        private const byte USER_CTRL_FIFO_EN = 0x40;
        private const byte USER_CTRL_FIFO_RST = 0x04;
        private const byte USER_CTRL_SIG_COND_RST = 0x01;
        private const double Gravity = 9.80665;
    }
}
