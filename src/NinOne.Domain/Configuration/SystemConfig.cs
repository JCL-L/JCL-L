using System;
using System.Collections.Generic;

namespace NinOne.Domain.Configuration
{
    public sealed class SystemConfig
    {
        public string SourcePath { get; set; }
        public PlcConfig Plc { get; set; } = new PlcConfig();
        public SerialDeviceConfig AuxiliaryPower { get; set; } = new SerialDeviceConfig { Model = "GPD2303S", PortName = "COM12", BaudRate = 9600 };
        public ElectronicLoadConfig ElectronicLoad { get; set; } = new ElectronicLoadConfig();
        public SerialDeviceConfig Multimeter { get; set; } = new SerialDeviceConfig { Model = "GDM9061", PortName = "COM13", BaudRate = 115200 };
        public PowerMeterConfig PowerMeter { get; set; } = new PowerMeterConfig();
        public CanConfig Can { get; set; } = new CanConfig();
        public OscilloscopeConfig Oscilloscope { get; set; } = new OscilloscopeConfig();
    }

    public sealed class PlcConfig
    {
        public string PcAddress { get; set; } = "192.168.4.100";
        public string PlcAddress { get; set; } = "192.168.4.101";
        public int Port { get; set; } = 102;
        public string CpuType { get; set; } = "Auto";
        public int Rack { get; set; }
        public int Slot { get; set; }
        public string LocalTsap { get; set; } = "AUTO";
        public string RemoteTsap { get; set; } = "AUTO";
        public int TimeoutMilliseconds { get; set; } = 3000;
    }

    public sealed class SerialDeviceConfig
    {
        public string Model { get; set; }
        public string PortName { get; set; }
        public int BaudRate { get; set; }
        public int TimeoutMilliseconds { get; set; } = 3000;
    }

    public sealed class ElectronicLoadConfig
    {
        public string Model { get; set; } = "N69206";
        public string Host { get; set; } = "192.168.4.102";
        public int Port { get; set; } = 7000;
        public int DeviceId { get; set; } = 160;
        public int TimeoutMilliseconds { get; set; } = 3000;
        public int CcRange { get; set; }
        public int CvRange { get; set; }
        public int CrRange { get; set; }
        public int CpRange { get; set; }
    }

    public sealed class PowerMeterConfig
    {
        public string Model { get; set; } = "PA333H";
        public string PortName { get; set; } = "COM4";
        public int BaudRate { get; set; } = 115200;
        public int SourceChannel { get; set; } = 1;
        public int OutputChannel { get; set; } = 3;
        public int TimeoutMilliseconds { get; set; } = 3000;
    }

    public sealed class CanConfig
    {
        public string AdapterModel { get; set; } = "ZLG_CANFD_200U";
        public int DeviceIndex { get; set; }
        public int ChannelIndex { get; set; }
        public int ChannelCount { get; set; } = 2;
        public int ArbitrationBitRate { get; set; } = 500000;
        public int DataBitRate { get; set; } = 2000000;
        public bool UseClassicCan { get; set; } = true;
        public bool TerminationEnabled { get; set; } = true;
        public decimal DcdcP1 { get; set; } = 2000m;
        public decimal DcdcP2 { get; set; } = 145m;
        public string TemplateCsvPath { get; set; }
        public string SignalCsvPath { get; set; }
        public string ReceiveCsvPath { get; set; }
    }

    public sealed class OscilloscopeConfig
    {
        public string Model { get; set; } = "ZDS2024C Plus";
        public string Host { get; set; } = "192.168.4.128";
        public int Port { get; set; } = 5025;
        public int TimeoutMilliseconds { get; set; } = 10000;
        public string HorizontalOffset { get; set; } = "0";
        public string TimeBase { get; set; } = "1ms";
        public string TimeBaseMode { get; set; } = "Main";
        public int AverageCount { get; set; } = 2;
        public string AcquisitionMode { get; set; } = "Normal";
        public int SampleDepth { get; set; } = 14000;
        public bool RollingMode { get; set; }
        public IList<OscilloscopeChannelConfig> Channels { get; } = new List<OscilloscopeChannelConfig>();
    }

    public sealed class OscilloscopeChannelConfig
    {
        public int Number { get; set; }
        public string Name { get; set; }
        public bool Enabled { get; set; }
        public string BandwidthLimit { get; set; }
        public string Coupling { get; set; }
        public bool Inverted { get; set; }
        public decimal ProbeRatio { get; set; }
        public string Unit { get; set; }
        public decimal VerticalScale { get; set; }
        public decimal VerticalOffset { get; set; }
    }
}
