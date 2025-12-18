using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.IO.Ports;
using System.Runtime.InteropServices;

namespace ModbusServer
{
    [ComVisible(true)]
    public enum SerialPortResponseCode { OK, QueueExceed, SerialPortBusy, WaitForProcessing, Timeout, Error };


    [ComVisible(true)]
    public class SerialPortResponse
    {
        [ComVisible(true)]
        public byte[] ResponseContent { get; private set; }
        [ComVisible(true)]
        public SerialPortResponseCode ResponseCode { get; private set; }

        public SerialPortResponse(byte[] responseContent, SerialPortResponseCode responseCode)
        {
            ResponseContent = responseContent;
            ResponseCode = responseCode;
        }

        [ComVisible(true)]
        public UInt16? Get16BitModbusResponseValue()
        {
            if (ResponseContent == null || ResponseContent.Length < 5)
                return null;

            return (UInt16)((256 * ResponseContent[3]) + ResponseContent[4]);
        }

        [ComVisible(true)]
        public UInt16[] Get16BitModbusResponseValues()
        {
            if (ResponseContent == null || ResponseContent.Length < 5)
                return null;

            byte countRegisters = (byte)(ResponseContent[2] / 2);
            if (ResponseContent.Length < (3 + countRegisters * 2))
                return null;

            UInt16[] readValues = new UInt16[countRegisters];
            int firstReadByteIndex = 3;
            int lastReadByteIndex = 2 + (countRegisters * 2);
            for (int readByteIndex = firstReadByteIndex, i = 0; readByteIndex < lastReadByteIndex; readByteIndex += 2, i++)
            {
                readValues[i] = (UInt16)((256 * ResponseContent[readByteIndex]) + ResponseContent[readByteIndex + 1]);
            }

            return readValues;
        }
    }

    /// <summary>
    /// Provide send Arduino commands and Modbus commands to serial port.
    /// Command can be executed by 3 ways:
    /// request command->wait response by AutoResetEvent object, request command->no wait response and polling progress of response, request->no wait, command  sended to queue thread for execution, response can be obtained via logs    
    /// Modbus command has binary format like : 'Slave Address 1 byte, Func Code 1 byte, Reg Address 2 byte, Data(when write)/Reads count(when read) 2 byte, crc16 2 byte(of prev. 6 bytes)'
    /// </summary>
    public class SerialPortServer : IDisposable
    {
        public const int Code_MoveEnded = 10000;
        public const int Code_IsMoveNotifyTimeout = 10001;
        public const int Code_Error = 10002;
        public const int MinMoveNotifyTimeout = 3000;
        public const int CommandTimeout = 5000;

        private Queue<object> _sendCommandQueue;
        private int _sendCommandQueueMaxLength;
        private AutoResetEvent _receiveResponseEvent;
        private AutoResetEvent _addedCommandToQueue;
        private AutoResetEvent _emptyCommandQueue;
        private bool _endCommandQueueLoop;
        private bool _disposed;
        private int _inputBufferChunkedResponseLeftOffset;
        private int _inputBufferResponseLastPos;
        private byte[] _inputBuffer;
        private byte[] _response;
        private volatile int _serialPortBusy;
        private Settings _settings;
        private SortedList<char, AxisSettings> _axisSettings;
        private char _curMovingAxis = ' ';
        private float _curMoveDestinationPoint;
        private uint _moveNotifyTimeout;
        private DateTime _lastMoveNotifyTime;
        private bool _hasMoveNotifyTimeout;
        public event EventHandler<byte[]> OnReceiveResponse;

        /// <summary>
        /// Serial port which handles commands
        /// </summary>
        public SerialPort SerialPort
        {
            get; set;
        }

        public Settings Settings
        {
            get { return _settings; }
            set { _settings = value; }
        }

        public bool ContinueReceiveResponse { get; private set; }
        /// <summary>
        /// True if all next commands will be sended in queue thread
        /// </summary>
        public bool SendCommandsToQueue { get; set; }

        public void Dispose()
        {
            if (_sendCommandQueueMaxLength == 0)
            {
                _disposed = true;
                _receiveResponseEvent.Close();
                _emptyCommandQueue.Close();
                if (SerialPort != null)
                    SerialPort.Close();
            }
            else
            {
                _disposed = true;
                _addedCommandToQueue.Set();//dispose executed in ProcessCommandsQueueLoop thread
            }
        }

        public SerialPort CreateSerialPort(string portName)
        {
            return CreateSerialPort(portName, 9600, "8-1-N");
        }

        public SerialPort CreateSerialPort(string portName, int baudRate, string dataBits_stopBits_Parity_combination)
        {
            return CreateSerialPort(portName, baudRate, dataBits_stopBits_Parity_combination, 100, 100, 0, 256, null);
        }

        public SerialPort CreateSerialPort(string portName, int baudRate, string dataBits_stopBits_Parity_combination, int readTimeout, int writeTimeout, int commandQueueMaxSize, int inputBufferSize, string settingsPath)
        {
            string[] allowedDataBits_stopBits_Parity_combinations = new string[] { "8-1-O", "8-1-E", "8-2-N", "8-1-N", "7-1-E" };
            if (baudRate < 0 || (dataBits_stopBits_Parity_combination != null && !allowedDataBits_stopBits_Parity_combinations.Contains((dataBits_stopBits_Parity_combination ?? "").ToUpper())))
                return null;

            string[] parts = dataBits_stopBits_Parity_combination != null ? dataBits_stopBits_Parity_combination.ToUpper().Split("-".ToCharArray(), StringSplitOptions.None) : null;
            SerialPort serialPort = null;

            try
            {
                _sendCommandQueue = new Queue<object>();
                _sendCommandQueueMaxLength = commandQueueMaxSize;
                _emptyCommandQueue = new AutoResetEvent(false);
                SendCommandsToQueue = false;
                _receiveResponseEvent = new AutoResetEvent(false);
                inputBufferSize = ((double)inputBufferSize / 2) == ((int)inputBufferSize / 2) ? inputBufferSize : inputBufferSize + 1;
                _inputBuffer = new byte[inputBufferSize];
                _serialPortBusy = 0;
                _endCommandQueueLoop = false;
                _settings = Settings.LoadXml(settingsPath);
                _axisSettings = _settings.Axis;
                
                serialPort = dataBits_stopBits_Parity_combination == null ? new SerialPort(portName, baudRate) :
                    new SerialPort(string.IsNullOrEmpty(portName) ? _settings.PortName : portName, baudRate == 0 ? _settings.BaudRate : baudRate, parts[2] == "N" ? Parity.None : parts[2] == "E" ? Parity.Even : parts[2] == "O" ? Parity.Odd : Parity.None, int.Parse(parts[0]), StopBits.One);
                serialPort.ReadTimeout = readTimeout;
                serialPort.WriteTimeout = writeTimeout;
                serialPort.DataReceived += SerialPort_DataReceived;
                serialPort.ErrorReceived += SerialPort_ErrorReceived;
                SerialPort = serialPort;
                if (_sendCommandQueueMaxLength > 0)
                {
                    _addedCommandToQueue = new AutoResetEvent(false);
                    Task.Run(() => ProcessCommandsQueueLoop());
                }

                OnReceiveResponse += SerialPortServer_OnReceiveResponse;
            }
            catch (Exception ex)
            {
                Log.LogException(ex);
                throw ex;
            }

            return serialPort;
        }

        private void SerialPort_ErrorReceived(object sender, SerialErrorReceivedEventArgs e)
        {
            throw new NotImplementedException();
        }

        public string SetAxisSettings(char axis, uint stepsPerMm, bool inverseMove, uint speed, uint g0Speed, float curPos, float axisMinValue, float axisMaxValue, uint curPosNotifyPeriod)
        {
            AxisSettings axisSettings = null;
            bool overwriteSettings = false;
            if (_axisSettings.ContainsKey(axis))
            {
                axisSettings = _axisSettings[axis];
                overwriteSettings = true;
            }
            else
            {
                axisSettings = new AxisSettings();
                _axisSettings.Add(axis, axisSettings);
            }

            axisSettings.CountStepsPerMm = stepsPerMm;
            axisSettings.InverseMove = inverseMove;
            axisSettings.Speed = Math.Min(speed, axisSettings.MaxSpeed);
            axisSettings.G0Speed = Math.Min(g0Speed, axisSettings.MaxSpeed);
            axisSettings.CurPos = curPos;
            axisSettings.AxisMinValue = !overwriteSettings ? axisMinValue : axisSettings.AxisMinValue;
            axisSettings.AxisMaxValue = !overwriteSettings ? axisMaxValue : axisSettings.AxisMaxValue;
            axisSettings.StartPos = 0;
            axisSettings.CurPositionNotifyPeriod = curPosNotifyPeriod;

            string request = $"SETAXIS;{axisSettings.AxisName};{axisSettings.CountStepsPerMm};{(axisSettings.InverseMove ? "true" : "false")};{axisSettings.Speed};{(int)(axisSettings.CurPos * axisSettings.CountStepsPerMm)};{axisSettings.CurPositionNotifyPeriod};{axisSettings.AxisMinValue};{axisSettings.AxisMaxValue};";
            SerialPortResponse response = SendCommandAndWaitResponse(request, true, CommandTimeout);

            string responseContent = System.Text.Encoding.ASCII.GetString(new SerialPortResponse(response.ResponseContent, SerialPortResponseCode.OK).ResponseContent).Trim();

            return responseContent;
        }

        public float GetCurPosition(char axis)
        {
            if (!_axisSettings.ContainsKey(axis))
            {
                return Code_Error;
            }

            float result = Code_Error;
            AxisSettings axisSettings = _axisSettings[axis];
            string request = $"GETPOS;{axisSettings.AxisName};";
            SerialPortResponse response = SendCommandAndWaitResponse(request, true, CommandTimeout);
            string responseContent = System.Text.Encoding.ASCII.GetString(new SerialPortResponse(response.ResponseContent, SerialPortResponseCode.OK).ResponseContent).Trim();
            if (!responseContent.StartsWith("CURPOS="))
            {
                result = Code_Error;
            }
            else
                result = float.Parse(responseContent.Substring("CURPOS=".Length)) / axisSettings.CountStepsPerMm;

            return result;
        }

        public string MoveAxis(char axis, float newPos, uint moveSpeed, bool waitResponse, int timeout)
        {
            string result = "";
            bool hasErrors = false;

            if (!_axisSettings.ContainsKey(axis))
            {
                return result = "Not found axis";
            }

            AxisSettings axisSettings = _axisSettings[axis];
            long dt = DateTime.Now.Ticks;

            if (axisSettings.MinDistanceToMove > 0 && Math.Abs(newPos - axisSettings.CurPos) < axisSettings.MinDistanceToMove)
            {
                return "OK";
            }

            string request = $"MOVE;{(int)(newPos * axisSettings.CountStepsPerMm)};{(moveSpeed > 0 ? moveSpeed.ToString() : axisSettings.Speed.ToString())};{axis};";
            if (newPos > axisSettings.AxisMaxValue || newPos < axisSettings.AxisMinValue)
            {
                result = $"New position out of range [{axisSettings.AxisMinValue}-{axisSettings.AxisMaxValue}]";
                hasErrors = true;
            }

            int countStepsToMove = (int)((newPos - axisSettings.CurPos) * axisSettings.CountStepsPerMm);
            if (countStepsToMove == 0)
            {
                return "OK";
            }

            if (!waitResponse && (axisSettings.CurPositionNotifyPeriod == 0) && !SendCommandsToQueue)
            {
                return "Move notification period not defined, use command queue for async execute command without notifications";
            }

            if (!hasErrors)
            {
                _curMovingAxis = axis;
                _curMoveDestinationPoint = newPos;

                SerialPortResponse response;
                if (!waitResponse)
                    _moveNotifyTimeout = 3 * axisSettings.CurPositionNotifyPeriod;
                if (_moveNotifyTimeout < MinMoveNotifyTimeout)
                    _moveNotifyTimeout += MinMoveNotifyTimeout;
                _lastMoveNotifyTime = DateTime.Now;
                _hasMoveNotifyTimeout = false;
                response = SendCommandAndWaitResponse(request, waitResponse, timeout);

                result = System.Text.Encoding.ASCII.GetString(new SerialPortResponse(response.ResponseContent, SerialPortResponseCode.OK).ResponseContent).Trim();
                if (waitResponse || axisSettings.CurPositionNotifyPeriod == 0)
                    axisSettings.CurPos = newPos;
            }

            return result;
        }

        public string Stop(char axis)
        {
            if (!_axisSettings.ContainsKey(axis))
            {
                return "Not found axis";
            }

            AxisSettings axisSettings = _axisSettings[axis];
            string request = $"STOP;{axisSettings.AxisName};";
            SerialPortResponse response = SendCommandAndWaitResponse(request, true, CommandTimeout);
            string responseContent = System.Text.Encoding.ASCII.GetString(new SerialPortResponse(response.ResponseContent, SerialPortResponseCode.OK).ResponseContent).Trim();

            return responseContent;
        }

        public string EnableJoystick(char joystickAxis, bool disable)
        {
            string request = disable || _settings.ArduinoConfigureJoystickCommand == "" ? "SETJS;false;" : (_settings.ArduinoConfigureJoystickCommand);
            if (request.StartsWith("SETJS;true;"))
            {
                if (!_axisSettings.ContainsKey(joystickAxis))
                {
                    return "Not found axis";
                }

                request = "SETJS;true;" + joystickAxis + request.Substring(request.IndexOf(";", "SETJS;true;".Length));
            }

            SerialPortResponse response;
            response = SendCommandAndWaitResponse(request, true, CommandTimeout);
            string responseContent = System.Text.Encoding.ASCII.GetString(new SerialPortResponse(response.ResponseContent, SerialPortResponseCode.OK).ResponseContent).Trim();

            return responseContent;
        }


        public float GetCurMovingAxisPos()
        {
            if (_curMovingAxis == ' ')
                return Code_MoveEnded;
            else if (_hasMoveNotifyTimeout)
                return Code_IsMoveNotifyTimeout;
            else
                return _axisSettings[_curMovingAxis].CurPos;
        }

        public SerialPortResponse SendCommandAndWaitResponse(byte[] command, bool waitResponse)
        {
            return SendCommandAndWaitResponse((object)command, waitResponse);
        }

        public SerialPortResponse SendCommandAndWaitResponse(string commandLine, bool waitResponse)
        {
            return SendCommandAndWaitResponse((object)commandLine, waitResponse);
        }


        /// <summary>
        /// Send command to serial port, and optionally wait response
        /// </summary>
        /// <param name="command">Command must be string or byte[] </param>
        /// <param name="waitResponse">true if need to wait response(on AutoResetEvent), false if command sended in queue or if response progress needed to be polled(like in Move command)</param></param>
        /// <param name="timeout">Define timeout of waiting response, 0 if timeout is equal to serial port ReadTimeout</param>
        /// <param name="fromQueue">private parameter, true if request was sended from queue thread</param>
        /// <returns></returns>
        private SerialPortResponse SendCommandAndWaitResponse(object command, bool waitResponse, int timeout = 0, bool fromQueue = false)
        {
            if (!SerialPort.IsOpen)
                return new SerialPortResponse(Encoding.ASCII.GetBytes("Serial port not open"), SerialPortResponseCode.Error);
            if (SendCommandsToQueue && waitResponse)
                throw new NotImplementedException("Command will be send to queue for async execution. In this case waiting response not implemented.");

            bool sendCommandToQueue = SendCommandsToQueue && !fromQueue;
            if (!sendCommandToQueue)
            {
                if (System.Threading.Interlocked.CompareExchange(ref _serialPortBusy, 1, 0) == 0)
                {
                    bool dataWritten = false;
                    bool readTimeOut = false;
                    DateTime dateStart = DateTime.Now;
                    bool hasErrors = false;

                    try
                    {
                        _response = null;
                        SerialPort_DataReceived(this, null);//read old serial port responses
                        _inputBufferResponseLastPos = 0;
                        _receiveResponseEvent.Reset();
                        ContinueReceiveResponse = true;

                        if (command is byte[])
                        {
                            SerialPort.Write((byte[])command, 0, ((byte[])command).Length);
                        }
                        if (command is string)
                        {
                            SerialPort.WriteLine((string)command);
                        }

                        dataWritten = true;

                        if (!waitResponse)
                        {
                            _response = Encoding.ASCII.GetBytes("OK");
                            return new SerialPortResponse(_response, SerialPortResponseCode.OK);
                        }

                        if (!_receiveResponseEvent.WaitOne(timeout == 0 ? SerialPort.ReadTimeout : timeout))
                        {
                            readTimeOut = true;
                            _response = Encoding.ASCII.GetBytes("Read Timeout.");
                            return new SerialPortResponse(_response, SerialPortResponseCode.Timeout);
                        }
                        else
                        {
                            return new SerialPortResponse(_response, SerialPortResponseCode.OK);
                        }
                    }
                    catch (System.IO.IOException ex)
                    {
                        hasErrors = true;
                        Log.LogException(ex);
                        _response = Encoding.ASCII.GetBytes("Serial Port IOERROR");
                        return new SerialPortResponse(_response, SerialPortResponseCode.Timeout);
                    }
                    catch (Exception ex)
                    {
                        hasErrors = true;
                        Log.LogException(ex);
                        throw ex;
                    }
                    finally
                    {
                        if (dataWritten && readTimeOut)
                        {
                            _receiveResponseEvent.Set();
                        }

                        _serialPortBusy = 0;

                        if (Log.Enabled && !hasErrors)
                        {
                            string responseContent = System.Text.Encoding.ASCII.GetString(_response).Trim();
                            double msDuration = (DateTime.Now - dateStart).TotalMilliseconds;
                            string commandLog = command is string ? (string)command : (((byte[])command).Length < 256 ? Utils.ConvertBytesToHex((byte[])command) : "[binary data]");
                            Log.LogInfo($"command={commandLog},response={responseContent},time={DateTime.Now.ToString("hh:mm:ss")},{(waitResponse ? "dtime=" + msDuration.ToString("N2") : "no wait")}");
                        }
                    }
                }
                else
                {
                    if (fromQueue)//serialPortBusy==0 was detected in queue but there it was captured again by other thread
                    {
                        return new SerialPortResponse(null, SerialPortResponseCode.SerialPortBusy);
                    }

                    sendCommandToQueue = true;
                }
                    
            }
            //if (sendCommandToQueue)
            {
                if (_sendCommandQueueMaxLength == 0)
                {
                    return new SerialPortResponse(null, SerialPortResponseCode.SerialPortBusy);
                }
                else
                {
                    if (_sendCommandQueue.Count < _sendCommandQueueMaxLength)
                    {
                        lock (_sendCommandQueue)
                        {
                            if (_sendCommandQueue.Count < _sendCommandQueueMaxLength)
                            {
                                _sendCommandQueue.Enqueue(command);
                                _addedCommandToQueue.Set();
                                _emptyCommandQueue.Reset();
                                return new SerialPortResponse(Encoding.ASCII.GetBytes("OK"), SerialPortResponseCode.WaitForProcessing);
                            }
                            else
                                return new SerialPortResponse(Encoding.ASCII.GetBytes("Queue Exceed"), SerialPortResponseCode.QueueExceed);
                        }
                    }
                    else
                        return new SerialPortResponse(Encoding.ASCII.GetBytes("Queue Exceed"), SerialPortResponseCode.QueueExceed);
                }
            }
        }

        public void WaitEndCommandQueue()
        {
            _emptyCommandQueue.WaitOne();
        }

        private void ProcessCommandsQueueLoop()
        {
            while (!_endCommandQueueLoop)
            {
                object command = null;
                while (_sendCommandQueue.Count > 0)
                {

                    if (command == null)
                    {
                        lock (_sendCommandQueue)
                        {
                            if (_sendCommandQueue.Count > 0)
                                command = _sendCommandQueue.Dequeue();
                        }
                    }

                    if (command != null)
                    {
                        while (_serialPortBusy != 0)//wait release serial port
                        {
                            Thread.Sleep(25);
                        }

                        try
                        {
                            SerialPortResponse response = SendCommandAndWaitResponse(command, true, 0, true);
                            if (response.ResponseCode != SerialPortResponseCode.SerialPortBusy)
                                command = null;
                        }
                        catch (Exception ex)
                        {
                            Log.LogException(ex);
                            throw ex;
                        }
                    }
                }
                _emptyCommandQueue.Set();

                _addedCommandToQueue.WaitOne();

                if (_disposed)
                {
                    _addedCommandToQueue.Close();
                    _receiveResponseEvent.Close();
                    _emptyCommandQueue.Close();
                    if (SerialPort != null)
                        SerialPort.Close();

                    return;
                }
            }
        }

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                int bytesToRead = SerialPort.BytesToRead;

                if (e == null)
                {
                    if (bytesToRead > 0)
                        //clear  serial port old response before execute new command, because there no unique id for request/response
                        SerialPort.ReadExisting();
                    return;
                }

                //handle buffer overflow
                if (_inputBuffer.Length <= _inputBufferResponseLastPos + bytesToRead)
                {
                    if (_inputBufferChunkedResponseLeftOffset > 0)
                    {
                        //move chunked response to begin of buffer
                        Array.Copy(_inputBuffer, _inputBufferResponseLastPos - _inputBufferChunkedResponseLeftOffset, _inputBuffer, 0, _inputBufferChunkedResponseLeftOffset);
                        _inputBufferResponseLastPos = _inputBufferChunkedResponseLeftOffset;
                        _inputBufferChunkedResponseLeftOffset = 0;
                    }
                    else
                        _inputBufferResponseLastPos = 0;
                }

                SerialPort.Read(_inputBuffer, _inputBufferResponseLastPos, bytesToRead);
                bool responseEndsWithNewLine = _inputBuffer[_inputBufferResponseLastPos + bytesToRead - 1] == '\n';

                if (!responseEndsWithNewLine)
                {
                    ContinueReceiveResponse = true;//read response by chunks until '\n'
                    _inputBufferChunkedResponseLeftOffset = _inputBufferChunkedResponseLeftOffset == -1 ? bytesToRead : _inputBufferChunkedResponseLeftOffset + bytesToRead;
                    _inputBufferResponseLastPos += bytesToRead;
                    return;
                }

                byte[] curResponse = new byte[bytesToRead + (_inputBufferChunkedResponseLeftOffset == -1 ? 0 : _inputBufferChunkedResponseLeftOffset)];
                Array.Copy(_inputBuffer, _inputBufferResponseLastPos, curResponse, 0, bytesToRead);
                _inputBufferResponseLastPos += bytesToRead;
                _inputBufferChunkedResponseLeftOffset = -1;
                Log.LogInfo(curResponse, 0, curResponse.Length, "Response:{0}");

                ContinueReceiveResponse = false;
                _response = curResponse;

                OnReceiveResponse(this, curResponse);
            }
            catch (Exception ex)
            {
                Log.LogException(ex);
                throw ex;
            }
            finally
            {
                if (!ContinueReceiveResponse)//this flag can be set in OnReceiveResponse handler
                {
                    _receiveResponseEvent.Set();
                }
            }
        }

        private void SerialPortServer_OnReceiveResponse(object sender, byte[] response)
        {
            if (_curMovingAxis != ' ')
            {
                string responseContent = System.Text.Encoding.ASCII.GetString(response);
                AxisSettings axisSettings = _axisSettings[_curMovingAxis];

                if (responseContent.Contains("NOSIGNAL"))
                {
                    
                }
                else if (responseContent.Contains("CURPOS="))
                {
                    int startPos = responseContent.LastIndexOf("CURPOS=") + "CURPOS=".Length;
                    int endPos = responseContent.IndexOf('\n', startPos);
                    if (endPos != -1)
                    {
                        axisSettings.CurPos = (float)(int.Parse(responseContent.Substring(startPos, endPos - startPos))) / axisSettings.CountStepsPerMm;
                    }
                    if ((DateTime.Now - _lastMoveNotifyTime).TotalMilliseconds > _moveNotifyTimeout)
                    {
                        _hasMoveNotifyTimeout = true;
                    }
                    else
                    {
                        _lastMoveNotifyTime = DateTime.Now;
                    }
                }

                bool movedToDestination = responseContent.Contains("OK");
                ContinueReceiveResponse = !movedToDestination && !responseContent.Contains("ERROR") && responseContent.Contains("CURPOS=");
                if (movedToDestination)
                {
                    axisSettings.CurPos = _curMoveDestinationPoint;
                    _curMovingAxis = ' ';
                    ContinueReceiveResponse = false;
                }
            }
            else
            {
                _curMovingAxis = ' ';
                ContinueReceiveResponse = false;
            }
        }
    }
}
