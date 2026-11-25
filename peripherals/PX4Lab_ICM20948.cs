//
// ICM-20948 / ICM-20649 on SPI (banked register map), modeled closely enough for PX4's
// src/drivers/imu/invensense/icm20948 and icm20649 drivers, including the ICM-20948's
// AK09916 magnetometer behind the I2C master. Derived from ArduPilot's AP_InvensenseV2
// Renode model (Tools/renode/peripherals/sensors, GPLv3); differences PX4 relies on:
//
//  * DEVICE_RESET restores the datasheet PWR_MGMT_1 reset value 0x41 (SLEEP | CLKSEL=1);
//    PX4 waits for exactly that before configuring, and otherwise resets forever.
//  * FIFO records follow FIFO_EN_2 (accel, gyro X/Y/Z, temp, in chip order): PX4 enables
//    accel + gyro only (12-byte records).
//  * Output data rate follows GYRO_CONFIG_1: FCHOICE=0 (PX4) is the unfiltered 9 kHz
//    stream, FCHOICE=1 is 1125 Hz / (1 + GYRO_SMPLRT_DIV).
//  * 512-byte FIFO; overflow reports a full FIFO, which PX4 detects and resets.
//  * Accel and gyro scaling follow the configured full-scale bits (20649 ranges differ).
//
// Samples are generated lazily from Renode virtual time when FIFO_COUNTH is read, so a
// 9 kHz FIFO needs no 9 kHz timer events.
//
using System;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals.Miscellaneous;
using Antmicro.Renode.Peripherals.SPI;

namespace Antmicro.Renode.Peripherals.Sensors
{
    public class PX4Lab_ICM20948 : ISPIPeripheral
    {
        public PX4Lab_ICM20948(IMachine machine, byte whoAmI = 0xEA, byte rotation = 0)
        {
            this.machine = machine;
            this.whoAmI = whoAmI;
            this.rotation = rotation;
            physics = AP_PhysicsState.ForMachine(machine);
            thermal = AP_BoardThermalState.ForMachine(machine);
            registers = new byte[BankCount, RegisterCount];
            fifo = new Queue<byte>();
            if(whoAmI == WhoAmIIcm20948)
            {
                auxiliaryCompass = new AP_AK09916(machine)
                {
                    CompensateExternalProbeRotation = false,
                };
            }
            Reset();
        }

        public void Reset()
        {
            ResetRegisters();
            transferByte = 0;
            reading = false;
            currentRegister = 0;
        }

        public byte Transmit(byte value)
        {
            if(transferByte++ == 0)
            {
                reading = (value & ReadFlag) != 0;
                currentRegister = (byte)(value & RegisterMask);
                if(reading && currentBank == 0 && IsExternalSensorData(currentRegister))
                {
                    UpdateAuxiliaryReads(); // snapshot slave data once per transfer
                }
                return 0;
            }

            if(reading)
            {
                var result = ReadRegister(currentRegister);
                if(currentRegister != FifoReadWrite)
                {
                    currentRegister = (byte)((currentRegister + 1) & RegisterMask);
                }
                return result;
            }

            WriteRegister(currentRegister, value);
            currentRegister = (byte)((currentRegister + 1) & RegisterMask);
            return 0;
        }

        public void FinishTransmission()
        {
            transferByte = 0;
        }

        public string AuxiliaryCompassHeaterInterference
        {
            get { return auxiliaryCompass == null ? "" : auxiliaryCompass.HeaterInterference; }
            set
            {
                if(auxiliaryCompass != null)
                {
                    auxiliaryCompass.HeaterInterference = value;
                }
            }
        }

        public byte AuxiliaryCompassRotation
        {
            get { return auxiliaryCompass == null ? (byte)0 : auxiliaryCompass.Rotation; }
            set
            {
                if(auxiliaryCompass != null)
                {
                    auxiliaryCompass.Rotation = value;
                }
            }
        }

        private void ResetRegisters()
        {
            Array.Clear(registers, 0, registers.Length);
            fifo.Clear();
            currentBank = 0;
            registers[0, PowerManagement1] = 0x41; // SLEEP | CLKSEL=1
            auxiliaryCompass?.Reset();
            previousAcceleration = null;
            previousGyro = null;
            lastSampleUs = NowUs();
        }

        private byte ReadRegister(byte register)
        {
            if(currentBank == 0)
            {
                switch(register)
                {
                case WhoAmI:
                    return whoAmI;
                case InterruptStatus1:
                    return fifo.Count > 0 ? (byte)1 : (byte)0;
                case FifoCountHigh:
                    Generate();
                    return (byte)(fifo.Count >> 8);
                case FifoCountLow:
                    return (byte)fifo.Count;
                case FifoReadWrite:
                    return fifo.Count > 0 ? fifo.Dequeue() : (byte)0;
                case TemperatureHigh:
                    return (byte)(TemperatureRaw() >> 8);
                case TemperatureLow:
                    return (byte)TemperatureRaw();
                }
            }
            return registers[currentBank, register];
        }

        private void WriteRegister(byte register, byte value)
        {
            if(register == BankSelect)
            {
                currentBank = (byte)((value >> 4) & 0x3);
                return;
            }
            if(currentBank == 0 && register == PowerManagement1 && (value & DeviceReset) != 0)
            {
                ResetRegisters();
                return;
            }
            if(currentBank == 0 && register == FifoReset && value != 0)
            {
                fifo.Clear();
                lastSampleUs = NowUs();
            }
            if(currentBank == 0 && register == UserControl)
            {
                if((value & SramReset) != 0)
                {
                    fifo.Clear();
                    lastSampleUs = NowUs();
                    value = (byte)(value & ~SramReset);
                }
                if((value & FifoEnable) != 0 && (registers[0, UserControl] & FifoEnable) == 0)
                {
                    lastSampleUs = NowUs();
                }
            }
            if(currentBank == 0 && register == FifoEnable2 && value != registers[0, FifoEnable2])
            {
                lastSampleUs = NowUs();
            }
            registers[currentBank, register] = value;
            if(currentBank == 3 && IsAuxiliaryControl(register))
            {
                ServiceAuxiliarySlave((register - I2cSlave0Control) / I2cSlaveStride);
            }
        }

        private static bool IsExternalSensorData(byte register)
        {
            return register >= ExternalSensorData && register < ExternalSensorData + ExternalSensorDataSize;
        }

        private bool IsAuxiliaryControl(byte register)
        {
            return register >= I2cSlave0Control && register <= I2cSlave3Control &&
                (register - I2cSlave0Control) % I2cSlaveStride == 0;
        }

        private void UpdateAuxiliaryReads()
        {
            var outputOffset = 0;
            for(var slave = 0; slave < AuxiliarySlaveCount; slave++)
            {
                var baseRegister = I2cSlave0Address + slave * I2cSlaveStride;
                var address = registers[3, baseRegister];
                var control = registers[3, baseRegister + 2];
                if((control & I2cSlaveEnabled) == 0 || (address & I2cRead) == 0)
                {
                    continue;
                }
                ReadAuxiliaryDevice(address, registers[3, baseRegister + 1], control & I2cTransferSizeMask, outputOffset);
                outputOffset += control & I2cTransferSizeMask;
            }
        }

        private void ServiceAuxiliarySlave(int slave)
        {
            var baseRegister = I2cSlave0Address + slave * I2cSlaveStride;
            var address = registers[3, baseRegister];
            var control = registers[3, baseRegister + 2];
            if((control & I2cSlaveEnabled) == 0)
            {
                return;
            }
            var transferSize = control & I2cTransferSizeMask;
            if((address & I2cRead) != 0)
            {
                UpdateAuxiliaryReads();
                return;
            }
            if(auxiliaryCompass != null && (address & I2cAddressMask) == AuxiliaryCompassAddress && transferSize == 1)
            {
                auxiliaryCompass.Write(new byte[] { registers[3, baseRegister + 1], registers[3, baseRegister + 3] });
            }
        }

        private void ReadAuxiliaryDevice(byte address, byte register, int count, int outputOffset)
        {
            if(auxiliaryCompass == null || (address & I2cAddressMask) != AuxiliaryCompassAddress ||
               outputOffset + count > ExternalSensorDataSize)
            {
                return;
            }
            auxiliaryCompass.Write(new byte[] { register });
            var data = auxiliaryCompass.Read(count);
            for(var index = 0; index < data.Length; index++)
            {
                registers[0, ExternalSensorData + outputOffset + index] = data[index];
            }
        }

        private void Generate()
        {
            var now = NowUs();
            var enables = registers[0, FifoEnable2];
            if((registers[0, UserControl] & FifoEnable) == 0 || enables == 0)
            {
                lastSampleUs = now;
                return;
            }
            var recordSize = ((enables & AccelFifo) != 0 ? 6 : 0) + ((enables & GyroXFifo) != 0 ? 2 : 0)
                + ((enables & GyroYFifo) != 0 ? 2 : 0) + ((enables & GyroZFifo) != 0 ? 2 : 0)
                + ((enables & TempFifo) != 0 ? 2 : 0);
            var gyroConfig1 = registers[2, GyroConfig1];
            var periodUs = (gyroConfig1 & GyroFchoice) == 0 ? 1e6 / 9000.0
                : (1e6 / 1125.0) * (1 + registers[2, GyroSampleRateDivider]);
            var due = (now - lastSampleUs) / periodUs;
            if(recordSize == 0 || due < 1)
            {
                return;
            }
            var room = (FifoCapacity - fifo.Count) / recordSize;
            var count = (int)Math.Min(Math.Floor(due), (double)room);
            var overflow = due >= room + 1;
            lastSampleUs = overflow ? now : lastSampleUs + count * periodUs;

            var truth = physics.Current;
            var acceleration = AP_SensorOrientation.BodyToSensor(truth.SpecificForceMS2, rotation);
            var gyro = AP_SensorOrientation.BodyToSensor(truth.GyroRadS, rotation);
            if(previousAcceleration == null)
            {
                previousAcceleration = acceleration;
                previousGyro = gyro;
            }
            var is20649 = whoAmI == WhoAmIIcm20649;
            var accelFs = (registers[2, AccelConfig] >> 1) & 3;
            var gyroFs = (gyroConfig1 >> 1) & 3;
            var accelScale = Gravity / ((is20649 ? 8192 : 16384) >> accelFs);
            var gyroScale = Math.PI / 180.0 / ((is20649 ? 65.5 : 131.0) / (1 << gyroFs));
            for(var i = 0; i < count; i++)
            {
                var alpha = (i + 1.0f) / count;
                // unfiltered mode: accel 4.5 kHz against the 9 kHz gyro, so each accel value fills two
                // FIFO records (PX4 checks A0==A1, A2==A3); filtered mode updates both every record
                if(heldAcceleration == null || (gyroConfig1 & GyroFchoice) != 0 || (sampleCounter++ & 1) == 0)
                {
                    heldAcceleration = Interpolate(previousAcceleration, acceleration, alpha);
                }
                var a = heldAcceleration;
                var g = Interpolate(previousGyro, gyro, alpha);
                // chip axes: the same raw words the original model produces for this mounting
                if((enables & AccelFifo) != 0)
                {
                    PushWord(ScaleWord(a[1], accelScale));
                    PushWord(ScaleWord(a[0], accelScale));
                    PushWord(ScaleWord(-a[2], accelScale));
                }
                if((enables & GyroXFifo) != 0)
                {
                    PushWord(ScaleWord(g[1], gyroScale));
                }
                if((enables & GyroYFifo) != 0)
                {
                    PushWord(ScaleWord(g[0], gyroScale));
                }
                if((enables & GyroZFifo) != 0)
                {
                    PushWord(ScaleWord(-g[2], gyroScale));
                }
                if((enables & TempFifo) != 0)
                {
                    PushWord(TemperatureRaw());
                }
            }
            if(overflow)
            {
                while(fifo.Count < FifoCapacity)
                {
                    fifo.Enqueue(0);
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
            return ScaleWord((float)(thermal.TemperatureC - 21.0), 1.0 / 333.87);
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
            fifo.Enqueue((byte)value);
        }

        private readonly IMachine machine;
        private readonly byte whoAmI;
        private readonly byte rotation;
        private readonly AP_PhysicsState physics;
        private readonly AP_BoardThermalState thermal;
        private readonly byte[,] registers;
        private readonly Queue<byte> fifo;
        private readonly AP_AK09916 auxiliaryCompass;
        private float[] previousAcceleration;
        private float[] previousGyro;
        private double lastSampleUs;
        private float[] heldAcceleration;
        private ulong sampleCounter;
        private byte currentBank;
        private byte currentRegister;
        private int transferByte;
        private bool reading;

        private const int BankCount = 4;
        private const int RegisterCount = 128;
        private const int FifoCapacity = 512;
        private const int AuxiliarySlaveCount = 4;
        private const int I2cSlaveStride = 4;
        private const int ExternalSensorDataSize = 24;

        private const byte ReadFlag = 0x80;
        private const byte RegisterMask = 0x7F;
        private const byte WhoAmI = 0x00;
        private const byte UserControl = 0x03;
        private const byte PowerManagement1 = 0x06;
        private const byte InterruptStatus1 = 0x1A;
        private const byte TemperatureHigh = 0x39;
        private const byte TemperatureLow = 0x3A;
        private const byte ExternalSensorData = 0x3B;
        private const byte FifoEnable2 = 0x67;
        private const byte FifoReset = 0x68;
        private const byte FifoCountHigh = 0x70;
        private const byte FifoCountLow = 0x71;
        private const byte FifoReadWrite = 0x72;
        private const byte BankSelect = 0x7F;
        private const byte GyroSampleRateDivider = 0x00; // bank 2
        private const byte GyroConfig1 = 0x01;           // bank 2
        private const byte AccelConfig = 0x14;           // bank 2
        private const byte I2cSlave0Address = 0x03;      // bank 3
        private const byte I2cSlave0Control = 0x05;
        private const byte I2cSlave3Control = 0x11;

        private const byte DeviceReset = 0x80;
        private const byte FifoEnable = 0x40;
        private const byte SramReset = 0x04;
        private const byte AccelFifo = 0x10;
        private const byte GyroZFifo = 0x08;
        private const byte GyroYFifo = 0x04;
        private const byte GyroXFifo = 0x02;
        private const byte TempFifo = 0x01;
        private const byte GyroFchoice = 0x01;
        private const byte I2cRead = 0x80;
        private const byte I2cSlaveEnabled = 0x80;
        private const byte I2cAddressMask = 0x7F;
        private const byte I2cTransferSizeMask = 0x0F;
        private const byte AuxiliaryCompassAddress = 0x0C;
        private const byte WhoAmIIcm20948 = 0xEA;
        private const byte WhoAmIIcm20649 = 0xE1;
        private const double Gravity = 9.80665;
    }
}
