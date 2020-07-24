//
// Copyright (c) 2010-2018 Antmicro
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using Antmicro.Renode.Peripherals.Bus;
using Antmicro.Renode.Core;
using Antmicro.Renode.Core.Structure.Registers;
using Antmicro.Renode.Peripherals.DMA;
using System.Collections.Generic;
using Antmicro.Renode.Core.Structure;
using System.Linq;
using Antmicro.Renode.Logging;
using Antmicro.Renode.Exceptions;
using Antmicro.Renode.Utilities;

namespace Antmicro.Renode.Peripherals.SD
{
    // Features NOT supported:
    // * interrupts (including: masking)
    // * CMD8 (from spec 2.0)
    [AllowedTranslations(AllowedTranslation.ByteToDoubleWord | AllowedTranslation.WordToDoubleWord)]
    public class MPFS_SDController : NullRegistrationPointPeripheralContainer<SDCard>, IPeripheralContainer<IPhysicalLayer<byte>, NullRegistrationPoint>, IDoubleWordPeripheral, IProvidesRegisterCollection<DoubleWordRegisterCollection>, IKnownSize, IDisposable
    {
        public MPFS_SDController(Machine machine) : base(machine)
        {
            this.machine = machine;
            IRQ = new GPIO();
            WakeupIRQ = new GPIO();
            irqManager = new InterruptManager<Interrupts>(this);
            internalBuffer = new Queue<byte>();

            RegistersCollection = new DoubleWordRegisterCollection(this);
            InitializeRegisters();
        }

        public uint ReadDoubleWord(long offset)
        {
            return RegistersCollection.Read(offset);
        }

        public void WriteDoubleWord(long offset, uint value)
        {
            RegistersCollection.Write(offset, value);
        }

        public override void Reset()
        {
            RegisteredPeripheral?.Reset();
            RegistersCollection.Reset();
            irqManager.Reset();
        }

        public void Dispose()
        {
            RegisteredPeripheral?.Dispose();
        }

        public IEnumerable<NullRegistrationPoint> GetRegistrationPoints(IPhysicalLayer<byte> peripheral)
        {
            return (peripheral == phy)
                ? new[] { NullRegistrationPoint.Instance }
                : new NullRegistrationPoint[0];
        }

        public void Register(IPhysicalLayer<byte> peripheral, NullRegistrationPoint registrationPoint)
        {
            if(phy != null)
            {
                throw new RegistrationException("There is already a PHY registered for this controller");
            }
            phy = peripheral;
        }

        public void Unregister(IPhysicalLayer<byte> peripheral)
        {
            if(phy != peripheral)
            {
                throw new RegistrationException("Trying to unregister PHY that is currently not registered in this controller");

            }
            phy = null;
        }

        [IrqProvider]
        public GPIO IRQ { get; private set; }

        public GPIO WakeupIRQ { get; private set; }

        public long Size => 0x2000;

        public DoubleWordRegisterCollection RegistersCollection { get; }

        IEnumerable<IRegistered<IPhysicalLayer<byte>, NullRegistrationPoint>> IPeripheralContainer<IPhysicalLayer<byte>, NullRegistrationPoint>.Children => phy != null
            ? new [] { Registered.Create(phy, NullRegistrationPoint.Instance) }
            : new IRegistered<IPhysicalLayer<byte>, NullRegistrationPoint>[0];

        private void InitializeRegisters()
        {
            var responseFields = new IValueRegisterField[4];

            Registers.PhySettings_HRS04.Define(this)
                .WithValueField(0, 6, out addressField, name: "UHS-I Delay Address Pointer (USI_ADDR)")
                .WithReservedBits(6, 2)
                .WithValueField(8, 8, out writeDataField, name: "UHS-I Settings Write Data (UIS_WDATA)")
                .WithValueField(16, 5, out readDataField, FieldMode.Read, name: "UHS-I Settings Read Data (UIS_RDATA)")
                .WithReservedBits(21, 3)
                .WithFlag(24, out var writeRequestFlag, name: "UHS-I Settings Write Request (UIS_WR)")
                .WithFlag(25, out var readRequestFlag, name: "UHS-I Settings Read Request (UIS_RD)")
                .WithFlag(26, out ackField, FieldMode.Read, name: "UHS-I Settings Acknowledge (UIS_ACK)")
                .WithReservedBits(27, 5)
                .WithWriteCallback((_, value) =>
                {
                    // here we set ack field even when no PHY is registered
                    // this is intentional - the uboot test showed that this field
                    // might be necessary to proceed with booting even when actual
                    // reads/writes to PHY are ignored
                    ackField.Value = (writeRequestFlag.Value || readRequestFlag.Value);

                    if(readRequestFlag.Value)
                    {
                        readRequestFlag.Value = false;
                        if(phy == null)
                        {
                            this.Log(LogLevel.Warning, "Trying to read from an unregistered PHY");
                        }
                        else
                        {
                            readDataField.Value = phy.Read((byte)addressField.Value);
                        }
                    }

                    if(writeRequestFlag.Value)
                    {
                        writeRequestFlag.Value = false;
                        if(phy == null)
                        {
                            this.Log(LogLevel.Warning, "Trying to write to an unregistered PHY");
                        }
                        else
                        {
                            phy.Write((byte)addressField.Value, (byte)writeDataField.Value);
                        }
                    }
                })
            ;

            Registers.BlockSizeBlockCount_SRS01.Define(this)
                .WithValueField(0, 12, out blockSizeField, name: "Transfer Block Size (TBS)")
                .WithValueField(16, 16, out blockCountField, name: "Block Count For Current Transfer (BCCT)")
            ;

            Registers.Argument1_SRS02.Define(this)
                .WithValueField(0, 32, out var commandArgument1Field, name: "Command Argument 1 (ARG1)")
            ;

            Registers.CommandTransferMode_SRS03.Define(this)
                .WithFlag(0, out isDmaEnabled, name: "DMA Enable")
                .WithTag("Block Count Enable (BCE)", 1, 1)
                .WithTag(" Auto CMD Enable (ACE)", 2, 2)
                .WithTag("Data Transfer Direction Select (DTDS)", 4, 1)
                .WithEnumField(16, 2, out responseTypeSelectField, name: "Response Type Select (RTS)")
                .WithTag("Command CRC Check Enable (CRCCE)", 19, 1)
                .WithTag("Command Index Check Enable (CICE)", 20, 1)
                .WithTag("Data Present Select (DPS)", 21, 1)
                .WithEnumField<DoubleWordRegister, SDCardCommand>(24, 6, out commandIndex, name: "Command Index (CI)")
                .WithWriteCallback((_, val) =>
                {
                    this.Log(LogLevel.Info, "[REG:CommandTransferMode_SRS03] commandIndex: {0}", commandIndex.Value);

                    var sdCard = RegisteredPeripheral;
                    if(sdCard == null)
                    {
                        this.Log(LogLevel.Warning, "Tried to send command, but no SD card is currently attached");
                        return;
                    }

                    var commandResult = sdCard.HandleCommand((uint)commandIndex.Value, commandArgument1Field.Value);
                    switch(responseTypeSelectField.Value)
                    {
                        case ResponseType.NoResponse:
                            if(commandResult.Length != 0)
                            {
                                this.Log(LogLevel.Warning, "Expected no response, but {0} bits received", commandResult.Length);
                                return;
                            }
                            break;
                        case ResponseType.Response136Bits:
                            // our response does not contain 16 bits:
                            // * start bit
                            // * transmission bit
                            // * command index / reserved bits (6 bits)
                            // * CRC7 (7 bits)
                            // * end bit
                            if(commandResult.Length != 128)
                            {
                                this.Log(LogLevel.Warning, "Unexpected a response of length 128 bits (excluding control bits), but {0} received", commandResult.Length);
                                return;
                            }
                            // the following bits are considered a part of returned register, but are not included in the response buffer:
                            // * CRC7 (7 bits)
                            // * end bit
                            // that's why we are skipping the initial 8-bits
                            responseFields[0].Value = commandResult.AsUInt32(8);
                            responseFields[1].Value = commandResult.AsUInt32(40);
                            responseFields[2].Value = commandResult.AsUInt32(72);
                            responseFields[3].Value = commandResult.AsUInt32(104, 24);
                            break;
                        case ResponseType.Response48Bits:
                        case ResponseType.Response48BitsWithBusy:
                            // our response does not contain 16 bits:
                            // * start bit
                            // * transmission bit
                            // * command index / reserved bits (6 bits)
                            // * CRC7 (7 bits)
                            // * end bit
                            if(commandResult.Length != 32)
                            {
                                this.Log(LogLevel.Warning, "(2) Expected a response of length {0} bits (excluding control bits and CRC), but {1} received", 32, commandResult.Length);
                                return;
                            }
                            responseFields[0].Value = commandResult.AsUInt32();
                            break;
                        default:
                            this.Log(LogLevel.Warning, "Unexpected response type selected: {0}. Ignoring the command response.", responseTypeSelectField.Value);
                            return;
                    }
                    ProcessData(sdCard, commandIndex.Value);
                })
            ;

            Registers.Response1_SRS04.Define(this)
                .WithValueField(0, 32, out responseFields[0], FieldMode.Read, name: "Response Register #1 (RESP1)")
            ;

            Registers.Response2_SRS05.Define(this)
                .WithValueField(0, 32, out responseFields[1], FieldMode.Read, name: "Response Register #2 (RESP2)")
            ;

            Registers.Response3_SRS06.Define(this)
                .WithValueField(0, 32, out responseFields[2], FieldMode.Read, name: "Response Register #3 (RESP3)")
            ;

            Registers.Response4_SRS07.Define(this)
                .WithValueField(0, 32, out responseFields[3], FieldMode.Read, name: "Response Register #4 (RESP4)")
            ;

            Registers.DataBuffer_SRS08.Define(this).WithValueField(0, 32, name: "Buffer Data Port (BDP)",
                valueProviderCallback: _ =>
                {
                    var sdCard = RegisteredPeripheral;
                    if(sdCard == null)
                    {
                        this.Log(LogLevel.Warning, "Tried to read data, but no SD card is currently attached");
                        return 0;
                    }
                    if(isDmaEnabled.Value)
                    {
                        this.Log(LogLevel.Warning, "Tried to read data in DMA mode from register that does not support it");
                        return 0;
                    }
                    var internalBytes = internalBuffer.DequeueRange(4);
                    if(!internalBytes.Any())
                    {
                        internalBytes = new byte[4] { 0, 0, 0, 0 };
                    }
                    irqManager.SetInterrupt(Interrupts.BufferReadReady);
                    irqManager.SetInterrupt(Interrupts.TransferComplete);
                    return internalBytes.ToUInt32Smart();
                },
                writeCallback: (_, value) =>
                {
                    var sdCard = RegisteredPeripheral;
                    if(sdCard == null)
                    {
                        this.Log(LogLevel.Warning, "Tried to write data, but no SD card is currently attached");
                        return;
                    }
                    if(isDmaEnabled.Value)
                    {
                        this.Log(LogLevel.Warning, "Tried to write data in DMA mode to register that does not support it");
                        return;
                    }
                    WriteToBuffer(BitConverter.GetBytes(value));
                })
            ;

            Registers.PresentState_SRS09.Define(this)
                .WithFlag(0, FieldMode.Read, name: "Command Inhibit CMD (CICMD)")
                .WithFlag(1, FieldMode.Read, name: "Command Inhibit DAT (CIDAT)") // as sending a command is instantienous those two bits will probably always be 0
                // ...
                .WithFlag(10, FieldMode.Read, name: "Buffer Write Enable (BWE)", valueProviderCallback: _ => true)
                .WithFlag(11, FieldMode.Read, name: "Buffer Read Enable (BRE)", valueProviderCallback: _ => RegisteredPeripheral == null ? false : RegisteredPeripheral.IsReadyForReadingData)
                .WithFlag(16, FieldMode.Read, name: "Card Inserted (CI)", valueProviderCallback: _ => RegisteredPeripheral != null)
                .WithFlag(17, FieldMode.Read, name: "Card State Stable (CSS)", valueProviderCallback: _ => true)
                .WithFlag(18, FieldMode.Read, name: "Card Detect Pin Level (CDSL)", valueProviderCallback: _ => RegisteredPeripheral != null)
            ;

            Registers.Capabilities_SRS16.Define(this)
                // these fields must return non-zero values in order for u-boot to boot
                .WithValueField(0, 6, FieldMode.Read, valueProviderCallback: _ => 4, name: "Timeout clock frequency (TCS)")
                .WithFlag(7, FieldMode.Read, valueProviderCallback: _ => true, name: "Timeout clock unit (TCU)")
                .WithValueField(8, 8, FieldMode.Read, valueProviderCallback: _ => 1, name: "Base Clock Frequency For SD Clock (BCSDCLK)")
                .WithFlag(24, FieldMode.Read, valueProviderCallback: _ => true, name: "Voltage Support 3.3V (VS33)")
            ;

            Registers.HostControl2_SRS11.Define(this)
                .WithFlag(1, FieldMode.Read, name: "Internal Clock Stable (ICS)", valueProviderCallback: _ => true)
            ;

            Registers.ErrorNormalInterruptStatus_SRS12.Bind(this, irqManager.GetRegister<DoubleWordRegister>(
                valueProviderCallback: (irq, val) => irqManager.IsSet(irq),
                writeCallback: (irq, prev, curr) => { if(curr) irqManager.ClearInterrupt(irq); }))
            ;

            Registers.DmaSystemAddressLow_SRS22.Define(this)
                .WithValueField(0, 32, out dmaSystemAddressLow, name: "ADMA/SDMA System Address 1")
            ;

            Registers.DmaSystemAddressHigh_SRS23.Define(this)
                .WithValueField(0, 32, out dmaSystemAddressHigh, name: "ADMA/SDMA System Address 2")
            ;
        }

        private void ProcessData(SDCard sdCard, SDCardCommand command)
        {
            if(internalBuffer.Any())
            {
                this.Log(LogLevel.Noisy, "Clearing a non-empty buffer while processing command: {0}", command);
                internalBuffer.Clear();
            }
            switch(command)
            {
                case SDCardCommand.CheckSwitchableFunction:
                    internalBuffer.EnqueueRange(sdCard.ReadSwitchFunctionStatusRegister());
                    break;
                case SDCardCommand.SendInterfaceConditionCommand:
                    internalBuffer.EnqueueRange(sdCard.ReadExtendedCardSpecificDataRegister());
                    break;
                case SDCardCommand.ReadSingleBlock:
                    var data = sdCard.ReadData(blockCountField.Value * blockSizeField.Value);

                    internalBuffer.EnqueueRange(data);
                    machine.LocalTimeSource.ExecuteInNearestSyncedState(arg2 =>
                    {
                        irqManager.SetInterrupt(Interrupts.TransferComplete);
                    });
                    break;
                case SDCardCommand.ReadMultipleBlocks:
                    if(isDmaEnabled.Value)
                    {
                        ulong counter = 0;
                        var limit = blockCountField.Value * blockSizeField.Value;
                        while(counter < limit)
                        {
                            ulong address = ((ulong)dmaSystemAddressHigh.Value << 32) | dmaSystemAddressLow.Value;
                            var bytes = sdCard.ReadData(4);
                            var b = BitHelper.ToUInt32(bytes, 0, 4, true);
                            Machine.SystemBus.WriteDoubleWord(address + counter, b);
                            counter += 4;
                        }
                    }
                    else
                    {
                        internalBuffer.EnqueueRange(sdCard.ReadData(blockCountField.Value * blockSizeField.Value));
                    }
                    machine.LocalTimeSource.ExecuteInNearestSyncedState(arg2 =>
                    {
                        irqManager.SetInterrupt(Interrupts.TransferComplete);
                    });
                    break;
            }
            irqManager.SetInterrupt(Interrupts.BufferReadReady);
            irqManager.SetInterrupt(Interrupts.BufferWriteReady);
            irqManager.SetInterrupt(Interrupts.CommandComplete);
        }

        private void WriteToBuffer(byte[] data)
        {
            var limit = blockCountField.Value * blockSizeField.Value;
            internalBuffer.EnqueueRange(data);
            if(internalBuffer.Count < limit)
            {
                return;
            }
            var sdCard = RegisteredPeripheral;
            sdCard.WriteData(internalBuffer.DequeueAll());
            machine.LocalTimeSource.ExecuteInNearestSyncedState(arg1 =>
            {
                irqManager.SetInterrupt(Interrupts.TransferComplete);
            });
        }

        private IValueRegisterField blockSizeField;
        private IValueRegisterField blockCountField;
        private IFlagRegisterField ackField;
        private IFlagRegisterField isDmaEnabled;
        private IValueRegisterField addressField;
        private IValueRegisterField writeDataField;
        private IEnumRegisterField<SDCardCommand> commandIndex;
        private IValueRegisterField readDataField;
        private IValueRegisterField dmaSystemAddressLow;
        private IValueRegisterField dmaSystemAddressHigh;
        private IEnumRegisterField<ResponseType> responseTypeSelectField;

        private IPhysicalLayer<byte> phy;
        private Queue<byte> internalBuffer;

        private readonly Machine machine;
        private readonly InterruptManager<Interrupts> irqManager;

        private enum Registers
        {
            PhySettings_HRS04 = 0x10,
            EMMCControl_HRS06 = 0x18,
            SDMASystemAddressArgument2_SRS00 = 0x200,
            BlockSizeBlockCount_SRS01 = 0x204,
            Argument1_SRS02 = 0x208,
            CommandTransferMode_SRS03 = 0x20C,
            Response1_SRS04 = 0x210,
            Response2_SRS05 = 0x214,
            Response3_SRS06 = 0x218,
            Response4_SRS07 = 0x21C,
            DataBuffer_SRS08 = 0x220,
            PresentState_SRS09 = 0x224,
            HostControl1_SRS10 = 0x228,
            HostControl2_SRS11 = 0x22C,
            ErrorNormalInterruptStatus_SRS12 = 0x230,
            ErrorNormalStatusEnable_SRS13 = 0x234,
            ErrorNormalSignalEnable_SRS14 = 0x238,
            Capabilities_SRS16 = 0x240,
            DmaSystemAddressLow_SRS22 = 0x258,
            DmaSystemAddressHigh_SRS23 = 0x25C
        }

        private enum ResponseType
        {
            NoResponse = 0,
            Response136Bits = 1,
            Response48Bits = 2,
            Response48BitsWithBusy = 3
        }

        private enum Interrupts
        {
            CommandComplete = 0,
            TransferComplete = 1,
            BlockGapEvent = 2,
            DMAInterrupt = 3,
            BufferWriteReady = 4,
            BufferReadReady = 5,
            QueuingEnabledInterrupt = 14,
            ErrorInterrupt = 15,
            ResponseError = 31
        }

        private enum SDCardCommand
        {
            CheckSwitchableFunction = 6,
            SendInterfaceConditionCommand = 8,
            ReadSingleBlock = 17,
            ReadMultipleBlocks = 18
        }
    }
}
