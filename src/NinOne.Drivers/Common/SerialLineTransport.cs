using System;
using System.IO.Ports;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NinOne.Drivers.Common
{
    internal sealed class SerialLineTransport : IDisposable
    {
        private readonly string _portName;
        private readonly int _baudRate;
        private readonly int _timeoutMilliseconds;
        private readonly string _newLine;
        private SerialPort _port;

        public SerialLineTransport(string portName, int baudRate, int timeoutMilliseconds, string newLine = "\r\n")
        {
            _portName = portName;
            _baudRate = baudRate;
            _timeoutMilliseconds = timeoutMilliseconds;
            if (newLine != "\r" && newLine != "\n" && newLine != "\r\n") throw new ArgumentOutOfRangeException(nameof(newLine), "串口结束符只允许 CR、LF 或 CRLF。");
            _newLine = newLine;
        }

        public bool IsOpen { get { return _port != null && _port.IsOpen; } }

        public Task OpenAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.Run(() =>
            {
                if (IsOpen) return;
                var port = new SerialPort(_portName, _baudRate, Parity.None, 8, StopBits.One)
                {
                    Encoding = Encoding.ASCII,
                    Handshake = Handshake.None,
                    NewLine = _newLine,
                    ReadTimeout = _timeoutMilliseconds,
                    WriteTimeout = _timeoutMilliseconds,
                    DtrEnable = true,
                    RtsEnable = false
                };
                port.Open();
                _port = port;
            }, cancellationToken);
        }

        public Task CloseAsync()
        {
            return Task.Run(() =>
            {
                var port = _port;
                _port = null;
                if (port == null) return;
                try { if (port.IsOpen) port.Close(); }
                finally { port.Dispose(); }
            });
        }

        public Task WriteAsync(string command, CancellationToken cancellationToken)
        {
            var port = RequireOpen();
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                port.WriteLine(command);
            }, cancellationToken);
        }

        public Task<string> QueryAsync(string command, CancellationToken cancellationToken)
        {
            var port = RequireOpen();
            return Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                port.DiscardInBuffer();
                port.WriteLine(command);
                var response = port.ReadLine();
                cancellationToken.ThrowIfCancellationRequested();
                return response;
            }, cancellationToken);
        }

        private SerialPort RequireOpen()
        {
            if (!IsOpen) throw new InvalidOperationException("串口 " + _portName + " 尚未打开。");
            return _port;
        }

        public void Dispose()
        {
            try { CloseAsync().GetAwaiter().GetResult(); } catch { }
        }
    }
}
