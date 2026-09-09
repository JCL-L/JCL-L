using System;
using System.Threading;
using System.Threading.Tasks;
using NinOne.Application.Devices;
using NinOne.Domain.Devices;

namespace NinOne.Infrastructure.Manual
{
    public sealed class ManualHighVoltageService : IManualHighVoltageService
    {
        private readonly IAppLogger _logger;
        private bool _disposed;

        public ManualHighVoltageService(IAppLogger logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public bool IsConnected { get; private set; }
        public bool OperatorReportedOn { get; private set; }
        public event EventHandler<DeviceTraceEventArgs> Trace;

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            IsConnected = true;
            RaiseTrace("STATUS", "人工高压电源状态记录已启用");
            return Task.CompletedTask;
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsConnected = false;
            RaiseTrace("STATUS", "人工高压电源状态记录已关闭");
            return Task.CompletedTask;
        }

        public async Task RecordAsync(bool enabled, string operatorNote, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsConnected) throw new InvalidOperationException("人工高压电源状态记录尚未启用。");
            OperatorReportedOn = enabled;
            var text = "操作员确认高压直流电源" + (enabled ? "已人工开启" : "已人工关闭") + (string.IsNullOrWhiteSpace(operatorNote) ? string.Empty : "；备注：" + operatorNote.Trim());
            RaiseTrace("MANUAL", text);
            await _logger.LogAsync(new OperationLogEntry
            {
                Level = "INFO",
                Device = "高压直流电源（人工）",
                Function = enabled ? "人工开启记录" : "人工关闭记录",
                Parameters = operatorNote ?? string.Empty,
                Message = text
            }).ConfigureAwait(false);
        }

        public void Dispose()
        {
            _disposed = true;
            IsConnected = false;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(ManualHighVoltageService));
        }

        private void RaiseTrace(string direction, string text)
        {
            var handler = Trace;
            if (handler != null) handler(this, new DeviceTraceEventArgs(direction, text));
        }
    }
}
