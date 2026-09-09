using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NinOne.Application.Devices;
using NinOne.Domain.Configuration;
using NinOne.Domain.Devices;
using NinOne.Drivers.Common;

namespace NinOne.Drivers.ZlgCan
{
    public sealed class ZlgDcdcCanService : RealDeviceServiceBase, IDcdcCanService
    {
        private readonly CanConfig _config;
        private readonly CanCsvProfile _profile;
        private readonly Dictionary<int, IntPtr> _channelHandles = new Dictionary<int, IntPtr>();
        private IntPtr _deviceHandle;

        public ZlgDcdcCanService(CanConfig config)
            : base("CAN 适配器")
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
            _profile = CanCsvProfile.Load(config.TemplateCsvPath, config.SignalCsvPath);
            Templates = _profile.Templates;
        }

        public IReadOnlyList<CanFrameTemplate> Templates { get; }
        public IReadOnlyList<int> OpenChannels
        {
            get
            {
                lock (_channelHandles) return _channelHandles.Keys.OrderBy(value => value).ToArray();
            }
        }

        protected override Task ConnectCoreAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var vendorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "vendor", "ZlgCan", "x86");
                if (!File.Exists(Path.Combine(vendorPath, "zlgcan.dll"))) throw new FileNotFoundException("找不到 ZLG x86 驱动 zlgcan.dll。", Path.Combine(vendorPath, "zlgcan.dll"));
                if (!ZlgNative.SetDllDirectory(vendorPath)) throw new InvalidOperationException("无法设置 ZLG DLL 搜索目录：" + vendorPath);
                var deviceType = ResolveDeviceType(_config.AdapterModel);
                var hardwareChannelCount = ResolveChannelCount(_config.AdapterModel);
                if (_config.ChannelCount != hardwareChannelCount) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "{0} 必须使用 {1} 个通道。", _config.AdapterModel, hardwareChannelCount));
                lock (_channelHandles) _channelHandles.Clear();
                if (_deviceHandle != IntPtr.Zero)
                {
                    try { ZlgNative.ZCAN_CloseDevice(_deviceHandle); } catch { }
                    _deviceHandle = IntPtr.Zero;
                }
                _deviceHandle = ZlgNative.ZCAN_OpenDevice(deviceType, (uint)_config.DeviceIndex, 0);
                if (_deviceHandle == IntPtr.Zero) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "ZCAN_OpenDevice 失败：{0}，设备类型 {1}，索引 {2}。", _config.AdapterModel, deviceType, _config.DeviceIndex));
                try
                {
                    for (var channelIndex = 0; channelIndex < _config.ChannelCount; channelIndex++) ConfigureChannelBeforeInitialization(channelIndex);
                }
                catch
                {
                    try { ZlgNative.ZCAN_CloseDevice(_deviceHandle); } catch { }
                    _deviceHandle = IntPtr.Zero;
                    throw;
                }
                RaiseTrace("STATUS", string.Format(CultureInfo.InvariantCulture, "{0} 设备已连接（类型 {1}/{2}），CAN0～CAN{3} 波特率已预配置；可分别打开或同时打开", _config.AdapterModel, deviceType, _config.DeviceIndex, _config.ChannelCount - 1));
            }, cancellationToken);
        }

        protected override Task DisconnectCoreAsync(CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                IntPtr[] handles;
                lock (_channelHandles)
                {
                    handles = _channelHandles.Values.Where(handle => handle != IntPtr.Zero).ToArray();
                    _channelHandles.Clear();
                }
                foreach (var handle in handles)
                {
                    try { ZlgNative.ZCAN_ResetCAN(handle); } catch { }
                }
                var deviceHandle = _deviceHandle;
                _deviceHandle = IntPtr.Zero;
                if (deviceHandle != IntPtr.Zero)
                {
                    try { ZlgNative.ZCAN_CloseDevice(deviceHandle); } catch { }
                }
            }, cancellationToken);
        }

        public Task OpenChannelAsync(int channelIndex, CancellationToken cancellationToken)
        {
            ValidateChannelIndex(channelIndex);
            return ExclusiveAsync(() => Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureConnected();
                OpenChannelCore(channelIndex);
            }, cancellationToken), cancellationToken);
        }

        public Task CloseChannelAsync(int channelIndex, CancellationToken cancellationToken)
        {
            ValidateChannelIndex(channelIndex);
            return ExclusiveAsync(() => Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                EnsureConnected();
                CloseChannelCore(channelIndex);
            }, cancellationToken), cancellationToken);
        }

        public Task SendAsync(int channelIndex, CanFrame frame, CancellationToken cancellationToken)
        {
            ValidateChannelIndex(channelIndex);
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (frame.Data == null || frame.Data.Length > (frame.IsCanFd ? 64 : 8)) throw new ArgumentException(frame.IsCanFd ? "CAN FD 数据长度必须为 0～64。" : "经典 CAN 数据长度必须为 0～8。", nameof(frame));
            if (frame.IsCanFd && _config.UseClassicCan) throw new InvalidOperationException("通道当前按经典 CAN 打开，不能发送 CAN FD 帧。");
            if (frame.IsCanFd && frame.IsRemote) throw new InvalidOperationException("CAN FD 不支持远程帧。");
            if ((!frame.IsExtended && frame.Id > 0x7FF) || (frame.IsExtended && frame.Id > ZlgNative.IdMask)) throw new ArgumentOutOfRangeException(nameof(frame), "CAN ID 超出范围。");
            return ExclusiveAsync(() => SendCoreAsync(channelIndex, frame, cancellationToken), cancellationToken);
        }

        public Task SendTemplateGroupAsync(int channelIndex, int group, CancellationToken cancellationToken)
        {
            ValidateChannelIndex(channelIndex);
            return ExclusiveAsync(async () =>
            {
                EnsureChannelAndOnline(channelIndex);
                var frames = Templates.Where(template => template.Group == group).ToArray();
                if (frames.Length == 0) throw new ArgumentOutOfRangeException(nameof(group), "send1.csv 中没有该模板组。");
                foreach (var template in frames)
                {
                    await SendCoreAsync(channelIndex, Clone(template.Frame), cancellationToken).ConfigureAwait(false);
                }
            }, cancellationToken);
        }

        public Task SetDcdcRunAsync(int channelIndex, bool enabled, CancellationToken cancellationToken)
        {
            ValidateChannelIndex(channelIndex);
            return ExclusiveAsync(async () =>
            {
                EnsureChannelAndOnline(channelIndex);
                var controlFrame = BuildDcdcControlFrame(enabled);
                var controlTemplate = Templates.First(item => item.Frame.Id == controlFrame.Id);
                var groupFrames = Templates.Where(item => item.Group == controlTemplate.Group).ToArray();
                foreach (var template in groupFrames)
                {
                    await SendCoreAsync(channelIndex, template.Frame.Id == controlFrame.Id ? controlFrame : Clone(template.Frame), cancellationToken).ConfigureAwait(false);
                }
                RaiseTrace("STATUS", string.Format(CultureInfo.InvariantCulture, "CAN{0} DCDC开机={1}，P1={2}，P2={3}；已发送模板组{4}共{5}帧", channelIndex, enabled ? 1 : 0, _config.DcdcP1, _config.DcdcP2, controlTemplate.Group, groupFrames.Length));
            }, cancellationToken);
        }

        internal CanFrame BuildDcdcControlFrame(bool enabled)
        {
            var runSignal = RequireSignal("DCDC开机");
            var p1Signal = RequireSignal("DCDCP1");
            var p2Signal = RequireSignal("DCDCP2");
            if (p1Signal.FrameId != runSignal.FrameId || p2Signal.FrameId != runSignal.FrameId) throw new InvalidDataException("DCDC开机、P1、P2 不在同一个 CAN 帧中。");
            var template = Templates.FirstOrDefault(item => item.Frame.Id == runSignal.FrameId);
            if (template == null) throw new InvalidDataException("send1.csv 中找不到 DCDC 控制帧 0x" + runSignal.FrameId.ToString("X", CultureInfo.InvariantCulture) + "。");
            var frame = Clone(template.Frame);
            SetMotorolaSignal(frame.Data, runSignal, enabled ? 1m : 0m);
            SetMotorolaSignal(frame.Data, p1Signal, _config.DcdcP1);
            SetMotorolaSignal(frame.Data, p2Signal, _config.DcdcP2);
            return frame;
        }

        private CanSignalDefinition RequireSignal(string name)
        {
            var signal = _profile.Signals.FirstOrDefault(item => string.Equals(item.Name, name, StringComparison.Ordinal));
            if (signal == null) throw new InvalidDataException("send2.csv 中找不到 " + name + " 信号定义。");
            return signal;
        }

        public Task<IReadOnlyList<CanFrame>> ReceiveAsync(int channelIndex, int maximumFrames, int waitMilliseconds, CancellationToken cancellationToken)
        {
            ValidateChannelIndex(channelIndex);
            if (maximumFrames < 1 || maximumFrames > 10000) throw new ArgumentOutOfRangeException(nameof(maximumFrames));
            if (waitMilliseconds < 0 || waitMilliseconds > 60000) throw new ArgumentOutOfRangeException(nameof(waitMilliseconds));
            return ExclusiveAsync<IReadOnlyList<CanFrame>>(() => Task.Run<IReadOnlyList<CanFrame>>(() =>
            {
                var channelHandle = EnsureChannelAndOnline(channelIndex);
                var result = new List<CanFrame>();
                var stopwatch = Stopwatch.StartNew();
                do
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var remaining = waitMilliseconds - (int)stopwatch.ElapsedMilliseconds;
                    var chunkWait = waitMilliseconds == 0 ? 0 : Math.Max(1, Math.Min(100, remaining));
                    if (_config.UseClassicCan)
                    {
                        ReceiveClassicCore(channelHandle, channelIndex, maximumFrames, chunkWait, result);
                    }
                    else
                    {
                        ReceiveFdCore(channelHandle, channelIndex, maximumFrames, chunkWait, result);
                        if (result.Count < maximumFrames) ReceiveClassicCore(channelHandle, channelIndex, maximumFrames, 0, result);
                    }
                    if (result.Count >= maximumFrames || waitMilliseconds == 0) break;
                } while (stopwatch.ElapsedMilliseconds < waitMilliseconds);
                return result;
            }, cancellationToken), cancellationToken);
        }

        private Task SendCoreAsync(int channelIndex, CanFrame frame, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var channelHandle = EnsureChannelAndOnline(channelIndex);
                var id = frame.Id;
                if (frame.IsExtended) id |= ZlgNative.ExtendedFlag;
                if (frame.IsRemote) id |= ZlgNative.RemoteFlag;
                frame.ChannelIndex = channelIndex;
                RaiseTrace("TX", frame.ToString());
                uint sent;
                if (frame.IsCanFd)
                {
                    var payload = new byte[64];
                    Array.Copy(frame.Data, payload, frame.Data.Length);
                    var flags = (byte)0;
                    if (frame.BitRateSwitch) flags |= ZlgNative.CanFdBitRateSwitch;
                    if (frame.ErrorStateIndicator) flags |= ZlgNative.CanFdErrorStateIndicator;
                    var nativeFd = new ZlgNative.TransmitFdData
                    {
                        Frame = new ZlgNative.NativeCanFdFrame { CanId = id, Length = (byte)frame.Data.Length, Flags = flags, Data = payload },
                        TransmitType = 0
                    };
                    sent = ZlgNative.ZCAN_TransmitFD(channelHandle, ref nativeFd, 1);
                }
                else
                {
                    var payload = new byte[8];
                    Array.Copy(frame.Data, payload, frame.Data.Length);
                    var native = new ZlgNative.TransmitData
                    {
                        Frame = new ZlgNative.NativeCanFrame { CanId = id, CanDlc = (byte)frame.Data.Length, Data = payload },
                        TransmitType = 0
                    };
                    sent = ZlgNative.ZCAN_Transmit(channelHandle, ref native, 1);
                }
                if (sent != 1)
                {
                    var deviceStatus = _deviceHandle == IntPtr.Zero ? ZlgNative.StatusError : ZlgNative.ZCAN_IsDeviceOnLine(_deviceHandle);
                    throw new InvalidOperationException("CAN" + channelIndex + " " + (frame.IsCanFd ? "ZCAN_TransmitFD" : "ZCAN_Transmit") + " 失败，期望发送 1 帧，实际 " + sent + " 帧；设备状态=" + deviceStatus + "；通道保持打开；" + ReadChannelDiagnostic(channelHandle));
                }
            }, cancellationToken);
        }

        private void ReceiveClassicCore(IntPtr channelHandle, int channelIndex, int maximumFrames, int waitMilliseconds, List<CanFrame> result)
        {
            var capacity = Math.Min(maximumFrames - result.Count, 256);
            if (capacity <= 0) return;
            var native = new ZlgNative.ReceiveData[capacity];
            for (var index = 0; index < native.Length; index++) native[index].Frame.Data = new byte[8];
            var count = ZlgNative.ZCAN_Receive(channelHandle, native, (uint)native.Length, waitMilliseconds);
            for (var index = 0; index < count && index < native.Length; index++)
            {
                var raw = native[index].Frame;
                var frame = new CanFrame
                {
                    Id = raw.CanId & ZlgNative.IdMask,
                    IsExtended = (raw.CanId & ZlgNative.ExtendedFlag) != 0,
                    IsRemote = (raw.CanId & ZlgNative.RemoteFlag) != 0,
                    Data = (raw.Data ?? new byte[0]).Take(raw.CanDlc).ToArray(),
                    TimestampMicroseconds = native[index].Timestamp,
                    ChannelIndex = channelIndex
                };
                result.Add(frame);
                RaiseTrace("RX", frame.ToString());
            }
        }

        private void ReceiveFdCore(IntPtr channelHandle, int channelIndex, int maximumFrames, int waitMilliseconds, List<CanFrame> result)
        {
            var capacity = Math.Min(maximumFrames - result.Count, 256);
            if (capacity <= 0) return;
            var native = new ZlgNative.ReceiveFdData[capacity];
            for (var index = 0; index < native.Length; index++) native[index].Frame.Data = new byte[64];
            var count = ZlgNative.ZCAN_ReceiveFD(channelHandle, native, (uint)native.Length, waitMilliseconds);
            for (var index = 0; index < count && index < native.Length; index++)
            {
                var raw = native[index].Frame;
                var frame = new CanFrame
                {
                    Id = raw.CanId & ZlgNative.IdMask,
                    IsExtended = (raw.CanId & ZlgNative.ExtendedFlag) != 0,
                    IsCanFd = true,
                    BitRateSwitch = (raw.Flags & ZlgNative.CanFdBitRateSwitch) != 0,
                    ErrorStateIndicator = (raw.Flags & ZlgNative.CanFdErrorStateIndicator) != 0,
                    Data = (raw.Data ?? new byte[0]).Take(raw.Length).ToArray(),
                    TimestampMicroseconds = native[index].Timestamp,
                    ChannelIndex = channelIndex
                };
                result.Add(frame);
                RaiseTrace("RX", frame.ToString());
            }
        }

        private string ReadChannelDiagnostic(IntPtr channelHandle)
        {
            try
            {
                var parts = new List<string>();
                ZlgNative.ChannelErrorInfo error;
                if (ZlgNative.ZCAN_ReadChannelErrInfo(channelHandle, out error) == ZlgNative.StatusOk)
                {
                    parts.Add("CAN错误码=0x" + error.ErrorCode.ToString("X8", CultureInfo.InvariantCulture));
                }
                ZlgNative.ChannelStatus status;
                if (ZlgNative.ZCAN_ReadChannelStatus(channelHandle, out status) == ZlgNative.StatusOk)
                {
                    parts.Add(string.Format(CultureInfo.InvariantCulture, "状态=0x{0:X2}，REC={1}，TEC={2}，ECC=0x{3:X2}", status.Status, status.ReceiveErrorCounter, status.TransmitErrorCounter, status.ErrorCodeCapture));
                }
                return parts.Count == 0 ? "无法读取 CAN 控制器诊断信息" : string.Join("；", parts);
            }
            catch (Exception exception)
            {
                return "读取 CAN 控制器诊断信息失败：" + exception.Message;
            }
        }

        private IntPtr EnsureChannelAndOnline(int channelIndex)
        {
            EnsureConnected();
            IntPtr channelHandle;
            IntPtr deviceHandle;
            lock (_channelHandles)
            {
                deviceHandle = _deviceHandle;
                if (deviceHandle == IntPtr.Zero || !_channelHandles.TryGetValue(channelIndex, out channelHandle) || channelHandle == IntPtr.Zero) throw new InvalidOperationException("CAN" + channelIndex + " 尚未打开。");
            }
            var deviceStatus = ZlgNative.ZCAN_IsDeviceOnLine(deviceHandle);
            if (deviceStatus != ZlgNative.StatusOnline)
            {
                throw new InvalidOperationException("ZLG CAN 设备当前状态=" + deviceStatus + "；通道保持打开。若 USB 已物理断开，请点击“断开适配器”后重新连接。");
            }
            return channelHandle;
        }

        private void OpenChannelCore(int channelIndex)
        {
            ValidateChannelIndex(channelIndex);
            if (_deviceHandle == IntPtr.Zero) throw new InvalidOperationException("ZLG CAN 设备尚未打开。");
            lock (_channelHandles)
            {
                if (_channelHandles.ContainsKey(channelIndex))
                {
                    var deviceStatus = ZlgNative.ZCAN_IsDeviceOnLine(_deviceHandle);
                    if (deviceStatus != ZlgNative.StatusOnline)
                    {
                        throw new InvalidOperationException("ZLG CAN 设备当前状态=" + deviceStatus + "；CAN" + channelIndex + " 通道状态保持不变。请手动断开适配器后重新连接。");
                    }
                    RaiseTrace("STATUS", "CAN" + channelIndex + " 已处于打开状态");
                    return;
                }
            }
            var init = BuildCanFdHardwareInitConfig();
            var handle = ZlgNative.ZCAN_InitCAN(_deviceHandle, (uint)channelIndex, ref init);
            if (handle == IntPtr.Zero) throw new InvalidOperationException("ZCAN_InitCAN 失败：CAN" + channelIndex + "。");
            try
            {
                Check(ZlgNative.ZCAN_SetValue(_deviceHandle, ResistancePath(channelIndex), _config.TerminationEnabled ? "1" : "0"), "CAN" + channelIndex + " 设置 120 Ω 终端电阻");
                Check(ZlgNative.ZCAN_ClearBuffer(handle), "CAN" + channelIndex + " 清空接收缓冲区");
                Check(ZlgNative.ZCAN_StartCAN(handle), "CAN" + channelIndex + " 启动通道");
                lock (_channelHandles) _channelHandles.Add(channelIndex, handle);
            }
            catch
            {
                try { ZlgNative.ZCAN_ResetCAN(handle); } catch { }
                throw;
            }
            RaiseTrace("STATUS", _config.UseClassicCan
                ? string.Format(CultureInfo.InvariantCulture, "CAN{0} 已打开，经典 CAN，仲裁 {1} kbps，120Ω={2}", channelIndex, _config.ArbitrationBitRate / 1000, _config.TerminationEnabled)
                : string.Format(CultureInfo.InvariantCulture, "CAN{0} 已打开，CAN FD，仲裁 {1} kbps，数据 {2} kbps，120Ω={3}", channelIndex, _config.ArbitrationBitRate / 1000, _config.DataBitRate / 1000, _config.TerminationEnabled));
        }

        private void CloseChannelCore(int channelIndex)
        {
            IntPtr handle;
            lock (_channelHandles)
            {
                if (!_channelHandles.TryGetValue(channelIndex, out handle))
                {
                    RaiseTrace("STATUS", "CAN" + channelIndex + " 已关闭");
                    return;
                }
                _channelHandles.Remove(channelIndex);
            }
            if (_deviceHandle == IntPtr.Zero || ZlgNative.ZCAN_IsDeviceOnLine(_deviceHandle) != ZlgNative.StatusOnline)
            {
                RaiseTrace("STATUS", "CAN" + channelIndex + " 设备已离线，旧通道状态已清除");
                return;
            }
            Check(ZlgNative.ZCAN_ResetCAN(handle), "CAN" + channelIndex + " 停止通道");
            RaiseTrace("STATUS", "CAN" + channelIndex + " 已独立关闭");
        }

        private void ConfigureChannelBeforeInitialization(int channelIndex)
        {
            Check(ZlgNative.ZCAN_SetValue(_deviceHandle, ProtocolPath(channelIndex), _config.UseClassicCan ? "0" : "1"), "CAN" + channelIndex + " 预配置总线协议");
            Check(ZlgNative.ZCAN_SetValue(_deviceHandle, ArbitrationBitRatePath(channelIndex), _config.ArbitrationBitRate.ToString(CultureInfo.InvariantCulture)), "CAN" + channelIndex + " 预配置仲裁域波特率");
            if (!_config.UseClassicCan)
            {
                Check(ZlgNative.ZCAN_SetValue(_deviceHandle, DataBitRatePath(channelIndex), _config.DataBitRate.ToString(CultureInfo.InvariantCulture)), "CAN" + channelIndex + " 预配置数据域波特率");
            }
        }

        internal static ZlgNative.ChannelInitConfig BuildCanFdHardwareInitConfig()
        {
            return new ZlgNative.ChannelInitConfig
            {
                // 100U/200U 是 CAN FD 硬件；即使协议设为经典 CAN，can_type 也必须是 TYPE_CANFD。
                CanType = ZlgNative.CanTypeFd,
                AcceptanceCode = 0,
                AcceptanceMask = uint.MaxValue,
                FdFilter = 0,
                FdMode = 0
            };
        }

        internal static string ProtocolPath(int channelIndex)
        {
            return channelIndex.ToString(CultureInfo.InvariantCulture) + "/protocol";
        }

        internal static string ArbitrationBitRatePath(int channelIndex)
        {
            return channelIndex.ToString(CultureInfo.InvariantCulture) + "/canfd_abit_baud_rate";
        }

        internal static string DataBitRatePath(int channelIndex)
        {
            return channelIndex.ToString(CultureInfo.InvariantCulture) + "/canfd_dbit_baud_rate";
        }

        internal static string ResistancePath(int channelIndex)
        {
            return channelIndex.ToString(CultureInfo.InvariantCulture) + "/initenal_resistance";
        }

        private void ValidateChannelIndex(int channelIndex)
        {
            if (channelIndex < 0 || channelIndex >= _config.ChannelCount) throw new ArgumentOutOfRangeException(nameof(channelIndex), "通道必须为 CAN0～CAN" + (_config.ChannelCount - 1) + "。");
        }

        internal static void SetMotorolaSignal(byte[] data, CanSignalDefinition signal, decimal physicalValue)
        {
            var rawDecimal = decimal.Round((physicalValue - signal.Offset) / signal.Factor, 0, MidpointRounding.AwayFromZero);
            if (rawDecimal < 0 || rawDecimal > ulong.MaxValue) throw new ArgumentOutOfRangeException(nameof(physicalValue), "CAN 信号物理值超出无符号范围。");
            var raw = (ulong)rawDecimal;
            var maximum = signal.BitLength == 64 ? ulong.MaxValue : (1UL << signal.BitLength) - 1;
            if (raw > maximum) throw new ArgumentOutOfRangeException(nameof(physicalValue), "CAN 信号值超出位长度。");
            var bit = signal.StartBit;
            for (var valueBit = signal.BitLength - 1; valueBit >= 0; valueBit--)
            {
                var byteIndex = bit / 8;
                var bitIndex = bit % 8;
                if (byteIndex < 0 || byteIndex >= data.Length) throw new InvalidDataException("CAN Motorola 信号定义超出帧长度：" + signal.Name);
                var mask = (byte)(1 << bitIndex);
                if (((raw >> valueBit) & 1UL) != 0) data[byteIndex] |= mask;
                else data[byteIndex] &= (byte)~mask;
                bit = bitIndex == 0 ? bit + 15 : bit - 1;
            }
        }

        private static CanFrame Clone(CanFrame source)
        {
            return new CanFrame { Id = source.Id, IsExtended = source.IsExtended, IsRemote = source.IsRemote, IsCanFd = source.IsCanFd, BitRateSwitch = source.BitRateSwitch, ErrorStateIndicator = source.ErrorStateIndicator, Data = (byte[])source.Data.Clone() };
        }

        internal static uint ResolveDeviceType(string adapterModel)
        {
            var normalized = NormalizeAdapterModel(adapterModel);
            if (normalized.Contains("200U")) return ZlgNative.UsbCanFd200U;
            if (normalized.Contains("100U")) return ZlgNative.UsbCanFd100U;
            if (normalized.Contains("VN1640A") || normalized.Contains("VECTOR")) throw new NotSupportedException("Vector VN1640A 选项已保留，当前版本尚未接入 Vector XL Driver Library。");
            throw new NotSupportedException("不支持的 CAN 适配器型号：" + adapterModel);
        }

        internal static int ResolveChannelCount(string adapterModel)
        {
            var deviceType = ResolveDeviceType(adapterModel);
            return deviceType == ZlgNative.UsbCanFd100U ? 1 : 2;
        }

        private static string NormalizeAdapterModel(string value)
        {
            return (value ?? string.Empty).Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty).ToUpperInvariant();
        }

        private static void Check(uint status, string operation)
        {
            if (status != ZlgNative.StatusOk) throw new InvalidOperationException(operation + "失败，ZLG 状态=" + status + "。");
        }
    }
}
