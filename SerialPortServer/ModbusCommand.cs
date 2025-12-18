using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ModbusServer
{
    public enum ModbusRegisterType { Holding, Input, Discrete, Coils };
    public enum ModbusFunction { ReadValue, SetValue, SetValues };
    public class ModbusCommandBuilder
    {
        /// <summary>
        /// Build modbus command 8 bytes.
        /// </summary>
        /// <param name="slaveAddress">Slave Address.</param>
        /// <param name="function">Function read/write.</param>
        /// <param name="registerType">Register type</param>
        /// <param name="data">For read operation this contains reads count, for write operation this contains 2 byte data.</param>
        /// <returns>Modbus command 8 bytes including crc16.</returns>
        public byte[] Build16bytesModbusCommand(byte slaveAddress, ModbusRegisterType registerType, ModbusFunction function, UInt16 registerAddress, UInt16 data)
        {
            byte[] command = new byte[8];
            command[0] = slaveAddress;
            command[1] = GetModbusFuncCode(registerType, function);
            command[2] = (byte)(registerAddress >> 8);
            command[3] = (byte)(registerAddress);
            command[4] = (byte)(data >> 8);
            command[5] = (byte)(data);
            byte[] crc16 = Utils.MakeCRC16(command, 6);
            command[6] = crc16[0];
            command[7] = crc16[1];

            return command;
        }

        private byte GetModbusFuncCode(ModbusRegisterType registerType, ModbusFunction function)
        {
            switch(registerType)
            {
                case ModbusRegisterType.Input:
                    return 4;
                    break;
                case ModbusRegisterType.Discrete:
                    return 2;
                    break;
                case ModbusRegisterType.Holding:
                    return (byte)(function == ModbusFunction.ReadValue ? 3 : function == ModbusFunction.SetValue ? 6 : 16);
                    break;
                case ModbusRegisterType.Coils:
                    return (byte)(function == ModbusFunction.ReadValue ? 1 : function == ModbusFunction.SetValue ? 5 : 15);
                    break;
                default:
                    throw new NotImplementedException();
            }
        }
    }
}
