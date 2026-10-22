using System;
using System.IO;
using ExplorerManager.Domain;
using ExplorerManager.Infrastructure;
using Xunit;

namespace ExplorerManager.Tests
{
    public sealed class CandidateQueueTests
    {
        [Theory]
        [InlineData(@"D:\archive", @"D:\archive\batch\nested\file.bin", @"D:\archive\batch")]
        [InlineData("D:/archive/", "d:/ARCHIVE/batch/nested/file.bin", @"d:\ARCHIVE\batch")]
        [InlineData(@"D:\archive", @"D:\archive\loose.bin", @"D:\archive\loose.bin")]
        [InlineData(@"D:\archive\", @"D:\archive\empty\", @"D:\archive\empty")]
        [InlineData(@"\\server\share\archive", @"\\SERVER\share\archive\batch\file", @"\\SERVER\share\archive\batch")]
        public void CleanupUnitUsesTheFirstSegmentBelowTheSnapshotRoot(string root, string path, string expected)
        { Assert.Equal(expected, PolicyRules.CleanupUnitPath(Snapshot(root, path))); }

        [Theory]
        [InlineData(@"D:\archive", @"D:\archive2\batch\file")]
        [InlineData(@"D:\archive", @"D:\archive")]
        [InlineData(@"D:\archive", @"D:\archive\")]
        [InlineData(@"D:\archive", @"E:\archive\batch\file")]
        [InlineData(@"D:\archive", @"D:\archive\..\other\file")]
        [InlineData(@"D:\archive", @"D:\archive\.\batch\file")]
        [InlineData(@"D:\archive", @"D:\archive\\batch\file")]
        [InlineData(null, @"D:\archive\batch\file")]
        [InlineData(@"D:\archive", null)]
        public void InvalidOrOutsideRootSnapshotsAreRejected(string root, string path)
        { Assert.Throws<ArgumentException>(() => PolicyRules.CleanupUnitPath(Snapshot(root, path))); }

        [Fact]
        public void NullSnapshotIsRejected()
        { Assert.Throws<ArgumentNullException>(() => PolicyRules.CleanupUnitPath(null)); }

        [Fact]
        public void ActiveUnitDrainsInAgeOrderBeforeReturningToGlobalOldest()
        {
            using (var q = new QueueScope())
            {
                q.Add(@"D:\archive\a\later.bin", 10);
                q.Add(@"D:\archive\b\middle.bin", 20);
                q.Add(@"D:\archive\a\oldest.bin", 30);
                var first = q.Store.TakeNextCandidate("run");
                Assert.Equal(@"D:\archive\a\oldest.bin", first.Path);
                string active = PolicyRules.CleanupUnitPath(first);
                Assert.Equal(@"D:\archive\a\later.bin", q.Store.TakeNextCandidate("run", active).Path);
                Assert.Null(q.Store.TakeNextCandidate("run", active));
                Assert.Equal(@"D:\archive\b\middle.bin", q.Store.TakeNextCandidate("run").Path);
                Assert.Null(q.Store.TakeNextCandidate("run"));
            }
        }

        [Fact]
        public void EqualAgesUsePathOrderAndProcessedItemsAreNotReturnedAgain()
        {
            using (var q = new QueueScope())
            {
                q.Add(@"D:\archive\a\z.bin", 30);
                q.Add(@"D:\archive\a\a.bin", 30);
                Assert.Equal(@"D:\archive\a\a.bin", q.Store.TakeNextCandidate("run", @"D:\archive\a").Path);
                Assert.Equal(@"D:\archive\a\z.bin", q.Store.TakeNextCandidate("run", @"D:\archive\a").Path);
                Assert.Null(q.Store.TakeNextCandidate("run", @"D:\archive\a"));
            }
        }

        [Fact]
        public void UnitKeyFoldsUnicodeCaseAndPathSeparatorsWithoutSqliteNoCase()
        {
            using (var q = new QueueScope())
            {
                q.Add(@"D:\archive\Résumé-Å-한글\first.bin", 30);
                q.Add("d:/ARCHIVE/rÉSUMÉ-å-한글/second.bin", 20);
                Assert.NotNull(q.Store.TakeNextCandidate("run", "d:/archive/résumé-å-한글/"));
                Assert.NotNull(q.Store.TakeNextCandidate("run", @"D:\ARCHIVE\RÉSUMÉ-Å-한글"));
                Assert.Null(q.Store.TakeNextCandidate("run", @"D:\archive\Résumé-Å-한글"));
            }
        }

        [Fact]
        public void SiblingPrefixesLooseFilesAndRunsRemainIsolated()
        {
            using (var q = new QueueScope())
            {
                q.Add(@"D:\archive\album\a.bin", 30);
                q.Add(@"D:\archive\album2\b.bin", 40);
                q.Add(@"D:\archive\loose.bin", 50);
                q.Add(@"D:\archive\album\other-run.bin", 60, run: "other");
                Assert.Equal(@"D:\archive\album\a.bin", q.Store.TakeNextCandidate("run", @"D:\archive\album").Path);
                Assert.Null(q.Store.TakeNextCandidate("run", @"D:\archive\album"));
                Assert.Equal(@"D:\archive\loose.bin", q.Store.TakeNextCandidate("run", @"D:\archive\loose.bin").Path);
                Assert.Null(q.Store.TakeNextCandidate("run", @"D:\archive\loose.bin"));
                Assert.Equal(@"D:\archive\album2\b.bin", q.Store.TakeNextCandidate("run").Path);
                Assert.NotNull(q.Store.TakeNextCandidate("other", @"D:\archive\album"));
            }
        }

        [Fact]
        public void MultipleAndNestedRootsUseTheirOwnSnapshotRootBoundary()
        {
            using (var q = new QueueScope())
            {
                q.Add(@"D:\archive\batch\a.bin", 30);
                q.Add(@"E:\archive\batch\b.bin", 40, @"E:\archive");
                q.Add(@"D:\archive\batch\nested\c.bin", 50, @"D:\archive\batch");
                Assert.Equal(@"D:\archive\batch\a.bin", q.Store.TakeNextCandidate("run", @"D:\archive\batch").Path);
                Assert.Null(q.Store.TakeNextCandidate("run", @"D:\archive\batch"));
                Assert.Equal(@"D:\archive\batch\nested\c.bin", q.Store.TakeNextCandidate("run", @"D:\archive\batch\nested").Path);
                Assert.Equal(@"E:\archive\batch\b.bin", q.Store.TakeNextCandidate("run", @"E:\archive\batch").Path);
            }
        }

        [Fact]
        public void NewlyEmptyParentsJoinTheSameUnitWithoutTakingASibling()
        {
            using (var q = new QueueScope())
            {
                q.Add(@"D:\archive\batch\nested\file.bin", 60);
                q.Add(@"D:\archive\other\older.bin", 90);
                string unit = PolicyRules.CleanupUnitPath(q.Store.TakeNextCandidate("run", @"D:\archive\batch"));
                q.Add(@"D:\archive\batch\nested", 30, directory: true);
                Assert.Equal(@"D:\archive\batch\nested", q.Store.TakeNextCandidate("run", unit).Path);
                q.Add(@"D:\archive\batch", 20, directory: true);
                Assert.Equal(@"D:\archive\batch", q.Store.TakeNextCandidate("run", unit).Path);
                Assert.Null(q.Store.TakeNextCandidate("run", unit));
                Assert.Equal(@"D:\archive\other\older.bin", q.Store.TakeNextCandidate("run").Path);
            }
        }

        [Fact]
        public void InvalidCandidatesAreRejectedBeforeTheyEnterTheQueue()
        {
            using (var q = new QueueScope())
            {
                Assert.Throws<ArgumentException>(() => q.Add(@"D:\archive2\outside.bin", 30));
                Assert.Null(q.Store.TakeNextCandidate("run"));
            }
        }

        private static FileSnapshot Snapshot(string root, string path, int days = 30, bool directory = false)
        {
            var age = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc).AddDays(-days);
            return new FileSnapshot { Root = root, Path = path, IsDirectory = directory, Length = directory ? 0 : 100,
                AgeUtc = age, LastWriteUtc = age, CreationUtc = age, VolumeId = "volume", FileId = Guid.NewGuid().ToString("N") };
        }

        private sealed class QueueScope : IDisposable
        {
            private readonly string _directory = Path.Combine(Path.GetTempPath(), "explorer-queue-" + Guid.NewGuid().ToString("N"));
            public SqliteJournal Store { get; }
            public QueueScope() { Store = new SqliteJournal(Path.Combine(_directory, "queue.db")); }
            public void Add(string path, int days, string root = @"D:\archive", bool directory = false, string run = "run")
            { Store.AddCandidate(run, Snapshot(root, path, days, directory)); }
            public void Dispose() { Store.Dispose(); Directory.Delete(_directory, true); }
        }
    }
}
