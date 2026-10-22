using System;
using System.Collections.Generic;
using System.ComponentModel;
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
    public sealed class Fixture : IDisposable, IVolumeCatalog, IClock, IExecutionGate, ILockInspector, IFileSystem
    {
        public readonly string DirectoryPath = Path.Combine(Path.GetTempPath(), "explorer-tests-" + Guid.NewGuid().ToString("N"));
        public readonly SqliteJournal Store;
        public readonly RecoveryCache Cache;
        public readonly List<FileSnapshot> Files = new List<FileSnapshot>();
        public readonly List<string> Deleted = new List<string>();
        public readonly HashSet<string> Locked = new HashSet<string>();
        public readonly DrivePolicy Policy = new DrivePolicy { VolumeId = "volume", StartThreshold = 90, StopThreshold = 80, Roots = new List<string> { "D:\\a", "D:\\b" } };
        public long Free = 100;
        public bool ScanTimeout;
        public string Gate;
        public Action<FileSnapshot> AfterDelete;
        public Action<VolumeStatus> AfterSpaceRead;
        public Func<FileSnapshot, FileSnapshot> EmptyParent;
        public Fixture() { Directory.CreateDirectory(DirectoryPath); Store = new SqliteJournal(Path.Combine(DirectoryPath, "test.db")); Cache = new RecoveryCache(1024 * 1024, Store.SetRecoveryState); }
        public CleanupEngine Engine(IJournal journal = null, IRecoveryCache cache = null) => new CleanupEngine(this, this, journal ?? Store, cache ?? Cache, this, this, this);
        public FileSnapshot Add(string path, int days, int length = 60)
        {
            var f = new FileSnapshot { Path = path, Root = Policy.Roots.First(x => path.StartsWith(x + "\\", StringComparison.OrdinalIgnoreCase)), ParentPath = Path.GetDirectoryName(path), VolumeId = "volume", FileId = Guid.NewGuid().ToString(), Length = length,
                LastWriteUtc = UtcNow.AddDays(-days), CreationUtc = UtcNow.AddDays(-days), AgeUtc = UtcNow.AddDays(-days) };
            Files.Add(f); return f;
        }
        public IList<VolumeStatus> List() => new[] { Get("volume") };
        public VolumeStatus Get(string id)
        { var snapshot = new VolumeStatus { Id = "volume", TotalBytes = 1000, FreeBytes = Free }; AfterSpaceRead?.Invoke(snapshot); return snapshot; }
        public DateTime UtcNow { get; } = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc);
        public string StopReason(DrivePolicy p) => Gate;
        public IList<ProcessIdentity> Inspect(string p) => new List<ProcessIdentity>();
        public Task ScanAsync(DrivePolicy p, DateTime cutoff, string id, IJournal j, CancellationToken ct)
        { foreach (var f in Files) j.AddCandidate(id, f); if (ScanTimeout) throw new TimeoutException(); return Task.CompletedTask; }
        public IFileLease OpenVerified(FileSnapshot f, DrivePolicy p)
        { if (Locked.Contains(f.Path)) throw new Win32Exception(32); return new Lease(this, f); }
        public bool IsAbsent(FileSnapshot f) => Deleted.Contains(f.Path);
        public FileSnapshot FindEmptyParent(FileSnapshot d, DrivePolicy p, DateTime c) => EmptyParent?.Invoke(d);
        public Task<FileSnapshot> RestoreAsync(FileSnapshot original, Stream input, string hash, string target, CancellationToken c) => Task.FromResult(original);
        private sealed class Lease : IFileLease
        {
            private readonly Fixture _f;
            public Lease(Fixture f, FileSnapshot snapshot) { _f = f; Snapshot = snapshot; Content = new MemoryStream(new byte[snapshot.Length]); }
            public FileSnapshot Snapshot { get; }
            public Stream Content { get; }
            public void MarkForDeletion() { _f.Deleted.Add(Snapshot.Path); _f.Free += Snapshot.Length; _f.AfterDelete?.Invoke(Snapshot); }
            public void Dispose() => Content.Dispose();
        }
        public void Dispose() { Cache.Dispose(); Store.Dispose(); Directory.Delete(DirectoryPath, true); }
    }
    public sealed class EngineTests
    {
        [Fact] public async Task LooseFilesKeepGlobalOldestOrderAndStopBetweenFiles()
        {
            using (var f = new Fixture())
            {
                f.Add("D:\\b\\new", 31); f.Add("D:\\a\\old", 60); f.Add("D:\\b\\middle", 40);
                var run = await f.Engine().RunAsync(f.Policy, false, "test", CancellationToken.None);
                Assert.Equal(new[] { "D:\\a\\old", "D:\\b\\middle" }, f.Deleted);
                Assert.Equal("TargetReached", run.State); Assert.Equal(120, run.DeletedBytes); Assert.Equal(120, run.ObservedFreeDelta);
            }
        }
        [Fact] public async Task PreviewNeverDeletesOrAllocatesRecoveryAndKeepsHistory()
        {
            using (var f = new Fixture())
            {
                f.Free = 900; f.Add("D:\\a\\file", 60);
                var run = await f.Engine().RunAsync(f.Policy, true, "test", CancellationToken.None);
                Assert.Empty(f.Deleted); Assert.Equal(0, f.Cache.Used); Assert.Equal("PreviewComplete", run.State);
                Assert.Equal("WouldDelete", f.Store.History(null, 0).Items.Single().State);
            }
        }
        [Fact] public async Task LockedOldestFileDoesNotBlockOtherCandidates()
        {
            using (var f = new Fixture())
            {
                f.Add("D:\\a\\locked", 60); f.Add("D:\\b\\free", 40, 120); f.Locked.Add("D:\\a\\locked");
                var run = await f.Engine().RunAsync(f.Policy, false, "test", CancellationToken.None);
                Assert.Single(f.Deleted); Assert.Equal(1, run.Skipped); Assert.Single(f.Store.Alerts());
                Assert.Equal("LockedFile", f.Store.Alerts()[0].Category);
            }
        }
        [Fact] public async Task RecentAndProtectedFilesAreKeptEvenWhenFull()
        {
            using (var f = new Fixture())
            {
                f.Add("D:\\a\\recent", 1);
                var protectedFile = f.Add("D:\\a\\restored", 60); f.Store.Protect(protectedFile, f.UtcNow.AddDays(1));
                await f.Engine().RunAsync(f.Policy, false, "test", CancellationToken.None); Assert.Empty(f.Deleted);
            }
        }
        [Fact] public async Task IncompleteSnapshotDoesNotDelete()
        {
            using (var f = new Fixture())
            {
                f.Add("D:\\a\\old", 60); f.ScanTimeout = true;
                var run = await f.Engine().RunAsync(f.Policy, false, "test", CancellationToken.None);
                Assert.Equal("ScanIncomplete", run.State); Assert.Empty(f.Deleted);
            }
        }
        [Fact] public async Task OversizeFilesAreSkippedWithoutPermanentDeletion()
        {
            using (var f = new Fixture())
            {
                f.Add("D:\\a\\huge", 60, 2000000);
                var run = await f.Engine().RunAsync(f.Policy, false, "test", CancellationToken.None);
                Assert.Equal(1, run.Skipped); Assert.Empty(f.Deleted);
            }
        }
        [Fact] public void JournalReconciliationNeverClaimsInterruptedIntentSucceeded()
        {
            using (var f = new Fixture())
            {
                var item = new ItemRecord { RunId = "run", File = f.Add("D:\\a\\file", 60), Utc = f.UtcNow, State = "DeleteIntent", RecoveryState = "CacheReady" };
                f.Store.RecordItem(item); f.Store.Reconcile();
                var recovered = f.Store.History(null, 0).Items.Single();
                Assert.Equal("OutcomeUnknown", recovered.State); Assert.Equal("UnavailableAfterRestart", recovered.RecoveryState);
            }
        }
        [Fact] public void HistorySearchTreatsPercentAndUnderscoreLiterally()
        {
            using (var f = new Fixture())
            {
                f.Store.RecordItem(new ItemRecord { RunId = "r", Utc = f.UtcNow, File = f.Add("D:\\a\\100%_data", 60), State = "Skipped" });
                f.Store.RecordItem(new ItemRecord { RunId = "r", Utc = f.UtcNow, File = f.Add("D:\\a\\100AAdata", 60), State = "Skipped" });
                Assert.Single(f.Store.History("%_", 0).Items);
            }
        }
        [Fact] public void RequestsAreDurablyDeduplicatedAndMismatchedPayloadIsRejected()
        {
            using (var f = new Fixture())
            {
                f.Store.BeginRequest("sid", "id", "hash");
                Assert.Throws<InvalidOperationException>(() => f.Store.RequestReceipt("sid", "id", "hash"));
                f.Store.EndRequest("sid", "id", new ExplorerManager.Contracts.Response { Id = "id", Success = true });
                Assert.True(f.Store.RequestReceipt("sid", "id", "hash").Success);
                Assert.Throws<InvalidOperationException>(() => f.Store.RequestReceipt("sid", "id", "other"));
            }
        }
        [Fact] public void SettingsAndAuditAreCommittedTogether()
        {
            using (var f = new Fixture())
            {
                f.Store.SaveSettings(new AppSettings { CacheBytes = 256L * 1048576 }, "test-operator");
                using (var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + Path.Combine(f.DirectoryPath, "test.db") + ";Pooling=False"))
                {
                    connection.Open();
                    using (var audit = connection.CreateCommand())
                    {
                        audit.CommandText = "SELECT detail FROM events WHERE action='SettingsChanged' ORDER BY id DESC LIMIT 1";
                        var saved = Newtonsoft.Json.Linq.JObject.Parse((string)audit.ExecuteScalar());
                        Assert.Equal(256L * 1048576, (long)saved["CacheBytes"]);
                        Assert.Equal(60, (int)saved["InterlockTripSeconds"]);
                        Assert.Equal(300, (int)saved["LockedFileNotificationIntervalSeconds"]);
                    }
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "CREATE TRIGGER fail_settings_audit BEFORE INSERT ON events WHEN NEW.action='SettingsChanged' BEGIN SELECT RAISE(ABORT,'audit unavailable'); END";
                        command.ExecuteNonQuery();
                    }
                    Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => f.Store.SaveSettings(new AppSettings { CacheBytes = 512L * 1048576 }, "test-operator"));
                    Assert.Equal(256L * 1048576, f.Store.Settings().CacheBytes);
                }
            }
        }
        [Fact] public void HistorySizeMaintenancePreservesRecoveryIntentsAndUnresolvedAlerts()
        {
            using (var f = new Fixture())
            {
                for (int i = 0; i < 25; i++)
                    f.Store.RecordItem(new ItemRecord { RunId = "retention", Utc = f.UtcNow, File = f.Add(@"D:\a\old-" + i, 60), State = "Skipped", Reason = new string('x', 20000) });
                f.Store.RecordItem(new ItemRecord { RunId = "retention", Utc = f.UtcNow, File = f.Add(@"D:\a\pending", 60), State = "DeleteIntent", RecoveryState = "CacheReady" });
                f.Store.RecordItem(new ItemRecord { RunId = "retention", Utc = f.UtcNow, File = f.Add(@"D:\a\recoverable", 60), State = "DeletedConfirmed", RecoveryState = "Recoverable" });
                f.Store.SaveAlert(new AlertRecord { Utc = f.UtcNow, Acknowledged = false, Message = "unresolved" });
                f.Store.SaveAlert(new AlertRecord { Utc = f.UtcNow, Acknowledged = true, Message = "resolved" });
                f.Store.Maintain(new AppSettings { HistoryDays = 3650, HistoryMaxBytes = 256 * 1024 });
                var history = f.Store.History(null, 0).Items;
                Assert.Equal(2, history.Count); Assert.Contains(history, x => x.State == "DeleteIntent");
                Assert.Contains(history, x => x.RecoveryState == "Recoverable");
                Assert.Equal("unresolved", f.Store.Alerts().Single().Message);
            }
        }
        [Fact] public void ResourceLatchPersistenceKeepsOnlyBlockingConditionsAndSurvivesReopen()
        {
            string directory = Path.Combine(Path.GetTempPath(), "explorer-interlock-store-" + Guid.NewGuid().ToString("N"));
            string database = Path.Combine(directory, "audit.db");
            try
            {
                using (var store = new SqliteJournal(database))
                    store.SaveInterlocks(new[] { new InterlockStatus { Code = "LowMemory", Entity = "RAM", Blocking = true, Latched = true, State = "Tripped" },
                        new InterlockStatus { Code = "CpuLoad", Entity = "CPU", State = "Clear" } });
                using (var store = new SqliteJournal(database))
                {
                    Assert.Equal("LowMemory", store.LoadInterlocks().Single().Code);
                    store.SaveInterlocks(new InterlockStatus[0]); Assert.Empty(store.LoadInterlocks());
                    Assert.Equal(AppSettings.DefaultCacheBytes, store.Settings().CacheBytes);
                }
            }
            finally { Directory.Delete(directory, true); }
        }
        [Fact] public async Task InterlockCancellationDuringCacheCopyPreservesFileAndRecordsBlockingReason()
        {
            using (var f = new Fixture())
            using (var cancellation = new CancellationTokenSource())
            {
                f.Add(@"D:\a\batch\file", 60);
                var cache = new AfterCaptureCache(f.Cache, () => { f.Gate = "PausedByInterlock"; cancellation.Cancel(); });
                var run = await f.Engine(cache: cache).RunAsync(f.Policy, false, "test", cancellation.Token);
                Assert.Empty(f.Deleted); Assert.Empty(f.Cache.List()); Assert.Equal(0, f.Cache.Used);
                Assert.Equal("PausedByInterlock", run.State);
            }
        }
        [Fact] public async Task FolderSummariesDoNotDoubleCountChildren()
        {
            using (var f = new Fixture())
            {
                var a = f.Add("D:\\a\\first", 60);
                var b = f.Add("D:\\a\\second", 40);
                a.ParentPath = b.ParentPath = "D:\\a";
                await f.Engine().RunAsync(f.Policy, false, "test", CancellationToken.None);
                var folder = f.Store.Folders("", 0).Single();
                Assert.Equal(2, folder.DeletedFiles); Assert.Equal(120, folder.LogicalBytes); Assert.Equal(120, folder.ObservedFreeDelta);
            }
        }
        [Fact] public async Task RechecksSpaceAfterCopyBeforeDeleting()
        {
            using (var f = new Fixture())
            {
                f.Add("D:\\a\\first", 60);
                var wrapped = new AfterCaptureCache(f.Cache, () => f.Free = 500);
                var run = await f.Engine(cache: wrapped).RunAsync(f.Policy, false, "test", CancellationToken.None);
                Assert.Empty(f.Deleted); Assert.Empty(f.Cache.List()); Assert.Equal(0, f.Cache.Used); Assert.Equal("TargetReached", run.State);
            }
        }
        [Fact] public async Task AuditIntentFailurePreventsDeletionAndReleasesCache()
        {
            using (var f = new Fixture())
            {
                f.Add("D:\\a\\first", 60);
                await Assert.ThrowsAsync<InvalidOperationException>(() => f.Engine(new FailIntentJournal(f.Store)).RunAsync(f.Policy, false, "test", CancellationToken.None));
                Assert.Empty(f.Deleted); Assert.Equal(0, f.Cache.Used);
            }
        }
        [Fact] public async Task FinishesActiveFolderAfterTargetAndKeepsInterleavedNextFolder()
        {
            using (var f = new Fixture())
            {
                f.Add(@"D:\a\batch\first", 80, 120);
                f.Add(@"D:\a\other\middle", 70);
                f.Add(@"D:\a\batch\nested\second", 60);
                f.Add(@"D:\a\batch\last", 40);
                var run = await f.Engine().RunAsync(f.Policy, false, "test", CancellationToken.None);
                Assert.Equal(new[] { @"D:\a\batch\first", @"D:\a\batch\nested\second", @"D:\a\batch\last" }, f.Deleted);
                Assert.Equal("TargetReached", run.State); Assert.Equal(240, run.DeletedBytes);
            }
        }
        [Fact] public async Task TargetObservedInFolderStaysLatchedWhenExternalWriterConsumesSpace()
        {
            using (var f = new Fixture())
            {
                f.Add(@"D:\a\batch\first", 80, 120); f.Add(@"D:\a\other\middle", 70); f.Add(@"D:\a\batch\last", 40);
                f.AfterDelete = file => { if (file.Path.EndsWith("last")) f.Free = 0; };
                var run = await f.Engine().RunAsync(f.Policy, false, "test", CancellationToken.None);
                Assert.Equal(2, f.Deleted.Count); Assert.DoesNotContain(@"D:\a\other\middle", f.Deleted);
                Assert.Equal("TargetReached", run.State); Assert.Equal(-100, run.ObservedFreeDelta);
            }
        }
        [Fact] public async Task LatchesTheSameSpaceObservationRecordedInHistory()
        {
            using (var f = new Fixture())
            {
                var first = f.Add(@"D:\a\batch\first", 80, 120); f.Add(@"D:\a\other\middle", 70); f.Add(@"D:\a\batch\last", 40);
                bool consumeNextRead = false;
                f.AfterDelete = file => { if (file.Path == first.Path) consumeNextRead = true; };
                f.AfterSpaceRead = snapshot => { if (consumeNextRead) { f.Free = 0; consumeNextRead = false; } };
                var run = await f.Engine().RunAsync(f.Policy, false, "test", CancellationToken.None);
                Assert.Equal(2, f.Deleted.Count); Assert.DoesNotContain(@"D:\a\other\middle", f.Deleted);
                Assert.Equal(220, f.Store.History(null, 0).Items.Single(x => x.Path == first.Path).FreeAfter);
                Assert.Equal("TargetReached", run.State);
            }
        }
        [Fact] public async Task TargetDuringLaterCaptureDoesNotInterruptStartedFolder()
        {
            using (var f = new Fixture())
            {
                f.Add(@"D:\a\batch\first", 80); f.Add(@"D:\a\other\middle", 70);
                f.Add(@"D:\a\batch\second", 60); f.Add(@"D:\a\batch\last", 40);
                int captures = 0;
                var wrapped = new AfterCaptureCache(f.Cache, () => { if (++captures == 2) f.Free = 500; });
                var run = await f.Engine(cache: wrapped).RunAsync(f.Policy, false, "test", CancellationToken.None);
                Assert.Equal(3, f.Deleted.Count); Assert.DoesNotContain(@"D:\a\other\middle", f.Deleted);
                Assert.Equal("TargetReached", run.State);
            }
        }
        [Fact] public async Task TargetDuringFirstFolderCapturePreventsStartingDeletion()
        {
            using (var f = new Fixture())
            {
                f.Add(@"D:\a\batch\first", 80); f.Add(@"D:\a\batch\last", 40);
                var wrapped = new AfterCaptureCache(f.Cache, () => f.Free = 500);
                var run = await f.Engine(cache: wrapped).RunAsync(f.Policy, false, "test", CancellationToken.None);
                Assert.Empty(f.Deleted); Assert.Empty(f.Cache.List()); Assert.Equal("TargetReached", run.State);
            }
        }
        [Fact] public async Task NewlyEmptyAncestorsStayInActiveFolderAfterTarget()
        {
            using (var f = new Fixture())
            {
                var child = f.Add(@"D:\a\batch\nested\file", 80, 120);
                f.Add(@"D:\a\other\middle", 70);
                var nested = new FileSnapshot { Path = @"D:\a\batch\nested", Root = @"D:\a", IsDirectory = true,
                    FileId = "nested", AgeUtc = child.AgeUtc, LastWriteUtc = child.LastWriteUtc, CreationUtc = child.CreationUtc };
                var parent = new FileSnapshot { Path = @"D:\a\batch", Root = @"D:\a", IsDirectory = true,
                    FileId = "batch", AgeUtc = child.AgeUtc, LastWriteUtc = child.LastWriteUtc, CreationUtc = child.CreationUtc };
                f.EmptyParent = file => file.Path == child.Path ? nested : file.Path == nested.Path ? parent : null;
                var run = await f.Engine().RunAsync(f.Policy, false, "test", CancellationToken.None);
                Assert.Equal(new[] { child.Path, nested.Path, parent.Path }, f.Deleted);
                Assert.Equal(2, run.DeletedDirectories); Assert.Equal("TargetReached", run.State);
            }
        }
        [Fact] public async Task ExactPathRetryNeverDeletesSiblingOrEmptyAncestor()
        {
            using (var f = new Fixture())
            {
                f.Policy.StopThreshold = 0;
                f.Add(@"D:\a\batch\sibling", 80); var retry = f.Add(@"D:\a\batch\nested\retry", 60);
                f.EmptyParent = file => throw new InvalidOperationException("Exact retry must not inspect parents");
                await f.Engine().RunAsync(f.Policy, false, "test", CancellationToken.None, retry.Path);
                Assert.Equal(new[] { retry.Path }, f.Deleted);
            }
        }
        [Fact] public async Task ActiveFolderKeepsLockedRecentProtectedAndOversizeChildren()
        {
            using (var f = new Fixture())
            {
                var first = f.Add(@"D:\a\batch\first", 90, 120);
                var locked = f.Add(@"D:\a\batch\locked", 80); f.Locked.Add(locked.Path);
                f.Add(@"D:\a\batch\recent", 1);
                var restored = f.Add(@"D:\a\batch\restored", 70); f.Store.Protect(restored, f.UtcNow.AddDays(1));
                f.Add(@"D:\a\batch\huge", 60, 2000000);
                var last = f.Add(@"D:\a\batch\last", 40); f.Add(@"D:\a\other\middle", 50);
                var run = await f.Engine().RunAsync(f.Policy, false, "test", CancellationToken.None);
                Assert.Equal(new[] { first.Path, last.Path }, f.Deleted); Assert.Equal(4, run.Skipped);
                Assert.Single(f.Store.Alerts()); Assert.Equal("TargetReached", run.State);
            }
        }
        [Fact] public async Task HealthGateAfterCaptureInterruptsActiveFolderImmediately()
        {
            using (var f = new Fixture())
            {
                f.Add(@"D:\a\batch\first", 80, 120); f.Add(@"D:\a\batch\last", 40);
                int captures = 0;
                var wrapped = new AfterCaptureCache(f.Cache, () => { if (++captures == 2) f.Gate = "PausedByHealth"; });
                var run = await f.Engine(cache: wrapped).RunAsync(f.Policy, false, "test", CancellationToken.None);
                Assert.Single(f.Deleted); Assert.Single(f.Cache.List()); Assert.Equal("PausedByHealth", run.State);
            }
        }
        [Fact] public async Task CancellationInterruptsActiveFolderImmediately()
        {
            using (var f = new Fixture())
            using (var cancellation = new CancellationTokenSource())
            {
                f.Add(@"D:\a\batch\first", 80, 120); f.Add(@"D:\a\batch\last", 40);
                f.AfterDelete = file => cancellation.Cancel();
                var run = await f.Engine().RunAsync(f.Policy, false, "test", cancellation.Token);
                Assert.Single(f.Deleted); Assert.Equal("UserCancelled", run.State);
            }
        }
        [Fact] public async Task ItemLimitInterruptsActiveFolderImmediately()
        {
            using (var f = new Fixture())
            {
                f.Policy.MaxItems = 1; f.Add(@"D:\a\batch\first", 80, 120); f.Add(@"D:\a\batch\last", 40);
                var run = await f.Engine().RunAsync(f.Policy, false, "test", CancellationToken.None);
                Assert.Single(f.Deleted); Assert.Equal("LimitReached", run.State);
            }
        }
        [Fact] public async Task RunTimeLimitIsRecheckedAfterCaptureInsideActiveFolder()
        {
            using (var f = new Fixture())
            {
                f.Policy.MaxRunSeconds = 1; f.Add(@"D:\a\batch\first", 80, 120); f.Add(@"D:\a\batch\last", 40);
                int captures = 0;
                var wrapped = new AfterCaptureCache(f.Cache, () => { if (++captures == 2) Thread.Sleep(1100); });
                var run = await f.Engine(cache: wrapped).RunAsync(f.Policy, false, "test", CancellationToken.None);
                Assert.Single(f.Deleted); Assert.Single(f.Cache.List()); Assert.Equal("LimitReached", run.State);
            }
        }
        [Fact] public async Task MissingExactPathRetryDoesNotSweepOtherFiles()
        {
            using (var f = new Fixture())
            {
                f.Add(@"D:\a\batch\first", 80); f.Add(@"D:\a\batch\last", 40);
                await f.Engine().RunAsync(f.Policy, false, "test", CancellationToken.None, @"D:\a\batch\missing");
                Assert.Empty(f.Deleted); Assert.Empty(f.Cache.List());
            }
        }
        [Fact] public async Task ByteLimitStillProtectsRemainingChildrenAfterTarget()
        {
            using (var f = new Fixture())
            {
                f.Policy.MaxDeleteBytes = 120; f.Add(@"D:\a\batch\first", 80, 120); f.Add(@"D:\a\batch\last", 40);
                var run = await f.Engine().RunAsync(f.Policy, false, "test", CancellationToken.None);
                Assert.Single(f.Deleted); Assert.Equal(1, run.Skipped); Assert.Equal("TargetReached", run.State);
                Assert.Equal("BatchByteLimit", f.Store.History(null, 0).Items.Single(x => x.State == "Skipped").Reason);
            }
        }
        private sealed class AfterCaptureCache : IRecoveryCache
        {
            private readonly IRecoveryCache _inner; private readonly Action _after;
            public AfterCaptureCache(IRecoveryCache inner, Action after) { _inner = inner; _after = after; }
            public long Capacity => _inner.Capacity; public long Used => _inner.Used; public string Epoch => _inner.Epoch;
            public bool CanFit(FileSnapshot f) => _inner.CanFit(f);
            public async Task<RecoveryInfo> CaptureAsync(FileSnapshot f, Stream s, string i, string r, string p, long rate, CancellationToken ct)
            { var result = await _inner.CaptureAsync(f, s, i, r, p, rate, ct); _after(); return result; }
            public void Commit(string i, bool c) => _inner.Commit(i, c); public void Remove(string i, string r) => _inner.Remove(i, r);
            public IList<RecoveryInfo> List() => _inner.List(); public IRecoveryLease Pin(string i) => _inner.Pin(i);
        }
        private sealed class FailIntentJournal : IJournal
        {
            private readonly IJournal _j; public FailIntentJournal(IJournal j) { _j = j; }
            public void SaveRun(RunRecord r) => _j.SaveRun(r);
            public void RecordItem(ItemRecord i) { if (i.State == "DeleteIntent") throw new InvalidOperationException("Audit unavailable"); _j.RecordItem(i); }
            public void AddCandidate(string r, FileSnapshot f) => _j.AddCandidate(r, f);
            public FileSnapshot TakeNextCandidate(string r, string cleanupUnit = null) => _j.TakeNextCandidate(r, cleanupUnit);
            public void ClearCandidates(string r) => _j.ClearCandidates(r);
            public void SetCandidateTotals(RunRecord r) => _j.SetCandidateTotals(r);
            public void SaveAlert(AlertRecord a) => _j.SaveAlert(a);
            public void SetRecoveryState(string i, string s) => _j.SetRecoveryState(i, s);
            public bool IsProtected(FileSnapshot f, DateTime n) => _j.IsProtected(f, n);
            public void Protect(FileSnapshot f, DateTime d) => _j.Protect(f, d);
            public void RecordEvent(string a, string b, string c, string d) => _j.RecordEvent(a, b, c, d);
        }
    }
}
