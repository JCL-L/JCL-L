using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace NinOne.Domain.Devices
{
    public static class CanTextCodec
    {
        public static byte[] ParseData(string text)
        {
            var source = Regex.Replace((text ?? string.Empty).Trim(), "0[xX]", string.Empty);
            if (source.Length == 0) return new byte[0];
            var hasSeparator = source.Any(character => char.IsWhiteSpace(character) || ",;:_-".IndexOf(character) >= 0);
            if (hasSeparator)
            {
                var tokens = Regex.Split(source, @"[\s,;:_-]+").Where(token => token.Length != 0).ToArray();
                var result = new byte[tokens.Length];
                for (var index = 0; index < tokens.Length; index++)
                {
                    byte value;
                    if (tokens[index].Length > 2 || !byte.TryParse(tokens[index], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                        throw new FormatException("CAN 数据必须是十六进制字节，例如 01020304 或 01 02 03 04。");
                    result[index] = value;
                }
                return result;
            }
            if ((source.Length & 1) != 0) throw new FormatException("连续十六进制 CAN 数据必须是偶数个字符，例如 01020304。");
            var bytes = new byte[source.Length / 2];
            for (var index = 0; index < bytes.Length; index++)
            {
                byte value;
                if (!byte.TryParse(source.Substring(index * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value))
                    throw new FormatException("CAN 数据必须是十六进制字节，例如 01020304 或 01 02 03 04。");
                bytes[index] = value;
            }
            return bytes;
        }
    }
}
