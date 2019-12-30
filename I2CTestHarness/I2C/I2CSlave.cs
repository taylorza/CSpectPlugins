﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace I2CTestHarness.I2C
{
    public class I2CSlave : II2CDevice
    {
        private I2CBus bus;
        private UpdateLogEventHandler logCallback;
        private bool lastSCL;
        private bool lastSDA;
        private int lastBit;
        private byte currentByte;
        private DataDirection currentDirection;
        private int bytesSinceStart;
        private CommandStates lastState;
        private CommandStates currentState;
        private bool justStarted;
        private bool justStopped;
        private bool justAckNacked;

        public I2CSlave(I2CBus Bus, UpdateLogEventHandler LogCallback = null)
        {
            bus = Bus;
            logCallback = LogCallback;
            lastState = currentState = CommandStates.Stopped;
            bus.Register(this);
            Log("State: " + currentState.ToString());
            justStarted = justAckNacked = false;
        }

        public virtual byte SlaveAddress
        {
            get
            {
                throw new NotImplementedException("SlaveAddress must be implemented in derived class");
            }
        }

        public byte WriteAddress
        {
            get
            {
                return Convert.ToByte((SlaveAddress << 1) & 255);
            }
        }

        public byte ReadAddress
        {
            get
            {
                return Convert.ToByte(((SlaveAddress << 1) & 255) | 1);
            }
        }

        public virtual string DeviceName
        {
            get
            {
                throw new NotImplementedException("DeviceName must be implemented in derived class");
            }
        }

        public bool IsMaster { get { return false; } }

        public void Log(string Text)
        {
            #if DEBUG
            if (logCallback != null)
                logCallback(Text);
            #endif
        }

        protected void LogBus(bool SDA, bool SCL)
        {
            Log("    SDA=" + (SDA ? "1" : "0") + ", SCL=" + (SCL ? "1" : "0"));
        }

        public bool HasAddress(byte Address, ref DataDirection Direction)
        {
            // The Address passed in is shifted one bit to the left, 
            // with the new bit 0 set for reads, and unset for writes.
            // Shift it one bit to the right to match this device's address.
            byte match = Convert.ToByte(Address >> 1);
            Direction = (Address & 1) == 1 ? DataDirection.Read : DataDirection.Write;
            return match == SlaveAddress;
        }

        private void SendACK()
        {
            Log("Tx ACK  bit 8=0");
            bus.SetSDA(this, false); // Master should sample on next falling clock edge
        }

        private void SendNACK()
        {
            Log("Tx NACK bit 8=1");
            bus.SetSDA(this, true); // Master should sample on next falling clock edge
        }

        public void Tick(bool NewSDA, bool NewSCL, bool OldSDA, bool OldSCL)
        {
            lastSDA = OldSDA;
            lastSCL = OldSCL;
            LogBus(NewSDA, NewSCL);

            // Process CMD_START
            // A change in the state of the data line, from HIGH to LOW, while the clock is HIGH, defines a START condition.
            // Trigger on falling edge of data
            if (!justStopped && (currentState == CommandStates.Stopped || currentState == CommandStates.Started) && NewSCL && lastSDA && !NewSDA)
            {
                Log("Rx CMD_START");
                justStarted = true;
                justStopped = justAckNacked = false;
                bytesSinceStart = 0;
                lastState = currentState;
                currentState = CommandStates.Started;
                Log("State: " + currentState.ToString());
            }
            else if (currentState == CommandStates.ReceivingByte && OldSCL && NewSCL && lastSDA && !NewSDA)
            {
                Log("Rx CMD_START");
                OnTransactionChanged(CommandStates.Started);
                justStarted = true;
                justStopped = justAckNacked = false;
                bytesSinceStart = 0;
                lastState = currentState;
                currentState = CommandStates.Started;
                Log("State: " + currentState.ToString());
            }

            // Process CMD_STOP
            // A change in the state of the data line, from LOW to HIGH, while the clock line is HIGH, defines a STOP condition.
            // Trigger on rising edge of data
            else if ((currentState == CommandStates.Started || currentState == CommandStates.ReceivingByte) && NewSCL && lastSCL && !lastSDA && NewSDA)
            {
                Log("Rx CMD_STOP");
                OnTransactionChanged(CommandStates.Stopped);
                justStopped = true;
                justStarted = justAckNacked = false;
                lastState = currentState;
                currentState = CommandStates.Stopped;
                Log("State: " + currentState.ToString());
            }

            // Receive data bit
            // Sample data, triggering on falling edge of clock
            else if (!justStarted && !justAckNacked && currentState == CommandStates.Started && lastSCL && !NewSCL)
            {
                Log("Rx CMD_TX");
                justStarted = justAckNacked = justStopped = false;
                lastBit = 0;
                currentByte = Convert.ToByte((NewSDA ? 1 : 0) << (7 - lastBit));
                Log("Rx data bit " + lastBit + "=" + (NewSDA ? "1" : "0"));
                lastState = currentState;
                currentState = CommandStates.ReceivingByte;
                Log("State: " + currentState.ToString());
            }
            else if (currentState == CommandStates.ReceivingByte && !lastSCL && NewSCL && lastBit >= 0 && lastBit <= 6)
            {
                lastBit++;
                justStarted = justAckNacked = justStopped = false;
                currentByte = Convert.ToByte(currentByte | ((NewSDA ? 1 : 0) << (7 - lastBit)));
                Log("Rx data bit " + lastBit + "=" + (NewSDA ? "1" : "0"));
            }
            else if (currentState == CommandStates.ReceivingByte && !lastSCL && NewSCL && lastBit == 7)
            {
                lastBit++;
                justStarted = justStopped = false;
                justAckNacked = true;
                Log("Rx byte=0x" + currentByte.ToString("X2"));
                if (bytesSinceStart == 0)
                {
                    // First byte since (re)start is always a slave address plus direction
                    bool isMine = HasAddress(currentByte, ref currentDirection);
                    if (isMine)
                    {
                        Log("Data address 0x" + (currentByte >> 1).ToString("X2") + " matches slave address");
                        Log("Accepting data " + currentDirection.ToString().ToUpper() + "s to " + DeviceName);
                        OnTransactionChanged(CommandStates.Started);
                        SendACK(); // Send an ACK to participate in the rest of the transaction
                        bytesSinceStart++;
                        lastState = currentState;
                        currentState = CommandStates.Started;
                        Log("State: " + currentState.ToString());
                    }
                    else
                    {
                        Log("Data address 0x" + (currentByte >> 1).ToString("X2") + " is for another slave");
                        Log("Ignoring further data until next CMD_START");
                        // Don't sent an ACK or NACK because we were only eavesdropping
                        lastState = currentState;
                        currentState = CommandStates.Stopped;
                        Log("State: " + currentState.ToString());
                    }
                }
                else
                {
                    // Second byte after start is usually a register address, but it depends on the concrete slave device.
                    // We can't assume this, so send all subsequent bytes to the slave and let it decide how to handle.
                    bool ack;
                    // See what the concrete slave device wants to do with the byte
                    if (currentDirection == DataDirection.Read)
                        ack = OnByteRead(currentByte);
                    else
                        ack = OnByteWritten(currentByte);
                    // Relay this decision back to the I2C master
                    if (ack)
                        SendACK(); // Send an ACK to continue participating in the rest of the transaction
                    else
                    {
                        OnTransactionChanged(CommandStates.Stopped);
                        SendNACK(); // Send a NACK to abort the transaction
                    }
                }
            }
            else
            {
                justStarted = justStopped = justAckNacked = false;
            }

            //if (currentState == CommandStates.ReceivingByte && NewSCL && (NewSDA != lastSDA))
            //{
            //    Log("Data cannot change when clock is high!");
                //throw new InvalidOperationException("Data cannot change when clock is high!");
            //}
        }

        protected virtual void OnTransactionChanged(CommandStates NewState)
        {
            throw new NotImplementedException("TransactionChange must be implemented in derived class");
        }

        protected virtual bool OnByteRead(byte Byte)
        {
            throw new NotImplementedException("ReadByte must be implemented in derived class");
        }

        protected virtual bool OnByteWritten(byte Byte)
        {
            throw new NotImplementedException("WriteByte must be implemented in derived class");
        }
    }
}
