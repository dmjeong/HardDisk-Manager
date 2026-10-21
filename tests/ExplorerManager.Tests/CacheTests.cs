using System;
using System.IO;
using System.Linq;
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
        private static FileSnapshot File(int length) => new FileSnapshot { Path = "D:\\archive\\" + Guid.NewGuid(), Length = length };
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
    }
}
