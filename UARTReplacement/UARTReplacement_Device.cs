﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Plugin;

namespace Plugins.UARTReplacement
{
    public class UARTReplacement_Device : iPlugin
    {
        private const ushort PORT_UART_TX = 0x133b;
        private const ushort PORT_UART_RX = 0x143b;
        private const ushort PORT_UART_CONTROL = 0x153b;
        private const byte REG_RESET = 0x02;
        private const byte REG_VIDEO_TIMING = 0x11;
        private const byte PI_GPIO_EN_1 = 0x90;
        private const byte PI_GPIO_RW_1 = 0x98;
        private const byte REG_ESP_GPIO_ENABLE = 0xa8;
        private const byte REG_ESP_GPIO = 0xa9;

        private iCSpect CSpect;
        private UARTTargets Target;
        private SerialPort espPort;
        private SerialPort piPort;
        private Settings settings;
        private bool UART_RX_Internal;
        private bool UART_TX_Internal;
        private Buffer espBuffer;
        private Buffer piBuffer;
        private object espSync;
        private object piSync;

        public List<sIO> Init(iCSpect _CSpect)
        {
            List<sIO> ports = new List<sIO>();
            try
            {
                // Initialise and load plugin settings
                espSync = new object();
                piSync = new object();
                CSpect = _CSpect;
                Target = UARTTargets.ESP;
                settings = Settings.Load();
                espBuffer = new Buffer(settings.EspPortName, UARTTargets.ESP);
                piBuffer = new Buffer(settings.PiPortName, UARTTargets.Pi);
                espPort = new SerialPort(settings.EspPortName, UARTTargets.ESP, Esp_DataReceived);
                piPort = new SerialPort(settings.PiPortName, UARTTargets.Pi, Pi_DataReceived);
                UART_RX_Internal = false;
                UART_TX_Internal = false;

                // create a list of the ports we need to implement replacement UARTs
                ports.Add(new sIO(PORT_UART_TX, eAccess.Port_Write));
                ports.Add(new sIO(PORT_UART_TX, eAccess.Port_Read));
                ports.Add(new sIO(PORT_UART_RX, eAccess.Port_Write));
                ports.Add(new sIO(PORT_UART_RX, eAccess.Port_Read));
                ports.Add(new sIO(PORT_UART_CONTROL, eAccess.Port_Write));
                ports.Add(new sIO(REG_RESET, eAccess.NextReg_Write));
                ports.Add(new sIO(REG_ESP_GPIO_ENABLE, eAccess.NextReg_Write));
                ports.Add(new sIO(REG_ESP_GPIO, eAccess.NextReg_Write));
                if (settings.GetPiMapGpio4And5ToDtrAndRtsEnable())
                {
                    Debug.WriteLine("Mapping Pi GPIO control pin 4 to DTR on " + (settings.PiPortName ?? "").Trim());
                    Debug.WriteLine("Mapping Pi GPIO control pin 5 to RTS on " + (settings.PiPortName ?? "").Trim());
                    // We are not subscribing to reads of these nextregs.
                    // If there is any Next software that needs to read them, add that functionalilty to the Plugin.
                    ports.Add(new sIO(PI_GPIO_EN_1, eAccess.NextReg_Write));
                    ports.Add(new sIO(PI_GPIO_RW_1, eAccess.NextReg_Write));
                }
                Debug.WriteLine("UARTReplacement_Device.Init complete");
            }
            catch (Exception ex)
            {
                Debug.Write("UARTReplacement_Device.Init Exception: ");
                Debug.Write(ex.Message);
                Debug.Write(ex.StackTrace);
            }
            return ports;
        }

        public void Quit()
        {
            try
            {
                if (espPort != null)
                {
                    espPort.Dispose();
                    espPort = null;
                }
                if (piPort != null)
                {
                    piPort.Dispose();
                    piPort = null;
                }
            }
            catch (Exception ex)
            {
                Debug.Write("UARTReplacement_Device.Quit Exception: ");
                Debug.Write(ex.Message);
                Debug.Write(ex.StackTrace);
            }
        }

        public bool Write(eAccess _type, int _port, int _id, byte _value)
        {
            try
            {
                switch (_port)
                {
                    case PORT_UART_CONTROL:
                        // Writes to this port contain siwtches between the ESP and Pi UARTs,  
                        // and also potential changes to the prescaler which cause the UART baud to change.
                        // We might not need to query REG_VIDEO_TIMING every single time the baud or UART selector changes,
                        // But just in case it can vary, let's do it. It doesn't seem to cause performance issues to do so.
                        var oldTarget = Target;
                        var target = (_value & 64) == 0 ? UARTTargets.ESP : UARTTargets.Pi;
                        Target = target;
                        if (oldTarget != Target)
                            Debug.WriteLine("UARTReplacement_Device.Target changed to " + Target.ToString());
                        currentPort.SetPrescalerAndClock(_value, CSpect.GetNextRegister(REG_VIDEO_TIMING));
                        // We are transparently logging without handling the write, so return false
                        return true;
                    case PORT_UART_TX:
                        // This is an outgoing UART byte which should be sent to our buffered UART implementation
                        currentPort.Write(new byte[] { _value }, 0, 1);
                        // If we are actively handling the write, return true.
                        return true;
                    case PORT_UART_RX:
                        // Writes to this port represent changes to the prescaler which cause the UART baud to change
                        currentPort.SetPrescaler(_value);
                        return true;
                    case REG_RESET:
                        // Only set this if we're currently targeting the ESP UART
                        if (Target == UARTTargets.ESP)
                            currentPort.EspReset(_value);
                        // Always handle this nextreg
                        return true;
                    case REG_ESP_GPIO_ENABLE:
                        // Always set this enable value, even if we're not currently targeting the ESP UART
                        espPort.EnableEspGpio(_value);
                        // Always handle this nextreg
                        return true;
                    case REG_ESP_GPIO:
                        // Only change ESP GPIO values if we're currently targeting the ESP UART
                        if (Target == UARTTargets.ESP)
                            currentPort.SetEspGpio(_value);
                        // Always handle this nextreg
                        return true;
                    case PI_GPIO_EN_1:
                        // Always set this enable value, even if we're not currently targeting the Pi UART
                        piPort.EnablePiGpio(_value);
                        // Always handle this nextreg
                        return true;
                    case PI_GPIO_RW_1:
                        // Only change Pi GPIO values if we're currently targeting the Pi UART
                        if (Target == UARTTargets.Pi)
                            currentPort.SetPiGpio(_value);
                        // Always handle this nextreg
                        return true;
                }
            }
            catch (Exception ex)
            {
                Debug.Write("UARTReplacement_Device.Write Exception: ");
                Debug.Write(ex.Message);
                Debug.Write(ex.StackTrace);
            }
            // Don't handle any writes we didn't register for
            return false;
        }

        public byte Read(eAccess _type, int _port, int _id, out bool _isvalid)
        {
            try
            {
                switch (_port)
                {
                    case PORT_UART_RX:
                        if (UART_RX_Internal)
                        {
                            // If this callback was triggered by the CSpect.InPort read from our own plugin,
                            // then return without handling the callback.
                            _isvalid = false;
                            return 0;
                        }
                        // Otherwise, the callback was triggered by another plugin or Z80 code running inside
                        // the emulator, so tell our plugin not to response to the next read callback,
                        // then read the actual value, then handle the callback with the read value.
                        // This is an incoming UART byte which should be read from our buffered UART implementation
                        // Z80 code will only call this if the status flag indicates data is ready when reading PORT_UART_TX.
                        _isvalid = true;
                        if (currentBuffer.Count > 0)
                        {
                            lock (currentSync)
                            {
                                return currentBuffer.Dequeue();
                            }
                        }
                        else
                            return 0x00;
                    case PORT_UART_TX:
                        if (UART_TX_Internal)
                        {
                            // If this callback was triggered by the CSpect.InPort read from our own plugin,
                            // then return without handling the callback.
                            _isvalid = false;
                            return 0;
                        }
                        // Reads from this port return a status flag in bit 0 indicating whether 
                        // there is any data available to read from the UART buffer.
                        _isvalid = true;
                        if (currentBuffer.Count > 0)
                            return 1;
                        else
                            return 0;
                }
            }
            catch (Exception ex)
            {
                Debug.Write("UARTReplacement_Device.Read Exception: ");
                Debug.Write(ex.Message);
                Debug.Write(ex.StackTrace);
            }

            // Don't handle any reads we didn't register for
            _isvalid = false;
            return 0xff;
        }

        public void Esp_DataReceived(object sender, System.IO.Ports.SerialDataReceivedEventArgs e)
        {
            var port = sender as System.IO.Ports.SerialPort;
            if (port != null && port.BytesToRead > 0)
            {
                var bytes = new byte[port.BytesToRead];
                port.Read(bytes, 0, bytes.Length);
                lock(espSync)
                {
                    foreach(Byte b in bytes)
                    {
                        espBuffer.Enqueue(b);
                    }
                }
            }
        }

        public void Pi_DataReceived(object sender, System.IO.Ports.SerialDataReceivedEventArgs e)
        {
            var port = sender as System.IO.Ports.SerialPort;
            if (port != null && port.BytesToRead > 0)
            {
                var bytes = new byte[port.BytesToRead];
                port.Read(bytes, 0, bytes.Length);
                lock (piSync)
                {
                    foreach (Byte b in bytes)
                    {
                        piBuffer.Enqueue(b);
                    }
                }
            }
        }

        public void Tick()
        {
        }

        public bool KeyPressed(int _id)
        {
            return false;
        }

        public void Reset()
        {
        }

        public void OSTick()
        {
        }

        private SerialPort currentPort
        {
            get
            {
                return Target == UARTTargets.ESP ? espPort : piPort;
            }
        }

        private Buffer currentBuffer
        {
            get
            {
                return Target == UARTTargets.ESP ? espBuffer : piBuffer;
            }
        }

        private object currentSync
        {
            get
            {
                return Target == UARTTargets.ESP ? espSync : piSync;
            }
        }
    }
}

