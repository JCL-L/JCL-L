using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using NinOne.Domain.Devices;

namespace NinOne.Drivers.ZlgCan
{
    internal sealed class CanCsvProfile
    {
        private CanCsvProfile(IReadOnlyList<CanFrameTemplate> templates, IReadOnlyList<CanSignalDefinition> signals)
        {
            Templates = templates;
            Signals = signals;
        }

        public IReadOnlyList<CanFrameTemplate> Templates { get; }
        public IReadOnlyList<CanSignalDefinition> Signals { get; }

        public static CanCsvProfile Load(string templatePath, string signalPath)
        {
            return new CanCsvProfile(LoadTemplates(templatePath), LoadSignals(signalPath));
        }

        private static IReadOnlyList<CanFrameTemplate> LoadTemplates(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("找不到 CAN 模板文件。", path);
            var result = new List<CanFrameTemplate>();
            foreach (var row in ReadRows(path))
            {
                if (row.Length < 7) throw new InvalidDataException("CAN 模板列数不足：" + string.Join(",", row));
                var dlc = ParseInt(row[4], "DLC", 0, 8);
                var bytes = row[5].Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(value => byte.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)).ToArray();
                if (bytes.Length != dlc) throw new InvalidDataException("CAN 模板 DLC 与数据长度不一致：" + string.Join(",", row));
                result.Add(new CanFrameTemplate
                {
                    Group = ParseInt(row[0], "组", 0, int.MaxValue),
                    Frame = new CanFrame
                    {
                        Id = uint.Parse(row[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        IsExtended = ParseInt(row[2], "扩展帧", 0, 1) != 0,
                        IsRemote = ParseInt(row[3], "远程帧", 0, 1) != 0,
                        Data = bytes
                    },
                    PeriodMilliseconds = ParseInt(row[6], "周期", 0, 600000)
                });
            }
            if (result.Count == 0) throw new InvalidDataException("CAN 模板文件为空：" + path);
            return result;
        }

        private static IReadOnlyList<CanSignalDefinition> LoadSignals(string path)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("找不到 CAN 信号文件。", path);
            var result = new List<CanSignalDefinition>();
            foreach (var row in ReadRows(path))
            {
                if (row.Length < 11 || string.IsNullOrWhiteSpace(row[5]) || string.IsNullOrWhiteSpace(row[7]) || string.IsNullOrWhiteSpace(row[8])) continue;
                try
                {
                    var factor = ParseFactor(row[9]);
                    var offset = ParseOffset(row[10]);
                    result.Add(new CanSignalDefinition
                    {
                        FrameId = uint.Parse(row[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture),
                        Name = row[5].Trim(),
                        StartBit = ParseInt(row[7], "起始位", 0, 63),
                        BitLength = ParseInt(row[8], "位长度", 1, 64),
                        Factor = factor,
                        Offset = offset
                    });
                }
                catch (Exception exception) when (exception is FormatException || exception is OverflowException || exception is InvalidDataException)
                {
                    var indexed = string.Join(" | ", row.Select((value, index) => index + "=[" + value + "]"));
                    throw new InvalidDataException("CAN 信号定义无效：" + indexed, exception);
                }
            }
            return result;
        }

        private static IEnumerable<string[]> ReadRows(string path)
        {
            var text = DecodeText(File.ReadAllBytes(path));
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    yield return line.Split(',').Select(value => value.Trim()).ToArray();
                }
            }
        }

        private static string DecodeText(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            try
            {
                return new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return Encoding.GetEncoding(936).GetString(bytes);
            }
        }

        private static int ParseInt(string value, string name, int minimum, int maximum)
        {
            int result;
            if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result) || result < minimum || result > maximum) throw new InvalidDataException(name + " 无效：" + value);
            return result;
        }

        private static decimal ParseFactor(string value)
        {
            decimal result;
            if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) || result == 0) throw new InvalidDataException("系数无效：" + value);
            return result;
        }

        private static decimal ParseOffset(string value)
        {
            decimal result;
            if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)) throw new InvalidDataException("偏置无效：" + value);
            return result;
        }
    }

    internal sealed class CanSignalDefinition
    {
        public uint FrameId { get; set; }
        public string Name { get; set; }
        public int StartBit { get; set; }
        public int BitLength { get; set; }
        public decimal Factor { get; set; }
        public decimal Offset { get; set; }
    }
}
