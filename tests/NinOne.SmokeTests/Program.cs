using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NinOne.Application.Devices;
using NinOne.Domain.Devices;
using NinOne.Drivers.Common;
using NinOne.Drivers.Gpd2303s;
using NinOne.Drivers.N69206;
using NinOne.Drivers.ZlgCan;
using NinOne.Infrastructure.Configuration;

namespace NinOne.SmokeTests
{
    internal static class Program
    {
        private static int Main()
        {
            try
            {
                var root = FindSolutionRoot(AppDomain.CurrentDomain.BaseDirectory);
                var systemPath = Path.Combine(root, "config", "System.ini");
                var productPath = Path.Combine(root, "config", "Product", "5615", "cfg.ini");
                var config = new SystemConfigLoader().Load(systemPath, productPath);

                VerifyAuthoritativeConfiguration(config);
                VerifyRelayMap();
                VerifyS7RelayAddresses();
                VerifyCanProfile(config);
                VerifyCanTextCodec();
                VerifyZlgInteropLayout();
                VerifyUiOwnedConfiguration(root);
                VerifyGpdBinaryStatus();
                VerifyN69206ModbusCodec();
                VerifyTcpTimeoutAsync().GetAwaiter().GetResult();
                VerifyNoDatabaseDependency(root);

                Console.WriteLine("PASS: S7 直连配置、继电器映射、CAN 文件和无数据库依赖检查全部通过。");
                return 0;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine("FAIL: " + exception);
                return 1;
            }
        }

        private static string FindSolutionRoot(string startDirectory)
        {
            var current = new DirectoryInfo(startDirectory);
            while (current != null)
            {
                if (File.Exists(Path.Combine(current.FullName, "NinOne.sln"))) return current.FullName;
                current = current.Parent;
            }
            throw new DirectoryNotFoundException("无法从测试输出目录定位 NinOne.sln。");
        }

        private static void VerifyAuthoritativeConfiguration(NinOne.Domain.Configuration.SystemConfig config)
        {
            Equal("192.168.4.100", config.Plc.PcAddress, "上位机 IP");
            Equal("192.168.4.101", config.Plc.PlcAddress, "PLC IP");
            Equal(102, config.Plc.Port, "PLC S7 端口");
            Equal("S7200Smart", config.Plc.CpuType, "PLC CPU 类型");
            Equal(0, config.Plc.Rack, "PLC 机架");
            Equal(0, config.Plc.Slot, "PLC 槽位");
            Equal("AUTO", config.Plc.LocalTsap, "PLC 本地 TSAP");
            Equal("AUTO", config.Plc.RemoteTsap, "PLC 远端 TSAP");
            Equal(3000, config.Plc.TimeoutMilliseconds, "PLC S7 超时");
            Equal("COM12", config.AuxiliaryPower.PortName, "GPD2303S 串口");
            Equal(9600, config.AuxiliaryPower.BaudRate, "GPD2303S 波特率");
            Equal("192.168.4.102", config.ElectronicLoad.Host, "N69206 IP");
            Equal(7000, config.ElectronicLoad.Port, "N69206 端口");
            Equal(160, config.ElectronicLoad.DeviceId, "N69206 地址");
            Equal(0, config.ElectronicLoad.CcRange, "N69206 CC 量程");
            Equal(0, config.ElectronicLoad.CvRange, "N69206 CV 量程");
            Equal(0, config.ElectronicLoad.CrRange, "N69206 CR 量程");
            Equal(0, config.ElectronicLoad.CpRange, "N69206 CP 量程");
            Equal("COM13", config.Multimeter.PortName, "GDM9061 串口");
            Equal("COM4", config.PowerMeter.PortName, "PA333H 串口");
            Equal(1, config.PowerMeter.SourceChannel, "PA333H 源侧通道");
            Equal(3, config.PowerMeter.OutputChannel, "PA333H 输出通道");
            Equal(500000, config.Can.ArbitrationBitRate, "CAN 仲裁域波特率");
            Equal(2000000, config.Can.DataBitRate, "CAN FD 数据域波特率");
            Equal("ZLG_CANFD_200U", config.Can.AdapterModel, "CAN 默认适配器");
            Equal(2, config.Can.ChannelCount, "CAN 通道数");
            Equal(true, config.Can.UseClassicCan, "经典 CAN");
            Equal(2000m, config.Can.DcdcP1, "DCDC P1");
            Equal(145m, config.Can.DcdcP2, "DCDC P2");
            Equal("192.168.4.128", config.Oscilloscope.Host, "示波器 IP");
            Equal(5025, config.Oscilloscope.Port, "示波器端口");
            Equal(14000, config.Oscilloscope.SampleDepth, "示波器采样深度");
            Equal(4, config.Oscilloscope.Channels.Count, "示波器通道数");
        }

        private static void VerifyRelayMap()
        {
            Equal(16, RelayDefinition.FirstStage.Count, "继电器数量");
            for (var index = 0; index < RelayDefinition.FirstStage.Count; index++)
            {
                var relayNumber = index + 1;
                var relay = RelayDefinition.FirstStage[index];
                var expectedAddress = relayNumber <= 8 ? "Q0." + index : "Q8." + (index - 8);
                Equal("K" + relayNumber, relay.Id, "继电器编号 " + relayNumber);
                Equal(expectedAddress, relay.PlcAddress, relay.Id + " PLC 地址");
                Equal(relayNumber >= 3 && relayNumber <= 7, relay.RequiresConfirmation, relay.Id + " 上电确认");
            }
        }

        private static void VerifyS7RelayAddresses()
        {
            var parserType = typeof(S7.Net.Plc).Assembly.GetType("S7.Net.PLCAddress", true);
            var parse = parserType.GetMethod("Parse", BindingFlags.Public | BindingFlags.Static);
            if (parse == null) throw new MissingMethodException("S7.Net.PLCAddress.Parse");
            foreach (var relay in RelayDefinition.FirstStage)
            {
                var parts = relay.PlcAddress.Substring(1).Split('.');
                var expectedByte = int.Parse(parts[0]);
                var expectedBit = int.Parse(parts[1]);
                var arguments = new object[] { relay.PlcAddress, S7.Net.DataType.Input, 0, S7.Net.VarType.Byte, 0, 0 };
                parse.Invoke(null, arguments);
                Equal(S7.Net.DataType.Output, (S7.Net.DataType)arguments[1], relay.Id + " S7 输出区");
                Equal(S7.Net.VarType.Bit, (S7.Net.VarType)arguments[3], relay.Id + " S7 位类型");
                Equal(expectedByte, (int)arguments[4], relay.Id + " S7 字节地址");
                Equal(expectedBit, (int)arguments[5], relay.Id + " S7 位地址");
            }
        }

        private static void VerifyCanProfile(NinOne.Domain.Configuration.SystemConfig config)
        {
            using (var service = new ZlgDcdcCanService(config.Can))
            {
                Equal(9, service.Templates.Count, "send1.csv 帧模板数");
                var expectedIds = new uint[] { 0x1F1, 0x598, 0x1D8, 0x215, 0x215, 0x234, 0x282, 0x1F1, 0x585 };
                var actualIds = service.Templates.Select(item => item.Frame.Id).ToArray();
                SequenceEqual(expectedIds, actualIds, "send1.csv CAN ID 顺序");
                Equal(true, service.Templates.Any(item => item.Group == 3 && item.Frame.Id == 0x234), "DCDC 控制模板");
                var on = service.BuildDcdcControlFrame(true);
                var off = service.BuildDcdcControlFrame(false);
                Equal(0x234u, on.Id, "DCDC 控制帧 ID");
                Equal(8, on.Data.Length, "DCDC 控制帧长度");
                Equal(0x10, on.Data[1] ^ off.Data[1], "DCDC 开关位");
                Equal(true, on.Data.Where((value, index) => index != 1).Any(value => value != 0), "DCDC P1/P2 编码");
            }
        }

        private static void VerifyNoDatabaseDependency(string root)
        {
            var prohibited = new[] { "EntityFramework", "System.Data.SQLite", "Microsoft.Data.Sqlite", "DbContext" };
            var projectFiles = Directory.GetFiles(Path.Combine(root, "src"), "*.csproj", SearchOption.AllDirectories);
            var sourceFiles = Directory.GetFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories)
                .Where(path => path.IndexOf(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) < 0)
                .ToArray();
            foreach (var path in projectFiles.Concat(sourceFiles))
            {
                var text = File.ReadAllText(path);
                foreach (var name in prohibited)
                {
                    if (text.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        throw new InvalidDataException("检测到数据库依赖或代码：" + path + " / " + name);
                    }
                }
            }
        }

        private static void VerifyZlgInteropLayout()
        {
            Equal(32, Marshal.SizeOf(typeof(ZlgNative.ChannelInitConfig)), "ZLG CHANNEL_INIT_CONFIG 大小");
            Equal(16, Marshal.SizeOf(typeof(ZlgNative.NativeCanFrame)), "ZLG can_frame 大小");
            Equal(20, Marshal.SizeOf(typeof(ZlgNative.TransmitData)), "ZLG Transmit_Data 大小");
            Equal(24, Marshal.SizeOf(typeof(ZlgNative.ReceiveData)), "ZLG Receive_Data 大小");
            Equal(72, Marshal.SizeOf(typeof(ZlgNative.NativeCanFdFrame)), "ZLG canfd_frame 大小");
            Equal(76, Marshal.SizeOf(typeof(ZlgNative.TransmitFdData)), "ZLG TransmitFD_Data 大小");
            Equal(80, Marshal.SizeOf(typeof(ZlgNative.ReceiveFdData)), "ZLG ReceiveFD_Data 大小");
            Equal(8, Marshal.SizeOf(typeof(ZlgNative.ChannelErrorInfo)), "ZLG CHANNEL_ERR_INFO 大小");
            Equal(12, Marshal.SizeOf(typeof(ZlgNative.ChannelStatus)), "ZLG CHANNEL_STATUS 大小");
            Equal(41u, ZlgNative.UsbCanFd200U, "ZLG CANFD 200U 类型常量");
            Equal(42u, ZlgNative.UsbCanFd100U, "ZLG CANFD 100U 类型常量");
            Equal(41u, ZlgDcdcCanService.ResolveDeviceType("ZLG CANFD 200U"), "200U 型号解析");
            Equal(42u, ZlgDcdcCanService.ResolveDeviceType("ZLG CANFD 100U"), "100U 型号解析");
            Equal(2, ZlgDcdcCanService.ResolveChannelCount("ZLG CANFD 200U"), "200U 通道数");
            Equal(1, ZlgDcdcCanService.ResolveChannelCount("ZLG CANFD 100U"), "100U 通道数");
            Equal(1, (int)ZlgNative.CanFdBitRateSwitch, "CAN FD BRS 标志");
            Equal(2u, ZlgNative.StatusOnline, "ZLG 在线状态常量");
            Equal(ZlgNative.CanTypeFd, ZlgDcdcCanService.BuildCanFdHardwareInitConfig().CanType, "100U/200U 经典 CAN 仍使用 CAN FD 硬件初始化类型");
            Equal("0/protocol", ZlgDcdcCanService.ProtocolPath(0), "CAN0 总线协议属性路径");
            Equal("0/canfd_abit_baud_rate", ZlgDcdcCanService.ArbitrationBitRatePath(0), "CAN0 仲裁波特率属性路径");
            Equal("1/canfd_dbit_baud_rate", ZlgDcdcCanService.DataBitRatePath(1), "CAN1 数据波特率属性路径");
            Equal("1/initenal_resistance", ZlgDcdcCanService.ResistancePath(1), "CAN1 终端电阻属性路径");
            var transmit = typeof(ZlgNative).GetMethod("ZCAN_Transmit", BindingFlags.Static | BindingFlags.NonPublic);
            var setValue = typeof(ZlgNative).GetMethod("ZCAN_SetValue", BindingFlags.Static | BindingFlags.NonPublic);
            Equal("zlgcan.dll", transmit.GetCustomAttribute<DllImportAttribute>().Value, "ZCAN_Transmit 使用官方核心 DLL");
            Equal(true, transmit.GetParameters()[1].ParameterType.IsByRef, "单帧发送使用 ref 结构体指针");
            Equal("zlgcan.dll", setValue.GetCustomAttribute<DllImportAttribute>().Value, "ZCAN_SetValue 使用官方核心 DLL");
        }

        private static void VerifyCanTextCodec()
        {
            SequenceEqual(new byte[] { 0x01, 0x02, 0xA0, 0xFF }, CanTextCodec.ParseData("0102A0FF"), "CAN 连续十六进制数据");
            SequenceEqual(new byte[] { 0x01, 0x02, 0xA0, 0xFF }, CanTextCodec.ParseData("01 02 A0 FF"), "CAN 空格分隔数据");
            SequenceEqual(new byte[] { 0x01, 0xA2 }, CanTextCodec.ParseData("0x01,0xA2"), "CAN 0x 前缀数据");
            var standardFrame = new CanFrame { ChannelIndex = 0, Id = 0x7FF, Data = new byte[8] };
            Equal(true, standardFrame.ToString().Contains("标准帧 ID=7FF"), "标准帧类型与 ID 明示");
            Equal(false, standardFrame.ToString().Contains("S000007FF"), "不再使用易混淆的 S 前缀");
            var extendedFrame = new CanFrame { ChannelIndex = 1, Id = 0x18FF50E5, IsExtended = true, Data = new byte[8] };
            Equal(true, extendedFrame.ToString().Contains("扩展帧 ID=18FF50E5"), "扩展帧类型与 ID 明示");
        }

        private static void VerifyUiOwnedConfiguration(string root)
        {
            var systemText = File.ReadAllText(Path.Combine(root, "config", "System.ini"));
            var productText = File.ReadAllText(Path.Combine(root, "config", "Product", "5615", "cfg.ini"));
            foreach (var prohibited in new[]
            {
                "串口信息", "辅助电源串口", "万用表串口", "功率计串口", "DCDC_CAN通讯设置", "CAN卡设备号",
                "PLC.S7.端口", "电脑IP", "PLC_IP", "低压负载IP", "低压负载端口", "设备地址.低压电子负载",
                "低压电子负载.量程", "示波器IP", "示波器端口"
            })
                Equal(false, systemText.IndexOf(prohibited, StringComparison.OrdinalIgnoreCase) >= 0, "System.ini 不再保存 " + prohibited);
            foreach (var prohibited in new[] { "产品条码", "气缸", "上一工位", "产品类型", "DCDC_CAN通讯设置" })
                Equal(false, productText.IndexOf(prohibited, StringComparison.OrdinalIgnoreCase) >= 0, "产品 cfg.ini 不再保存 " + prohibited);
            var xaml = File.ReadAllText(Path.Combine(root, "src", "NinOne.App", "MainWindow.xaml"));
            Equal(true, xaml.Contains("仲裁波特率 (kbps)") && xaml.Contains("数据波特率 (kbps)"), "CAN 波特率界面使用 kbps");
            Equal(true, xaml.Contains("同时打开全部通道"), "CAN 双通道同时打开入口");
            Equal(true, xaml.Contains("自动接收：等待通道打开"), "CAN 自动接收状态区");
            Equal(true, xaml.Contains("ms（0=一次）"), "CAN 循环发送间隔规则");
            Equal(true, xaml.Contains("CanDataLengthBox"), "CAN 数据长度输入");
            Equal(true, xaml.Contains("CAN 收发帧") && xaml.Contains("CanFrameChannelFilterBox") && xaml.Contains("CanFrameIdFilterText") && xaml.Contains("CanFrameDirectionFilterBox"), "CAN 收发合并及通道、ID、方向筛选");
            Equal(false, xaml.Contains("DCDC 控制与模板") || xaml.Contains("CanSendGroup_Click"), "CAN 页面隐藏 DCDC 控制和模板");
            var runtime = File.ReadAllText(Path.Combine(root, "src", "NinOne.App", "Runtime", "DeviceRuntime.cs"));
            Equal(false, runtime.Contains("SetDcdcRunAsync"), "退出过程不发送隐藏的 DCDC 控制帧");
            Equal(true, xaml.Contains("PlcIpText") && xaml.Contains("LoadIpText") && xaml.Contains("ScopeIpText"), "网络地址由对应页面输入");
            Equal(true, xaml.Contains("LoadCcRangeBox") && xaml.Contains("LoadCpRangeBox"), "N69206 四模式量程由页面选择");
            Equal(true, xaml.Contains("GpdPortText\" Width=\"100\" IsEditable=\"True\"") && xaml.Contains("DmmPortText\" Width=\"100\" IsEditable=\"True\"") && xaml.Contains("PowerPortText\" Width=\"100\" IsEditable=\"True\""), "串口使用可选可输下拉框");
            Equal(false, xaml.Contains("限流"), "辅助电源界面不声明独立限流功能");
            Equal(true, xaml.Contains("电流上限 (ISET，A)"), "GPD ISET 明确显示为电流上限");
            Equal(true, xaml.Contains("电压升降循环") && xaml.Contains("上升目标电压 (V)") && xaml.Contains("下降目标电压 (V)"), "GPD 电压升降目标界面");
            Equal(true, xaml.Contains("上升速率 (V/s)") && xaml.Contains("下降速率 (V/s)") && xaml.Contains("GpdRampCycleCountText"), "GPD 电压速率和循环次数界面");
            Equal(true, xaml.Contains("保持当前 ISET 和输出 ON/OFF 状态不变"), "GPD 电压循环不隐式切换输出或电流上限");
            Equal(true, xaml.Contains("ClipToBounds=\"True\"") && xaml.Contains("CanContentScroll=\"False\""), "设备页滚动区域受当前工作区约束");
        }

        private static void VerifyGpdBinaryStatus()
        {
            Equal(true, typeof(IAuxiliaryPowerService).GetMethod("SetVoltageAsync") != null, "GPD 电压斜坡使用独立 VSET 接口");
            Equal(true, Gpd2303sService.ParseOutputStatus("11001110"), "GPD 现场输出 ON 状态");
            Equal(false, Gpd2303sService.ParseOutputStatus("00001010"), "GPD 现场输出 OFF 状态");
            Equal(true, Gpd2303sService.ParseOutputStatus(new string((char)0x20, 1)), "GPD 二进制输出 ON 状态");
            Equal(false, Gpd2303sService.ParseOutputStatus(new string((char)0x00, 1)), "GPD 二进制输出 OFF 状态");
            Equal(true, Gpd2303sService.ParseOutputStatus("32"), "GPD 十进制输出 ON 状态");
        }

        private static void VerifyN69206ModbusCodec()
        {
            var request = ModbusRtuCodec.BuildWriteUInt32Request(1, 2, 0x12345678);
            var expected = new byte[] { 0x01, 0x10, 0x00, 0x02, 0x00, 0x02, 0x04, 0x56, 0x78, 0x12, 0x34, 0xEE, 0x90 };
            SequenceEqual(expected, request, "N69206 手册 0x12345678 写报文");

            var floatRequest = ModbusRtuCodec.BuildWriteFloatRequest(160, 40, 12.5m);
            Equal(160, (int)floatRequest[0], "N69206 ID 160");
            Equal(40, (int)floatRequest[3], "N69206 CC 电流寄存器");
            Equal(12.5m, ModbusRtuCodec.DecodeFloat(floatRequest, 7), "N69206 浮点字序往返");
        }

        private static async Task VerifyTcpTimeoutAsync()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            try
            {
                var endpoint = (IPEndPoint)listener.LocalEndpoint;
                var accepted = listener.AcceptTcpClientAsync();
                using (var transport = new TcpScpiTransport("127.0.0.1", endpoint.Port, 250))
                {
                    await transport.OpenAsync(CancellationToken.None).ConfigureAwait(false);
                    using (await accepted.ConfigureAwait(false))
                    {
                        var timedOut = false;
                        try { await transport.QueryAsync("*IDN?", CancellationToken.None).ConfigureAwait(false); }
                        catch (TimeoutException) { timedOut = true; }
                        Equal(true, timedOut, "TCP 无响应超时");
                        Equal(false, transport.IsOpen, "TCP 超时后释放连接");
                    }
                }
            }
            finally
            {
                listener.Stop();
            }
        }

        private static void Equal<T>(T expected, T actual, string name)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual))
            {
                throw new InvalidDataException(name + " 不一致：期望 " + expected + "，实际 " + actual + "。");
            }
        }

        private static void SequenceEqual<T>(IEnumerable<T> expected, IEnumerable<T> actual, string name)
        {
            if (!expected.SequenceEqual(actual)) throw new InvalidDataException(name + " 不一致。");
        }
    }
}
