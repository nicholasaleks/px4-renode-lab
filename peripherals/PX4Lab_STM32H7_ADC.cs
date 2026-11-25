//
// STM32H7 ADC (RM0433 ch. 25) as PX4's platforms/nuttx/src/px4/stm/stm32h7/adc driver uses it:
// software-triggered single conversions. PX4 programs SQR1 with one channel, sets ADSTART and
// polls ISR.EOC for at most 50 us with interrupts off, then reads DR. On silicon a 64.5-cycle
// conversion takes ~1 us, so here a software start converts immediately: DR = the channel's
// value, EOC|EOS set, ADSTART cleared (hardware clears it at the end of a single conversion).
//
// The board's original AP_STM32_ADC_v3 model instead runs a 10 kHz background scan for
// continuous DMA mode, delivered at Renode quantum boundaries - always after PX4's 50 us
// timeout. Its FeedSample(value, channel, repeat) entry point is kept, so the board script's
// physics battery voltage/current feeds (AP_Physics.FeedAnalog) drive this model unchanged.
//
// One instance covers an ADC pair: master at +0x000, slave at +0x100, common at +0x300
// (ADC1/2 at 0x40022000), or a single ADC (ADC3 at 0x58026000).
//
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Peripherals;
using Antmicro.Renode.Peripherals.Bus;

namespace Antmicro.Renode.Peripherals.Analog
{
    public class PX4Lab_STM32H7_ADC : IDoubleWordPeripheral, IWordPeripheral, IKnownSize
    {
        public PX4Lab_STM32H7_ADC(IMachine machine)
        {
            Reset();
        }

        public long Size => 0x400;
        public GPIO IRQ { get; } = new GPIO();

        public void Reset()
        {
            registers.Clear();
            IRQ.Unset();
        }

        public void FeedSample(uint value, uint channel, int repeat = -1)
        {
            samples[channel] = value & 0xFFFF;
        }

        // monitor: sysbus.adcM1S2 DebugInfo
        public string DebugInfo
        {
            get
            {
                var parts = new List<string>();
                foreach(var entry in samples)
                {
                    parts.Add(string.Format("ch{0}={1}", entry.Key, entry.Value));
                }
                return string.Format("conversions={0} lastChannel={1} samples: {2}", conversions, lastChannel,
                    string.Join(" ", parts));
            }
        }

        public uint ReadDoubleWord(long offset)
        {
            var value = Get(offset);
            var bank = offset & ~0xFFL;
            if(bank < Common && (offset & 0xFF) == DR)
            {
                Set(bank + ISR, Get(bank + ISR) & ~EOC);
                UpdateInterrupt();
            }
            return value;
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            var bank = offset & ~0xFFL;
            var register = offset & 0xFF;
            if(bank >= Common)
            {
                Set(offset, value);
                return;
            }
            switch(register)
            {
            case ISR:
                Set(offset, Get(offset) & ~value); // write 1 to clear
                break;
            case CR:
                value &= ~ADCAL;                   // calibration completes immediately
                if((value & ADDIS) != 0)
                {
                    value &= ~(ADDIS | ADEN | ADSTART);
                    Set(bank + ISR, Get(bank + ISR) & ~ADRDY);
                }
                if((value & ADSTP) != 0)
                {
                    value &= ~(ADSTP | ADSTART);
                }
                if((value & ADEN) != 0)
                {
                    Set(bank + ISR, Get(bank + ISR) | ADRDY);
                }
                if((value & ADSTART) != 0)
                {
                    Convert(bank);
                    value &= ~ADSTART;
                }
                Set(offset, value);
                break;
            default:
                Set(offset, value);
                break;
            }
            UpdateInterrupt();
        }

        public ushort ReadWord(long offset)
        {
            return (ushort)ReadDoubleWord(offset);
        }

        public void WriteWord(long offset, ushort value)
        {
            WriteDoubleWord(offset, value);
        }

        private void Convert(long bank)
        {
            var channel = (Get(bank + SQR1) >> 6) & 0x1F;  // SQ1
            Set(bank + DR, samples.TryGetValue(channel, out var sample) ? sample : 0);
            Set(bank + ISR, Get(bank + ISR) | EOC | EOS);
            conversions++;
            lastChannel = channel;
        }

        private uint Get(long offset)
        {
            return registers.TryGetValue(offset, out var value) ? value : 0;
        }

        private void Set(long offset, uint value)
        {
            registers[offset] = value;
        }

        private void UpdateInterrupt()
        {
            IRQ.Set((Get(ISR) & Get(IER)) != 0 || (Get(0x100 + ISR) & Get(0x100 + IER)) != 0);
        }

        private readonly Dictionary<long, uint> registers = new Dictionary<long, uint>();
        private readonly Dictionary<uint, uint> samples = new Dictionary<uint, uint>();
        private ulong conversions;
        private uint lastChannel;

        private const long ISR = 0x00;
        private const long IER = 0x04;
        private const long CR = 0x08;
        private const long SQR1 = 0x30;
        private const long DR = 0x40;
        private const long Common = 0x300;

        private const uint ADRDY = 1U << 0;
        private const uint EOC = 1U << 2;
        private const uint EOS = 1U << 3;
        private const uint ADEN = 1U << 0;
        private const uint ADDIS = 1U << 1;
        private const uint ADSTART = 1U << 2;
        private const uint ADSTP = 1U << 4;
        private const uint ADCAL = 1U << 31;
    }
}
