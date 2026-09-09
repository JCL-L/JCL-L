using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NinOne.Application.Devices;
using NinOne.Domain.Configuration;
using NinOne.Domain.Devices;
using NinOne.Drivers.Gdm9061;
using NinOne.Drivers.Gpd2303s;
using NinOne.Drivers.N69206;
using NinOne.Drivers.Pa333h;
using NinOne.Drivers.PlcS7;
using NinOne.Drivers.Zds2024CPlus;
using NinOne.Drivers.ZlgCan;
using NinOne.Infrastructure.Logging;
using NinOne.Infrastructure.Manual;

namespace NinOne.App.Runtime
{
    public sealed class DeviceRuntime : IDisposable
    {
        private readonly List<IDeviceService> _devices;
        private bool _disposed;

        public DeviceRuntime(SystemConfig config, FileAppLogger logger)
        {
            Config = config ?? throw new ArgumentNullException(nameof(config));
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            Plc = new S7PlcRelayService(config.Plc);
            AuxiliaryPower = new Gpd2303sService(config.AuxiliaryPower);
            ElectronicLoad = new N69206Service(config.ElectronicLoad);
            Multimeter = new Gdm9061Service(config.Multimeter);
            PowerMeter = new Pa333hService(config.PowerMeter);
            DcdcCan = new ZlgDcdcCanService(config.Can);
            Oscilloscope = new Zds2024CPlusService(config.Oscilloscope);
            HighVoltage = new ManualHighVoltageService(logger);
            _devices = new List<IDeviceService> { Plc, AuxiliaryPower, ElectronicLoad, Multimeter, PowerMeter, DcdcCan, Oscilloscope, HighVoltage };
            Subscribe(Plc, "PLC");
            Subscribe(AuxiliaryPower, "GPD2303S");
            Subscribe(ElectronicLoad, "N69206");
            Subscribe(Multimeter, "GDM9061");
            Subscribe(PowerMeter, "PA333H");
            Subscribe(DcdcCan, "CAN");
            Subscribe(Oscilloscope, "ZDS2024C Plus");
            Subscribe(HighVoltage, "高压电源（人工）");
        }

        public SystemConfig Config { get; }
        public FileAppLogger Logger { get; }
        public IPlcRelayService Plc { get; }
        public IAuxiliaryPowerService AuxiliaryPower { get; }
        public IElectronicLoadService ElectronicLoad { get; }
        public IMultimeterService Multimeter { get; }
        public IPowerMeterService PowerMeter { get; }
        public IDcdcCanService DcdcCan { get; }
        public IOscilloscopeService Oscilloscope { get; }
        public IManualHighVoltageService HighVoltage { get; }

        public async Task ShutdownAsync(CancellationToken cancellationToken)
        {
            await SafeOutputAsync("示波器停止", Oscilloscope.IsConnected, () => Oscilloscope.StopAsync(cancellationToken)).ConfigureAwait(false);
            await SafeOutputAsync("电子负载关闭", ElectronicLoad.IsConnected, () => ElectronicLoad.SetInputAsync(false, cancellationToken)).ConfigureAwait(false);
            await SafeOutputAsync("辅助电源输出关闭", AuxiliaryPower.IsConnected, () => AuxiliaryPower.SetOutputAsync(false, cancellationToken)).ConfigureAwait(false);
            await SafeOutputAsync("PLC K1～K16 全部关闭", Plc.IsConnected, () => Plc.AllOffAsync(cancellationToken)).ConfigureAwait(false);
            for (var index = _devices.Count - 1; index >= 0; index--)
            {
                if (!_devices[index].IsConnected) continue;
                try { await _devices[index].DisconnectAsync(cancellationToken).ConfigureAwait(false); }
                catch (Exception exception) { await LogFailureAsync("退出释放", _devices[index].GetType().Name, exception).ConfigureAwait(false); }
            }
        }

        private async Task SafeOutputAsync(string operation, bool connected, Func<Task> action)
        {
            if (!connected) return;
            try { await action().ConfigureAwait(false); }
            catch (Exception exception) { await LogFailureAsync("退出安全关闭", operation, exception).ConfigureAwait(false); }
        }

        private Task LogFailureAsync(string function, string device, Exception exception)
        {
            return Logger.LogAsync(new OperationLogEntry
            {
                Level = "ERROR",
                Device = device,
                Function = function,
                Message = exception.Message,
                ErrorCode = exception.HResult.ToString("X8"),
                RawResponse = exception.ToString()
            });
        }

        private void Subscribe(IDeviceService service, string name)
        {
            service.Trace += (sender, args) =>
            {
                var ignored = Logger.LogAsync(new OperationLogEntry
                {
                    Timestamp = args.Timestamp,
                    Level = args.Direction == "RX" || args.Direction == "TX" ? "TRACE" : "INFO",
                    Device = name,
                    Function = args.Direction,
                    Message = args.Text,
                    RawResponse = args.Text
                });
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            for (var index = _devices.Count - 1; index >= 0; index--)
            {
                try { _devices[index].Dispose(); } catch { }
            }
            Logger.Dispose();
            _disposed = true;
        }
    }
}
