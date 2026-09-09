using System;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NinOne.Application.Devices;
using NinOne.Domain.Configuration;
using NinOne.Domain.Devices;
using NinOne.Drivers.Common;

namespace NinOne.Drivers.Gpd2303s
{
    public sealed class Gpd2303sService : RealDeviceServiceBase, IAuxiliaryPowerService
    {
        private readonly SerialDeviceConfig _config;
        private SerialLineTransport _transport;

        public Gpd2303sService(SerialDeviceConfig config)
            : base("GPD2303S")
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
        {
            if (_transport != null) _transport.Dispose();
            _transport = new SerialLineTransport(_config.PortName, _config.BaudRate, _config.TimeoutMilliseconds);
            await _transport.OpenAsync(cancellationToken).ConfigureAwait(false);
            var identity = await QueryAsync("*IDN?", cancellationToken).ConfigureAwait(false);
            if (identity.IndexOf("GPD", StringComparison.OrdinalIgnoreCase) < 0) throw new InvalidOperationException("串口返回的设备不是 GPD2303S：" + identity);
        }

        protected override Task DisconnectCoreAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _transport == null ? Task.CompletedTask : _transport.CloseAsync();
        }

        public Task SetAsync(int channel, decimal voltage, decimal current, CancellationToken cancellationToken)
        {
            ValidateChannel(channel);
            ValidateVoltage(voltage);
            ValidateCurrent(current);
            return ExclusiveAsync(async () =>
            {
                EnsureConnected();
                await WriteAndVerifyVoltageAsync(channel, voltage, cancellationToken).ConfigureAwait(false);
                await WriteAndVerifyCurrentAsync(channel, current, cancellationToken).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task SetVoltageAsync(int channel, decimal voltage, CancellationToken cancellationToken)
        {
            ValidateChannel(channel);
            ValidateVoltage(voltage);
            return ExclusiveAsync(async () =>
            {
                EnsureConnected();
                await WriteAndVerifyVoltageAsync(channel, voltage, cancellationToken).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task SetOutputAsync(bool enabled, CancellationToken cancellationToken)
        {
            return ExclusiveAsync(async () =>
            {
                EnsureConnected();
                await WriteAsync(enabled ? "OUT1" : "OUT0", cancellationToken).ConfigureAwait(false);
                var response = await QueryAsync("STATUS?", cancellationToken).ConfigureAwait(false);
                var actual = ParseOutputStatus(response);
                if (actual != enabled) throw new InvalidOperationException("GPD2303S 输出写后回读不一致，STATUS?=" + Escape(response) + "。");
            }, cancellationToken);
        }

        public Task<PowerSupplyReading> ReadAsync(int channel, CancellationToken cancellationToken)
        {
            ValidateChannel(channel);
            return ExclusiveAsync(async () =>
            {
                EnsureConnected();
                var setVoltage = ParseDecimal(await QueryAsync("VSET" + channel + "?", cancellationToken).ConfigureAwait(false));
                var setCurrent = ParseDecimal(await QueryAsync("ISET" + channel + "?", cancellationToken).ConfigureAwait(false));
                var outputVoltage = ParseDecimal(await QueryAsync("VOUT" + channel + "?", cancellationToken).ConfigureAwait(false));
                var outputCurrent = ParseDecimal(await QueryAsync("IOUT" + channel + "?", cancellationToken).ConfigureAwait(false));
                var status = await QueryAsync("STATUS?", cancellationToken).ConfigureAwait(false);
                return new PowerSupplyReading
                {
                    Channel = channel,
                    SetVoltage = setVoltage,
                    SetCurrent = setCurrent,
                    OutputVoltage = outputVoltage,
                    OutputCurrent = outputCurrent,
                    OutputEnabled = ParseOutputStatus(status)
                };
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
            RaiseTrace("RX", Escape(response));
            return response;
        }

        private async Task WriteAndVerifyVoltageAsync(int channel, decimal voltage, CancellationToken cancellationToken)
        {
            await WriteAsync("VSET" + channel + ":" + Format(voltage), cancellationToken).ConfigureAwait(false);
            var actual = ParseDecimal(await QueryAsync("VSET" + channel + "?", cancellationToken).ConfigureAwait(false));
            if (Math.Abs(actual - voltage) > 0.01m)
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "GPD2303S 电压写后回读不一致：期望 {0} V，实际 {1} V。", voltage, actual));
        }

        private async Task WriteAndVerifyCurrentAsync(int channel, decimal current, CancellationToken cancellationToken)
        {
            await WriteAsync("ISET" + channel + ":" + Format(current), cancellationToken).ConfigureAwait(false);
            var actual = ParseDecimal(await QueryAsync("ISET" + channel + "?", cancellationToken).ConfigureAwait(false));
            if (Math.Abs(actual - current) > 0.01m)
                throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "GPD2303S 电流上限写后回读不一致：期望 {0} A，实际 {1} A。", current, actual));
        }

        internal static bool ParseOutputStatus(string response)
        {
            // GPD STATUS? 的八个 ASCII 位按 bit0..bit7 返回，输出状态为 bit5，
            // 因而它位于字符串下标 5，而不是常规二进制文本的倒数第 6 位。
            if (Regex.IsMatch(response ?? string.Empty, "^[01]{8}$")) return response[5] == '1';
            int status;
            if (int.TryParse(response, NumberStyles.Integer, CultureInfo.InvariantCulture, out status)) return (status & 0x20) != 0;
            if (response.Length == 1) return (response[0] & 0x20) != 0;
            throw new FormatException("无法解析 GPD2303S STATUS? 响应：" + Escape(response));
        }

        private static decimal ParseDecimal(string response)
        {
            var match = Regex.Match(response ?? string.Empty, @"[-+]?\d+(?:\.\d+)?(?:[Ee][-+]?\d+)?");
            decimal value;
            if (!match.Success || !decimal.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) throw new FormatException("无法解析 GPD2303S 数值响应：" + Escape(response));
            return value;
        }

        private static string Format(decimal value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string Escape(string value)
        {
            return (value ?? string.Empty).Replace("\r", "\\r").Replace("\n", "\\n");
        }

        private static void ValidateChannel(int channel)
        {
            if (channel < 1 || channel > 2) throw new ArgumentOutOfRangeException(nameof(channel), "GPD2303S 只允许通道 1 或 2。");
        }

        private static void ValidateVoltage(decimal voltage)
        {
            if (voltage < 0 || voltage > 30) throw new ArgumentOutOfRangeException(nameof(voltage), "GPD2303S 电压必须为 0～30 V。");
        }

        private static void ValidateCurrent(decimal current)
        {
            if (current < 0 || current > 3) throw new ArgumentOutOfRangeException(nameof(current), "GPD2303S 电流上限必须为 0～3 A。");
        }

        protected override void DisposeCore()
        {
            if (_transport != null) _transport.Dispose();
        }
    }
}
