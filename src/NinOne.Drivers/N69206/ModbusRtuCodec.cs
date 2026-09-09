using System;
using System.Globalization;
using System.IO;
using System.Linq;

namespace NinOne.Drivers.N69206
{
    internal static class ModbusRtuCodec
    {
        public static byte[] BuildReadRequest(byte deviceId, ushort startRegister, ushort registerCount)
        {
            if (deviceId < 1 || deviceId > 248) throw new ArgumentOutOfRangeException(nameof(deviceId));
            if (registerCount == 0 || (registerCount & 1) != 0) throw new ArgumentOutOfRangeException(nameof(registerCount), "N69206 读取寄存器数量必须为非零偶数。");
            return AppendCrc(new[]
            {
                deviceId, (byte)0x03,
                (byte)(startRegister >> 8), (byte)startRegister,
                (byte)(registerCount >> 8), (byte)registerCount
            });
        }

        public static byte[] BuildWriteUInt32Request(byte deviceId, ushort startRegister, uint value)
        {
            return BuildWriteRequest(deviceId, startRegister, EncodeUInt32(value));
        }

        public static byte[] BuildWriteFloatRequest(byte deviceId, ushort startRegister, decimal value)
        {
            var single = (float)value;
            if (float.IsNaN(single) || float.IsInfinity(single)) throw new ArgumentOutOfRangeException(nameof(value));
            var bits = BitConverter.ToUInt32(BitConverter.GetBytes(single), 0);
            return BuildWriteUInt32Request(deviceId, startRegister, bits);
        }

        public static byte[] ParseReadResponse(byte[] response, byte deviceId, ushort registerCount)
        {
            ValidateCommon(response, deviceId, 0x03);
            var expectedBytes = checked((byte)(registerCount * 2));
            if (response.Length != expectedBytes + 5 || response[2] != expectedBytes) throw new InvalidDataException("N69206 Modbus 读响应长度不一致。");
            var data = new byte[expectedBytes];
            Array.Copy(response, 3, data, 0, data.Length);
            return data;
        }

        public static void ValidateWriteResponse(byte[] response, byte[] request, byte deviceId, ushort startRegister)
        {
            ValidateCommon(response, deviceId, 0x10);
            if (response.Length != 8) throw new InvalidDataException("N69206 Modbus 写响应长度不正确。");
            if (response[2] != (byte)(startRegister >> 8) || response[3] != (byte)startRegister || response[4] != 0 || response[5] != 2)
            {
                throw new InvalidDataException("N69206 Modbus 写响应未回显请求的寄存器和数量。");
            }
            if (request == null || request.Length < 6 || !response.Take(6).SequenceEqual(request.Take(6))) throw new InvalidDataException("N69206 Modbus 写响应与请求不一致。");
        }

        public static uint DecodeUInt32(byte[] data, int offset)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            if (offset < 0 || offset + 4 > data.Length) throw new ArgumentOutOfRangeException(nameof(offset));
            var lowWord = (uint)((data[offset] << 8) | data[offset + 1]);
            var highWord = (uint)((data[offset + 2] << 8) | data[offset + 3]);
            return (highWord << 16) | lowWord;
        }

        public static decimal DecodeFloat(byte[] data, int offset)
        {
            var bits = DecodeUInt32(data, offset);
            var value = BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException("N69206 返回了无效浮点数。");
            return (decimal)value;
        }

        public static string ToHex(byte[] bytes)
        {
            return string.Join(" ", (bytes ?? new byte[0]).Select(value => value.ToString("X2", CultureInfo.InvariantCulture)));
        }

        private static byte[] BuildWriteRequest(byte deviceId, ushort startRegister, byte[] value)
        {
            if (deviceId < 1 || deviceId > 248) throw new ArgumentOutOfRangeException(nameof(deviceId));
            if ((startRegister & 1) != 0) throw new ArgumentOutOfRangeException(nameof(startRegister), "N69206 可读写寄存器起始地址必须为偶数。");
            if (value == null || value.Length != 4) throw new ArgumentException("N69206 32 位寄存器值必须为 4 字节。", nameof(value));
            var payload = new[]
            {
                deviceId, (byte)0x10,
                (byte)(startRegister >> 8), (byte)startRegister,
                (byte)0, (byte)2, (byte)4,
                value[0], value[1], value[2], value[3]
            };
            return AppendCrc(payload);
        }

        private static byte[] EncodeUInt32(uint value)
        {
            return new[]
            {
                (byte)(value >> 8), (byte)value,
                (byte)(value >> 24), (byte)(value >> 16)
            };
        }

        private static void ValidateCommon(byte[] response, byte deviceId, byte function)
        {
            if (response == null || response.Length < 5) throw new InvalidDataException("N69206 Modbus 响应过短。");
            ValidateCrc(response);
            if (response[0] != deviceId) throw new InvalidDataException("N69206 Modbus 响应 ID 不一致。");
            if (response[1] == (function | 0x80)) throw new InvalidOperationException("N69206 Modbus 异常响应，功能码=0x" + function.ToString("X2", CultureInfo.InvariantCulture) + "，异常码=0x" + response[2].ToString("X2", CultureInfo.InvariantCulture) + "。");
            if (response[1] != function) throw new InvalidDataException("N69206 Modbus 响应功能码不一致。");
        }

        internal static byte[] AppendCrc(byte[] payload)
        {
            if (payload == null) throw new ArgumentNullException(nameof(payload));
            var crc = ComputeCrc(payload, 0, payload.Length);
            var result = new byte[payload.Length + 2];
            Array.Copy(payload, result, payload.Length);
            result[result.Length - 2] = (byte)crc;
            result[result.Length - 1] = (byte)(crc >> 8);
            return result;
        }

        private static void ValidateCrc(byte[] frame)
        {
            var expected = ComputeCrc(frame, 0, frame.Length - 2);
            var actual = (ushort)(frame[frame.Length - 2] | (frame[frame.Length - 1] << 8));
            if (actual != expected) throw new InvalidDataException("N69206 Modbus CRC 校验失败。");
        }

        private static ushort ComputeCrc(byte[] bytes, int offset, int count)
        {
            ushort crc = 0xFFFF;
            for (var index = offset; index < offset + count; index++)
            {
                crc ^= bytes[index];
                for (var bit = 0; bit < 8; bit++) crc = (ushort)(((crc & 1) != 0) ? (crc >> 1) ^ 0xA001 : crc >> 1);
            }
            return crc;
        }
    }
}
