using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using NinOne.Application.Devices;
using NinOne.Domain.Configuration;
using NinOne.Domain.Devices;
using NinOne.Drivers.Common;

namespace NinOne.Drivers.N69206
{
    public sealed class N69206Service : RealDeviceServiceBase, IElectronicLoadService
    {
        private const ushort StatusRegister = 8;
        private const ushort ModeRegister = 26;
        private const ushort InputRegister = 28;
        private const ushort RemoteRegister = 32;
        private const ushort CcHighCurrentRegister = 40;
        private const ushort CcRangeRegister = 44;
        private const ushort CcHighRiseSlopeRegister = 46;
        private const ushort CcHighFallSlopeRegister = 48;
        private const ushort CcLowCurrentRegister = 42;
        private const ushort CcLowRiseSlopeRegister = 320;
        private const ushort CcLowFallSlopeRegister = 322;
        private const ushort CcMediumCurrentRegister = 418;
        private const ushort CcMediumRiseSlopeRegister = 420;
        private const ushort CcMediumFallSlopeRegister = 422;
        private const ushort CvHighVoltageRegister = 50;
        private const ushort CvLowVoltageRegister = 52;
        private const ushort CvMediumVoltageRegister = 424;
        private const ushort CvRangeRegister = 54;
        private const ushort CvHighRateRegister = 740;
        private const ushort CvLowRateRegister = 742;
        private const ushort CvMediumRateRegister = 744;
        private const ushort CvUserDefinedRateRegister = 830;
        private const ushort CrHighResistanceRegister = 64;
        private const ushort CrLowResistanceRegister = 66;
        private const ushort CrMediumResistanceRegister = 470;
        private const ushort CrRangeRegister = 68;
        private const ushort CrHighRiseSlopeRegister = 70;
        private const ushort CrHighFallSlopeRegister = 72;
        private const ushort CrLowRiseSlopeRegister = 328;
        private const ushort CrLowFallSlopeRegister = 330;
        private const ushort CrMediumRiseSlopeRegister = 472;
        private const ushort CrMediumFallSlopeRegister = 474;
        private const ushort CpRangeRegister = 430;
        private const ushort CpHighPowerRegister = 74;
        private const ushort CpHighRiseSlopeRegister = 76;
        private const ushort CpHighFallSlopeRegister = 78;
        private const ushort CpLowPowerRegister = 432;
        private const ushort CpLowRiseSlopeRegister = 434;
        private const ushort CpLowFallSlopeRegister = 436;
        private const ushort CpMediumPowerRegister = 464;
        private const ushort CpMediumRiseSlopeRegister = 466;
        private const ushort CpMediumFallSlopeRegister = 468;
        private const ushort ExitRemoteRegister = 838;

        private readonly ElectronicLoadConfig _config;
        private ModbusRtuOverTcpTransport _transport;

        public N69206Service(ElectronicLoadConfig config)
            : base("N69206")
        {
            _config = config ?? throw new ArgumentNullException(nameof(config));
        }

        protected override async Task ConnectCoreAsync(CancellationToken cancellationToken)
        {
            if (_config.DeviceId < 1 || _config.DeviceId > 248) throw new ArgumentOutOfRangeException(nameof(_config.DeviceId), "N69206 Modbus ID 必须为 1～248。");
            if (_transport != null) _transport.Dispose();
            _transport = new ModbusRtuOverTcpTransport(_config.Host, _config.Port, _config.TimeoutMilliseconds);
            await _transport.OpenAsync(cancellationToken).ConfigureAwait(false);
            var status = await ReadUInt32Async(StatusRegister, cancellationToken).ConfigureAwait(false);
            await WriteUInt32Async(RemoteRegister, 1, cancellationToken).ConfigureAwait(false);
            RaiseTrace("STATUS", string.Format(CultureInfo.InvariantCulture, "Modbus RTU over TCP 已响应，ID={0}，状态寄存器2=0x{1:X8}", _config.DeviceId, status));
        }

        protected override async Task DisconnectCoreAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (_transport != null && _transport.IsOpen) await WriteUInt32Async(ExitRemoteRegister, 1, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (_transport != null) await _transport.CloseAsync().ConfigureAwait(false);
            }
        }

        public Task ConfigureConstantCurrentAsync(decimal amperes, int range, decimal riseSlope, decimal fallSlope, CancellationToken cancellationToken)
        {
            ValidateValueAndSlopes(amperes, nameof(amperes), range, riseSlope, fallSlope);
            return ConfigureWithSlopesAsync(ElectronicLoadMode.ConstantCurrent, range, amperes,
                Select(range, CcHighCurrentRegister, CcLowCurrentRegister, CcMediumCurrentRegister), CcRangeRegister,
                Select(range, CcHighRiseSlopeRegister, CcLowRiseSlopeRegister, CcMediumRiseSlopeRegister),
                Select(range, CcHighFallSlopeRegister, CcLowFallSlopeRegister, CcMediumFallSlopeRegister), riseSlope, fallSlope, cancellationToken);
        }

        public Task ConfigureConstantVoltageAsync(decimal volts, int range, ElectronicLoadVoltageRate rate, decimal? userDefinedRate, CancellationToken cancellationToken)
        {
            if (volts < 0) throw new ArgumentOutOfRangeException(nameof(volts));
            ValidateRange(range);
            if ((int)rate < 0 || (int)rate > 3) throw new ArgumentOutOfRangeException(nameof(rate));
            if (rate == ElectronicLoadVoltageRate.UserDefined && (!userDefinedRate.HasValue || userDefinedRate.Value < 0.01m || userDefinedRate.Value > 10m))
            {
                throw new ArgumentOutOfRangeException(nameof(userDefinedRate), "N69206 自定义 V-Rate 必须为 0.01～10 V/ms。");
            }
            return ExclusiveAsync(async () =>
            {
                EnsureConnected();
                await PrepareModeAsync(ElectronicLoadMode.ConstantVoltage, cancellationToken).ConfigureAwait(false);
                await WriteUInt32Async(CvRangeRegister, (uint)range, cancellationToken).ConfigureAwait(false);
                await WriteFloatAsync(Select(range, CvHighVoltageRegister, CvLowVoltageRegister, CvMediumVoltageRegister), volts, cancellationToken).ConfigureAwait(false);
                await WriteUInt32Async(Select(range, CvHighRateRegister, CvLowRateRegister, CvMediumRateRegister), (uint)rate, cancellationToken).ConfigureAwait(false);
                if (rate == ElectronicLoadVoltageRate.UserDefined)
                {
                    await WriteFloatAsync(CvUserDefinedRateRegister, userDefinedRate.Value, cancellationToken).ConfigureAwait(false);
                }
                RaiseTrace("STATUS", string.Format(CultureInfo.InvariantCulture, "CV={0} V，量程={1}，V-Rate={2}{3}", volts, RangeName(range), rate, userDefinedRate.HasValue ? " / " + userDefinedRate.Value + " V/ms" : string.Empty));
            }, cancellationToken);
        }

        public Task ConfigureConstantResistanceAsync(decimal ohms, int range, decimal riseSlope, decimal fallSlope, CancellationToken cancellationToken)
        {
            ValidateValueAndSlopes(ohms, nameof(ohms), range, riseSlope, fallSlope);
            return ConfigureWithSlopesAsync(ElectronicLoadMode.ConstantResistance, range, ohms,
                Select(range, CrHighResistanceRegister, CrLowResistanceRegister, CrMediumResistanceRegister), CrRangeRegister,
                Select(range, CrHighRiseSlopeRegister, CrLowRiseSlopeRegister, CrMediumRiseSlopeRegister),
                Select(range, CrHighFallSlopeRegister, CrLowFallSlopeRegister, CrMediumFallSlopeRegister), riseSlope, fallSlope, cancellationToken);
        }

        public Task ConfigureConstantPowerAsync(decimal watts, int range, decimal riseSlope, decimal fallSlope, CancellationToken cancellationToken)
        {
            ValidateValueAndSlopes(watts, nameof(watts), range, riseSlope, fallSlope);
            return ConfigureWithSlopesAsync(ElectronicLoadMode.ConstantPower, range, watts,
                Select(range, CpHighPowerRegister, CpLowPowerRegister, CpMediumPowerRegister), CpRangeRegister,
                Select(range, CpHighRiseSlopeRegister, CpLowRiseSlopeRegister, CpMediumRiseSlopeRegister),
                Select(range, CpHighFallSlopeRegister, CpLowFallSlopeRegister, CpMediumFallSlopeRegister), riseSlope, fallSlope, cancellationToken);
        }

        public Task SetInputAsync(bool enabled, CancellationToken cancellationToken)
        {
            return ExclusiveAsync(async () =>
            {
                EnsureConnected();
                await WriteUInt32Async(InputRegister, enabled ? 1u : 0u, cancellationToken).ConfigureAwait(false);
                var status = await ReadUInt32Async(StatusRegister, cancellationToken).ConfigureAwait(false);
                var actual = (status & 1u) != 0;
                if (actual != enabled) throw new InvalidOperationException("N69206 加载写后回读不一致：状态寄存器2=0x" + status.ToString("X8", CultureInfo.InvariantCulture) + "。");
            }, cancellationToken);
        }

        public Task<ElectronicLoadReading> ReadAsync(CancellationToken cancellationToken)
        {
            return ExclusiveAsync(async () =>
            {
                EnsureConnected();
                var data = await ReadRegistersAsync(StatusRegister, 8, cancellationToken).ConfigureAwait(false);
                var status = ModbusRtuCodec.DecodeUInt32(data, 0);
                return new ElectronicLoadReading
                {
                    Voltage = ModbusRtuCodec.DecodeFloat(data, 4),
                    Current = ModbusRtuCodec.DecodeFloat(data, 8),
                    Power = ModbusRtuCodec.DecodeFloat(data, 12),
                    InputEnabled = (status & 1u) != 0
                };
            }, cancellationToken);
        }

        private async Task<uint> ReadUInt32Async(ushort register, CancellationToken cancellationToken)
        {
            var data = await ReadRegistersAsync(register, 2, cancellationToken).ConfigureAwait(false);
            return ModbusRtuCodec.DecodeUInt32(data, 0);
        }

        private async Task<byte[]> ReadRegistersAsync(ushort register, ushort registerCount, CancellationToken cancellationToken)
        {
            var request = ModbusRtuCodec.BuildReadRequest((byte)_config.DeviceId, register, registerCount);
            RaiseTrace("TX", ModbusRtuCodec.ToHex(request));
            var response = await _transport.ExchangeAsync(request, 5 + registerCount * 2, cancellationToken).ConfigureAwait(false);
            RaiseTrace("RX", ModbusRtuCodec.ToHex(response));
            return ModbusRtuCodec.ParseReadResponse(response, (byte)_config.DeviceId, registerCount);
        }

        private Task WriteUInt32Async(ushort register, uint value, CancellationToken cancellationToken)
        {
            return WriteAsync(ModbusRtuCodec.BuildWriteUInt32Request((byte)_config.DeviceId, register, value), register, cancellationToken);
        }

        private Task WriteFloatAsync(ushort register, decimal value, CancellationToken cancellationToken)
        {
            return WriteAsync(ModbusRtuCodec.BuildWriteFloatRequest((byte)_config.DeviceId, register, value), register, cancellationToken);
        }

        private Task ConfigureWithSlopesAsync(ElectronicLoadMode mode, int range, decimal value, ushort valueRegister, ushort rangeRegister, ushort riseRegister, ushort fallRegister, decimal riseSlope, decimal fallSlope, CancellationToken cancellationToken)
        {
            return ExclusiveAsync(async () =>
            {
                EnsureConnected();
                await PrepareModeAsync(mode, cancellationToken).ConfigureAwait(false);
                await WriteUInt32Async(rangeRegister, (uint)range, cancellationToken).ConfigureAwait(false);
                await WriteFloatAsync(valueRegister, value, cancellationToken).ConfigureAwait(false);
                await WriteFloatAsync(riseRegister, riseSlope, cancellationToken).ConfigureAwait(false);
                await WriteFloatAsync(fallRegister, fallSlope, cancellationToken).ConfigureAwait(false);
                RaiseTrace("STATUS", string.Format(CultureInfo.InvariantCulture, "{0}={1}，量程={2}，Rise={3} A/ms，Fall={4} A/ms", ModeName(mode), value, RangeName(range), riseSlope, fallSlope));
            }, cancellationToken);
        }

        private async Task PrepareModeAsync(ElectronicLoadMode mode, CancellationToken cancellationToken)
        {
            // 厂商手册要求切换模式前必须卸载。
            await WriteUInt32Async(InputRegister, 0, cancellationToken).ConfigureAwait(false);
            await WriteUInt32Async(ModeRegister, (uint)mode, cancellationToken).ConfigureAwait(false);
        }

        private static ushort Select(int range, ushort high, ushort low, ushort medium)
        {
            ValidateRange(range);
            return range == 0 ? high : range == 1 ? low : medium;
        }

        private static void ValidateValueAndSlopes(decimal value, string valueName, int range, decimal riseSlope, decimal fallSlope)
        {
            if (value < 0) throw new ArgumentOutOfRangeException(valueName);
            ValidateRange(range);
            if (riseSlope <= 0) throw new ArgumentOutOfRangeException(nameof(riseSlope));
            if (fallSlope <= 0) throw new ArgumentOutOfRangeException(nameof(fallSlope));
        }

        private static void ValidateRange(int range)
        {
            if (range < 0 || range > 2) throw new ArgumentOutOfRangeException(nameof(range), "N69206 量程只允许 0=大、1=小、2=中。");
        }

        private static string RangeName(int range)
        {
            return range == 0 ? "大量程" : range == 1 ? "小量程" : "中量程";
        }

        private static string ModeName(ElectronicLoadMode mode)
        {
            switch (mode)
            {
                case ElectronicLoadMode.ConstantCurrent: return "CC";
                case ElectronicLoadMode.ConstantVoltage: return "CV";
                case ElectronicLoadMode.ConstantResistance: return "CR";
                case ElectronicLoadMode.ConstantPower: return "CP";
                default: throw new ArgumentOutOfRangeException(nameof(mode));
            }
        }

        private async Task WriteAsync(byte[] request, ushort register, CancellationToken cancellationToken)
        {
            RaiseTrace("TX", ModbusRtuCodec.ToHex(request));
            var response = await _transport.ExchangeAsync(request, 8, cancellationToken).ConfigureAwait(false);
            RaiseTrace("RX", ModbusRtuCodec.ToHex(response));
            ModbusRtuCodec.ValidateWriteResponse(response, request, (byte)_config.DeviceId, register);
        }

        protected override void DisposeCore()
        {
            if (_transport != null) _transport.Dispose();
        }
    }
}
