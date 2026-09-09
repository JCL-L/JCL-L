using System;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using NinOne.Domain.Configuration;

namespace NinOne.Infrastructure.Configuration
{
    public sealed class SystemConfigLoader
    {
        public SystemConfig Load(string systemIniPath, string productIniPath)
        {
            var system = LabViewIniReader.Load(systemIniPath);
            var product = LabViewIniReader.Load(productIniPath);
            var result = new SystemConfig { SourcePath = systemIniPath + " + " + productIniPath };

            result.Plc.CpuType = system.Required("系统设置1", "PLC.S7.CPU类型", "Auto/S7200/S7200Smart");
            if (!string.Equals(result.Plc.CpuType, "Auto", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(result.Plc.CpuType, "S7200", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(result.Plc.CpuType, "S7200Smart", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("PLC.S7.CPU类型只允许 Auto、S7200 或 S7200Smart。");
            }
            result.Plc.Rack = system.RequiredInt("系统设置1", "PLC.S7.机架", 0, 15);
            result.Plc.Slot = system.RequiredInt("系统设置1", "PLC.S7.槽位", 0, 15);
            result.Plc.LocalTsap = system.Required("系统设置1", "PLC.S7.本地TSAP", "AUTO 或两字节十六进制 TSAP");
            result.Plc.RemoteTsap = system.Required("系统设置1", "PLC.S7.远端TSAP", "AUTO 或两字节十六进制 TSAP");
            ValidateTsapPair(result.Plc.LocalTsap, result.Plc.RemoteTsap);
            result.Plc.TimeoutMilliseconds = system.RequiredInt("系统设置1", "PLC.S7.超时(ms)", 250, 120000);

            result.ElectronicLoad.Model = system.Required("系统设置1", "仪器通讯.仪器类型.低压电子负载类型", "型号");
            result.ElectronicLoad.TimeoutMilliseconds = system.RequiredInt("系统设置1", "仪器通讯.超时(ms).低压电子负载", 100, 120000);

            var productDirectory = Path.GetDirectoryName(Path.GetFullPath(productIniPath));
            result.Can.TemplateCsvPath = Path.Combine(productDirectory, "send1.csv");
            result.Can.SignalCsvPath = Path.Combine(productDirectory, "send2.csv");
            result.Can.ReceiveCsvPath = Path.Combine(productDirectory, "receive.csv");
            if (!File.Exists(result.Can.TemplateCsvPath)) throw new FileNotFoundException("找不到 CAN 帧模板 send1.csv。", result.Can.TemplateCsvPath);
            if (!File.Exists(result.Can.SignalCsvPath)) throw new FileNotFoundException("找不到 CAN 信号定义 send2.csv。", result.Can.SignalCsvPath);
            if (!File.Exists(result.Can.ReceiveCsvPath)) throw new FileNotFoundException("找不到 CAN 接收定义 receive.csv。", result.Can.ReceiveCsvPath);
            result.Oscilloscope.Model = system.Required("系统设置1", "仪器通讯.仪器类型.示波器类型", "型号");
            result.Oscilloscope.TimeoutMilliseconds = product.RequiredInt("基本参数设置", "ZDS设置.读取超时(ms)", 100, 120000);
            result.Oscilloscope.HorizontalOffset = product.Required("基本参数设置", "ZDS设置.水平偏置", "数值");
            result.Oscilloscope.TimeBase = product.Required("基本参数设置", "ZDS设置.时基", "时基字符串");
            result.Oscilloscope.TimeBaseMode = product.Required("基本参数设置", "ZDS设置.时基模式", "模式");
            result.Oscilloscope.AverageCount = product.RequiredInt("基本参数设置", "ZDS设置.平均次数", 1, 65536);
            result.Oscilloscope.AcquisitionMode = product.Required("基本参数设置", "ZDS设置.采集模式", "模式");
            result.Oscilloscope.SampleDepth = product.RequiredInt("基本参数设置", "ZDS设置.采样深度", 1, 100000000);
            result.Oscilloscope.RollingMode = product.RequiredBoolean("基本参数设置", "ZDS设置.滚动模式");
            if (result.Oscilloscope.SampleDepth != 14000) throw new InvalidDataException("当前手动模式只允许示波器采样深度 14000。配置文件：" + productIniPath);

            for (var channel = 1; channel <= 4; channel++) result.Oscilloscope.Channels.Add(ReadChannel(product, channel));
            return result;
        }

        public static string NormalizeSerialPort(string value, string file, string key)
        {
            var normalized = Regex.Replace(value ?? string.Empty, @"\\[0-9A-Fa-f]{2}", string.Empty).Trim();
            if (!Regex.IsMatch(normalized, @"^COM[1-9][0-9]*$", RegexOptions.IgnoreCase))
            {
                throw new InvalidDataException(string.Format("配置无效：文件“{0}”，键“{1}”，值“{2}”，期望 COM 端口（例如 COM12）。", file, key, value));
            }
            return normalized.ToUpperInvariant();
        }

        private static OscilloscopeChannelConfig ReadChannel(LabViewIniReader ini, int number)
        {
            var prefix = "ZDS设置.CH" + number.ToString(CultureInfo.InvariantCulture) + ".";
            decimal probe;
            decimal scale;
            decimal offset;
            if (!decimal.TryParse(ini.Required("基本参数设置", prefix + "探头倍率", "十进制数"), NumberStyles.Number, CultureInfo.InvariantCulture, out probe)) throw new InvalidDataException(prefix + "探头倍率不是有效数字。");
            if (!decimal.TryParse(ini.Required("基本参数设置", prefix + "垂直档位", "十进制数"), NumberStyles.Number, CultureInfo.InvariantCulture, out scale)) throw new InvalidDataException(prefix + "垂直档位不是有效数字。");
            if (!decimal.TryParse(ini.Required("基本参数设置", prefix + "垂直偏置", "十进制数"), NumberStyles.Number, CultureInfo.InvariantCulture, out offset)) throw new InvalidDataException(prefix + "垂直偏置不是有效数字。");
            return new OscilloscopeChannelConfig
            {
                Number = number,
                Name = ini.Required("基本参数设置", prefix + "通道", "通道名"),
                Enabled = ini.RequiredBoolean("基本参数设置", prefix + "启用"),
                BandwidthLimit = ini.Required("基本参数设置", prefix + "带宽限制", "枚举值"),
                Coupling = ini.Required("基本参数设置", prefix + "耦合方式", "枚举值"),
                Inverted = ini.RequiredBoolean("基本参数设置", prefix + "反相显示"),
                ProbeRatio = probe,
                Unit = ini.Required("基本参数设置", prefix + "通道单位", "单位"),
                VerticalScale = scale,
                VerticalOffset = offset
            };
        }

        private static decimal RequireDecimal(LabViewIniReader ini, string section, string key, decimal minimum, decimal maximum)
        {
            var value = ini.Required(section, key, "十进制数");
            decimal result;
            if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result) || result < minimum || result > maximum)
            {
                throw new InvalidDataException(string.Format(CultureInfo.InvariantCulture, "配置无效：节“{0}”，键“{1}”，值“{2}”，期望 {3}～{4} 的十进制数。", section, key, value, minimum, maximum));
            }
            return result;
        }

        private static void ValidateTsapPair(string local, string remote)
        {
            var localAuto = string.Equals(local, "AUTO", StringComparison.OrdinalIgnoreCase);
            var remoteAuto = string.Equals(remote, "AUTO", StringComparison.OrdinalIgnoreCase);
            if (localAuto != remoteAuto)
            {
                throw new InvalidDataException("PLC.S7.本地TSAP 和 PLC.S7.远端TSAP 必须同时为 AUTO，或同时填写明确值。");
            }
            if (localAuto) return;
            const string pattern = @"^[0-9A-Fa-f]{2}[\.:-][0-9A-Fa-f]{2}$";
            if (!Regex.IsMatch(local, pattern) || !Regex.IsMatch(remote, pattern))
            {
                throw new InvalidDataException("PLC S7 TSAP 格式必须为两个十六进制字节，例如 10.00 和 10.01。");
            }
        }
    }
}
