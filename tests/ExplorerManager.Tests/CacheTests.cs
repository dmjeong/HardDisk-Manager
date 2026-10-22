using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Application;
using ExplorerManager.Domain;
using ExplorerManager.Infrastructure;
using Xunit;

namespace ExplorerManager.Tests
{
    public sealed class CacheTests
    {
        private static FileSnapshot File(long length) => new FileSnapshot { Path = "D:\\archive\\" + Guid.NewGuid(), Length = length };
        [Fact] public async Task ContentsSurviveAndFifoEvictsOnlyOldCommittedEntries()
        {
            using (var cache = new RecoveryCache(1024 * 1024))
            {
                var bytes = new byte[600000]; new Random(12).NextBytes(bytes);
                var a = await cache.CaptureAsync(File(bytes.Length), new MemoryStream(bytes), "a", "run", "p", 0, CancellationToken.None); cache.Commit(a.Id, true);
                using (var pin = cache.Pin(a.Id))
                    await Assert.ThrowsAsync<CacheCapacityException>(() => cache.CaptureAsync(File(bytes.Length), new MemoryStream(bytes), "b", "run", "p", 0, CancellationToken.None));
                var b = await cache.CaptureAsync(File(bytes.Length), new MemoryStream(bytes), "b", "run", "p", 0, CancellationToken.None); cache.Commit(b.Id, true);
                Assert.Single(cache.List()); Assert.Equal(b.Id, cache.List()[0].Id); Assert.True(cache.Used <= cache.Capacity);
                Assert.Throws<InvalidOperationException>(() => cache.Pin(a.Id));
                using (var pin = cache.Pin(b.Id))
                using (var data = pin.OpenContent())
                using (var output = new MemoryStream())
                { await data.CopyToAsync(output); Assert.Equal(bytes, output.ToArray()); }
            }
        }
        [Fact] public async Task CancelledPreparationReturnsAllReservations()
        {
            using (var cache = new RecoveryCache(1024 * 1024))
            {
                var cancelled = new CancellationToken(true);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.CaptureAsync(File(100), new MemoryStream(new byte[100]), "a", "r", "p", 0, cancelled));
                Assert.Equal(0, cache.Used); Assert.Empty(cache.List());
            }
        }
        [Fact] public async Task TruncatedInputIsNotRecoverable()
        {
            using (var cache = new RecoveryCache(1024 * 1024))
            {
                await Assert.ThrowsAsync<FileChangedException>(() => cache.CaptureAsync(File(1000), new MemoryStream(new byte[20]), "a", "r", "p", 0, CancellationToken.None));
                Assert.Equal(0, cache.Used);
            }
        }
        [Fact] public void ExactBudgetFileCannotFitBecauseMetadataAlsoConsumesBudget()
        {
            using (var cache = new RecoveryCache(1024 * 1024)) Assert.False(cache.CanFit(File(1024 * 1024)));
        }
        [Fact] public async Task ParallelPreparationNeverOvercommits()
        {
            using (var cache = new RecoveryCache(1024 * 1024))
            {
                var tasks = Enumerable.Range(0, 10).Select(async i =>
                {
                    try { var item = await cache.CaptureAsync(File(300000), new MemoryStream(new byte[300000]), i.ToString(), "r", "p", 0, CancellationToken.None); cache.Commit(item.Id, true); }
                    catch (CacheCapacityException) { }
                    Assert.True(cache.Used <= cache.Capacity);
                });
                await Task.WhenAll(tasks); Assert.True(cache.Used <= cache.Capacity);
            }
        }
        [Fact] public async Task GrowPreservesContentsHashAndEpochWithoutAllocatingTheNewBudget()
        {
            using (var cache = new RecoveryCache(AppSettings.MinimumCacheBytes))
            {
                var bytes = new byte[200000]; new Random(42).NextBytes(bytes);
                var entry = await Capture(cache, bytes, "first");
                string epoch = cache.Epoch; long used = cache.Used, allocated = AllocatedBytes(cache);
                cache.Resize(AppSettings.MaximumCacheBytes);
                Assert.Equal(AppSettings.MaximumCacheBytes, cache.Capacity);
                Assert.Equal(epoch, cache.Epoch); Assert.Equal(used, cache.Used); Assert.Equal(allocated, AllocatedBytes(cache));
                Assert.Equal(entry.Hash, cache.List().Single().Hash);
                Assert.True(cache.CanFit(File(2L * 1073741824)));
                Assert.False(cache.CanFit(File(long.MaxValue)));
                Assert.False(cache.CanFit(File(-1)));
                Assert.Equal(bytes, Read(cache, entry.Id));
            }
        }
        [Fact] public async Task ShrinkEvictsOldestFirstAndReleasesPooledBlocksBeforeFurtherCaptures()
        {
            var removed = new List<string>();
            using (var cache = new RecoveryCache(4L * 1048576, (id, reason) => { if (reason == "CapacityReduced") removed.Add(id); }))
            {
                var first = await Capture(cache, new byte[500000], "first"); await Task.Delay(20);
                var second = await Capture(cache, new byte[500000], "second"); await Task.Delay(20);
                var lastBytes = new byte[500000]; lastBytes[499999] = 123;
                var last = await Capture(cache, lastBytes, "last");
                cache.Resize(AppSettings.MinimumCacheBytes);
                Assert.Equal(new[] { first.Id, second.Id }, removed);
                Assert.Equal(last.Id, cache.List().Single().Id); Assert.Equal(lastBytes, Read(cache, last.Id));
                Assert.True(cache.Used <= cache.Capacity);
                Assert.True(AllocatedBytes(cache) <= cache.Capacity - cache.Capacity / 8);
                Assert.True(cache.CanFit(File(800000))); Assert.False(cache.CanFit(File(950000)));
                var next = await Capture(cache, new byte[800000], "next");
                Assert.Equal(next.Id, cache.List().Single().Id);
                Assert.Equal(800000, Read(cache, next.Id).Length);
                Assert.True(AllocatedBytes(cache) <= cache.Capacity - cache.Capacity / 8);
            }
        }
        [Fact] public async Task ShrinkEnforcesTheSmallerMetadataBudgetAsWellAsContentBytes()
        {
            using (var cache = new RecoveryCache(8L * 1048576))
            {
                for (int i = 0; i < 40; i++) await Capture(cache, new byte[0], i.ToString());
                Assert.Equal(40, cache.List().Count);
                cache.Resize(AppSettings.MinimumCacheBytes);
                Assert.True(cache.List().Count < 40);
                Assert.True(cache.Used <= cache.Capacity / 8);
            }
        }
        [Fact] public async Task PinnedShrinkFailureDoesNotEvictAnyOtherEntryOrPersist()
        {
            var removed = new List<string>();
            using (var cache = new RecoveryCache(4L * 1048576, (id, reason) => removed.Add(id)))
            {
                var old = await Capture(cache, new byte[400000], "old");
                var pinned = await Capture(cache, new byte[1200000], "pinned");
                long used = cache.Used; bool persisted = false;
                using (cache.Pin(pinned.Id))
                    Assert.Throws<CacheCapacityException>(() => cache.Resize(AppSettings.MinimumCacheBytes, () => persisted = true));
                Assert.False(persisted); Assert.Empty(removed);
                Assert.Equal(4L * 1048576, cache.Capacity); Assert.Equal(used, cache.Used);
                Assert.Equal(2, cache.List().Count); Assert.Equal(400000, Read(cache, old.Id).Length);
            }
        }
        [Fact] public async Task PreparingAndUncommittedReadyEntriesBlockShrinkWithoutPartialEviction()
        {
            var removed = new List<string>();
            using (var cache = new RecoveryCache(4L * 1048576, (id, reason) => removed.Add(id)))
            using (var input = new GatedStream(new byte[1200000]))
            {
                var old = await Capture(cache, new byte[400000], "old");
                var preparation = cache.CaptureAsync(File(1200000), input, "preparing", "run", "p", 0, CancellationToken.None);
                await input.Started.Task;
                long used = cache.Used;
                try
                {
                    Assert.Throws<CacheCapacityException>(() => cache.Resize(AppSettings.MinimumCacheBytes));
                    Assert.Empty(removed); Assert.Equal(used, cache.Used);
                    Assert.Equal(old.Id, cache.List().Single().Id);
                }
                finally { input.Release.TrySetResult(true); }
                var ready = await preparation;
                Assert.Throws<CacheCapacityException>(() => cache.Resize(AppSettings.MinimumCacheBytes));
                Assert.Empty(removed); Assert.Equal(used, cache.Used);
                cache.Commit(ready.Id, true);
                Assert.Equal(2, cache.List().Count);
            }
        }
        [Fact] public async Task InvalidCapacityAndPersistenceFailureLeaveTheCacheUnchanged()
        {
            var removed = new List<string>();
            using (var cache = new RecoveryCache(4L * 1048576, (id, reason) => removed.Add(id)))
            {
                var entry = await Capture(cache, new byte[1200000], "entry");
                string epoch = cache.Epoch; long used = cache.Used, allocated = AllocatedBytes(cache);
                Assert.Throws<ArgumentOutOfRangeException>(() => cache.Resize(AppSettings.MinimumCacheBytes - 1));
                Assert.Throws<ArgumentOutOfRangeException>(() => cache.Resize(AppSettings.MaximumCacheBytes + 1));
                Assert.Throws<IOException>(() => cache.Resize(AppSettings.MinimumCacheBytes, () => { throw new IOException("settings write failed"); }));
                Assert.Equal(4L * 1048576, cache.Capacity); Assert.Equal(used, cache.Used); Assert.Equal(allocated, AllocatedBytes(cache));
                Assert.Equal(epoch, cache.Epoch); Assert.Equal(entry.Id, cache.List().Single().Id); Assert.Empty(removed);
                Assert.Equal(1200000, Read(cache, entry.Id).Length);
            }
            var disposed = new RecoveryCache(AppSettings.MinimumCacheBytes); disposed.Dispose();
            Assert.Throws<ObjectDisposedException>(() => disposed.Resize(AppSettings.DefaultCacheBytes));
        }
        [Fact] public async Task AuditCallbackFailureDuringShrinkLeavesConsistentOldBudgetAndPool()
        {
            bool fail = false;
            using (var cache = new RecoveryCache(4L * 1048576, (id, reason) => { if (fail && reason == "CapacityReduced") throw new IOException("audit failed"); }))
            {
                await Capture(cache, new byte[600000], "a"); await Capture(cache, new byte[600000], "b");
                fail = true;
                Assert.Throws<IOException>(() => cache.Resize(AppSettings.MinimumCacheBytes));
                Assert.Equal(4L * 1048576, cache.Capacity);
                Assert.True(cache.Used <= cache.Capacity); Assert.Single(cache.List());
                fail = false;
                cache.Resize(AppSettings.MinimumCacheBytes);
                Assert.True(cache.Used <= cache.Capacity);
                Assert.True(AllocatedBytes(cache) <= cache.Capacity - cache.Capacity / 8);
                await Capture(cache, new byte[800000], "after-failure");
                Assert.True(cache.Used <= cache.Capacity);
            }
        }
        [Fact] public async Task ParallelResizeAndCaptureKeepReservationsWithinThePublishedBudget()
        {
            using (var cache = new RecoveryCache(4L * 1048576))
            {
                var captures = Enumerable.Range(0, 20).Select(i => Task.Run(async () =>
                {
                    try { await Capture(cache, new byte[300000], i.ToString()); }
                    catch (CacheCapacityException) { }
                })).ToArray();
                var resize = Task.Run(() =>
                {
                    for (int i = 0; i < 40; i++)
                    {
                        try { cache.Resize(i % 2 == 0 ? AppSettings.MinimumCacheBytes : 4L * 1048576); }
                        catch (CacheCapacityException) { }
                    }
                });
                await Task.WhenAll(captures.Concat(new[] { resize }));
                cache.Resize(AppSettings.MinimumCacheBytes);
                Assert.True(cache.Used <= cache.Capacity);
                Assert.True(AllocatedBytes(cache) <= cache.Capacity - cache.Capacity / 8);
                foreach (var entry in cache.List()) Assert.Equal(entry.Length, Read(cache, entry.Id).LongLength);
            }
        }

        private static async Task<RecoveryInfo> Capture(RecoveryCache cache, byte[] bytes, string id)
        {
            using (var input = new MemoryStream(bytes))
            {
                var result = await cache.CaptureAsync(File(bytes.Length), input, id, "run", "p", 0, CancellationToken.None);
                cache.Commit(result.Id, true); return result;
            }
        }
        private static byte[] Read(RecoveryCache cache, string id)
        {
            using (var pin = cache.Pin(id))
            using (var input = pin.OpenContent())
            using (var output = new MemoryStream())
            { input.CopyTo(output); return output.ToArray(); }
        }
        // Used reports live reservations; this diagnostic separately verifies actual retained pool blocks.
        private static long AllocatedBytes(RecoveryCache cache) =>
            (long)typeof(RecoveryCache).GetField("_allocatedBlocks", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cache) * (65536 + 32);

        private sealed class GatedStream : MemoryStream
        {
            public readonly TaskCompletionSource<bool> Started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public readonly TaskCompletionSource<bool> Release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public GatedStream(byte[] bytes) : base(bytes) { }
            public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            {
                Started.TrySetResult(true); await Release.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return await base.ReadAsync(buffer, offset, count, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
