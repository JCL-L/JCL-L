using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NinOne.Application.Devices;
using NinOne.Domain.Devices;

namespace NinOne.Infrastructure.Logging
{
    public sealed class FileAppLogger : IAppLogger, IDisposable
    {
        private readonly string _directory;
        private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

        public FileAppLogger(string directory)
        {
            _directory = directory ?? throw new ArgumentNullException(nameof(directory));
            Directory.CreateDirectory(_directory);
        }

        public event EventHandler<OperationLogEntry> EntryLogged;

        public async Task LogAsync(OperationLogEntry entry)
        {
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            var path = Path.Combine(_directory, entry.Timestamp.ToString("yyyy-MM-dd") + ".log");
            var line = Format(entry) + Environment.NewLine;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, true))
                {
                    var bytes = new UTF8Encoding(false).GetBytes(line);
                    await stream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
                }
            }
            finally
            {
                _gate.Release();
            }
            var handler = EntryLogged;
            if (handler != null) handler(this, entry);
        }

        public void Dispose()
        {
            _gate.Dispose();
        }

        private static string Format(OperationLogEntry entry)
        {
            return string.Join(" | ", new[]
            {
                entry.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"),
                entry.Level ?? string.Empty,
                entry.Device ?? string.Empty,
                entry.Function ?? string.Empty,
                entry.Parameters ?? string.Empty,
                entry.ErrorCode ?? string.Empty,
                entry.Message ?? string.Empty,
                entry.RawResponse ?? string.Empty
            }).Replace("\r", "\\r").Replace("\n", "\\n");
        }
    }
}
