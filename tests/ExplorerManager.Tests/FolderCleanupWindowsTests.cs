using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Application;
using ExplorerManager.Domain;
using ExplorerManager.Infrastructure;
using Xunit;

namespace ExplorerManager.Tests
{
    public sealed class FolderCleanupWindowsTests
    {
        [WindowsFact]
        public async Task TargetReachedFinishesNestedFolderAndExpiredParentsBeforeNextFolder()
        {
            using (var f = new NativeFolderFixture())
            {
                string first = f.File("active\\deep\\nested\\oldest.bin", 90, 120);
                string later = f.File("active\\newer.bin", 40, 60);
                string next = f.File("next\\interleaved.bin", 60, 60);
                f.ExpireDirectories();

                var run = await f.Engine.RunAsync(f.Policy, false, "native-folder-test", CancellationToken.None);

                Assert.Equal("TargetReached", run.State);
                Assert.Equal(2, run.DeletedFiles);
                Assert.Equal(3, run.DeletedDirectories);
                Assert.Equal(180, run.DeletedBytes);
                Assert.Equal(100, run.FreeBefore);
                Assert.Equal(280, run.FreeAfter);
                Assert.False(File.Exists(first)); Assert.False(File.Exists(later));
                Assert.False(Directory.Exists(Path.Combine(f.Root, "active")));
                Assert.True(File.Exists(next)); Assert.True(Directory.Exists(f.Root));
                Assert.DoesNotContain(f.Store.History(null, 0).Items, item => item.Path == next);
            }
        }

        [WindowsFact]
        public async Task FinishingFolderKeepsRecentProtectedExcludedAndLockedChildren()
        {
            using (var f = new NativeFolderFixture())
            {
                string first = f.File("active\\oldest.bin", 90, 120);
                string later = f.File("active\\sub\\eligible.bin", 40, 60);
                string recent = f.File("active\\recent.bin", 1, 20);
                string protectedPath = f.File("active\\sub\\protected.bin", 60, 20);
                string excluded = f.File("active\\excluded.bin", 70, 20);
                string locked = f.File("active\\locked.bin", 80, 20);
                string next = f.File("next\\interleaved.bin", 50, 60);
                f.Policy.Exclusions.Add(excluded);
                var protectedSnapshot = f.Files.Inspect(new FileSnapshot { Path = protectedPath, Root = f.Root }, f.Policy);
                f.Store.Protect(protectedSnapshot, DateTime.UtcNow.AddDays(1));
                f.ExpireDirectories();

                using (var held = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var run = await f.Engine.RunAsync(f.Policy, false, "native-folder-test", CancellationToken.None);
                    Assert.Equal("TargetReached", run.State);
                    Assert.Equal(2, run.DeletedFiles); Assert.Equal(0, run.DeletedDirectories);
                    Assert.False(File.Exists(first)); Assert.False(File.Exists(later));
                    Assert.True(File.Exists(recent)); Assert.True(File.Exists(protectedPath));
                    Assert.True(File.Exists(excluded)); Assert.True(File.Exists(locked)); Assert.True(File.Exists(next));
                    Assert.True(Directory.Exists(Path.Combine(f.Root, "active", "sub")));
                    Assert.Contains(f.Store.Alerts(), alert => alert.Category == "LockedFile" && alert.Path == locked);
                }
                foreach (string survivor in new[] { recent, protectedPath, excluded, locked })
                    Assert.Equal(20, new FileInfo(survivor).Length);
            }
        }

        [WindowsFact]
        public async Task ExactPathRetryDoesNotSweepSiblingsOrNewlyEmptyParents()
        {
            using (var f = new NativeFolderFixture())
            {
                string target = f.File("active\\deep\\target.bin", 60, 120);
                string sibling = f.File("active\\older-sibling.bin", 90, 60);
                string next = f.File("next\\other.bin", 80, 60);
                f.ExpireDirectories();

                var run = await f.Engine.RunAsync(f.Policy, false, "native-retry-test", CancellationToken.None, target);

                Assert.Equal(1, run.DeletedFiles); Assert.Equal(0, run.DeletedDirectories);
                Assert.False(File.Exists(target)); Assert.True(File.Exists(sibling)); Assert.True(File.Exists(next));
                Assert.True(Directory.Exists(Path.Combine(f.Root, "active", "deep")));
                var deleted = f.Store.History(null, 0).Items.Where(item => item.State == "DeletedConfirmed").ToList();
                Assert.Single(deleted); Assert.Equal(target, deleted[0].Path);
            }
        }

        private sealed class NativeFolderFixture : IDisposable
        {
            private readonly DateTime _createdUtc = DateTime.UtcNow;
            private readonly ObservedDeletionVolumes _targetVolumes;
            private string Base { get; } = Path.Combine(Path.GetTempPath(), "ExplorerFolder-" + Guid.NewGuid().ToString("N"));
            public string Root { get; }
            public DrivePolicy Policy { get; }
            public SqliteJournal Store { get; }
            public RecoveryCache Cache { get; }
            public WindowsFileSystem Files { get; }
            public CleanupEngine Engine { get; }

            public NativeFolderFixture()
            {
                Root = Path.Combine(Base, "managed"); Directory.CreateDirectory(Root);
                var nativeVolume = WindowsVolumeCatalog.Read(Path.GetPathRoot(Root));
                Assert.Equal("NTFS", nativeVolume.FileSystem);
                Policy = new DrivePolicy { VolumeId = nativeVolume.Id, Roots = new List<string> { Root }, StartThreshold = 90, StopThreshold = 80 };
                Store = new SqliteJournal(Path.Combine(Base, "audit.db"));
                Cache = new RecoveryCache(1024 * 1024, Store.SetRecoveryState);
                Files = new WindowsFileSystem(new WindowsVolumeCatalog());
                _targetVolumes = new ObservedDeletionVolumes(nativeVolume);
                Engine = new CleanupEngine(_targetVolumes, Files, Store, Cache, new SystemClock(), new AlwaysRun(), new RestartManagerInspector());
            }

            public string File(string relativePath, int ageDays, int length)
            {
                string path = Path.Combine(Root, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                System.IO.File.WriteAllBytes(path, Enumerable.Repeat((byte)0x5A, length).ToArray());
                System.IO.File.SetLastWriteTimeUtc(path, _createdUtc.AddDays(-ageDays));
                _targetVolumes.Track(path, length);
                return path;
            }

            public void ExpireDirectories()
            {
                foreach (string directory in Directory.GetDirectories(Root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
                    Directory.SetLastWriteTimeUtc(directory, _createdUtc.AddDays(-80));
            }

            public void Dispose()
            {
                Cache.Dispose(); Store.Dispose();
                Directory.Delete(Base, true);
            }
        }

        // Target accounting is deterministic and follows actual namespace deletion, never real disk capacity.
        private sealed class ObservedDeletionVolumes : IVolumeCatalog
        {
            private readonly VolumeStatus _identity;
            private readonly Dictionary<string, long> _tracked = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            public ObservedDeletionVolumes(VolumeStatus identity) { _identity = identity; }
            public void Track(string path, long bytes) { _tracked.Add(path, bytes); }
            public IList<VolumeStatus> List() => new[] { Get(_identity.Id) };
            public VolumeStatus Get(string volumeId)
            {
                if (!string.Equals(volumeId, _identity.Id, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected test volume identity.");
                long free = 100 + _tracked.Where(entry => !System.IO.File.Exists(entry.Key)).Sum(entry => entry.Value);
                return new VolumeStatus { Id = _identity.Id, Mount = _identity.Mount, FileSystem = _identity.FileSystem,
                    TotalBytes = 1000, FreeBytes = free, AvailableBytes = free };
            }
        }

        private sealed class AlwaysRun : IExecutionGate
        {
            public string StopReason(DrivePolicy policy) => null;
        }
    }
}
