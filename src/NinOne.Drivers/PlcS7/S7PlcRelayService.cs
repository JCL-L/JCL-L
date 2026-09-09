using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NinOne.Application.Devices;
using NinOne.Domain.Configuration;
using NinOne.Domain.Devices;
using S7.Net;
using S7.Net.Protocol;

namespace NinOne.Drivers.PlcS7
{
    public sealed class S7PlcRelayService : IPlcRelayService
    {
        private static readonly IReadOnlyDictionary<string, string> RelayAddresses = RelayDefinition.FirstStage
            .ToDictionary(item => item.Id, item => item.PlcAddress, StringComparer.OrdinalIgnoreCase);

        private readonly PlcConfig _config;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private Plc _plc;
        private string _activeProfile;
        private bool _disposed;

        public S7PlcRelayService(PlcConfig config)
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public bool IsConnected { get; private set; }
        public event EventHandler<DeviceTraceEventArgs> Trace;

        public async Task ConnectAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                if (IsConnected) return;
                await Task.Run((Action)ConnectCore, cancellationToken).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        public async Task DisconnectAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try { await Task.Run((Action)DisconnectCore, cancellationToken).ConfigureAwait(false); }
            finally { _gate.Release(); }
        }

        public async Task<bool> ReadRelayAsync(string relayId, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureConnected();
                return await Task.Run(() => ReadCore(relayId), cancellationToken).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        public async Task<IReadOnlyDictionary<string, bool>> ReadAllAsync(CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureConnected();
                return await Task.Run<IReadOnlyDictionary<string, bool>>(() =>
                {
                    var result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
                    foreach (var relay in RelayDefinition.FirstStage) result[relay.Id] = ReadCore(relay.Id);
                    return result;
                }, cancellationToken).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        public async Task WriteRelayAsync(string relayId, bool state, CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                EnsureConnected();
                await Task.Run(() =>
                {
                    WriteCore(relayId, state);
                    var actual = ReadCore(relayId);
                    if (actual != state)
                    {
                        throw new InvalidOperationException(relayId + " 写后回读不一致：期望 " + state + "，实际 " + actual + "。");
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            finally { _gate.Release(); }
        }

        public async Task AllOffAsync(CancellationToken cancellationToken)
        {
            for (var index = 16; index >= 1; index--)
            {
                await WriteRelayAsync("K" + index.ToString(CultureInfo.InvariantCulture), false, cancellationToken).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            DisconnectCore();
            _gate.Dispose();
            _disposed = true;
        }

        private void ConnectCore()
        {
            var failures = new List<string>();
            foreach (var candidate in BuildConnectionCandidates())
            {
                Plc plc = null;
                try
                {
                    RaiseTrace("TX", "CONNECT " + _config.PlcAddress + ":" + _config.Port + "，" + candidate.Name);
                    plc = candidate.Create();
                    plc.ReadTimeout = _config.TimeoutMilliseconds;
                    plc.WriteTimeout = _config.TimeoutMilliseconds;
                    plc.Open();
                    if (!plc.IsConnected) throw new InvalidOperationException("S7 连接未进入已连接状态。");

                    var q0 = Convert.ToBoolean(plc.Read("Q0.0"), CultureInfo.InvariantCulture);
                    var q8 = Convert.ToBoolean(plc.Read("Q8.0"), CultureInfo.InvariantCulture);
                    _plc = plc;
                    _activeProfile = candidate.Name;
                    IsConnected = true;
                    RaiseTrace("RX", "S7 验证读取成功：Q0.0=" + q0.ToString().ToUpperInvariant() + "，Q8.0=" + q8.ToString().ToUpperInvariant());
                    RaiseTrace("STATUS", "已直连 PLC " + _config.PlcAddress + ":" + _config.Port + "，" + _activeProfile);
                    return;
                }
                catch (Exception exception)
                {
                    failures.Add(candidate.Name + "：" + exception.Message);
                    RaiseTrace("ERROR", candidate.Name + " 失败：" + exception.Message);
                    if (plc != null)
                    {
                        try { plc.Close(); } catch { }
                        try { ((IDisposable)plc).Dispose(); } catch { }
                    }
                }
            }

            throw new InvalidOperationException(
                "PLC 的 TCP 端口可用，但 S7 握手或输出区读取失败。请检查 CPU 类型、机架/槽位和 TSAP。尝试结果："
                + string.Join("；", failures));
        }

        private IReadOnlyList<ConnectionCandidate> BuildConnectionCandidates()
        {
            if (!string.Equals(_config.LocalTsap, "AUTO", StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    new ConnectionCandidate("自定义 TSAP " + _config.LocalTsap + "→" + _config.RemoteTsap,
                        () => new Plc(_config.PlcAddress, _config.Port,
                            new TsapPair(ParseTsap(_config.LocalTsap), ParseTsap(_config.RemoteTsap))))
                };
            }

            if (string.Equals(_config.CpuType, "S7200", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { CreateDefaultCandidate(CpuType.S7200, "S7-200，TSAP 10.00→10.01") };
            }
            if (string.Equals(_config.CpuType, "S7200Smart", StringComparison.OrdinalIgnoreCase))
            {
                return new[] { CreateDefaultCandidate(CpuType.S7200Smart, "S7-200 SMART") };
            }

            return new[]
            {
                CreateDefaultCandidate(CpuType.S7200, "自动尝试 S7-200，TSAP 10.00→10.01"),
                CreateDefaultCandidate(CpuType.S7200Smart, "自动尝试 S7-200 SMART")
            };
        }

        private ConnectionCandidate CreateDefaultCandidate(CpuType cpuType, string name)
        {
            return new ConnectionCandidate(name,
                () => new Plc(cpuType, _config.PlcAddress, _config.Port, (short)_config.Rack, (short)_config.Slot));
        }

        private static Tsap ParseTsap(string text)
        {
            var parts = text.Split(new[] { '.', ':', '-' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2) throw new FormatException("TSAP 格式无效：" + text);
            byte first;
            byte second;
            if (!byte.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out first)
                || !byte.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out second))
            {
                throw new FormatException("TSAP 格式无效：" + text);
            }
            return new Tsap(first, second);
        }

        private bool ReadCore(string relayId)
        {
            var address = ResolveAddress(relayId);
            RaiseTrace("TX", "READ " + address + " (" + relayId + ")");
            var result = Convert.ToBoolean(_plc.Read(address), CultureInfo.InvariantCulture);
            RaiseTrace("RX", address + "=" + result.ToString().ToUpperInvariant());
            return result;
        }

        private void WriteCore(string relayId, bool state)
        {
            var address = ResolveAddress(relayId);
            RaiseTrace("TX", "WRITE " + address + "=" + state.ToString().ToUpperInvariant() + " (" + relayId + ")");
            _plc.Write(address, state);
            RaiseTrace("RX", "WRITE " + address + " 已确认");
        }

        private void DisconnectCore()
        {
            var plc = _plc;
            _plc = null;
            _activeProfile = null;
            IsConnected = false;
            if (plc != null)
            {
                try { plc.Close(); } catch { }
                try { ((IDisposable)plc).Dispose(); } catch { }
            }
            RaiseTrace("STATUS", "S7 PLC 已断开");
        }

        private static string ResolveAddress(string relayId)
        {
            string address;
            if (relayId == null || !RelayAddresses.TryGetValue(relayId, out address))
            {
                throw new ArgumentOutOfRangeException(nameof(relayId), relayId, "只允许 K1～K16。");
            }
            return address;
        }

        private void EnsureConnected()
        {
            ThrowIfDisposed();
            if (!IsConnected || _plc == null || !_plc.IsConnected) throw new InvalidOperationException("PLC 尚未建立 S7 直连。");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(S7PlcRelayService));
        }

        private void RaiseTrace(string direction, string text)
        {
            var handler = Trace;
            if (handler != null) handler(this, new DeviceTraceEventArgs(direction, text));
        }

        private sealed class ConnectionCandidate
        {
            public ConnectionCandidate(string name, Func<Plc> create)
            {
                Name = name;
                Create = create;
            }

            public string Name { get; }
            public Func<Plc> Create { get; }
        }
    }
}
