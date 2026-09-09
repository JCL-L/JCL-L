using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NinOne.Application.Devices;
using NinOne.Domain.Configuration;
using NinOne.Domain.Devices;
using NinOne.Drivers.Common;

namespace NinOne.Drivers.Zds2024CPlus
{
    public sealed class Zds2024CPlusService : RealDeviceServiceBase, IOscilloscopeService
    {
        private TcpScpiTransport _transport;
        private OscilloscopeConfig _activeConfig;

        public Zds2024CPlusService(OscilloscopeConfig config)
            : base("ZDS2024C Plus")
        {
            _activeConfig = config ?? throw new ArgumentNullException(nameof(config));
        }

        protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
        {
            if (_transport != null) _transport.Dispose();
            _transport = new TcpScpiTransport(_activeConfig.Host, _activeConfig.Port, _activeConfig.TimeoutMilliseconds);
            await _transport.OpenAsync(cancellationToken).ConfigureAwait(false);
            var identity = await QueryAsync("*IDN?", cancellationToken).ConfigureAwait(false);
            if (identity.IndexOf("ZDS", StringComparison.OrdinalIgnoreCase) < 0) throw new InvalidOperationException("TCP 端点返回的设备不是 ZDS 示波器：" + identity);
        }

        protected override Task DisconnectCoreAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _transport == null ? Task.CompletedTask : _transport.CloseAsync();
        }

        public Task ApplyConfigAsync(OscilloscopeConfig config, CancellationToken cancellationToken)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            if (config.SampleDepth != 14000) throw new InvalidOperationException("当前现场验证配置要求示波器采样深度固定为 14000。");
            return ExclusiveAsync(async () =>
            {
                EnsureConnected();
                await WriteAsync(":ACQuire:MDEPth " + config.SampleDepth.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
                await WriteAsync(":ACQuire:TYPE " + NormalizeToken(config.AcquisitionMode), cancellationToken).ConfigureAwait(false);
                await WriteAsync(":ACQuire:AVERages " + config.AverageCount.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
                await WriteAsync(":ACQuire:AUTOroll " + OnOff(config.RollingMode), cancellationToken).ConfigureAwait(false);
                await WriteAsync(":TIMebase:MODE " + NormalizeToken(config.TimeBaseMode), cancellationToken).ConfigureAwait(false);
                await WriteAsync(":TIMebase:SCALe " + config.TimeBase, cancellationToken).ConfigureAwait(false);
                await WriteAsync(":TIMebase:OFFSet " + config.HorizontalOffset, cancellationToken).ConfigureAwait(false);
                foreach (var channel in config.Channels)
                {
                    var prefix = ":CHANnel" + channel.Number.ToString(CultureInfo.InvariantCulture);
                    await WriteAsync(prefix + ":DISPlay " + OnOff(channel.Enabled), cancellationToken).ConfigureAwait(false);
                    await WriteAsync(prefix + ":SCALe " + Format(channel.VerticalScale), cancellationToken).ConfigureAwait(false);
                    await WriteAsync(prefix + ":OFFSet " + Format(channel.VerticalOffset), cancellationToken).ConfigureAwait(false);
                    await WriteAsync(prefix + ":COUPling " + NormalizeToken(channel.Coupling), cancellationToken).ConfigureAwait(false);
                    await WriteAsync(prefix + ":BWLimit " + NormalizeToken(channel.BandwidthLimit), cancellationToken).ConfigureAwait(false);
                    await WriteAsync(prefix + ":UNITs " + NormalizeToken(channel.Unit), cancellationToken).ConfigureAwait(false);
                    await WriteAsync(prefix + ":PROBe " + Format(channel.ProbeRatio), cancellationToken).ConfigureAwait(false);
                    await WriteAsync(prefix + ":INVert " + OnOff(channel.Inverted), cancellationToken).ConfigureAwait(false);
                }
                _activeConfig = config;
            }, cancellationToken);
        }

        public Task RunAsync(CancellationToken cancellationToken)
        {
            return SendSimpleAsync(":RUN", cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return SendSimpleAsync(":STOP", cancellationToken);
        }

        public Task ResetAsync(CancellationToken cancellationToken)
        {
            return SendSimpleAsync("*RST", cancellationToken);
        }

        public Task<WaveformSet> ReadWaveformsAsync(CancellationToken cancellationToken)
        {
            return ExclusiveAsync(async () =>
            {
                EnsureConnected();
                var channels = _activeConfig.Channels.Where(channel => channel.Enabled).Select(channel => channel.Number).ToArray();
                if (channels.Length == 0) throw new InvalidOperationException("示波器配置中没有启用的通道。");
                await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                var channelList = string.Join(",", channels.Select(channel => "CHANnel" + channel.ToString(CultureInfo.InvariantCulture)));
                var command = ":GLOBal:MULTiwave? MEMOry," + channelList;
                RaiseTrace("TX", command);
                var bytes = await _transport.QueryLittleEndianLengthBlockAsync(command, 128 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                RaiseTrace("RX", "WFM " + bytes.Length.ToString(CultureInfo.InvariantCulture) + " bytes");
                return new WaveformSet
                {
                    RawWfmBytes = bytes,
                    ConfiguredSampleDepth = _activeConfig.SampleDepth,
                    EnabledChannels = channels
                };
            }, cancellationToken);
        }

        public Task<byte[]> CaptureBitmapAsync(CancellationToken cancellationToken)
        {
            return ExclusiveAsync(async () =>
            {
                EnsureConnected();
                const string command = ":DISPlay:DATA?";
                RaiseTrace("TX", command);
                var bytes = await _transport.QueryDefiniteBlockAsync(command, cancellationToken).ConfigureAwait(false);
                RaiseTrace("RX", "BMP " + bytes.Length.ToString(CultureInfo.InvariantCulture) + " bytes");
                return bytes;
            }, cancellationToken);
        }

        private Task SendSimpleAsync(string command, CancellationToken cancellationToken)
        {
            return ExclusiveAsync(async () =>
            {
                EnsureConnected();
                await WriteAsync(command, cancellationToken).ConfigureAwait(false);
            }, cancellationToken);
        }

        private async Task WriteAsync(string command, CancellationToken cancellationToken)
        {
            RaiseTrace("TX", command);
            await _transport.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        }

        private async Task<string> QueryAsync(string command, CancellationToken cancellationToken)
        {
            RaiseTrace("TX", command);
            var response = await _transport.QueryAsync(command, cancellationToken).ConfigureAwait(false);
            RaiseTrace("RX", response);
            return response;
        }

        private static string OnOff(bool value)
        {
            return value ? "ON" : "OFF";
        }

        private static string Format(decimal value)
        {
            return value.ToString("0.########", CultureInfo.InvariantCulture);
        }

        private static string NormalizeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("示波器枚举配置不能为空。", nameof(value));
            return value.Replace(" ", string.Empty).ToUpperInvariant();
        }

        protected override void DisposeCore()
        {
            if (_transport != null) _transport.Dispose();
        }
    }
}
