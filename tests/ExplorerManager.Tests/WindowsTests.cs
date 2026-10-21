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
    public sealed class WindowsFactAttribute : FactAttribute
    {
        public WindowsFactAttribute() { if (Environment.OSVersion.Platform != PlatformID.Win32NT) Skip = "Windows NTFS integration"; }
    }
    public sealed class WindowsTests
    {
        [WindowsFact]
        public async Task NativeDeleteAndRestorePreserveContentsAndNeverOverwrite()
        {
            string root = Path.Combine(Path.GetTempPath(), "ExplorerNative-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                var volume = WindowsVolumeCatalog.Read(Path.GetPathRoot(root));
                var policy = new DrivePolicy { VolumeId = volume.Id, Roots = new List<string> { root }, StartThreshold = 0.000001, StopThreshold = 0 };
                string path = Path.Combine(root, "한글 file.bin"); var bytes = new byte[150000]; new Random(42).NextBytes(bytes);
                File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-60));
                using (var journal = new SqliteJournal(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db")))
                using (var cache = new RecoveryCache(1024 * 1024, journal.SetRecoveryState))
                {
                    var fs = new WindowsFileSystem(new WindowsVolumeCatalog());
                    var engine = new CleanupEngine(new WindowsVolumeCatalog(), fs, journal, cache, new SystemClock(), new AlwaysRun(), new RestartManagerInspector());
                    var run = await engine.RunAsync(policy, false, "integration", CancellationToken.None);
                    Assert.Equal(1, run.DeletedFiles); Assert.False(File.Exists(path));
                    var recovery = cache.List().Single();
                    File.WriteAllText(path, "new content");
                    string restored = await engine.RestoreAsync(recovery.Id, null, policy, "test", CancellationToken.None);
                    Assert.NotEqual(path, restored); Assert.Equal("new content", File.ReadAllText(path)); Assert.Equal(bytes, File.ReadAllBytes(restored));
                    Assert.True(File.GetLastWriteTimeUtc(restored) < DateTime.UtcNow.AddDays(-59));
                }
            }
            finally { Directory.Delete(root, true); }
        }
        [WindowsFact]
        public async Task NativeEmptyFolderDeletionAndRecovery()
        {
            string root = Path.Combine(Path.GetTempPath(), "ExplorerNative-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            try
            {
                string child = Path.Combine(root, "old"); Directory.CreateDirectory(child); Directory.SetLastWriteTimeUtc(child, DateTime.UtcNow.AddDays(-60));
                var volume = WindowsVolumeCatalog.Read(Path.GetPathRoot(root));
                var p = new DrivePolicy { VolumeId = volume.Id, Roots = new List<string> { root }, StartThreshold = 0.000001, StopThreshold = 0 };
                using (var journal = new SqliteJournal(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".db")))
                using (var cache = new RecoveryCache(1024 * 1024, journal.SetRecoveryState))
                {
                    var engine = new CleanupEngine(new WindowsVolumeCatalog(), new WindowsFileSystem(new WindowsVolumeCatalog()), journal, cache, new SystemClock(), new AlwaysRun(), new RestartManagerInspector());
                    var run = await engine.RunAsync(p, false, "test", CancellationToken.None); Assert.Equal(1, run.DeletedDirectories); Assert.False(Directory.Exists(child));
                    await engine.RestoreAsync(cache.List().Single().Id, null, p, "test", CancellationToken.None); Assert.True(Directory.Exists(child));
                }
            }
            finally { Directory.Delete(root, true); }
        }
        [WindowsFact]
        public void RejectsDriveRootAndWindowsDirectories()
        {
            var volume = WindowsVolumeCatalog.Read(Path.GetPathRoot(Environment.SystemDirectory));
            var p = new DrivePolicy { VolumeId = volume.Id, Roots = new List<string> { Path.GetPathRoot(Environment.SystemDirectory) } };
            Assert.ThrowsAny<Exception>(() => PathGuard.ValidatePolicy(p, new WindowsVolumeCatalog()));
            p.Roots[0] = Environment.SystemDirectory; Assert.ThrowsAny<Exception>(() => PathGuard.ValidatePolicy(p, new WindowsVolumeCatalog()));
        }
        private sealed class AlwaysRun : IExecutionGate { public string StopReason(DrivePolicy p) => null; }
    }
}
