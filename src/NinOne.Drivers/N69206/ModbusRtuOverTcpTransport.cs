using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace NinOne.Drivers.N69206
{
    internal sealed class ModbusRtuOverTcpTransport : IDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private readonly int _timeoutMilliseconds;
        private TcpClient _client;
        private NetworkStream _stream;

        public ModbusRtuOverTcpTransport(string host, int port, int timeoutMilliseconds)
        {
            _host = host;
            _port = port;
            _timeoutMilliseconds = timeoutMilliseconds;
        }

        public bool IsOpen { get { return _client != null && _client.Connected && _stream != null; } }

        public async Task OpenAsync(CancellationToken cancellationToken)
        {
            if (IsOpen) return;
            CloseCore();
            var client = new TcpClient();
            var connect = client.ConnectAsync(_host, _port);
            var timeout = Task.Delay(_timeoutMilliseconds, cancellationToken);
            var winner = await Task.WhenAny(connect, timeout).ConfigureAwait(false);
            if (winner != connect)
            {
                client.Close();
                ObserveFault(connect);
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException(string.Format(CultureInfo.InvariantCulture, "连接 N69206 {0}:{1} 超时（{2} ms）。", _host, _port, _timeoutMilliseconds));
            }
            await connect.ConfigureAwait(false);
            client.NoDelay = true;
            _client = client;
            _stream = client.GetStream();
        }

        public Task CloseAsync()
        {
            CloseCore();
            return Task.CompletedTask;
        }

        public async Task<byte[]> ExchangeAsync(byte[] request, int normalResponseLength, CancellationToken cancellationToken)
        {
            if (request == null || request.Length == 0) throw new ArgumentException("Modbus 请求不能为空。", nameof(request));
            if (normalResponseLength < 5) throw new ArgumentOutOfRangeException(nameof(normalResponseLength));
            var stream = RequireOpen();
            await WaitWithTimeoutAsync(stream.WriteAsync(request, 0, request.Length, cancellationToken), "发送", cancellationToken).ConfigureAwait(false);
            var prefix = await ReadExactAsync(stream, 2, cancellationToken).ConfigureAwait(false);
            var responseLength = (prefix[1] & 0x80) != 0 ? 5 : normalResponseLength;
            var suffix = await ReadExactAsync(stream, responseLength - prefix.Length, cancellationToken).ConfigureAwait(false);
            var response = new byte[responseLength];
            Array.Copy(prefix, response, prefix.Length);
            Array.Copy(suffix, 0, response, prefix.Length, suffix.Length);
            return response;
        }

        private async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken cancellationToken)
        {
            var buffer = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = stream.ReadAsync(buffer, offset, length - offset, cancellationToken);
                await WaitWithTimeoutAsync(read, "读取", cancellationToken).ConfigureAwait(false);
                var count = await read.ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("N69206 在 Modbus 响应完成前关闭了 TCP 连接。");
                offset += count;
            }
            return buffer;
        }

        private async Task WaitWithTimeoutAsync(Task operation, string action, CancellationToken cancellationToken)
        {
            var timeout = Task.Delay(_timeoutMilliseconds, cancellationToken);
            var winner = await Task.WhenAny(operation, timeout).ConfigureAwait(false);
            if (winner == operation)
            {
                await operation.ConfigureAwait(false);
                return;
            }
            CloseCore();
            ObserveFault(operation);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(string.Format(CultureInfo.InvariantCulture, "N69206 Modbus {0}超时（{1} ms）。", action, _timeoutMilliseconds));
        }

        private NetworkStream RequireOpen()
        {
            if (!IsOpen) throw new InvalidOperationException("N69206 TCP 连接尚未打开。");
            return _stream;
        }

        private void CloseCore()
        {
            var stream = _stream;
            var client = _client;
            _stream = null;
            _client = null;
            try { if (stream != null) stream.Dispose(); } catch { }
            try { if (client != null) client.Close(); } catch { }
        }

        private static void ObserveFault(Task task)
        {
            task.ContinueWith(completed => { var ignored = completed.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        public void Dispose()
        {
            CloseCore();
        }
    }
}
