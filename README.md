# NinOne C# 手动模式

这是从现有 NinOne LabVIEW 工程移植的第一阶段手动控制程序。工程使用 WPF、.NET Framework 4.8 和 x86 平台，直接连接真实 PLC 与仪器。程序没有自动模式、配方执行引擎或数据库；IP、端口、串口、CAN 连接参数和低压负载量程都在对应设备页面设置，运行记录写入本地文本日志。

## 工程入口

- 解决方案：`NinOne.sln`
- WPF 程序：`src/NinOne.App/NinOne.App.csproj`
- 离线冒烟验证：`tests/NinOne.SmokeTests/NinOne.SmokeTests.csproj`
- 系统配置：`config/System.ini`
- 5615 产品配置：`config/Product/5615/cfg.ini`
- CAN 参数：`config/Product/5615/send1.csv`、`send2.csv`、`receive.csv`
- LabVIEW 到 C# 映射：`docs/LabVIEW到CSharp映射.md`
- 现场验收状态：`docs/现场验收记录_20260908.md`

## 设备实现

| 设备 | 通讯实现 | 首期功能 |
|---|---|---|
| PLC | 页面设置本机 IP、PLC IP 和端口，Siemens S7 直连 | K1～K16 读取、单点 ON/OFF、写后回读、K16→K1 全关 |
| GPD2303S | 界面下拉选择 COM 口/波特率，8N1，CR+LF | CH1/CH2 电压及 ISET 电流上限设定与回读、单次输出、毫秒时间记录、循环关断、按 V/s 的有限次数电压升降循环、测量读取 |
| N69206 | 页面设置 IP/端口/ID/四模式量程，Modbus RTU over TCP | CC/CV/CR/CP 手动参数、加载开关、状态与 U/I/P 读取 |
| GDM9061 | 界面选择 COM 口/波特率，8N1，CR+LF，SCPI | 测量功能和合法量程下拉、单次读取 |
| PA333H | 界面选择 COM 口/波特率及源侧/输出侧通道，8N1，LF，SCPI | 一次读取、非阻塞连续读取 |
| CAN | 界面选择 ZLG CANFD 100U/200U、CAN/CAN FD、仲裁/数据波特率（kbps）、BRS、120Ω | 通道单独或同时打开/关闭、经典 CAN 与 CAN FD 单帧/循环发送、自动接收、收发记录筛选；Vector VN1640A 预留 |
| ZDS2024C Plus | 页面设置 TCP 地址，SCPI | 界面配置时基、采集和 CH1～CH4 参数并应用；运行、停止、14000 点多通道 WFM、界面波形、BMP 截图、复位 |
| 高压直流电源 | 人工记录 | 只记录人工开启/关闭及备注，不发送控制命令 |

N69206 使用旧工程实际采用的 Modbus RTU over TCP。32 位值按厂商约定使用低字在前、每个寄存器高字节在前的顺序，并校验 Modbus CRC。CC/CV/CR/CP 的量程代码、设定值、斜率和 V-Rate 均来自手动界面。模式切换前程序先关闭负载输入。

各设备页面会填满导航右侧的可用工作区，并将对应的原始通信日志放在右侧且默认展开。CAN 页面右侧同时显示收发帧和原始日志；示波器页面保留波形主体，右侧显示 SCPI 日志。界面以 1440×860 为设计基准统一等比缩放，窗口最大化时导航、文字和全部控件同步放大。

GPD2303S 页面单独显示每次输出 ON/OFF 的写后回读确认时间，格式精确到毫秒，不与原始串口日志混合。“循环关断功能”使用独立的 ON/OFF 毫秒参数和循环专用启动、停止按钮；停止、断开或退出时会结束循环并关闭输出。设备协议提供 `ISET1/ISET2`，它是对应通道的电流上限设定，并支持 `ISET1?/ISET2?` 回读确认；没有另一个限流启用开关，界面据此标为“电流上限 (ISET)”。

“电压升降循环”可选择 CH1/CH2，分别设置上升目标、下降目标、上升速率、下降速率和循环次数。启动后先从设备当前 VSET 按对应速率运行到下降目标，再执行指定次数的“下降目标→上升目标→下降目标”。程序按实际经过时间计算每个 VSET，并逐步写入和回读；受 9600 波特率串口传输、设备响应及 0.001 V 命令分辨率影响，它属于软件定时斜坡。停止时保持最后一次确认的 VSET，完成后停在下降目标；整个过程不改变 ISET 或输出 ON/OFF 状态。电压升降循环与循环关断功能互斥。

ZLG 200U 连接时先为 CAN0/CAN1 预配置协议和波特率；通道初始化后、启动前设置各自的 120Ω。100U/200U 始终按 CAN FD 硬件类型初始化，页面的 CAN/CAN FD 选择通过 `protocol` 属性控制。CAN 收发直接调用官方 `zlgcan.dll`；发送失败只记录诊断并保留通道，操作员关闭、断开或退出时才释放通道。

CAN 通道打开后会自动持续接收全部已打开通道。发送和接收记录合并显示，可按 CAN0/CAN1、十六进制 ID 和 TX/RX 方向筛选。帧类型明确显示为“标准帧”或“扩展帧”，避免把旧日志中的 `S`/`X` 类型前缀误认为 ID 内容。单帧 Data 可使用连续十六进制或分隔格式；数据长度单独设置，短数据自动补 00。发送间隔为 0 时发送一次，大于 0 时循环发送，使用独立按钮停止。本版 CAN 页面暂不提供 DCDC 控制或模板组发送。

## 构建和运行

开发机需要 Visual Studio 2022、.NET 桌面开发工作负载和 .NET Framework 4.8 Developer Pack。解决方案只提供 x86 配置。

```powershell
msbuild .\NinOne.sln /m /p:Configuration=Release /p:Platform=x86
.\tests\NinOne.SmokeTests\bin\x86\Release\net48\NinOne.SmokeTests.exe
.\src\NinOne.App\bin\x86\Release\net48\NinOne.App.exe
```

发布或复制程序时，应整体复制 `src/NinOne.App/bin/x86/Release/net48`。其中必须保留 `config` 和 `vendor/ZlgCan/x86` 子目录。日志写在程序目录下的 `logs`，示波器原始波形和截图写在 `logs/Captures`。

## 现场前置条件

1. 上位机有 `192.168.4.100` 网卡地址，并能访问 PLC `.101`、N69206 `.102` 和示波器 `.128`；这些地址作为对应页面的默认值显示，可在连接前修改。
2. PLC 的 TCP 102 端口可连接；程序内置 S7 客户端，不需要安装 NI OPC Servers。
3. 在 Windows 设备管理器核对实际 COM 号，并在 GPD、GDM、PA 页面填写；默认显示 GPD `COM12`、GDM `COM13`、PA333H `COM4`，不再从 INI 读取。
4. ZLG x86 驱动已安装，实际使用的 CANFD 100U 或 200U 可被系统识别；程序目录包含配套 DLL 和 `kerneldlls`。
5. 初次通电先逐台做最小闭环。PLC 的 K3～K7 在 ON 前由界面二次确认。

可运行只读预检：

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\HardwarePreflight.ps1
```

程序退出时依次停止示波器、关闭电子负载与辅助电源、按 K16→K1 关闭 PLC 输出，然后反向释放各设备连接。退出过程不会发送隐藏的 DCDC CAN 报文。任何通讯失败都会显示错误并写入日志；高风险 PLC 输出只在写后回读一致时更新为成功。

## 离线验证范围

`NinOne.SmokeTests` 会验证界面默认配置、K1～K16 点位、K3～K7 二次确认标记、CAN 连续十六进制输入、标准/扩展帧明示格式、收发筛选入口、退出时不发送 DCDC 报文、ZLG 100U/200U 型号映射、经典 CAN/CAN FD 原生结构尺寸、GPD 二进制状态字节、TCP 超时释放、N69206 厂商 Modbus 报文样例、已从 INI 移除的网络/串口/量程项，以及生产项目不存在数据库依赖。

离线测试不会打开串口、PLC 网络、ZLG 设备或向现场仪器发送命令。真机结果填写在现场验收记录中。
