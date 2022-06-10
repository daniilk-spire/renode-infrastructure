//
// Copyright (c) 2010-2018 Antmicro
// Copyright (c) 2011-2015 Realtime Embedded
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Core.Structure;
using System.Collections.Generic;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;

namespace Antmicro.Renode.Peripherals.SPI
{
    public sealed class SAM3X_SPI : NullRegistrationPointPeripheralContainer<ISPIPeripheral>, IWordPeripheral, IDoubleWordPeripheral, IBytePeripheral, IKnownSize
    {
        public SAM3X_SPI(Machine machine) : base(machine)
        {
            receiveBuffer = new Queue<byte>();
            IRQ = new GPIO();
            SetupRegisters();
            Reset();
        }

        public byte ReadByte(long offset)
        {
            // byte interface is there for DMA
            if(offset % 4 == 0)
            {
                return (byte)ReadDoubleWord(offset);
            }
            this.LogUnhandledRead(offset);
            return 0;
        }

        public void WriteByte(long offset, byte value)
        {
            if(offset % 4 == 0)
            {
                WriteDoubleWord(offset, (uint)value);
            }
            else
            {
                this.LogUnhandledWrite(offset, value);
            }
        }

        public ushort ReadWord(long offset)
        {
            return (ushort)ReadDoubleWord(offset);
        }

        public void WriteWord(long offset, ushort value)
        {
            WriteDoubleWord(offset, (uint)value);
        }

        public uint ReadDoubleWord(long offset)
        {
            switch((Registers)offset)
            {
            case Registers.Receive:
                return HandleDataRead();
            default:
                return registers.Read(offset);
            }
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            switch((Registers)offset)
            {
            case Registers.Transmit:
                HandleDataWrite(value);
                break;
            default:
                registers.Write(offset, value);
                break;
            }
        }

        public override void Reset()
        {
            lock(receiveBuffer)
            {
                receiveBuffer.Clear();
            }
            registers.Reset();
        }

        public long Size
        {
            get
            {
                return 0x400;
            }
        }

        public GPIO IRQ
        {    
            get;
            private set;
        }

        private uint HandleDataRead()
        {
            IRQ.Unset();
            lock(receiveBuffer)
            {
                if(receiveBuffer.Count > 0)
                {
                    var value = receiveBuffer.Dequeue();
                    return value; // TODO: verify if Update should be called
                }
                this.Log(LogLevel.Warning, "Trying to read data register while no data has been received.");
                return 0;
            }
        }

        private void HandleDataWrite(uint value)
        {
            IRQ.Unset();
            lock(receiveBuffer)
            {
                var peripheral = RegisteredPeripheral;
                if(peripheral == null)
                {
                    this.Log(LogLevel.Warning, "SPI transmission while no SPI peripheral is connected.");
                    receiveBuffer.Enqueue(0x0);
                    return;
                }
                receiveBuffer.Enqueue(peripheral.Transmit((byte)value)); // currently byte mode is the only one we support
                this.NoisyLog("Transmitted 0x{0:X}, received 0x{1:X}.", value, receiveBuffer.Peek());
            }
            Update();
        }

        private void Update()
        {
            // TODO: verify this condition
            IRQ.Set(txBufferEmptyInterruptEnable.Value || rxBufferNotEmptyInterruptEnable.Value);
        }

        private void SetupRegisters()
        {
            var interrupt_enable = new DoubleWordRegister(this);
            txBufferEmptyInterruptEnable = cointerrupt_enablentrol.DefineFlagField(1);
            rxBufferNotEmptyInterruptEnable = interrupt_enable.DefineFlagField(0);

            var registerDictionary = new Dictionary<long, DoubleWordRegister>
            { 
                { (long)Registers.Control, new DoubleWordRegister(this)
                        .WithFlag(24, FieldMode.Write, name:"LastXFer")
                        .WithFlag(7, FieldMode.Write, name:"SpiReset")
                        .WithFlag(1, FieldMode.Write, name:"SpiDisable")
                        .WithFlag(0, FieldMode.Write, changeCallback: (oldValue, newValue) => {
                            if(!newValue)
                            {
                                IRQ.Unset();
                            }
                        }, name:"SpiEnable")},
                
                { (long)Registers.Mode, new DoubleWordRegister(this)
                        .WithValueField(24, 8, name:"DLYBCS")
                        .WithReservedBits(20, 4)
                        .WithValueField(16, 4, name:"PCS")
                        .WithFlag(7, name:"LLB")
                        .WithFlag(5, name:"WDRBT")
                        .WithFlag(4, name:"MODFDIS")
                        .WithFlag(2, name:"PCSDEC")
                        .WithFlag(1, name:"PS")
                        .WithFlag(0, name:"MSTR")},

                { (long)Registers.Status, new DoubleWordRegister(this)
                        .WithReservedBits(17, 15)
                        .WithFlag(16, FieldMode.Read, name:"SPIENS")
                        .WithReservedBits(11, 5)
                        .WithFlag(10, FieldMode.Read, name:"UNDES")
                        .WithFlag(9, FieldMode.Read, name:"TXEMPTY")
                        .WithFlag(8, FieldMode.Read, name:"NSSR")
                        .WithReservedBits(4, 4)
                        .WithFlag(3, FieldMode.Read, name:"OVRES")
                        .WithFlag(2, FieldMode.Read, name:"MODF")
                        .WithFlag(1, FieldMode.Read, name:"TDRE")
                        .WithFlag(0, FieldMode.Read, valueProviderCallback: _ => receiveBuffer.Count != 0, name:"RDRF")},
                
                { (long)Registers.InterruptEnable, interrupt_enable}

            };
            registers = new DoubleWordRegisterCollection(this, registerDictionary);
        }

        private DoubleWordRegisterCollection registers;
        private IFlagRegisterField txBufferEmptyInterruptEnable, rxBufferNotEmptyInterruptEnable;

        private readonly Queue<byte> receiveBuffer;

        private enum Registers
        {
            Control = 0x0, // SPI_CR,
            Mode = 0x4, // SPI_MR
            Receive = 0x8, // SPI_RDR
            Transmit = 0xC, // SPI_TDR
            Status = 0x10, // SPI_SR
            InterruptEnable = 0x14, // SPI_IER
            InterruptDisable = 0x18, // SPI_IDR
            InterruptMask = 0x1C, // SPI_IMR

            ChipSelect0 = 0x30, // SPI_CSR0
            ChipSelect1 = 0x34, // SPI_CSR1
            ChipSelect2 = 0x38, // SPI_CSR2
            ChipSelect3 = 0x3C, // SPI_CSR3

            WriteProtectionControl = 0xE4, // SPI_WPMR
            WriteProtectionStatus = 0xE8 // SPI_WPSR
        }
    }
}
