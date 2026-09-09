using System;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NinOne.Drivers.Common
{
    internal sealed class TcpScpiTransport : IDisposable
    {
        private readonly string _host;
        private readonly int _port;
        private readonly int _timeoutMilliseconds;
        private TcpClient _client;
        private NetworkStream _stream;

        public TcpScpiTransport(string host, int port, int timeoutMilliseconds)
        {
            _host = host;
            _port = port;
            _timeoutMilliseconds = timeoutMilliseconds;
        }

        public bool IsOpen { get { return _client != null && _client.Connected && _stream != null; } }

        public async Task OpenAsync(CancellationToken cancellationToken)
        {
            if (IsOpen) return;
            var client = new TcpClient();
            var connect = client.ConnectAsync(_host, _port);
            var timeout = Task.Delay(_timeoutMilliseconds, cancellationToken);
            var winner = await Task.WhenAny(connect, timeout).ConfigureAwait(false);
            if (winner != connect)
            {
                client.Close();
                ObserveFault(connect);
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException(string.Format(CultureInfo.InvariantCulture, "连接 {0}:{1} 超时（{2} ms）。", _host, _port, _timeoutMilliseconds));
            }
            await connect.ConfigureAwait(false);
            client.NoDelay = true;
            client.ReceiveTimeout = _timeoutMilliseconds;
            client.SendTimeout = _timeoutMilliseconds;
            _client = client;
            _stream = client.GetStream();
        }

        public Task CloseAsync()
        {
            var stream = _stream;
            var client = _client;
            _stream = null;
            _client = null;
            if (stream != null) stream.Dispose();
            if (client != null) client.Close();
            return Task.CompletedTask;
        }

        public async Task WriteAsync(string command, CancellationToken cancellationToken)
        {
            var bytes = Encoding.ASCII.GetBytes(command + "\n");
            var write = RequireOpen().WriteAsync(bytes, 0, bytes.Length, cancellationToken);
            await WaitWithTimeoutAsync(write, "发送命令", cancellationToken).ConfigureAwait(false);
        }

        public async Task<string> QueryAsync(string command, CancellationToken cancellationToken)
        {
            await WriteAsync(command, cancellationToken).ConfigureAwait(false);
            var bytes = await ReadUntilAsync((byte)'\n', 1024 * 1024, cancellationToken).ConfigureAwait(false);
            return Encoding.ASCII.GetString(bytes).TrimEnd('\r', '\n', ' ');
        }

        public async Task<byte[]> QueryDefiniteBlockAsync(string command, CancellationToken cancellationToken)
        {
            await WriteAsync(command, cancellationToken).ConfigureAwait(false);
            var stream = RequireOpen();
            var hash = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
            if (hash != '#') throw new InvalidDataException("SCPI 二进制块缺少 # 头，收到 0x" + hash.ToString("X2", CultureInfo.InvariantCulture) + "。");
            var digitCountByte = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
            if (digitCountByte < '1' || digitCountByte > '9') throw new InvalidDataException("SCPI 二进制块长度位数无效。");
            var digitCount = digitCountByte - '0';
            var lengthBytes = await ReadExactAsync(stream, digitCount, cancellationToken).ConfigureAwait(false);
            int length;
            if (!int.TryParse(Encoding.ASCII.GetString(lengthBytes), NumberStyles.None, CultureInfo.InvariantCulture, out length) || length < 0 || length > 128 * 1024 * 1024)
            {
                throw new InvalidDataException("SCPI 二进制块长度无效。");
            }
            var payload = await ReadExactAsync(stream, length, cancellationToken).ConfigureAwait(false);
            DrainAvailableLineEndings(stream);
            return payload;
        }

        public async Task<byte[]> QueryLittleEndianLengthBlockAsync(string command, int maximumBytes, CancellationToken cancellationToken)
        {
            await WriteAsync(command, cancellationToken).ConfigureAwait(false);
            var stream = RequireOpen();
            var header = await ReadExactAsync(stream, 4, cancellationToken).ConfigureAwait(false);
            var length = BitConverter.ToInt32(header, 0);
            if (length < 0 || length > maximumBytes) throw new InvalidDataException("波形块长度无效：" + length + "。");
            return await ReadExactAsync(stream, length, cancellationToken).ConfigureAwait(false);
        }

        private async Task<byte[]> ReadUntilAsync(byte terminator, int maximumBytes, CancellationToken cancellationToken)
        {
            var stream = RequireOpen();
            using (var memory = new MemoryStream())
            {
                while (memory.Length < maximumBytes)
                {
                    var value = await ReadByteAsync(stream, cancellationToken).ConfigureAwait(false);
                    memory.WriteByte(value);
                    if (value == terminator) return memory.ToArray();
                }
            }
            throw new InvalidDataException("响应超过允许的最大长度。");
        }

        private async Task<byte> ReadByteAsync(Stream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[1];
            var read = await ReadWithTimeoutAsync(stream, buffer, 0, 1, cancellationToken).ConfigureAwait(false);
            if (read != 1) throw new EndOfStreamException("设备在响应完成前关闭了连接。");
            return buffer[0];
        }

        private async Task<byte[]> ReadExactAsync(Stream stream, int length, CancellationToken cancellationToken)
        {
            var buffer = new byte[length];
            var offset = 0;
            while (offset < length)
            {
                var read = await ReadWithTimeoutAsync(stream, buffer, offset, length - offset, cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException("设备在二进制响应完成前关闭了连接。");
                offset += read;
            }
            return buffer;
        }

        private async Task<int> ReadWithTimeoutAsync(Stream stream, byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var read = stream.ReadAsync(buffer, offset, count, cancellationToken);
            var timeout = Task.Delay(_timeoutMilliseconds, cancellationToken);
            var winner = await Task.WhenAny(read, timeout).ConfigureAwait(false);
            if (winner == read) return await read.ConfigureAwait(false);
            Abort();
            ObserveFault(read);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(string.Format(CultureInfo.InvariantCulture, "从 {0}:{1} 读取响应超时（{2} ms）。", _host, _port, _timeoutMilliseconds));
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
            Abort();
            ObserveFault(operation);
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException(string.Format(CultureInfo.InvariantCulture, "向 {0}:{1} {2}超时（{3} ms）。", _host, _port, action, _timeoutMilliseconds));
        }

        private void Abort()
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

        private static void DrainAvailableLineEndings(NetworkStream stream)
        {
            while (stream.DataAvailable)
            {
                var value = stream.ReadByte();
                if (value != '\r' && value != '\n') throw new InvalidDataException("二进制块后收到意外字节 0x" + value.ToString("X2", CultureInfo.InvariantCulture) + "。");
            }
        }

        private NetworkStream RequireOpen()
        {
            if (!IsOpen) throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture, "TCP {0}:{1} 尚未连接。", _host, _port));
            return _stream;
        }

        public void Dispose()
        {
            CloseAsync().GetAwaiter().GetResult();
        }
    }
}
