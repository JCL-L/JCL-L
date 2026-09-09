using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NinOne.Application.Devices;
using NinOne.Domain.Configuration;
using NinOne.Domain.Devices;
using NinOne.Drivers.Common;

namespace NinOne.Drivers.Pa333h
{
    public sealed class Pa333hService : RealDeviceServiceBase, IPowerMeterService
    {
        private readonly PowerMeterConfig _config;
        private SerialLineTransport _transport;

        public Pa333hService(PowerMeterConfig config)
            : base("PA333H")
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
        {
            if (_transport != null) _transport.Dispose();
            // PA300 通信命令手册 5.1.1：串口命令和响应以 NL/LF (0x0A) 结束。
            _transport = new SerialLineTransport(_config.PortName, _config.BaudRate, _config.TimeoutMilliseconds, "\n");
            await _transport.OpenAsync(cancellationToken).ConfigureAwait(false);
            var identity = await QueryAsync("*IDN?", cancellationToken).ConfigureAwait(false);
            if (identity.IndexOf("PA333", StringComparison.OrdinalIgnoreCase) < 0 && identity.IndexOf("PA300", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException("串口返回的设备不是 PA333H/PA300 系列：" + identity);
            }
        }

        protected override Task DisconnectCoreAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _transport == null ? Task.CompletedTask : _transport.CloseAsync();
        }

        public Task ConfigureAsync(CancellationToken cancellationToken)
        {
            return ExclusiveAsync(async () =>
            {
                EnsureConnected();
                await WriteAsync(":NUMERIC:FORMAT ASCII", cancellationToken).ConfigureAwait(false);
                await WriteAsync(":NUMERIC:NORMAL:CLEAR ALL", cancellationToken).ConfigureAwait(false);
                await WriteAsync(":NUMERIC:NORMAL:NUMBER 6", cancellationToken).ConfigureAwait(false);
                await WriteAsync(":NUMERIC:NORMAL:ITEM1 U," + _config.SourceChannel, cancellationToken).ConfigureAwait(false);
                await WriteAsync(":NUMERIC:NORMAL:ITEM2 I," + _config.SourceChannel, cancellationToken).ConfigureAwait(false);
                await WriteAsync(":NUMERIC:NORMAL:ITEM3 P," + _config.SourceChannel, cancellationToken).ConfigureAwait(false);
                await WriteAsync(":NUMERIC:NORMAL:ITEM4 U," + _config.OutputChannel, cancellationToken).ConfigureAwait(false);
                await WriteAsync(":NUMERIC:NORMAL:ITEM5 I," + _config.OutputChannel, cancellationToken).ConfigureAwait(false);
                await WriteAsync(":NUMERIC:NORMAL:ITEM6 P," + _config.OutputChannel, cancellationToken).ConfigureAwait(false);
            }, cancellationToken);
        }

        public Task<PowerMeterReading> ReadAsync(CancellationToken cancellationToken)
        {
            return ExclusiveAsync(async () =>
            {
                EnsureConnected();
                var response = await QueryAsync(":NUMERIC:NORMAL:VALUE?", cancellationToken).ConfigureAwait(false);
                var parts = response.Split(',').Select(value => value.Trim()).ToArray();
                if (parts.Length < 6) throw new FormatException("PA333H 返回值不足 6 项：" + response);
                return new PowerMeterReading
                {
                    SourceVoltage = Parse(parts[0], response),
                    SourceCurrent = Parse(parts[1], response),
                    SourcePower = Parse(parts[2], response),
                    OutputVoltage = Parse(parts[3], response),
                    OutputCurrent = Parse(parts[4], response),
                    OutputPower = Parse(parts[5], response)
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
            RaiseTrace("RX", response);
            return response;
        }

        private static decimal Parse(string value, string response)
        {
            decimal result;
            if (!decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result)) throw new FormatException("无法解析 PA333H 响应：" + response);
            return result;
        }

        protected override void DisposeCore()
        {
            if (_transport != null) _transport.Dispose();
        }
    }
}
