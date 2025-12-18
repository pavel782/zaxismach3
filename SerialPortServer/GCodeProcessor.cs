using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace ModbusServer
{
    public enum AxisType { StepMotor = 1, ServoMotor = 2, ServoContinousMotor = 4 }
    [Serializable]
    /// <summary>
    /// Mach3 axis
    /// </summary>
    public class AxisSettings
    {
        public string AxisName;//axis name
        public AxisType AxisType;//Only Step motor
        public bool Disabled;//true if axis not used in cnc processing and gcode conversion
        public bool InverseMove;//inverse rotation motor
        public uint CountStepsPerMm;//count steps per mm, this info can be copyed from Mach3 settings
        public float MinDistanceToMove;//min distance in mm when motor rotates, no less than one step
        public uint Speed;//axis speed mm per minute by default
        public uint G0Speed;//axis speed mm per minute for G0 command, jiggle move
        public uint MaxSpeed;//max speed mm per minute limit feed speed from gcode file
        public int StartPos;//start position which measured in steps, used in init macros
        public uint CurPositionNotifyPeriod;//period of current position notification
        public float AxisMaxValue = 40;//max value of axis in mm
        public float AxisMinValue = -20;//min value of axis in mm
        public uint MoveResponseTimeout;//timeout for move axis, it will be added to calculated timeout dt = distance in steps / speed in steps per sec
        public uint Mach3OemDro = 31;//mach3 oem dro - it is mach3 number of text label where axis changes can be seen during motion(this label with axis updated with period=ReportCurPosPeriod). Oem dro number = 1 - 836. Oem dro 31 is label from mach3 tab Settings, Encoder Z. -1 disable drp
        public uint Mach3OemDroMultiplier = 10000;

        public bool EnableEstopOnError;//generate press Mach3 EStop button, when error occured, most often error - arduino ch340 serial port connection error
        private uint _CurPositionCheckPeriod = 0;
        [XmlIgnore]
        public uint CurPositionCheckPeriod
        {
            get
            {
                if (CurPositionNotifyPeriod > 0 && _CurPositionCheckPeriod == 0)
                {
                    _CurPositionCheckPeriod = (uint)(CurPositionNotifyPeriod * 1.4);
                    if (CurPositionNotifyPeriod < 200)
                        _CurPositionCheckPeriod += 150;
                }

                return _CurPositionCheckPeriod;
            }
            private set
            {
                _CurPositionCheckPeriod = value;
            }
        }

        [XmlIgnore]
        public float CurPos;//current position in mm, in move state
        [XmlIgnore]
        public int Index;//Axis index in collection, disabled axis also counted


        public bool ReplaceAxis;//true if current axis in gcode must be replaced by macros eqvivalent, false if macros eqvivalent added after original move axis line(it is used for see axis changes in Mach3 UI)
    }


    public class Settings
    {
        [XmlIgnore]
        public readonly string InitMacrosName = "M4";//Mach3 macros which setup serial port connection and initialize arduino z axis 
        [XmlIgnore]
        public readonly string CloseSerialPortMacrosName = "M5";//Mach3 macros close serial port connection with arduino
        [XmlIgnore]
        public readonly string MoveMacrosName = "M3";//Mach3 macros which perform move axis

        public string PortName;//arduino board com port name
        public int BaudRate;   //serial port baud rate
        public bool EnableLog; //allow logging in files
        public string LogFolderPath;//folder where located log files
        public bool EnableJoystick;//allow use 5 pin, 2 axis arduino joystick for move axis
        public string ArduinoConfigureJoystickCommand;//arduino command which configure joystick
        public string DisablePluginMacrosList;
        public string OpenGCodeFileInitialDirectory;
        public string SaveGCodeFileInitialDirectory;
        public string SaveMacrosInitialDirectory;

        [XmlArrayAttribute]
        public AxisSettings[] _Axis;
        [XmlIgnore]
        public SortedList<char, AxisSettings> Axis;

        public void BeforeSerialize()
        {
            _Axis = Axis.Values.OrderBy(a => a.AxisName).ToArray();
        }

        public void AfterDeserialize()
        {
            Axis = new SortedList<char, AxisSettings>();
            int i = 0;
            foreach (AxisSettings axis in _Axis)
            {
                axis.Index = i++;
                Axis.Add(axis.AxisName[0], axis);
            }
        }

        public static Settings LoadXml(string path)
        {
            //by default settings.xml stored in current folder or subfolder GCConverter
            string settingsPath = path;
            if (settingsPath != null)
            {
                if (!File.Exists(settingsPath))
                    throw new FileNotFoundException(path);
            }
            else
            {
                settingsPath = Path.Combine(Environment.CurrentDirectory, "settings.xml");
                if (!File.Exists(settingsPath))
                {
                    string settingsPath2 = Path.Combine(Environment.CurrentDirectory, "GCConverter", "settings.xml");
                    if (!File.Exists(settingsPath2))
                    {
                        throw new FileNotFoundException($"File settings.xml must be located in current folder(Mach3) or subfolder GCConverter, allowed file path: {string.Join(Environment.NewLine, settingsPath, settingsPath2)}");
                    }
                    settingsPath = settingsPath2;
                }
            }

            Settings settings = null;
            XmlSerializer xmlSerializer = new XmlSerializer(typeof(Settings));
            using (FileStream fs = new FileStream(settingsPath, FileMode.Open))
            {
                settings = (Settings)xmlSerializer.Deserialize(fs);
                settings.AfterDeserialize();
            }

            return settings;
        }

        public void SaveXml(string path)
        {
            XmlSerializer xmlSerializer = new XmlSerializer(typeof(Settings));
            this.BeforeSerialize();

            using (FileStream fs = new FileStream(path, FileMode.Create))
            {
                xmlSerializer.Serialize(fs, this);
            }
        }
    }

    /// <summary>
    /// Convert GO,G1 linear move mach3 commands to their Arduino equivalent
    /// </summary>
    public class GCodeProcessor
    {
        private readonly SortedList<char, AxisSettings> _replacedAxisSettings;
        private readonly Settings _settings;
        private readonly char[] _replacedAxises;
        private readonly char[] _allAxises;
        private readonly string[] _disablePluginMacrosList;
        private ConvertGCodeContext _context;

        /*Mach3 allowed using numeric variables in gcode with index address, syntax: #index=value #1=10.1*/
        //variables memory schema: index address 1-14 store one-use start position for axis(it can overwrite default start pos.=0), index address 15 store current moved axis index, index address 16 store cur axis move end location, index address 17 store one-use cur axis speed(it overwrite default cur axis speed), index address 18 store one-use trigger show cur position window, 19,20 - disable/enable joystick and assign joystick moved axis, index address 21 store one-use spindle trigger(for run/stop spindle on m3,m4,m5), index address 22 store flag for disable/enable this plugin
        public const uint Mach3AxisInitMacrosVariablesCount = 14;
        public const uint Mach3AxisMoveMacrosVariablesCount = 9;
        public const uint Mach3AxisCurPosStartIndex = 1;
        public const uint Mach3CommonVariablesStartIndex = 15;
        public const uint Mach3AxisVariablesStartIndex = 23;

        public GCodeProcessor(Settings settings, char[] allAxises)
        {
            _settings = settings;
            _replacedAxises = _settings.Axis.Keys.Where(a => !_settings.Axis[a].Disabled).ToArray();
            _replacedAxisSettings = new SortedList<char, AxisSettings>();
            foreach (char axis in _replacedAxises)
                _replacedAxisSettings.Add(axis, _settings.Axis[axis]);

            _allAxises = allAxises ?? new char[] { 'X', 'Y', 'Z', 'A', 'B', 'C', 'S' };
            _disablePluginMacrosList = string.IsNullOrEmpty(settings.DisablePluginMacrosList) ? new string[] { } : _settings.DisablePluginMacrosList.Split(";".ToCharArray(), StringSplitOptions.RemoveEmptyEntries);
        }

        public enum CommandType { MOVE, G0, G1 };
        class LineReplacedAxisParams
        {
            public char axis;
            public int axisIndex;
            public float newLocation;
            public uint speed;
            public CommandType commandType;
        }

        class ConvertGCodeContext
        {
            public byte[] curBuffer;
            public byte[] prevBuffer;
            public byte[] lineBuffer;
            public int sourceLineNumber;
            public int dstLineNumber;
            public int curBufferSize;
            public int lastLineStartPos;
            public int convertedFileBufferSize;
            public StreamWriter convertGCodeFile;
            public StringBuilder convertedGCode;
            public int curMovedAxisIndex = -1;
        }

        private void AppendGCodeNewLines(StringBuilder convertedGCode, string newGCodeLines)
        {
            if ((convertedGCode.Length + (newGCodeLines.Length * 3)) > _context.convertedFileBufferSize)//write buffer to file when data accumulated
            {
                _context.convertGCodeFile.Write(convertedGCode.ToString());
                convertedGCode.Clear();
            }

            convertedGCode.Append(newGCodeLines);
        }

        private string HandleGCodeSpindle(StringBuilder convertedGCode, string line)//one line can contain only one m3,m4 or m5
        {
            int spindleMacrosCodeStart = line.IndexOf("M3", StringComparison.OrdinalIgnoreCase);
            bool isM5 = false;
            spindleMacrosCodeStart = spindleMacrosCodeStart == -1 ? line.IndexOf("M4", StringComparison.OrdinalIgnoreCase) : spindleMacrosCodeStart;
            if (spindleMacrosCodeStart == -1)
            {
                spindleMacrosCodeStart = line.IndexOf("M5", StringComparison.OrdinalIgnoreCase);
                if (spindleMacrosCodeStart == -1)
                    return line;
                isM5 = true;
            }

            if (line.Length > spindleMacrosCodeStart + 2 && char.IsDigit(line.Substring(spindleMacrosCodeStart + 2, 1)[0]))
            {
                return line;
            }

            string spindleSwitchPrefix = $"#{(Mach3CommonVariablesStartIndex + 6)}=1";

            if (isM5)
            {
                convertedGCode.AppendLine(spindleSwitchPrefix);
                convertedGCode.AppendLine("M5");
                return line.Remove(spindleMacrosCodeStart, 2);
            }
            else
            {
                int spindleSpeedCodeStart = line.IndexOf("S", StringComparison.OrdinalIgnoreCase);
                if (spindleSpeedCodeStart == -1)
                {
                    convertedGCode.AppendLine(spindleSwitchPrefix);
                    convertedGCode.AppendLine(line.Substring(spindleMacrosCodeStart, 2));
                    return line.Remove(spindleMacrosCodeStart, 2);
                }

                int spindleSpeedCodeLength;
                int i = spindleSpeedCodeStart + 1;
                for (; i < line.Length; i++)
                {
                    if (!char.IsDigit(line[i]))
                    {
                        i -= 1;
                        break;
                    }
                }

                i = Math.Min(i, line.Length - 1);
                if (i > spindleSpeedCodeStart)
                {
                    spindleSpeedCodeLength = i - spindleSpeedCodeStart + 1;
                    convertedGCode.AppendLine(spindleSwitchPrefix);
                    convertedGCode.Append(line.Substring(spindleSpeedCodeStart, spindleSpeedCodeLength));
                    convertedGCode.AppendLine(line.Substring(spindleMacrosCodeStart, 2));
                    return spindleSpeedCodeStart > spindleMacrosCodeStart ? line.Remove(spindleSpeedCodeStart, spindleSpeedCodeLength).Remove(spindleMacrosCodeStart, 2) : line.Remove(spindleMacrosCodeStart, 2).Remove(spindleSpeedCodeStart, spindleSpeedCodeLength);
                }
                else
                {
                    convertedGCode.AppendLine(spindleSwitchPrefix);
                    convertedGCode.AppendLine(line.Substring(spindleMacrosCodeStart, 2));
                    return line.Remove(spindleMacrosCodeStart, 2);
                }
            }
        }

        private void InsertInitMacros()
        {
            _context.convertedGCode.AppendLine($"#{(Mach3CommonVariablesStartIndex + 7)}=1");//enable plugin before execute gcode

            foreach (char axis in _replacedAxisSettings.Keys)
            {
                AxisSettings aset = _replacedAxisSettings[axis];
                if (!aset.Disabled)
                {
                    _context.convertedGCode.AppendLine($"#{(Mach3AxisCurPosStartIndex + _replacedAxisSettings.Keys.IndexOf(axis))}={aset.StartPos.ToString("N3")}");
                }
                _context.dstLineNumber++;
            }

            _context.convertedGCode.AppendLine(_settings.CloseSerialPortMacrosName);

            _context.convertedGCode.AppendLine(_settings.InitMacrosName);
            _context.dstLineNumber += 2;
        }

        private void HandleGCodeLine(StringBuilder convertedGCode, string line)
        {
            List<string> newGCodeLines = new List<string>();

            CommandType commandType = line.StartsWith("G0") ? CommandType.G0 : line.StartsWith("G1") ? CommandType.G1 : CommandType.MOVE;
            List<LineReplacedAxisParams> replacedAxisParams = new List<LineReplacedAxisParams>();

            int i = 0, j;
            string replacedLine = line.Trim();
            float feedSpeed = -1;
            _context.sourceLineNumber++;
            if (_context.sourceLineNumber == 1)
            {
                _context.convertedGCode.Clear();
                InsertInitMacros();
                AppendGCodeNewLines(convertedGCode, _context.convertedGCode.ToString());
                return;
            }

            if (line.StartsWith("("))//skip comments
            {
                AppendGCodeNewLines(convertedGCode, line + Environment.NewLine);
                _context.dstLineNumber++;
                return;
            }

            if (_disablePluginMacrosList.Any(m => line.Contains(m)))
            {
                string enablePluginVariable = "#" + (Mach3CommonVariablesStartIndex + 7);
                AppendGCodeNewLines(convertedGCode, $"{enablePluginVariable}=0{Environment.NewLine}{line}{Environment.NewLine}{enablePluginVariable}=1{Environment.NewLine}");
                _context.dstLineNumber++;
                return;
            }

            replacedLine = HandleGCodeSpindle(convertedGCode, replacedLine);
            if (replacedLine.Length == 0)
                return;

            for (; i < line.Length; i++)
            {
                char column = line[i];
                if (char.IsLetter(column))
                {
                    if (column == 'F')
                    {
                        int feedSpeedStartPos = i;
                        int feedSpeedEndPos = feedSpeedStartPos;

                        for (j = i + 1; j < line.Length; j++)
                        {
                            char column2 = line[j];
                            if (char.IsDigit(column2) || column2 == '-' || column2 == '.')
                            {
                                feedSpeedEndPos++;
                            }
                            else
                                break;
                        }

                        if (feedSpeedEndPos > feedSpeedStartPos)
                        {
                            string feed = line.Substring(feedSpeedStartPos + 1, feedSpeedEndPos - feedSpeedStartPos);
                            feedSpeed = float.Parse(feed);
                        }

                        i = j - 1;
                        continue;
                    }

                    char replacedAxis = char.MinValue;
                    for (j = 0; j < _replacedAxises.Length; j++)
                    {
                        if (_replacedAxises[j] == column)
                        {
                            replacedAxis = column;
                            j = _replacedAxises.Length;
                        }
                    }

                    //parse axis new move location
                    if (replacedAxis != char.MinValue)
                    {
                        int locationStartPos = i;
                        int locationEndPos = locationStartPos;

                        for (j = i + 1; j < line.Length; j++)
                        {
                            char column2 = line[j];
                            if (char.IsDigit(column2) || column2 == '-' || column2 == '.')
                            {
                                locationEndPos++;
                            }
                            else
                                break;
                        }

                        if (locationEndPos > locationStartPos)
                        {
                            string newLocation = line.Substring(locationStartPos + 1, locationEndPos - locationStartPos);
                            LineReplacedAxisParams axisParams = new LineReplacedAxisParams()
                            {
                                axis = column,
                                axisIndex = _replacedAxisSettings[column].Index,
                                commandType = commandType,
                                newLocation = float.Parse(newLocation)
                            };

                            if (_replacedAxisSettings[axisParams.axis].ReplaceAxis)
                                replacedLine = replacedLine.Replace(column + newLocation, "");
                            replacedAxisParams.Add(axisParams);
                        }

                        i = j - 1;
                    }
                }
            }

            if (feedSpeed != 0 || commandType == CommandType.G0)
            {
                foreach (LineReplacedAxisParams axisParams in replacedAxisParams)
                {
                    axisParams.speed = feedSpeed != 0 ? Math.Min((uint)feedSpeed, _replacedAxisSettings[axisParams.axis].MaxSpeed) : _replacedAxisSettings[axisParams.axis].G0Speed;//feed measured like mm's per minute
                }
            }

            StringBuilder sb = _context.convertedGCode;
            sb.Clear();

            if (replacedAxisParams.Count > 0 && _allAxises.All(ax => replacedLine.IndexOf(ax) == -1))
                replacedLine = "";

            if (replacedLine != "")
            {
                sb.AppendLine(replacedLine);
                _context.dstLineNumber++;
            }

            for (i = 0; i < replacedAxisParams.Count; i++)
            {
                LineReplacedAxisParams axisParams = replacedAxisParams[i];
                AxisSettings axisSettings = _replacedAxisSettings[axisParams.axis];
                if (_context.curMovedAxisIndex != axisParams.axisIndex)
                {
                    sb.AppendLine($"#{Mach3CommonVariablesStartIndex}={axisParams.axisIndex}");
                    _context.curMovedAxisIndex = axisParams.axisIndex;
                    _context.dstLineNumber++;
                }

                sb.AppendLine($"#{(Mach3CommonVariablesStartIndex + 1)}={axisParams.newLocation.ToString("N3")}");

                if (axisParams.speed != 0 && axisSettings.Speed != axisParams.speed)
                {
                    sb.AppendLine($"#{(Mach3CommonVariablesStartIndex + 2)}={axisParams.speed}");
                    _context.dstLineNumber++;
                }

                sb.AppendLine(_settings.MoveMacrosName);
                _context.dstLineNumber += 2;
            }

            AppendGCodeNewLines(convertedGCode, sb.ToString());
        }

        private void ProcessGCode(StringBuilder convertedGCode)
        {
            string gCodeLine;
            int gCodeLineLength = 0;
            int lineStartPos = 0;
            int lineEndPos = 0;

            if (_context.prevBuffer != null && !object.Equals(_context.curBuffer, _context.prevBuffer) && _context.lastLineStartPos != -1)
            {
                lineStartPos = _context.lastLineStartPos;
                lineEndPos = Array.IndexOf(_context.curBuffer, (byte)'\n', 0);
                if (lineEndPos == -1)
                    lineEndPos = _context.curBufferSize - 1;
                Array.Copy(_context.prevBuffer, _context.lastLineStartPos, _context.lineBuffer, 0, _context.prevBuffer.Length - _context.lastLineStartPos);
                Array.Copy(_context.curBuffer, 0, _context.lineBuffer, _context.prevBuffer.Length - _context.lastLineStartPos, lineEndPos);
                gCodeLineLength = _context.prevBuffer.Length - _context.lastLineStartPos + lineEndPos;
                gCodeLine = Encoding.UTF8.GetString(_context.lineBuffer, 0, gCodeLineLength).Trim();
                _context.lastLineStartPos = lineEndPos + 1;
                HandleGCodeLine(convertedGCode, gCodeLine);
                if (_context.lastLineStartPos == _context.curBufferSize)
                {
                    _context.lastLineStartPos = -1;
                    return;
                }
                else
                {
                    lineStartPos = _context.lastLineStartPos;
                }
            }
            else if (object.Equals(_context.curBuffer, _context.prevBuffer))//last chunk of file
            {
            }

            while (lineStartPos > -1)
            {
                lineEndPos = Array.IndexOf(_context.curBuffer, (byte)'\n', lineStartPos);
                if (lineEndPos == -1)
                {
                    _context.lastLineStartPos = lineStartPos;
                    lineStartPos = -1;
                }
                else
                {
                    gCodeLineLength = lineEndPos - lineStartPos;
                    Array.Copy(_context.curBuffer, lineStartPos, _context.lineBuffer, 0, gCodeLineLength);
                    gCodeLine = Encoding.UTF8.GetString(_context.lineBuffer, 0, gCodeLineLength).Trim();
                    lineStartPos = lineEndPos + 1;
                    if (lineStartPos == _context.curBufferSize)//reach the end of buffer
                    {
                        _context.lastLineStartPos = lineStartPos = -1;
                    }

                    HandleGCodeLine(convertedGCode, gCodeLine);
                }
            }
        }

        //Convert mach3 axis moves in GCode file to arduino axis movements
        public void ConvertGCodeFile(string fileName, string convertedFileName, int maxLineLength, ref string errMes)
        {
            if (_settings.Axis.Keys.Count == 0 || _settings.Axis.Values.All(a => a.Disabled))
                errMes = "No axis selected. Operation canceled.";

            StringBuilder result = new StringBuilder(4096 * 16);
            char[] replacedAxises = _replacedAxisSettings.Keys.ToArray();
            byte[] curBuffer = new byte[4096];
            byte[] prevBuffer = new byte[4096];
            byte[] buffer;
            int readBytes;
            bool useCurBuffer = false;

            _context = new ConvertGCodeContext();

            _context.sourceLineNumber = 0;
            _context.dstLineNumber = 0;
            _context.curBuffer = null;
            _context.curBufferSize = 0;
            _context.prevBuffer = null;
            _context.lineBuffer = new byte[maxLineLength];
            _context.lastLineStartPos = -1;
            _context.convertedFileBufferSize = 4096 * 16 * 8;
            _context.convertedGCode = new StringBuilder(512);

            using (FileStream fs = new FileStream(fileName, FileMode.Open))
            {
                File.Delete(convertedFileName);

                using (_context.convertGCodeFile = new StreamWriter(convertedFileName, true))
                {
                    buffer = useCurBuffer ? curBuffer : prevBuffer;
                    while ((readBytes = fs.Read(buffer, 0, 4096)) > 0)
                    {
                        _context.curBuffer = buffer;
                        _context.curBufferSize = readBytes;
                        ProcessGCode(result);

                        useCurBuffer = !useCurBuffer;
                        buffer = useCurBuffer ? curBuffer : prevBuffer;
                        _context.prevBuffer = _context.curBuffer;
                    }

                    if (result.Length > 0)
                    {
                        result.AppendLine(_settings.CloseSerialPortMacrosName);

                        _context.convertGCodeFile.Write(result.ToString());
                        _context.convertGCodeFile.Flush();
                    }
                }
            }
        }
    }
}
