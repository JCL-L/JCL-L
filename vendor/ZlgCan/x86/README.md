# ZLG CAN x86 运行依赖

这些文件复制自当前 LabVIEW 工程 `NinOne_EOL_20260825V6/data`，用于 ZLG_CANFD_200U 的 32 位调用。驱动项目会把本目录完整复制到应用输出的 `vendor/ZlgCan/x86`。

| 文件 | 位数 | 文件/产品版本 | SHA-256 |
|---|---|---|---|
| zlgcan.dll | x86 | 文件未携带版本资源 | `745DBE54AA6C739436ED13049E8EF0B7B97CB981B87971D6BEC2AA3A0B2EB732` |
| zlgcan_wrap.dll | x86 | 1.0.0.1 / 20250331 | `764C199AF0F0B65EF8F358167725D2225A7241ADD1A9181ACB92F735C42722D2` |

`kerneldlls` 必须与入口 DLL 一起部署。C# P/Invoke 直接使用官方 `zlgcan.dll` 的 stdcall 接口；`zlgcan_wrap.dll` 只为原 LabVIEW 工程兼容保留。设备类型 ZCAN_USBCANFD_200U 为 41，在线状态返回值为 2。
