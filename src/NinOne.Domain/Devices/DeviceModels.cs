using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace NinOne.Domain.Devices
{
    public sealed class RelayDefinition
    {
        public RelayDefinition(string id, string plcAddress, string displayName, bool requiresConfirmation)
        {
            Id = id;
            PlcAddress = plcAddress;
            DisplayName = displayName;
            RequiresConfirmation = requiresConfirmation;
        }

        public string Id { get; }
        public string PlcAddress { get; }
        public string DisplayName { get; }
        public bool RequiresConfirmation { get; }

        public static IReadOnlyList<RelayDefinition> FirstStage { get; } = new[]
        {
            new RelayDefinition("K1", "Q0.0", "KL30 / 12V1 供电相关切换", false),
            new RelayDefinition("K2", "Q0.1", "KL15 / 12V1+", false),
            new RelayDefinition("K3", "Q0.2", "AC 输入接触器线圈", true),
            new RelayDefinition("K4", "Q0.3", "HVDC+ 继电器线圈", true),
            new RelayDefinition("K5", "Q0.4", "HVDC− 继电器线圈", true),
            new RelayDefinition("K6", "Q0.5", "LVDC+ 继电器线圈", true),
            new RelayDefinition("K7", "Q0.6", "LVDC− 继电器线圈", true),
            new RelayDefinition("K8", "Q0.7", "CANH/CANL 与 GDM+/GDM− 切换", false),
            new RelayDefinition("K9", "Q8.0", "CANH/GND 与 GDM+/GDM− 切换", false),
            new RelayDefinition("K10", "Q8.1", "GND/CANL 与 GDM+/GDM− 切换", false),
            new RelayDefinition("K11", "Q8.2", "报警灯—绿", false),
            new RelayDefinition("K12", "Q8.3", "报警灯—黄", false),
            new RelayDefinition("K13", "Q8.4", "报警灯—红", false),
            new RelayDefinition("K14", "Q8.5", "蜂鸣器", false),
            new RelayDefinition("K15", "Q8.6", "DC 输出辅助电源供电", false),
            new RelayDefinition("K16", "Q8.7", "运行指示灯", false)
        };
    }

    public sealed class DeviceOperationResult
    {
        public bool Success { get; set; }
        public string Summary { get; set; }
        public string RawRequest { get; set; }
        public string RawResponse { get; set; }
        public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.Now;
    }

    public sealed class PowerSupplyReading
    {
        public int Channel { get; set; }
        public decimal SetVoltage { get; set; }
        public decimal SetCurrent { get; set; }
        public decimal OutputVoltage { get; set; }
        public decimal OutputCurrent { get; set; }
        public bool OutputEnabled { get; set; }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "CH{0}: 设定 {1:0.###} V / {2:0.###} A，实测 {3:0.###} V / {4:0.###} A，输出 {5}", Channel, SetVoltage, SetCurrent, OutputVoltage, OutputCurrent, OutputEnabled ? "ON" : "OFF");
        }
    }

    public sealed class ElectronicLoadReading
    {
        public decimal Voltage { get; set; }
        public decimal Current { get; set; }
        public decimal Power { get; set; }
        public bool InputEnabled { get; set; }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0:0.###} V / {1:0.###} A / {2:0.###} W，加载 {3}", Voltage, Current, Power, InputEnabled ? "ON" : "OFF");
        }
    }

    public enum ElectronicLoadMode
    {
        ConstantCurrent,
        ConstantVoltage,
        ConstantResistance,
        ConstantPower
    }

    public enum ElectronicLoadRange
    {
        High = 0,
        Low = 1,
        Medium = 2
    }

    public enum ElectronicLoadVoltageRate
    {
        Fast = 0,
        Normal = 1,
        Slow = 2,
        UserDefined = 3
    }

    public enum MultimeterFunction
    {
        DcVoltage,
        AcVoltage,
        DcCurrent,
        AcCurrent,
        Resistance2Wire,
        Resistance4Wire,
        Frequency,
        Period,
        Diode,
        Continuity
    }

    public sealed class MultimeterReading
    {
        public MultimeterFunction Function { get; set; }
        public decimal Value { get; set; }
        public string Unit { get; set; }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0}: {1:G12} {2}", Function, Value, Unit);
        }
    }

    public sealed class PowerMeterReading
    {
        public decimal SourceVoltage { get; set; }
        public decimal SourceCurrent { get; set; }
        public decimal SourcePower { get; set; }
        public decimal OutputVoltage { get; set; }
        public decimal OutputCurrent { get; set; }
        public decimal OutputPower { get; set; }
        public decimal? Efficiency
        {
            get { return SourcePower == 0 ? (decimal?)null : OutputPower / SourcePower; }
        }

        public override string ToString()
        {
            var efficiency = Efficiency.HasValue ? (Efficiency.Value * 100m).ToString("0.00", CultureInfo.InvariantCulture) + "%" : "--";
            return string.Format(CultureInfo.InvariantCulture, "源侧 {0:0.###} V / {1:0.###} A / {2:0.###} W；输出 {3:0.###} V / {4:0.###} A / {5:0.###} W；效率 {6}", SourceVoltage, SourceCurrent, SourcePower, OutputVoltage, OutputCurrent, OutputPower, efficiency);
        }
    }

    public sealed class CanFrame
    {
        public uint Id { get; set; }
        public bool IsExtended { get; set; }
        public bool IsRemote { get; set; }
        public bool IsCanFd { get; set; }
        public bool BitRateSwitch { get; set; }
        public bool ErrorStateIndicator { get; set; }
        public byte[] Data { get; set; } = new byte[0];
        public ulong TimestampMicroseconds { get; set; }
        public int ChannelIndex { get; set; } = -1;

        public override string ToString()
        {
            var channel = ChannelIndex >= 0 ? "CAN" + ChannelIndex.ToString(CultureInfo.InvariantCulture) + " " : string.Empty;
            var protocol = IsCanFd ? (BitRateSwitch ? "CAN FD+BRS " : "CAN FD ") : string.Empty;
            var frameType = IsExtended ? "扩展帧" : "标准帧";
            var id = Id.ToString(IsExtended ? "X8" : "X3", CultureInfo.InvariantCulture);
            return string.Format(CultureInfo.InvariantCulture, "{0}{1}{2} ID={3} [{4}] {5}", channel, protocol, frameType, id, Data.Length, string.Join(" ", Data.Select(value => value.ToString("X2", CultureInfo.InvariantCulture))));
        }
    }

    public sealed class CanFrameTemplate
    {
        public int Group { get; set; }
        public CanFrame Frame { get; set; }
        public int PeriodMilliseconds { get; set; }

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "组 {0} / {1} / {2} ms", Group, Frame, PeriodMilliseconds);
        }
    }

    public sealed class WaveformSet
    {
        public byte[] RawWfmBytes { get; set; } = new byte[0];
        public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.Now;
        public int ConfiguredSampleDepth { get; set; }
        public IReadOnlyList<int> EnabledChannels { get; set; } = new int[0];

        public override string ToString()
        {
            return string.Format(CultureInfo.InvariantCulture, "{0} 通道，{1} 点配置，WFM {2} 字节", EnabledChannels.Count, ConfiguredSampleDepth, RawWfmBytes.Length);
        }
    }

    public sealed class OperationLogEntry
    {
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.Now;
        public string Level { get; set; }
        public string Device { get; set; }
        public string Function { get; set; }
        public string Parameters { get; set; }
        public string Message { get; set; }
        public string ErrorCode { get; set; }
        public string RawResponse { get; set; }

        public override string ToString()
        {
            return string.Format("{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2} | {3} | {4}", Timestamp, Level, Device, Function, Message);
        }
    }
}
