using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Domain;

namespace ExplorerManager.Application
{
    public sealed class AsyncByteLimiter
    {
        private readonly object _sync = new object();
        private double _next;
        private long _rate;
        public async Task WaitAsync(int bytes, long rate, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (rate <= 0 || bytes <= 0) return;
            double wait;
            lock (_sync)
            {
                double now = (double)Stopwatch.GetTimestamp() / Stopwatch.Frequency;
                if (_rate != rate) { _rate = rate; _next = now; }
                double start = Math.Max(now, _next); _next = start + (double)bytes / rate; wait = start - now;
            }
            while (wait > 0) { int ms = (int)Math.Min(60000, Math.Ceiling(wait * 1000)); await Task.Delay(ms, ct).ConfigureAwait(false); wait -= ms / 1000.0; }
        }
    }
    public static class TransferIO
    {
        public const int BufferBytes = 262144;
        public static async Task<string> HashAsync(Stream stream, long length, Func<int, CancellationToken, Task> limiter, CancellationToken ct)
        {
            using (var sha = SHA256.Create())
            {
                byte[] buffer = new byte[BufferBytes]; long read = 0;
                while (read < length)
                {
                    ct.ThrowIfCancellationRequested();
                    int want = (int)Math.Min(buffer.Length, length - read);
                    if (limiter != null) await limiter(want, ct).ConfigureAwait(false);
                    int n = await stream.ReadAsync(buffer, 0, want, ct).ConfigureAwait(false);
                    if (n == 0) throw new TransferFailure("SourceLengthChanged", "예상 길이보다 데이터가 짧습니다.");
                    sha.TransformBlock(buffer, 0, n, buffer, 0); read += n;
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0); return TransferRules.Hex(sha.Hash);
            }
        }
    }
    // A bounded seekable view of the already verified source; never reopens its path.
    public sealed class TransferReadStream : Stream
    {
        private readonly Stream _source;
        private readonly Func<int, CancellationToken, Task> _limit;
        private readonly CancellationToken _operation;
        public TransferReadStream(Stream source, Func<int, CancellationToken, Task> limit, CancellationToken operation)
        { _source = source; _limit = limit; _operation = operation; }
        public override bool CanRead => true;
        public override bool CanSeek => _source.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _source.Length;
        public override long Position { get => _source.Position; set => _source.Position = value; }
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            _operation.ThrowIfCancellationRequested(); ct.ThrowIfCancellationRequested();
            int take = Math.Min(count, TransferIO.BufferBytes);
            if (_limit != null) await _limit(take, _operation).ConfigureAwait(false);
            return await _source.ReadAsync(buffer, offset, take, _operation).ConfigureAwait(false);
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, _operation).GetAwaiter().GetResult();
        public override long Seek(long offset, SeekOrigin origin) => _source.Seek(offset, origin);
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { base.Dispose(disposing); } // source lease owns the underlying stream
    }
}
