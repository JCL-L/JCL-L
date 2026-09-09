using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NinOne.Application.Devices;

namespace NinOne.Drivers.Common
{
    public abstract class RealDeviceServiceBase : IDeviceService
    {
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
        private bool _disposed;

        protected RealDeviceServiceBase(string deviceName)
        {
            DeviceName = deviceName ?? throw new ArgumentNullException(nameof(deviceName));
        }

        protected string DeviceName { get; }
        public bool IsConnected { get; protected set; }
        public event EventHandler<DeviceTraceEventArgs> Trace;

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            return ExclusiveAsync(async () =>
            {
                if (IsConnected) return;
                try
                {
                    await ConnectCoreAsync(cancellationToken).ConfigureAwait(false);
                    IsConnected = true;
                    RaiseTrace("STATUS", DeviceName + " 已连接");
                }
                catch
                {
                    IsConnected = false;
                    try { await DisconnectCoreAsync(CancellationToken.None).ConfigureAwait(false); } catch { }
                    throw;
                }
            }, cancellationToken);
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            return ExclusiveAsync(async () =>
            {
                await DisconnectCoreAsync(cancellationToken).ConfigureAwait(false);
                IsConnected = false;
                RaiseTrace("STATUS", DeviceName + " 已断开");
            }, cancellationToken);
        }

        protected abstract Task ConnectCoreAsync(CancellationToken cancellationToken);
        protected abstract Task DisconnectCoreAsync(CancellationToken cancellationToken);

        protected async Task ExclusiveAsync(Func<Task> operation, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                await operation().ConfigureAwait(false);
            }
            catch (Exception exception) when (IsCommunicationFailure(exception))
            {
                IsConnected = false;
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        protected async Task<T> ExclusiveAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();
                return await operation().ConfigureAwait(false);
            }
            catch (Exception exception) when (IsCommunicationFailure(exception))
            {
                IsConnected = false;
                throw;
            }
            finally
            {
                _gate.Release();
            }
        }

        protected void EnsureConnected()
        {
            ThrowIfDisposed();
            if (!IsConnected) throw new InvalidOperationException(DeviceName + " 尚未连接。");
        }

        protected void RaiseTrace(string direction, string text)
        {
            var handler = Trace;
            if (handler != null) handler(this, new DeviceTraceEventArgs(direction, text));
        }

        private static bool IsCommunicationFailure(Exception exception)
        {
            return exception is TimeoutException || exception is IOException || exception is SocketException;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(GetType().Name);
        }

        public void Dispose()
        {
            if (_disposed) return;
            try
            {
                _gate.Wait();
                try { DisconnectCoreAsync(CancellationToken.None).GetAwaiter().GetResult(); }
                finally { _gate.Release(); }
            }
            catch { }
            IsConnected = false;
            _disposed = true;
            _gate.Dispose();
            DisposeCore();
        }

        protected virtual void DisposeCore()
        {
        }
    }
}
