using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModbusServer
{
    /// <summary>
    /// Provide logging info/error events into files.
    /// </summary>
    public class Log
    {
        static readonly FileStream _errorsLog;
        static readonly FileStream _infoLog;

        public static bool AutoFlush { get; set; }
        public static bool Enabled { get; set; }
        

        static Log()
        {
            Settings settings = Settings.LoadXml(null);
            Enabled = settings.EnableLog;
            if (Enabled)
            {
                string logFolderPath = settings.LogFolderPath;
                if (!string.IsNullOrEmpty(settings.LogFolderPath))
                {
                    if (!Directory.Exists(settings.LogFolderPath))
                    {
                        throw new DirectoryNotFoundException(settings.LogFolderPath);
                    }
                }
                else
                {
                    logFolderPath = Environment.CurrentDirectory;
                    if (!Directory.Exists(logFolderPath))
                    {
                        string logFolderPath2 = Path.Combine(Environment.CurrentDirectory, "GCConverter");
                        if (!Directory.Exists(logFolderPath2))
                        {
                            throw new DirectoryNotFoundException($@"Log files must be located in folder Mach3 or in Mach3 subfolder GCConverter\Logs, allowed file path: {string.Join(Environment.NewLine, logFolderPath, logFolderPath2)}");
                        }
                        logFolderPath = logFolderPath2;
                    }
                }

                _errorsLog = File.Open(Path.Combine(logFolderPath, "GCCErrors.txt"), FileMode.Append, FileAccess.Write, FileShare.Write);
                _infoLog = File.Open(Path.Combine(logFolderPath, "GCCInfo.txt"), FileMode.Append, FileAccess.Write, FileShare.Write);
                AutoFlush = true;
            }
        }

        public static void LogException(Exception ex)
        {
            LogError(GetExceptionErrorDescription(ex));
        }

        public static void LogError(string error)
        {
            if (Enabled)
            {
                byte[] message = Encoding.UTF8.GetBytes(DateTime.Now.ToString("dd/MM/yyyy hh:mm ss ms  ") + error + Environment.NewLine);
                _errorsLog.Write(message, 0, message.Length);
                if (AutoFlush)
                    _errorsLog.FlushAsync();
            }
        }
        public static void LogInfo(string info)
        {
            if (Enabled)
            {
                byte[] message = Encoding.UTF8.GetBytes(DateTime.Now.ToString("dd/MM/yyyy hh:mm ss ms  ") + info + Environment.NewLine);
                _infoLog.Write(message, 0, message.Length);
                if (AutoFlush)
                    _infoLog.FlushAsync();
            }
        }

        public static void LogInfo(byte[] text, int startPos, int length, string infoFormat)
        {
            if (Enabled)
            {
                byte[] message = Encoding.UTF8.GetBytes(DateTime.Now.ToString("dd/MM/yyyy hh:mm ss ms  ") +
                string.Format(infoFormat, Encoding.ASCII.GetString(text, startPos, length)) + Environment.NewLine);
                _infoLog.Write(message, 0, message.Length);
                if (AutoFlush)
                    _infoLog.FlushAsync();
            }
        }

        public static string GetExceptionErrorDescription(Exception ex)
        {
            string error = "Error: " + ex.Message;
            Exception innerEx = ex.InnerException;
            while (innerEx != null)
            {
                error += ", Inner Exception message: ";
                innerEx = innerEx.InnerException;
            }

            return error;
        }
    }
}
