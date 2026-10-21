using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Application;
using ExplorerManager.Domain;

namespace ExplorerManager.Infrastructure
{
    public sealed class RecoveryCache : IRecoveryCache, IDisposable
    {
        private const int BlockSize = 65536;
        private const int BlockCharge = BlockSize + 32;
        private readonly object _sync = new object();
        private readonly Dictionary<string, Entry> _entries = new Dictionary<string, Entry>();
        private readonly Stack<byte[]> _free = new Stack<byte[]>();
        private readonly long _metadataBudget;
        private readonly Action<string, string> _removed;
        private long _metadataUsed, _reservedBlocks, _allocatedBlocks;
        private bool _disposed;
        private sealed class Entry
        {
            public RecoveryInfo Info;
            public FileSnapshot File;
            public List<byte[]> Blocks = new List<byte[]>();
            public int BlockCount;
            public long Metadata;
            public bool Pinned = true;
        }
        public RecoveryCache(long capacity, Action<string, string> removed = null)
        {
            if (capacity < 1024 * 1024 || capacity > 1073741824) throw new ArgumentOutOfRangeException(nameof(capacity));
            Capacity = capacity;
            _metadataBudget = Math.Min(16L * 1024 * 1024, capacity / 8);
            _removed = removed;
        }
        public long Capacity { get; }
        public string Epoch { get; } = Guid.NewGuid().ToString("N");
        public long Used { get { lock (_sync) return _reservedBlocks * BlockCharge + _metadataUsed; } }
        private static int BlocksFor(long length) => checked((int)((length + BlockSize - 1) / BlockSize));
        private static long MetadataFor(FileSnapshot f) => 4096L + (f.Path?.Length ?? 0) * 4L + (f.Dacl?.Length ?? 0) * 4L;
        public bool CanFit(FileSnapshot f) => f.Length >= 0 && f.Length <= Capacity &&
            MetadataFor(f) <= _metadataBudget && BlocksFor(f.Length) * (long)BlockCharge <= Capacity - _metadataBudget;

        public async Task<RecoveryInfo> CaptureAsync(FileSnapshot file, Stream input, string itemId, string runId, string policyId, long bytesPerSecond, CancellationToken cancellation)
        {
            if (!CanFit(file)) throw new CacheCapacityException("파일 전체와 메타데이터를 캐시에 보관할 수 없습니다.");
            var e = new Entry
            {
                File = file, Metadata = MetadataFor(file), BlockCount = BlocksFor(file.Length),
                Info = new RecoveryInfo { Id = Guid.NewGuid().ToString("N"), ItemId = itemId, RunId = runId,
                    PolicyId = policyId, Epoch = Epoch, Path = file.Path, IsDirectory = file.IsDirectory,
                    Length = file.Length, State = "Preparing", DeletedUtc = DateTime.UtcNow }
            };
            lock (_sync)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(RecoveryCache));
                while (_metadataUsed + e.Metadata > _metadataBudget ||
                       (_reservedBlocks + e.BlockCount) * BlockCharge > Capacity - _metadataBudget || _entries.Count >= 4096)
                {
                    var victim = _entries.Values.Where(x => !x.Pinned && x.Info.State != "Preparing").OrderBy(x => x.Info.DeletedUtc).FirstOrDefault();
                    if (victim == null) throw new CacheCapacityException("복구 중이거나 준비 중인 항목 때문에 캐시 공간이 부족합니다.");
                    Drop(victim, "EvictedFIFO");
                }
                _reservedBlocks += e.BlockCount; _metadataUsed += e.Metadata;
                e.Info.ChargedBytes = e.BlockCount * (long)BlockCharge + e.Metadata;
                _entries.Add(e.Info.Id, e);
            }
            try
            {
                var watch = Stopwatch.StartNew();
                long copied = 0;
                using (var hash = SHA256.Create())
                {
                    for (int index = 0; index < e.BlockCount; index++)
                    {
                        cancellation.ThrowIfCancellationRequested();
                        byte[] block;
                        lock (_sync)
                        {
                            if (_free.Count > 0) block = _free.Pop();
                            else
                            {
                                if ((_allocatedBlocks + 1) * BlockCharge > Capacity - _metadataBudget) throw new CacheCapacityException("메모리 풀 예산을 초과했습니다.");
                                block = new byte[BlockSize]; _allocatedBlocks++;
                            }
                            e.Blocks.Add(block);
                        }
                        int count = (int)Math.Min(BlockSize, file.Length - copied), read = 0;
                        while (read < count)
                        {
                            int n = await input.ReadAsync(block, read, count - read, cancellation).ConfigureAwait(false);
                            if (n == 0) throw new FileChangedException("원본 길이가 변경되었거나 읽기가 중단되었습니다.");
                            read += n;
                        }
                        hash.TransformBlock(block, 0, count, block, 0); copied += count;
                        if (bytesPerSecond > 0)
                        {
                            var delay = copied * 1000.0 / bytesPerSecond - watch.Elapsed.TotalMilliseconds;
                            if (delay > 1) await Task.Delay((int)Math.Min(delay, 60000), cancellation).ConfigureAwait(false);
                        }
                    }
                    if (!file.IsDirectory && input.ReadByte() != -1) throw new FileChangedException("원본 길이가 증가했습니다.");
                    hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
                    e.Info.Hash = BitConverter.ToString(hash.Hash).Replace("-", "").ToLowerInvariant();
                }
                using (var verify = SHA256.Create())
                using (var content = new BlocksStream(e.Blocks, file.Length))
                    if (BitConverter.ToString(verify.ComputeHash(content)).Replace("-", "").ToLowerInvariant() != e.Info.Hash)
                        throw new IOException("캐시 무결성 확인에 실패했습니다.");
                e.Info.State = "CacheReady";
                return Copy(e.Info);
            }
            catch { lock (_sync) { if (_entries.ContainsKey(e.Info.Id)) Drop(e, "PreparationFailed"); } throw; }
        }
        public void Commit(string id, bool confirmed)
        {
            lock (_sync)
            {
                var e = _entries[id];
                e.Info.State = confirmed ? "Recoverable" : "OutcomeUnknown";
                e.Info.DeletedUtc = DateTime.UtcNow; e.Pinned = false;
            }
        }
        public void Remove(string id, string reason)
        {
            lock (_sync)
            {
                Entry e;
                if (_entries.TryGetValue(id, out e))
                {
                    if (e.Pinned && e.Info.State == "Restoring") throw new InvalidOperationException("복구 중인 항목입니다.");
                    Drop(e, reason);
                }
            }
        }
        private void Drop(Entry e, string reason)
        {
            _entries.Remove(e.Info.Id);
            foreach (var block in e.Blocks) { Array.Clear(block, 0, block.Length); _free.Push(block); }
            _metadataUsed -= e.Metadata; _reservedBlocks -= e.BlockCount;
            _removed?.Invoke(e.Info.Id, reason);
        }
        public IList<RecoveryInfo> List()
        {
            lock (_sync) return _entries.Values.Where(e => e.Info.State != "Preparing" && e.Info.State != "CacheReady")
                .Select(e => Copy(e.Info)).OrderByDescending(e => e.DeletedUtc).ToList();
        }
        public IRecoveryLease Pin(string id)
        {
            lock (_sync)
            {
                Entry e;
                if (!_entries.TryGetValue(id, out e) || e.Pinned) throw new InvalidOperationException("복구본이 퇴출되었거나 사용 중입니다.");
                e.Pinned = true;
                return new Lease(this, e);
            }
        }
        private static RecoveryInfo Copy(RecoveryInfo x) => new RecoveryInfo { Id = x.Id, ItemId = x.ItemId, RunId = x.RunId,
            PolicyId = x.PolicyId, Epoch = x.Epoch, Path = x.Path, Hash = x.Hash, IsDirectory = x.IsDirectory,
            Length = x.Length, ChargedBytes = x.ChargedBytes, DeletedUtc = x.DeletedUtc, State = x.State };
        private sealed class Lease : IRecoveryLease
        {
            private RecoveryCache _owner;
            private readonly Entry _e;
            public Lease(RecoveryCache owner, Entry e) { _owner = owner; _e = e; }
            public RecoveryInfo Info => Copy(_e.Info);
            public FileSnapshot Original => _e.File;
            public Stream OpenContent() => new BlocksStream(_e.Blocks, _e.File.Length);
            public void Dispose() { if (_owner != null) { lock (_owner._sync) _e.Pinned = false; _owner = null; } }
        }
        private sealed class BlocksStream : Stream
        {
            private readonly IList<byte[]> _blocks;
            private readonly long _length;
            private long _position;
            public BlocksStream(IList<byte[]> blocks, long length) { _blocks = blocks; _length = length; }
            public override int Read(byte[] buffer, int offset, int count)
            {
                if (buffer == null || offset < 0 || count < 0 || offset > buffer.Length - count) throw new ArgumentException("버퍼 범위");
                int total = 0;
                while (count > 0 && _position < _length)
                {
                    int b = (int)(_position / BlockSize), at = (int)(_position % BlockSize);
                    int n = (int)Math.Min(Math.Min(count, BlockSize - at), _length - _position);
                    Buffer.BlockCopy(_blocks[b], at, buffer, offset, n);
                    offset += n; count -= n; total += n; _position += n;
                }
                return total;
            }
            public override bool CanRead => true;
            public override bool CanSeek => true;
            public override bool CanWrite => false;
            public override long Length => _length;
            public override long Position { get => _position; set { if (value < 0 || value > _length) throw new ArgumentOutOfRangeException(); _position = value; } }
            public override long Seek(long offset, SeekOrigin origin) { Position = (origin == SeekOrigin.Begin ? 0 : origin == SeekOrigin.Current ? Position : Length) + offset; return Position; }
            public override void Flush() { }
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                foreach (var e in _entries.Values.ToList()) Drop(e, "UnavailableAfterRestart");
                _free.Clear();
            }
        }
    }
}
