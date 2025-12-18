using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using System.Text;
using System.Reflection;
using ModbusServer;
using System.Threading;
using System.IO;

namespace SerialPortAxControl
{
    /// <summary>
    /// ActiveX control/wrapper for ModbusServer.SerialPortServer
    /// used in Mach3 context, environment directory = Mach3 folder
    /// script for register activex in windows:
    /// C:\Windows\SysWOW64\regsvr32.exe C:\SerialPortActiveXControl.dll
    /// script for update activex: 
    /// "C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\TlbExp.exe" "$(TargetFileName)" "$(TargetFileName)".tlb
    /// </summary>
    [ProgId("SerialPortAxControl.SerialPortAxControl")]
    [Guid("fd8a161f-8563-4118-8130-7f51940f5088")]
    [ComVisible(true)]
    public class SerialPortAxControl : IDisposable
    {
        const int InvalidPos = SerialPortServer.Code_MoveEnded;
        private class SerialPortActiveXControlContext
        {
            public SerialPortServer _serialPortServer;
            public Settings _settings;
        }

        private SerialPortActiveXControlContext _ctx;

        /// <summary>
        /// This dictionary cache used when Activex wrapper around serial porty used in Mach3 like plugin. Typically Mach3 plugin include vbscript macros and activex control, activex control lived in macros. 
        /// With using cache one serial port instance can keep connection during gcode file execution not only during in macros execution and activex control recreated with each call 'CreateObject(SerialPortAxControl.SerialPortAxControl)'. 
        /// </summary>
        private static readonly Dictionary<string, SerialPortActiveXControlContext> _cachedSerialPortActiveX;
        private static volatile int _cacheItemIsAdded;

        [ComVisible(true)]
        public bool ContinueReceiveResponse
        {
            get
            {
                if (_ctx == null)
                    return false;
                else
                    return _ctx._serialPortServer.ContinueReceiveResponse;

            }
        }

        static SerialPortAxControl()
        {
            _cacheItemIsAdded = 0;
            _cachedSerialPortActiveX = new Dictionary<string, SerialPortActiveXControlContext>();
        }


        private static SerialPortActiveXControlContext GetContextFromCache(string cacheKey)
        {
            _cachedSerialPortActiveX.TryGetValue(cacheKey, out SerialPortActiveXControlContext result);

            return result;
        }

        private static void AddContextToCache(SerialPortActiveXControlContext context, string cacheKey)
        {
            if (context == null)
                return;

            while (System.Threading.Interlocked.CompareExchange(ref _cacheItemIsAdded, 1, 0) != 0)
            {
                for (int i = 0, j = 0; i < 100; i++)
                {
                    j += i * 3;
                }
            }

            try
            {
                if (!_cachedSerialPortActiveX.ContainsKey(cacheKey))
                {
                    _cachedSerialPortActiveX.Add(cacheKey, context);
                }
                else
                    _cachedSerialPortActiveX[cacheKey] = context;
            }
            finally
            {
                _cacheItemIsAdded = 0;
            }
        }

        public void Dispose()
        {
        }

        [ComVisible(true)]
        public string CreateSerialPort2(string portName, int baudRate, int readTimeout, string cacheKey)
        {
            return CreateSerialPort(portName, baudRate, "8-1-N", readTimeout, 1000, 1, 256, cacheKey);
        }

        [ComVisible(true)]
        public string CreateSerialPort(string portName, int baudRate, string dataBits_stopBits_Parity_combination, int readTimeout, int writeTimeout, int commandQueueSize, int inputBufferSize, string cacheKey)
        {
            _ctx = new SerialPortActiveXControlContext() { _serialPortServer = new SerialPortServer() };

            try
            {
                _ctx._serialPortServer.CreateSerialPort(portName, baudRate, dataBits_stopBits_Parity_combination, readTimeout, writeTimeout, commandQueueSize, inputBufferSize, null);
                _ctx._settings = _ctx._serialPortServer.Settings;

                if (cacheKey != null)
                {
                    AddContextToCache(_ctx, cacheKey);
                }
            }
            catch (Exception ex)
            {
                return "Create serial port error:" + Log.GetExceptionErrorDescription(ex);
            }

            Log.LogInfo("Activex Serial Port was created.");
            return "OK";
        }

        [ComVisible(true)]
        public string SetCacheKey(string cacheKey)
        {
            if (cacheKey != null)
            {
                _ctx = GetContextFromCache(cacheKey);
                if (_ctx != null)
                    return "OK";
                else
                    return $"Serial port instance with key '{cacheKey}' not exists";
            }
            else
                return "cacheKey argument not set";
        }

        private void HandleCommonErrors(bool throwException = true)
        {
            HandleCommonErrors(out AxisSettings axis, out string error);
            if (error != "" && throwException)
            {
                throw new Exception(error);
            }
        }

        private void HandleCommonErrors(out AxisSettings axis, out string error, int axisIndex = -1)
        {
            axis = null;
            error = "";

            if (_ctx == null)
                error = "Serial port Activex control not inialized";
            if (!_ctx._serialPortServer.SerialPort.IsOpen)
                error = "Serial port not open";
            if (axisIndex != -1)
            {
                KeyValuePair<char, AxisSettings> axisSettings = _ctx._settings.Axis.FirstOrDefault(a => a.Value.Index == axisIndex);
                if (axisSettings.Value == null)
                    error = "Axis index argument out of range";
                if (axisSettings.Value.Disabled)
                    error = "Axis was disabled and not used";

                if (error == "")
                    axis = axisSettings.Value;
            }
        }

        [ComVisible(true)]
        public string CreateAxisSettings(int axisIndex, uint stepsPerMm, bool inverseMove, uint constantSpeed, float curPosition, float axisMinValue, float axisMaxValue, uint curPosNotifyPeriod)
        {
            HandleCommonErrors(out AxisSettings axisSettings, out string error, axisIndex);
            if (error.StartsWith("Axis was disabled"))
            {
                error = "AXISDISABLED";
            }
            if (error != "")
                return error;

            return _ctx._serialPortServer.SetAxisSettings(axisSettings.AxisName[0], stepsPerMm, inverseMove, constantSpeed, constantSpeed, curPosition, axisMinValue, axisMaxValue, curPosNotifyPeriod);
        }

        [ComVisible(true)]
        public string MoveAxis(int axisIndex, float newPos, uint moveSpeed, bool waitResponse, int timeout)
        {
            HandleCommonErrors(out AxisSettings axisSettings, out string error, axisIndex);
            if (error != "")
                return error;

            return _ctx._serialPortServer.MoveAxis(axisSettings.AxisName[0], newPos, moveSpeed, waitResponse, timeout);
        }



        [ComVisible(true)]
        public float GetCurPosition(int axisIndex)
        {
            HandleCommonErrors(out AxisSettings axisSettings, out string error, axisIndex);
            if (error != "")
                return InvalidPos;

            return _ctx._serialPortServer.GetCurPosition(axisSettings.AxisName[0]);
        }

        [ComVisible(true)]
        public float GetCurMovingAxisPos(int axisIndex)
        {
            HandleCommonErrors(out AxisSettings axisSettings, out string error, axisIndex);
            if (error != "")
                return InvalidPos;

            return _ctx._serialPortServer.GetCurMovingAxisPos();
        }

        [ComVisible(true)]
        public string Stop(string axis)
        {
            HandleCommonErrors(out AxisSettings axisSettings, out string error);
            if (error != "")
                return error;
            if (string.IsNullOrEmpty(axis))
                return "Axis argument not set";

            return _ctx._serialPortServer.Stop(axis[0]);
        }

        [ComVisible(true)]
        public string EnableJoystick(int axisIndex, bool disable)
        {
            HandleCommonErrors(out AxisSettings axisSettings, out string error, axisIndex);
            if (error != "")
                return error;

            return _ctx._serialPortServer.EnableJoystick(axisSettings.AxisName[0], disable);
        }

        [ComVisible(true)]
        public void SetSendCommandsToQueue(bool sendCommandsToQueue)
        {
            HandleCommonErrors();
            _ctx._serialPortServer.SendCommandsToQueue = sendCommandsToQueue;
        }

        [ComVisible(true)]
        public void WaitEndCommandQueue()
        {
            HandleCommonErrors();
            _ctx._serialPortServer.WaitEndCommandQueue();
        }

        /// <summary>
        /// Open serial port after it was created via CreateSerialPort method.
        /// </summary>
        /// <returns>"OK" if serial port connection was established, else error description.</returns>
        [ComVisible(true)]
        public string OpenSerialPort()
        {
            if (_ctx != null)
            {
                try
                {
                    Log.LogInfo("Before Activex Serial port open.");
                    _ctx._serialPortServer.SerialPort.Open();
                }
                catch (Exception ex)
                {
                    Log.LogException(ex);
                    return ex.Message;
                }

                Log.LogInfo("Activex Serial port was opened.");
                return "OK";
            }

            return "Activex Serial port not initialized.";
        }


        /// <summary>
        /// Close serial port.
        /// </summary>
        [ComVisible(true)]
        public string CloseSerialPort(string cacheKey, bool closeAllCachedSerialPorts)
        {
            if (closeAllCachedSerialPorts)
            {
                foreach (SerialPortActiveXControlContext ctx in _cachedSerialPortActiveX.Values)
                {
                    if (ctx != null && ctx._serialPortServer.SerialPort.IsOpen)
                        ctx._serialPortServer.SerialPort.Close();
                }
            }
            else
            {
                if (cacheKey != null)
                {
                    _ctx = GetContextFromCache(cacheKey);
                }

                if (_ctx != null && _ctx._serialPortServer.SerialPort.IsOpen)
                    _ctx._serialPortServer.SerialPort.Close();

                Log.LogInfo("Connection closed");
            }

            return "OK";
        }

        public void Dispose(string cacheKey, bool disposeAllCachedSerialPorts)
        {
            if (disposeAllCachedSerialPorts)
            {
                foreach (SerialPortActiveXControlContext ctx in _cachedSerialPortActiveX.Values)
                {
                    if (ctx != null)
                        ctx._serialPortServer.Dispose();
                }
            }
            else
            {
                if (cacheKey != null)
                {
                    _ctx = GetContextFromCache(cacheKey);
                }

                if (_ctx != null)
                    _ctx._serialPortServer.Dispose();
            }
        }


        /// <summary>
        /// Send modbus command to serial port and get response.
        /// </summary>
        /// <param name="slaveAddress">Modbus device slave address.</param>
        /// <param name="registerType">Register type: Holding, Input, Discrete, Coils</param>
        /// <param name="functionType">Function type: ReadValue, SetValue, SetValues</param>
        /// <param name="registerAddress">Register address</param>
        /// <param name="data">For ReadValue operation it is count registers(registers sequence), which needed read. For SetValue it is value written to register.</param>
        /// <param name="cacheKey">Cache key when activex control cached(in mach3 this activex control live in macros body).</param>
        /// <returns></returns>
        [ComVisible(true)]
        public UInt16[] SendModbusCommandAndWaitResponse(byte slaveAddress, string registerType, string functionType, UInt16 registerAddress, UInt16 data, bool waitResponse)
        {
            HandleCommonErrors();
            ModbusCommandBuilder mbc = new ModbusCommandBuilder();

            if (!Enum.TryParse<ModbusRegisterType>(registerType, out ModbusRegisterType mrt) || !Enum.TryParse<ModbusFunction>(functionType, out ModbusFunction mf))
                return null;

            byte[] command = mbc.Build16bytesModbusCommand(slaveAddress, mrt, mf, registerAddress, data);
            return SendModbusCommandAndWaitResponse2(command, waitResponse);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="command"></param>
        /// <returns></returns>
        [ComVisible(true)]
        public UInt16[] SendModbusCommandAndWaitResponse2(byte[] command, bool waitResponse)
        {
            HandleCommonErrors();
            byte[] modbusReadFunctions = new byte[] { 1, 2, 3, 4 };
            //calculate crc16 if last 2 bytes zeroed
            if (command.Length > 2 && command[command.Length - 1] == 0 && command[command.Length - 2] == 0)
            {
                Utils.MakeCRC16(command, command.Length - 2);
            }

            SerialPortResponse response = SendCommandAndWaitResponse(command, waitResponse);

            if (response == null || response.ResponseCode != SerialPortResponseCode.OK)
                return null;
            else
            {
                if (modbusReadFunctions.Contains(command[1]))
                    return response.Get16BitModbusResponseValues();
                else
                    return new UInt16[] { 0 };
            }
        }


        /// <summary>
        /// Send binary command to serial port
        /// </summary>
        /// <param name="command"></param>
        /// <returns></returns>
        [ComVisible(true)]
        public SerialPortResponse SendCommandAndWaitResponse(byte[] command, bool waitResponse)
        {
            HandleCommonErrors();
            SerialPortResponse response;
            response = _ctx._serialPortServer.SendCommandAndWaitResponse(command, waitResponse);

            if (response == null || response.ResponseCode != SerialPortResponseCode.OK)
                return null;
            else
                return response;
        }

        /// <summary>
        /// Used with regasm.exe to register Activex in windows register
        /// </summary>
        /// <param name="key"></param>
        [ComRegisterFunction()]
        public static void RegisterClass(string key)
        {
            StringBuilder sb = new StringBuilder(key);
            sb.Replace(@"HKEY_CLASSES_ROOT\", "");

            RegistryKey k = Registry.ClassesRoot.OpenSubKey(sb.ToString(), true);

            RegistryKey ctrl = k.CreateSubKey("Control");
            ctrl.Close();

            RegistryKey inprocServer32 = k.OpenSubKey("InprocServer32", true);
            inprocServer32.SetValue("CodeBase", Assembly.GetExecutingAssembly().CodeBase);
            inprocServer32.Close();

            k.Close();
        }

        /// <summary>
        /// Used with regasm.exe to unregister Activex
        /// </summary>
        /// <param name="key"></param>
        [ComUnregisterFunction()]
        public static void UnregisterClass(string key)
        {
            StringBuilder sb = new StringBuilder(key);
            sb.Replace(@"HKEY_CLASSES_ROOT\", "");

            RegistryKey k = Registry.ClassesRoot.OpenSubKey(sb.ToString(), true);

            if (k == null)
            {
                return;
            }
            k.DeleteSubKey("Control", false);

            RegistryKey inprocServer32 = k.OpenSubKey("InprocServer32", true);

            inprocServer32.DeleteSubKey("CodeBase", false);

            inprocServer32.Close();
        }
    }
}
