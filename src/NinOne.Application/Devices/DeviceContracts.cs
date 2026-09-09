using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NinOne.Domain.Configuration;
using NinOne.Domain.Devices;

namespace NinOne.Application.Devices
{
    public interface IDeviceService : IDisposable
    {
        bool IsConnected { get; }
        event EventHandler<DeviceTraceEventArgs> Trace;
        Task ConnectAsync(CancellationToken cancellationToken);
        Task DisconnectAsync(CancellationToken cancellationToken);
    }

    public interface IPlcRelayService : IDeviceService
    {
        Task<bool> ReadRelayAsync(string relayId, CancellationToken cancellationToken);
        Task<IReadOnlyDictionary<string, bool>> ReadAllAsync(CancellationToken cancellationToken);
        Task WriteRelayAsync(string relayId, bool state, CancellationToken cancellationToken);
        Task AllOffAsync(CancellationToken cancellationToken);
    }

    public interface IAuxiliaryPowerService : IDeviceService
    {
        Task SetAsync(int channel, decimal voltage, decimal current, CancellationToken cancellationToken);
        Task SetVoltageAsync(int channel, decimal voltage, CancellationToken cancellationToken);
        Task SetOutputAsync(bool enabled, CancellationToken cancellationToken);
        Task<PowerSupplyReading> ReadAsync(int channel, CancellationToken cancellationToken);
    }

    public interface IElectronicLoadService : IDeviceService
    {
        Task ConfigureConstantCurrentAsync(decimal amperes, int range, decimal riseSlope, decimal fallSlope, CancellationToken cancellationToken);
        Task ConfigureConstantVoltageAsync(decimal volts, int range, ElectronicLoadVoltageRate rate, decimal? userDefinedRate, CancellationToken cancellationToken);
        Task ConfigureConstantResistanceAsync(decimal ohms, int range, decimal riseSlope, decimal fallSlope, CancellationToken cancellationToken);
        Task ConfigureConstantPowerAsync(decimal watts, int range, decimal riseSlope, decimal fallSlope, CancellationToken cancellationToken);
        Task SetInputAsync(bool enabled, CancellationToken cancellationToken);
        Task<ElectronicLoadReading> ReadAsync(CancellationToken cancellationToken);
    }

    public interface IMultimeterService : IDeviceService
    {
        Task ConfigureAsync(MultimeterFunction function, decimal? range, CancellationToken cancellationToken);
        Task<MultimeterReading> ReadAsync(CancellationToken cancellationToken);
    }

    public interface IPowerMeterService : IDeviceService
    {
        Task ConfigureAsync(CancellationToken cancellationToken);
        Task<PowerMeterReading> ReadAsync(CancellationToken cancellationToken);
    }

    public interface IDcdcCanService : IDeviceService
    {
        IReadOnlyList<CanFrameTemplate> Templates { get; }
        IReadOnlyList<int> OpenChannels { get; }
        Task OpenChannelAsync(int channelIndex, CancellationToken cancellationToken);
        Task CloseChannelAsync(int channelIndex, CancellationToken cancellationToken);
        Task SendAsync(int channelIndex, CanFrame frame, CancellationToken cancellationToken);
        Task SendTemplateGroupAsync(int channelIndex, int group, CancellationToken cancellationToken);
        Task SetDcdcRunAsync(int channelIndex, bool enabled, CancellationToken cancellationToken);
        Task<IReadOnlyList<CanFrame>> ReceiveAsync(int channelIndex, int maximumFrames, int waitMilliseconds, CancellationToken cancellationToken);
    }

    public interface IOscilloscopeService : IDeviceService
    {
        Task ApplyConfigAsync(OscilloscopeConfig config, CancellationToken cancellationToken);
        Task RunAsync(CancellationToken cancellationToken);
        Task StopAsync(CancellationToken cancellationToken);
        Task<WaveformSet> ReadWaveformsAsync(CancellationToken cancellationToken);
        Task<byte[]> CaptureBitmapAsync(CancellationToken cancellationToken);
        Task ResetAsync(CancellationToken cancellationToken);
    }

    public interface IManualHighVoltageService : IDeviceService
    {
        bool OperatorReportedOn { get; }
        Task RecordAsync(bool enabled, string operatorNote, CancellationToken cancellationToken);
    }

    public interface IAppLogger
    {
        event EventHandler<OperationLogEntry> EntryLogged;
        Task LogAsync(OperationLogEntry entry);
    }

    public sealed class DeviceTraceEventArgs : EventArgs
    {
        public DeviceTraceEventArgs(string direction, string text)
        {
            Timestamp = DateTimeOffset.Now;
            Direction = direction;
            Text = text;
        }

        public DateTimeOffset Timestamp { get; }
        public string Direction { get; }
        public string Text { get; }
    }
}
