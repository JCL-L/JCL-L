using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using NinOne.Application.Devices;
using NinOne.Domain.Configuration;
using NinOne.Domain.Devices;
using NinOne.Drivers.Common;

namespace NinOne.Drivers.Gdm9061
{
    public sealed class Gdm9061Service : RealDeviceServiceBase, IMultimeterService
    {
        private readonly SerialDeviceConfig _config;
        private SerialLineTransport _transport;
        private MultimeterFunction _function = MultimeterFunction.DcVoltage;

        public Gdm9061Service(SerialDeviceConfig config)
            : base("GDM9061")
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
        {
            if (_transport != null) _transport.Dispose();
            _transport = new SerialLineTransport(_config.PortName, _config.BaudRate, _config.TimeoutMilliseconds);
            await _transport.OpenAsync(cancellationToken).ConfigureAwait(false);
            var identity = await QueryAsync("*IDN?", cancellationToken).ConfigureAwait(false);
            if (identity.IndexOf("GDM-906", StringComparison.OrdinalIgnoreCase) < 0 && identity.IndexOf("GDM906", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException("串口返回的设备不是 GDM9061：" + identity);
            }
        }

        protected override Task DisconnectCoreAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return _transport == null ? Task.CompletedTask : _transport.CloseAsync();
        }

        public Task ConfigureAsync(MultimeterFunction function, decimal? range, CancellationToken cancellationToken)
        {
            if (range.HasValue && range.Value <= 0) throw new ArgumentOutOfRangeException(nameof(range), "量程必须大于 0，留空表示自动量程。");
            return ExclusiveAsync(async () =>
            {
                EnsureConnected();
                var command = GetConfigureCommand(function);
                if (range.HasValue) command += " " + range.Value.ToString("0.########", CultureInfo.InvariantCulture);
                await WriteAsync(command, cancellationToken).ConfigureAwait(false);
                _function = function;
            }, cancellationToken);
        }

        public Task<MultimeterReading> ReadAsync(CancellationToken cancellationToken)
        {
            return ExclusiveAsync(async () =>
            {
                EnsureConnected();
                var response = await QueryAsync("READ?", cancellationToken).ConfigureAwait(false);
                decimal value;
                if (!decimal.TryParse(response, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) throw new FormatException("无法解析 GDM9061 数值响应：" + response);
                return new MultimeterReading { Function = _function, Value = value, Unit = GetUnit(_function) };
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

        private static string GetConfigureCommand(MultimeterFunction function)
        {
            switch (function)
            {
                case MultimeterFunction.DcVoltage: return "CONFigure:VOLTage:DC";
                case MultimeterFunction.AcVoltage: return "CONFigure:VOLTage:AC";
                case MultimeterFunction.DcCurrent: return "CONFigure:CURRent:DC";
                case MultimeterFunction.AcCurrent: return "CONFigure:CURRent:AC";
                case MultimeterFunction.Resistance2Wire: return "CONFigure:RESistance";
                case MultimeterFunction.Resistance4Wire: return "CONFigure:FRESistance";
                case MultimeterFunction.Frequency: return "CONFigure:FREQuency";
                case MultimeterFunction.Period: return "CONFigure:PERiod";
                case MultimeterFunction.Diode: return "CONFigure:DIODe";
                case MultimeterFunction.Continuity: return "CONFigure:CONTinuity";
                default: throw new ArgumentOutOfRangeException(nameof(function), function, null);
            }
        }

        private static string GetUnit(MultimeterFunction function)
        {
            switch (function)
            {
                case MultimeterFunction.DcVoltage:
                case MultimeterFunction.AcVoltage: return "V";
                case MultimeterFunction.DcCurrent:
                case MultimeterFunction.AcCurrent: return "A";
                case MultimeterFunction.Resistance2Wire:
                case MultimeterFunction.Resistance4Wire:
                case MultimeterFunction.Continuity: return "Ω";
                case MultimeterFunction.Diode: return "V";
                case MultimeterFunction.Frequency: return "Hz";
                case MultimeterFunction.Period: return "s";
                default: return string.Empty;
            }
        }

        protected override void DisposeCore()
        {
            if (_transport != null) _transport.Dispose();
        }
    }
}
