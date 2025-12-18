using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SerialPortAxControl;
using ModbusServer;
using System.IO;

namespace TestAx
{
    class Program
    {

        private static void TestModBusPWM8A04Generator()
        {
            SerialPortAxControl.SerialPortAxControl serialPortAx = new SerialPortAxControl.SerialPortAxControl();
            try
            {
                Settings settings = Settings.LoadXml(null);
                serialPortAx.CreateSerialPort("COM3", 9600, "8-1-N", 100, 100, 255, 512, "1");
                serialPortAx.OpenSerialPort();
                //write/read frequency
                serialPortAx.SendModbusCommandAndWaitResponse(1, ModbusRegisterType.Holding.ToString(), ModbusFunction.SetValue.ToString(), 0, 20000, true);
                var responseBytes = serialPortAx.SendModbusCommandAndWaitResponse(1, "Holding", "ReadValue", 0, 1, true);

                //write/read cycle/duty
                serialPortAx.SendModbusCommandAndWaitResponse(1, "Holding", "SetValue", 112, 10, true);
                responseBytes = serialPortAx.SendModbusCommandAndWaitResponse(1, "Holding", "ReadValue", 112, 1, true);

                serialPortAx.SendModbusCommandAndWaitResponse(1, "Holding", "SetValue", 112, 50, true);
                responseBytes = serialPortAx.SendModbusCommandAndWaitResponse(1, "Holding", "ReadValue", 112, 1, true);

                //write frequency without wait response
                serialPortAx.SendModbusCommandAndWaitResponse(1, "Holding", "SetValue", 0, 5010, false);
                serialPortAx.SendModbusCommandAndWaitResponse(1, "Holding", "SetValue", 0, 5020, false);

                Task.Run(() => { serialPortAx.SendModbusCommandAndWaitResponse(1, "Holding", "SetValue", 0, 5030, false); });
                Task.Run(() => { serialPortAx.SendModbusCommandAndWaitResponse(1, "Holding", "SetValue", 0, 5040, false); });
                Task.Run(() => { serialPortAx.SendModbusCommandAndWaitResponse(1, "Holding", "SetValue", 0, 5050, false); });
                Task.Run(() => { serialPortAx.SendModbusCommandAndWaitResponse(1, "Holding", "SetValue", 0, 5060, false); });
                Task.Run(() => { serialPortAx.SendModbusCommandAndWaitResponse(1, "Holding", "SetValue", 0, 5070, false); });
                Task.Run(() => { serialPortAx.SendModbusCommandAndWaitResponse(1, "Holding", "SetValue", 0, 5080, false); });
                Task.Run(() => { serialPortAx.SendModbusCommandAndWaitResponse(1, "Holding", "SetValue", 0, 5090, false); });
                Task.Run(() => { serialPortAx.SendModbusCommandAndWaitResponse(1, "Holding", "SetValue", 0, 5100, false); });
                Task.Run(() => { serialPortAx.SendModbusCommandAndWaitResponse(1, "Holding", "SetValue", 0, 5110, false); });
                Task.Run(() => { serialPortAx.SendModbusCommandAndWaitResponse(1, "Holding", "SetValue", 0, 5120, false); });

                Thread.Sleep(4000);
            }
            catch (Exception ex)
            {
                Log.LogException(ex);
                throw ex;
            }
            finally
            {
                serialPortAx.Dispose("1", true);
            }
        }

        private static void TestCommandsQueue(float commandMaxMoveDistance, int countCommands)
        {
            SerialPortAxControl.SerialPortAxControl serialPortAx = new SerialPortAxControl.SerialPortAxControl();
            try
            {
                Settings settings = Settings.LoadXml(null);
                AxisSettings axisSettings = settings.Axis['Z'];
                int zIndex = settings.Axis.Keys.IndexOf('Z');
                axisSettings.CurPositionNotifyPeriod = 0;//disable notifications, because each command will be executed in queue
                serialPortAx.CreateSerialPort(settings.PortName, settings.BaudRate, "8-1-N", 120000, 120000, 255, 512, "1");
                serialPortAx.OpenSerialPort();

                string response = serialPortAx.CreateAxisSettings(zIndex, axisSettings.CountStepsPerMm, axisSettings.InverseMove, axisSettings.Speed, axisSettings.StartPos, axisSettings.AxisMinValue, axisSettings.AxisMaxValue, axisSettings.CurPositionNotifyPeriod);
                if (response == "OK")
                {
                    Random rnd = new Random((int)DateTime.Now.Ticks);
                    float curPos = 0;
                    int dir = 1;
                    serialPortAx.SetSendCommandsToQueue(true);//all next commands will be put in queue
                    for (int i = 0; i < countCommands; i++)
                    {
                        float distance = commandMaxMoveDistance * ((float)rnd.Next(25, 100) / 100F);
                        if ((dir == 1 && (curPos + dir * distance) > axisSettings.AxisMaxValue) ||
                            (dir == -1 && (curPos + dir * distance) < axisSettings.AxisMinValue))
                            dir = -dir;
                        curPos += dir * distance;
                        serialPortAx.MoveAxis(zIndex, curPos, axisSettings.Speed, false, 0);//put commands sequence in queue without wait, they will be executed in the same order. axis must be moved in up-down loop
                    }
                }
            }
            catch (Exception ex)
            {
                Log.LogException(ex);
                throw ex;
            }
            finally
            {
                serialPortAx.WaitEndCommandQueue();//wait until all commands executed
                serialPortAx.Dispose("1", true);
            }
        }

        private static void TestZAxisMach3()
        {
            SerialPortAxControl.SerialPortAxControl serialPortAx = new SerialPortAxControl.SerialPortAxControl();
            try
            {
                Settings settings = Settings.LoadXml(null);
                AxisSettings axisSettings = settings.Axis['Z'];
                int zIndex = settings.Axis.Keys.IndexOf('Z');
                serialPortAx.CreateSerialPort(settings.PortName, settings.BaudRate, "8-1-N", 120000, 120000, 255, 512, "1");
                serialPortAx.OpenSerialPort();

                axisSettings.CurPositionNotifyPeriod = 0;
                string response = serialPortAx.CreateAxisSettings(zIndex, axisSettings.CountStepsPerMm, axisSettings.InverseMove, axisSettings.Speed, axisSettings.StartPos, axisSettings.AxisMinValue, axisSettings.AxisMaxValue, axisSettings.CurPositionNotifyPeriod);
                if (response == "OK")
                {
                    //move z axis down to 10mm, wait until move end
                    string response1 = serialPortAx.MoveAxis(zIndex, -10, 150, true, 0);
                    //create axis, reset current position to 0, enable cur position notification each 0.5 seconds
                    axisSettings.CurPositionNotifyPeriod = 500;
                    response = serialPortAx.CreateAxisSettings(zIndex, axisSettings.CountStepsPerMm, axisSettings.InverseMove, axisSettings.Speed, axisSettings.StartPos, axisSettings.AxisMinValue, axisSettings.AxisMaxValue, axisSettings.CurPositionNotifyPeriod);
                    float curPos2 = serialPortAx.GetCurPosition(zIndex);//collect last position after move ended


                    //move z axis up to 10mm, with slow speed for catch getposition events
                    response1 = serialPortAx.MoveAxis(zIndex, 10, 25, false, 30000);
                    DateTime dt = DateTime.Now;

                    //contains list of moved positions
                    List<string> progressEvents = new List<string>();
                    float curPos, curMovePos = 0;
                    while ((DateTime.Now - dt).TotalMilliseconds < 40000 && serialPortAx.ContinueReceiveResponse)
                    {
                        Thread.Sleep((int)axisSettings.CurPositionCheckPeriod);
                        curMovePos = serialPortAx.GetCurMovingAxisPos(zIndex);//collect get position event each 0.5 seconds
                        if (curMovePos == SerialPortServer.Code_MoveEnded)
                            break;
                        progressEvents.Add(curMovePos.ToString("N4"));
                    }

                    curPos = serialPortAx.GetCurPosition(zIndex);//collect last position after move ended
                    progressEvents.Add(curPos.ToString("N4"));
                    string response2 = response1;
                }
            }
            catch(Exception ex)
            {
                Log.LogException(ex);
                throw ex;
            }
            finally
            {
                serialPortAx.Dispose("1", true);
            }
        }

        static void Main(string[] args)
        {
            TestZAxisMach3();
            TestCommandsQueue(4, 20);
            //all results in log file GCCInfo.txt
        }
    }
}
