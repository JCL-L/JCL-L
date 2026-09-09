# LabVIEW 到 C# 手动模式映射

## 权威来源

实现以 `NinOne_CSharp_手动模式开发交接.md` 为需求依据，以 `YFHIL_(2QT16)_260714.opf` 为 PLC 点位依据，以 `NinOne_EOL_20260825V6` 中当前 VI、配置和 x86 DLL 为旧工程依据。设备命令由旧 VI 和对应厂商手册交叉核对。

旧工程主入口为 `NinOne_EOL_260601.vi`，手动入口为 `15VIs/0main/manual/manual2.vi`。C# 主窗口为 `src/NinOne.App/MainWindow.xaml`，设备统一所有者为 `src/NinOne.App/Runtime/DeviceRuntime.cs`。

## 功能映射

| 旧工程入口/依赖 | C# 实现 | 说明 |
|---|---|---|
| `15VIs/3IO/PLC_O_global2.vi`、当前 OPF | `PlcS7/S7PlcRelayService.cs` | 从 OPF 固化 Q0.0～Q0.7、Q8.0～Q8.7；通过 TCP 102 直连并逐点写后回读 |
| `DCDC辅助电源(GPD2303)_viread.vi`、`GPD2303s_L.vi`、`GPD2303s_R.vi` | `Gpd2303s/Gpd2303sService.cs` | GPD 命令集，保留 `STATUS?` 原始二进制状态字节 |
| `N69200_20260715_update/N6920x_Connect(Modbus RTU).vi`、`write tcp (modbus RTU).vi`、`crc16.vi` | `N69206/N69206Service.cs`、`ModbusRtuCodec.cs`、`ModbusRtuOverTcpTransport.cs` | TCP 7000 内传 Modbus RTU 帧与 CRC；使用 ID 160，不采用 SCPI 文本通道 |
| `Instrument_vis/GDM9061.vi` | `Gdm9061/Gdm9061Service.cs` | 34401A 兼容 SCPI，串口和波特率由界面设置，CR+LF；功能相关合法量程由界面下拉选择 |
| `manual/PA333H_manual.vi`、`PA333h.vi` | `Pa333h/Pa333hService.cs` | 串口、波特率、源侧/输出侧通道由界面设置；`NUMERIC:NORMAL` 六项与异步连续读取 |
| `DCDC_CAN.vi`、`DCDC_CAN_2`、ZLG USBCANFD VI | `ZlgCan/ZlgDcdcCanService.cs`、`ZlgNative.cs` | ZLG 100U 类型 42/单通道、200U 类型 41/双通道；界面选择 CAN/CAN FD、仲裁/数据波特率、BRS 和 120Ω；适配器连接与通道打开分离；Vector VN1640A 预留 |
| 5615 `send1.csv`、`send2.csv`、`receive.csv` | `ZlgCan/CanCsvProfile.cs` | CSV 只保存模板和信号参数；手动 Function 在接口和按钮中定义 |
| `ZDS2024C_Plus_TCP.vi`、`ZDS_参数设置.vi`、`ZDS_波形.vi` | `Zds2024CPlus/Zds2024CPlusService.cs` | TCP/SCPI，14000 点，读取前 1000 ms，WFM 保存并在界面显示示波器多通道屏幕 |
| 高压电源人工步骤 | `Infrastructure/Manual/ManualHighVoltageService.cs` | 仅记录人工状态，不存在远程输出命令 |

## PLC 最终映射

| 继电器 | PLC 点位 | S7 直连地址 | ON 二次确认 |
|---|---|---|---|
| K1 | Q0.0 | Q0.0 | 否 |
| K2 | Q0.1 | Q0.1 | 否 |
| K3 | Q0.2 | Q0.2 | 是 |
| K4 | Q0.3 | Q0.3 | 是 |
| K5 | Q0.4 | Q0.4 | 是 |
| K6 | Q0.5 | Q0.5 | 是 |
| K7 | Q0.6 | Q0.6 | 是 |
| K8 | Q0.7 | Q0.7 | 否 |
| K9 | Q8.0 | Q8.0 | 否 |
| K10 | Q8.1 | Q8.1 | 否 |
| K11 | Q8.2 | Q8.2 | 否 |
| K12 | Q8.3 | Q8.3 | 否 |
| K13 | Q8.4 | Q8.4 | 否 |
| K14 | Q8.5 | Q8.5 | 否 |
| K15 | Q8.6 | Q8.6 | 否 |
| K16 | Q8.7 | Q8.7 | 否 |

## N69206 Modbus 映射

| 功能 | 寄存器 | 类型/值 |
|---|---:|---|
| 状态寄存器 2 | 8 | uint32；bit0 为输入开关实际状态 |
| 电压/电流/功率 | 10 / 12 / 14 | float32，V/A/W |
| 模式 | 26 | uint32；0=CC，1=CV，2=CR，3=CP；切换前卸载 |
| 输入开关 | 28 | uint32；0=OFF，1=ON |
| 本地/远程 | 32 | uint32；1=远程 |
| CC 大/小/中量程电流 | 40 / 42 / 418 | float32，A |
| CC 量程 | 44 | uint32；0=大，1=小，2=中 |
| CC 大/小/中量程上升斜率 | 46 / 320 / 420 | float32，A/ms |
| CC 大/小/中量程下降斜率 | 48 / 322 / 422 | float32，A/ms |
| CV 大/小/中量程电压 | 50 / 52 / 424 | float32，V |
| CV 量程 | 54 | uint32；0=大，1=小，2=中 |
| CV 大/小/中量程 V-Rate | 740 / 742 / 744 | uint32；0=FAST，1=NORMAL，2=SLOW，3=UD |
| CV 自定义 V-Rate | 830 | float32；0.01～10 V/ms |
| CR 大/小/中量程阻值 | 64 / 66 / 470 | float32，Ω |
| CR 量程 | 68 | uint32；0=大，1=小，2=中 |
| CR 大/小/中量程上升斜率 | 70 / 328 / 472 | float32，A/ms |
| CR 大/小/中量程下降斜率 | 72 / 330 / 474 | float32，A/ms |
| CP 大/小/中量程功率 | 74 / 432 / 464 | float32，W |
| CP 量程 | 430 | uint32；0=大，1=小，2=中 |
| CP 大/小/中量程上升斜率 | 76 / 434 / 466 | float32，A/ms |
| CP 大/小/中量程下降斜率 | 78 / 436 / 468 | float32，A/ms |
| 退出远程 | 838 | uint32；写 1 |

所有 32 位数据占两个寄存器。厂商约定低字在前，寄存器内高字节在前；功能码使用 0x03 和 0x10，TCP 数据仍包含 Modbus RTU CRC。

## ZLG x86 依赖

依赖集中在 `vendor/ZlgCan/x86` 并由驱动项目复制到输出目录。`zlgcan_wrap.dll` 文件版本为 1.0.0.1、产品版本为 20250331；两个入口 DLL 都是 x86。原生布局由冒烟测试固定为：CHANNEL_INIT_CONFIG 32 字节、can_frame 16 字节、Transmit_Data 20 字节、Receive_Data 24 字节、canfd_frame 72 字节、TransmitFD_Data 76 字节、ReceiveFD_Data 80 字节。

## 生命周期和日志

每个设备服务有独立异步锁，同一设备只执行一个命令。串口、S7、TCP 和 CAN 操作不在 UI 线程执行；网络通讯有显式读写超时。`DeviceRuntime` 是唯一设备所有者，并在退出时关闭输出和释放资源。

原始 TX/RX、操作结果与异常统一写入 `logs/yyyy-MM-dd.log`。示波器文件写入 `logs/Captures`。没有数据库组件或数据表。
