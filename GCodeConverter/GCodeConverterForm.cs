using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using ModbusServer;

namespace GCodeConverter
{
    public partial class GCodeConverterForm : Form
    {
        char[] allAxis = new char[] { 'X', 'Y', 'Z', 'A', 'B', 'C', 'S' };
        Settings settings;
        private bool hasLastLoadXmlError = false;
        private bool formIsClosing = false;

        public GCodeConverterForm()
        {
            InitializeComponent();

            LoadXml();

            if (settings != null)
            {
                if (Directory.Exists(settings.OpenGCodeFileInitialDirectory))
                    openFileDialog1.InitialDirectory = settings.OpenGCodeFileInitialDirectory;
                else
                    openFileDialog1.InitialDirectory = Environment.CurrentDirectory;
                if (Directory.Exists(settings.SaveMacrosInitialDirectory))
                    folderBrowserDialog1.SelectedPath = settings.SaveMacrosInitialDirectory;
                else
                    folderBrowserDialog1.SelectedPath = Environment.CurrentDirectory;
                if (Directory.Exists(settings.SaveGCodeFileInitialDirectory))
                    saveFileDialog1.InitialDirectory = settings.SaveGCodeFileInitialDirectory;
                else
                    saveFileDialog1.InitialDirectory = Environment.CurrentDirectory;
            }
        }

        private void LoadXml()
        {
            hasLastLoadXmlError = false;

            try
            {
                settings = Settings.LoadXml(null);
            }
            catch (Exception ex)
            {
                hasLastLoadXmlError = true;
                if (!formIsClosing)
                    MessageBox.Show($"Load Xml Error: {Log.GetExceptionErrorDescription(ex)}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string[] GetAxisSet()
        {
            string[] axisSet = new string[settings.Axis.Keys.Count * GCodeProcessor.Mach3AxisInitMacrosVariablesCount];
            int i = 0, ai = 0;
            foreach (char axis in settings.Axis.Keys)
            {
                AxisSettings axisSettings = settings.Axis[axis];
                //"axis stepsPerMm speed curPosCPeriod estop moveTimeout droNumber droMultiplier startPos curPosNPeriod inverse g0speed axisMinValue axisMaxValue";
                axisSet[i++] = ai++ + "";
                axisSet[i++] = axisSettings.CountStepsPerMm + "";
                axisSet[i++] = axisSettings.Speed + "";
                axisSet[i++] = axisSettings.CurPositionCheckPeriod + "";
                axisSet[i++] = axisSettings.EnableEstopOnError ? "1" : "0";
                axisSet[i++] = axisSettings.MoveResponseTimeout + "";
                axisSet[i++] = axisSettings.Mach3OemDro + "";
                axisSet[i++] = axisSettings.Mach3OemDroMultiplier + "";
                axisSet[i++] = axisSettings.StartPos + "";
                axisSet[i++] = axisSettings.CurPositionNotifyPeriod + "";
                axisSet[i++] = axisSettings.InverseMove ? "1" : "0";
                axisSet[i++] = axisSettings.G0Speed + "";
                axisSet[i++] = axisSettings.AxisMinValue + "";
                axisSet[i++] = axisSettings.AxisMaxValue + "";
            }

            return axisSet;
        }

        private void FormatVBSArrayByHeader(string[] vbsArray, string[] header, StringBuilder sb)
        {
            if (vbsArray.Length == header.Length)
            {
                for (int i = 0; i < header.Length - 1; i++)
                {
                    sb.Append(vbsArray[i]);
                    sb.Append(",");
                    sb.Append(new string(' ', header[i].Length - vbsArray[i].Length));
                }

                sb.Append(vbsArray[header.Length - 1]);
            }
            else if (vbsArray.Length > header.Length && (((double)(vbsArray.Length) / (double)(header.Length)) == vbsArray.Length / header.Length))
            {
                for (int i1 = 0; i1 < vbsArray.Length;)
                {
                    for (int i2 = 0; i2 < header.Length; i1++, i2++)
                    {
                        sb.Append(vbsArray[i1]);
                        sb.Append(",");
                        sb.Append(new string(' ', header[i2].Length - vbsArray[i1].Length));
                    }
                }

                int trimEndLength = header[header.Length - 1].Length - vbsArray[vbsArray.Length - 1].Length + 1;
                sb.Remove(sb.Length - trimEndLength, trimEndLength);
            }
        }


        private void SaveMacrosInFolder(string macrosFolder)
        {
            LoadXml();
            
            string macrosInitAxis = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "MacrosInitTemplate.txt"));
            string macrosCloseSerialPort = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "MacrosCloseSerialPortTemplate.txt"));
            string macrosMoveAxis = File.ReadAllText(Path.Combine(Environment.CurrentDirectory, "MacrosMoveTemplate.txt"));

            //format macros variables stored in array with header for more easy edit them
            string varsHeader = "axis stepsPerMm speed curPosCPeriod estop moveTimeout droNumber droMultiplier startPos curPosNPeriod inverse g0speed axisMinValue axisMaxValue";
            string[] varsHeaderColumns = varsHeader.Split(new string[] { " " }, StringSplitOptions.RemoveEmptyEntries);
            StringBuilder setParameters = new StringBuilder();
            int countAxis = settings.Axis.Keys.Count;
            string array = "axisSet = Array(";
            string[] indexHeader = new string[countAxis];
            string[] varsHeaderLine = new string[countAxis];
            string[] axisSet = GetAxisSet();

            for (int i = 0; i < countAxis; i++)
            {
                indexHeader[i] = settings.Axis.Keys[i] + "";
                varsHeaderLine[i] = varsHeader;
            }

            setParameters.Append(new string(' ', array.Length - 1));
            setParameters.Append("'");
            FormatVBSArrayByHeader(indexHeader, varsHeaderLine, setParameters);
            setParameters.AppendLine();
            setParameters.Append(new string(' ', array.Length - 1));
            setParameters.Append("'");
            for (int i = 0; i < countAxis; i++)
            {
                setParameters.Append(varsHeader);
                setParameters.Append(" ");
            }
            setParameters.AppendLine();
            setParameters.Append(array);
            FormatVBSArrayByHeader(axisSet, varsHeaderColumns, setParameters);
            setParameters.Append(")");
            macrosInitAxis = string.Format(macrosInitAxis, setParameters.ToString(), countAxis, GCodeProcessor.Mach3AxisInitMacrosVariablesCount, GCodeProcessor.Mach3AxisMoveMacrosVariablesCount, settings.PortName, settings.BaudRate, GCodeProcessor.Mach3AxisCurPosStartIndex, GCodeProcessor.Mach3CommonVariablesStartIndex, GCodeProcessor.Mach3AxisVariablesStartIndex);
            macrosMoveAxis = string.Format(macrosMoveAxis, countAxis, GCodeProcessor.Mach3AxisMoveMacrosVariablesCount, GCodeProcessor.Mach3AxisCurPosStartIndex, GCodeProcessor.Mach3CommonVariablesStartIndex, GCodeProcessor.Mach3AxisVariablesStartIndex);
            macrosCloseSerialPort = string.Format(macrosCloseSerialPort, GCodeProcessor.Mach3CommonVariablesStartIndex);

            File.WriteAllText(Path.Combine(macrosFolder, settings.InitMacrosName + ".m1s"), macrosInitAxis);
            File.WriteAllText(Path.Combine(macrosFolder, settings.MoveMacrosName + ".m1s"), macrosMoveAxis);
            File.WriteAllText(Path.Combine(macrosFolder, settings.CloseSerialPortMacrosName + ".m1s"), macrosCloseSerialPort);

            MessageBox.Show("Macroses was saved.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void button2_Click(object sender, EventArgs e)
        {
            string gCodeFileName = null;
            string convertedGCodeFileName = null;

            if (ValidateSettings(true))
            {
                try
                {
                    if (MessageBox.Show("Choose Original GCode file. Usually it has '.tap' extension", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information) != DialogResult.OK)
                        return;

                    DialogResult dialogResult = openFileDialog1.ShowDialog();
                    if (dialogResult == DialogResult.OK)
                    {
                        gCodeFileName = openFileDialog1.FileName;
                    }
                    else
                        return;
                }
                catch
                {
                    MessageBox.Show("Open file error", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                MessageBox.Show("Original GCode file was loaded. Choose location where to save converted file.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);

                try
                {
                    if (string.IsNullOrEmpty(saveFileDialog1.InitialDirectory))
                        saveFileDialog1.InitialDirectory = openFileDialog1.InitialDirectory;
                    saveFileDialog1.FileName = Path.Combine(saveFileDialog1.InitialDirectory, Path.GetFileName(openFileDialog1.FileName));
                    if (!Path.HasExtension(saveFileDialog1.FileName))
                    {
                        saveFileDialog1.FileName += ".tap";
                    }

                    DialogResult dialogResult = saveFileDialog1.ShowDialog();
                    if (dialogResult == DialogResult.OK)
                    {
                        if (!Path.HasExtension(saveFileDialog1.FileName))
                        {
                            saveFileDialog1.FileName += ".tap";
                        }
                        convertedGCodeFileName = saveFileDialog1.FileName;
                    }
                    else
                        return;

                    if (MessageBox.Show("Press OK for begin converting file.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information) == DialogResult.OK)
                    {
                        GCodeProcessor gcodeProcessor = new GCodeProcessor(settings, allAxis);
                        string errMes = "";
                        gcodeProcessor.ConvertGCodeFile(gCodeFileName, convertedGCodeFileName, 4 * 4096, ref errMes);
                        if (errMes == "")
                            MessageBox.Show("GCode file was converted.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        else
                        {
                            MessageBox.Show(errMes, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        }
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Convert GCode file error: {Log.GetExceptionErrorDescription(ex)}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void button1_Click(object sender, EventArgs e)
        {
            if (ValidateSettings(true))
            {
                if (MessageBox.Show("Choose folder where to save macros files.", "Info", MessageBoxButtons.OK, MessageBoxIcon.Information) != DialogResult.OK)
                    return;

                DialogResult dialogResult = folderBrowserDialog1.ShowDialog();
                if (dialogResult == DialogResult.OK)
                {

                    string macrosFolder = folderBrowserDialog1.SelectedPath;
                    try
                    {
                        SaveMacrosInFolder(macrosFolder);
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Save macros error: " + Log.GetExceptionErrorDescription(ex), "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }
                }
                else
                    return;
            }

        }

        private void GCodeConverterForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (!hasLastLoadXmlError)
            {
                formIsClosing = true;
                LoadXml();
                if (!hasLastLoadXmlError)
                {
                    settings.OpenGCodeFileInitialDirectory = openFileDialog1.InitialDirectory;
                    settings.SaveGCodeFileInitialDirectory = saveFileDialog1.InitialDirectory;
                    settings.SaveMacrosInitialDirectory = folderBrowserDialog1.SelectedPath;
                    settings.BeforeSerialize();
                    settings.SaveXml(Path.Combine(Environment.CurrentDirectory, "settings.xml"));
                }
            }
        }

        private bool ValidateSettings(bool showError)
        {
            try
            {
                List<string> errors = new List<string>();
                LoadXml();

                if (!hasLastLoadXmlError)
                {
                    if (settings.Axis.Keys.Count == 0 || settings.Axis.Values.All(a => a.Disabled))
                    {
                        errors.Add("No axis selected.");
                    }

                    foreach (char axisName in settings.Axis.Keys)
                    {
                        AxisSettings axis = settings.Axis[axisName];
                        if ((axisName < 'A' || axisName > 'Z') && (axisName < 'a' || axisName > 'z'))
                            errors.Add($"Axis name '{axisName}' must be 'A'-'Z'");
                        if (axis.MinDistanceToMove < 0)
                        {
                            errors.Add("Minimal distance must be great or equal to zero." + Environment.NewLine);
                        }
                    }

                    if (showError && errors.Count > 0)
                    {
                        MessageBox.Show(string.Join(Environment.NewLine, errors), "XML Error", MessageBoxButtons.OK, MessageBoxIcon.Error);

                    }

                    return errors.Count == 0;
                }
                else
                {
                    return false;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Exception occured: {Log.GetExceptionErrorDescription(ex)}", "Exception", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return true;
            }
        }

        private void buttonValidateSettings_Click(object sender, EventArgs e)
        {
            if (ValidateSettings(true))
                MessageBox.Show($"Xml has no errors", "No errors", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
