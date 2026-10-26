using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Application;
using ExplorerManager.Domain;
using ExplorerManager.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ExplorerManager.Tests
{
    public sealed class TransferTestContext : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "explorer-transfers-" + Guid.NewGuid().ToString("N"));
        public SqliteJournal Store { get; }
        public TransferConnection Connection { get; private set; }
        public TransferPolicy Policy { get; set; }
        public TestTransferSource Source { get; } = new TestTransferSource();
        public TestTransferRemote Remote { get; } = new TestTransferRemote();
        public TransferTestContext()
        {
            Directory.CreateDirectory(DirectoryPath); Store = new SqliteJournal(Path.Combine(DirectoryPath, "manager.db"));
            Connection = Store.SaveTransferConnection(new TransferConnection { Host = "localhost", UserName = "images", Encryption = TransferEncryption.Plain }, "test");
            Policy = Store.SaveTransferPolicy(new TransferPolicy { ConnectionId = Connection.Id, VolumeId = "volume", LocalRoot = @"D:\Images", Enabled = true,
                StableSeconds = 2, PublishCompatibilityConfirmed = true, ExclusiveDestinationConfirmed = true }, "test");
        }
        public TransferJob Add(string name = "one.png", int length = 4096)
        {
            byte[] data = new byte[length]; for (int i = 0; i < data.Length; i++) data[i] = (byte)(i % 251);
            Array.Copy(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, data, 8);
            var file = new FileSnapshot { Path = Policy.LocalRoot + "\\" + name, Root = Policy.LocalRoot, VolumeId = Policy.VolumeId, FileId = Guid.NewGuid().ToString("N"),
                Length = data.Length, CreationUtc = DateTime.UtcNow.AddDays(-60), LastWriteUtc = DateTime.UtcNow.AddDays(-40) };
            Source.Files[file.Path] = new TestTransferSource.Entry { File = file, Bytes = data };
            return Store.ObserveTransferFile(Policy, file, DateTime.UtcNow.AddSeconds(-5), false);
        }
        public async Task<TransferJob> Run(TransferJob selected = null, CancellationToken ct = default(CancellationToken))
        {
            var job = selected ?? Store.ClaimTransfer("test", Array.Empty<string>(), DateTime.UtcNow);
            Assert.NotNull(job);
            await new TransferCoordinator(Store, Source, c => Remote).RunAsync(job, ct);
            Store.ReleaseTransferJob(job); return Store.TransferJob(job.Id);
        }
        public void Dispose() { Store.Dispose(); Directory.Delete(DirectoryPath, true); }
        public static string Hash(byte[] data) { using (var sha = SHA256.Create()) return TransferRules.Hex(sha.ComputeHash(data)); }
    }
    public sealed class TestTransferSource : ITransferSource
    {
        public sealed class Entry { public FileSnapshot File; public byte[] Bytes; }
        public Dictionary<string, Entry> Files { get; } = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        public int Opens;
        public IEnumerable<FileSnapshot> Enumerate(TransferPolicy p, CancellationToken ct) => Files.Values.Select(x => x.File).Where(x => TransferRules.Matches(p, x));
        public ITransferReadLease OpenVerified(FileSnapshot expected)
        {
            Entry entry; if (!Files.TryGetValue(expected.Path, out entry)) throw new TransferFailure("SourceMissing", "missing");
            if (!TransferRules.SameFile(expected, entry.File)) throw new TransferFailure("SourceChanged", "changed", true);
            Opens++; return new Lease(entry);
        }
        private sealed class Lease : ITransferReadLease
        {
            public FileSnapshot Snapshot { get; } public Stream Content { get; }
            public Lease(Entry e) { Snapshot = e.File; Content = new MemoryStream(e.Bytes, false); }
            public void Dispose() => Content.Dispose();
        }
    }
    public sealed class TestTransferRemote : ITransferTransport
    {
        public Dictionary<string, byte[]> Files { get; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        public int Uploads, Publications, Removes;
        public long ResumeOffset;
        public bool ConsumeAndLoseReply, CorruptUpload, UnknownInspection, UnavailableVerification, RejectTransferReply;
        public Action BeforePublish;
        public Task PrepareAsync(TransferJob job, CancellationToken ct) => Task.CompletedTask;
        public Task<TransferCapabilities> ProbeAsync(CancellationToken ct) => Task.FromResult(new TransferCapabilities { ResumeAdvertised = true, UploadResumeVerified = true });
        public Task<TransferRemoteState> InspectAsync(string path, CancellationToken ct)
        { byte[] data; bool exists = Files.TryGetValue(path, out data); return Task.FromResult(new TransferRemoteState { Known = !UnknownInspection, Exists = exists, Length = data?.Length ?? 0 }); }
        public async Task<TransferUploadResult> UploadAsync(TransferJob job, Stream source, long offset, Action<TransferProgress> progress, CancellationToken ct)
        {
            Uploads++; ResumeOffset = offset;
            using (var data = new MemoryStream())
            {
                if (offset > 0) data.Write(Files[job.RemoteTemp], 0, (int)offset);
                await source.CopyToAsync(data, 4096, ct); var bytes = data.ToArray(); if (CorruptUpload) bytes[bytes.Length - 1] ^= 1;
                Files[job.RemoteTemp] = bytes;
                if (RejectTransferReply) throw new TransferFailure("TransferReplyUnconfirmed", "426", true);
                return new TransferUploadResult { Sent = bytes.Length, Reply = "226" };
            }
        }
        public Task<TransferVerificationResult> VerifyAsync(string path, long length, TransferVerification mode, Action<long> progress, CancellationToken ct)
        {
            if (UnavailableVerification && mode != TransferVerification.SizeOnly) throw new TransferFailure("VerificationUnavailable", "no RETR/hash");
            byte[] data; if (!Files.TryGetValue(path, out data) || data.Length != length) throw new TransferFailure("RemoteLengthMismatch", "length");
            progress?.Invoke(length);
            return Task.FromResult(new TransferVerificationResult { Sha256 = mode == TransferVerification.SizeOnly ? null : TransferTestContext.Hash(data),
                Level = mode == TransferVerification.SizeOnly ? TransferEvidenceLevel.SizeChecked : TransferEvidenceLevel.ContentVerified, Method = "test read-back" });
        }
        public Task<string> PublishAsync(TransferJob job, CancellationToken ct)
        {
            BeforePublish?.Invoke(); ct.ThrowIfCancellationRequested();
            if (Files.ContainsKey(job.RemoteFinal)) throw new TransferFailure("Conflict", "exists");
            Files[job.RemoteFinal] = Files[job.RemoteTemp]; Files.Remove(job.RemoteTemp); Publications++;
            if (ConsumeAndLoseReply) { Files.Remove(job.RemoteFinal); throw new TransferFailure("PublicationUnknown", "consumed; reply lost"); }
            return Task.FromResult("RNTO 250");
        }
        public Task RemoveOwnedTempAsync(TransferJob job, CancellationToken ct) { Files.Remove(job.RemoteTemp); Removes++; return Task.CompletedTask; }
        public void Dispose() { }
    }
    public sealed class TransferTests
    {
        [Fact] public async Task UploadPersistsStrongEvidenceAndDeletionRequiresCurrentHash()
        {
            using (var f = new TransferTestContext())
            {
                var job = f.Add(); Assert.False(f.Store.CheckDeletion(job.File).Allowed);
                var done = await f.Run(); Assert.Equal("Completed", done.State); Assert.Equal(1, f.Remote.Uploads); Assert.Equal(1, f.Remote.Publications);
                string hash = TransferTestContext.Hash(f.Source.Files[job.Path].Bytes);
                var allowed = f.Store.CheckDeletion(job.File, hash); Assert.True(allowed.Allowed); Assert.Single(allowed.EvidenceIds);
                Assert.False(f.Store.CheckDeletion(job.File, new string('a', 64)).Allowed);
                Assert.Single(f.Store.TransferEvidenceForJob(job.Id)); Assert.Single(f.Store.TransferAttempts(job.Id));
            }
        }
        [Fact] public void ScopeProtectsFilesBeforeWatcherOrInitialScanRegistersThem()
        {
            using (var f = new TransferTestContext())
            {
                var file = new FileSnapshot { Path = @"D:\Images\not-discovered.jpg", VolumeId = "volume", FileId = "unseen" };
                Assert.Equal("AwaitingVerifiedUpload", f.Store.CheckDeletion(file).Reason);
                file.Path = @"D:\ImagesOther\not-managed.jpg"; Assert.True(f.Store.CheckDeletion(file).Allowed);
                file.Path = @"D:\Images\notes.txt"; Assert.True(f.Store.CheckDeletion(file).Allowed);
            }
        }
        [Fact] public async Task CorruptedRemoteBytesNeverPublishOrUnlockCleanup()
        {
            using (var f = new TransferTestContext())
            { var j = f.Add(); f.Remote.CorruptUpload = true; var failed = await f.Run(); Assert.Equal("HashMismatch", failed.ErrorCode); Assert.Equal(0, f.Remote.Publications); Assert.False(f.Store.CheckDeletion(j.File).Allowed); }
        }
        [Fact] public async Task FalseTransferReplyCannotBecomeCompletedEvenWhenAllBytesArrive()
        {
            using (var f = new TransferTestContext())
            { var j = f.Add(); f.Remote.RejectTransferReply = true; var failed = await f.Run(); Assert.Equal("RetryWaiting", failed.State); Assert.Equal(0, f.Remote.Publications); Assert.False(f.Store.CheckDeletion(j.File).Allowed); }
        }
        [Fact] public async Task LostPublicationReplyWithImmediateConsumptionNeverBlindlyReuploads()
        {
            using (var f = new TransferTestContext())
            {
                var j = f.Add(); f.Remote.ConsumeAndLoseReply = true; Assert.Equal("PublicationUnknown", (await f.Run()).State);
                f.Store.RequestTransferRetry(j.Id, "test"); var retry = await f.Run(); Assert.Equal("PublicationUnknown", retry.State);
                Assert.Equal(1, f.Remote.Uploads); Assert.Equal(1, f.Remote.Publications); Assert.False(f.Store.CheckDeletion(j.File).Allowed);
            }
        }
        [Fact] public async Task FinalContentCanReconcileIntentAfterRestartWithoutSourceOrReupload()
        {
            using (var f = new TransferTestContext())
            {
                var j = f.Add(); var claimed = f.Store.ClaimTransfer("old", Array.Empty<string>(), DateTime.UtcNow);
                claimed.PublicationIntent = true; claimed.State = "Publishing"; claimed.SourceSha256 = TransferTestContext.Hash(f.Source.Files[j.Path].Bytes); f.Store.SaveTransferJob(claimed);
                f.Remote.Files[j.RemoteFinal] = f.Source.Files[j.Path].Bytes; f.Source.Files.Clear(); f.Store.RecoverTransfers("new");
                var done = await f.Run(); Assert.Equal("Completed", done.State); Assert.Equal(0, f.Remote.Uploads); Assert.Equal(0, f.Source.Opens);
            }
        }
        [Fact] public async Task VerifiedOwnedPrefixCanResumeAndUnverifiedPrefixRestarts()
        {
            using (var f = new TransferTestContext())
            {
                var j = f.Add(); f.Remote.Files[j.RemoteTemp] = f.Source.Files[j.Path].Bytes.Take(1024).ToArray();
                Assert.Equal("Completed", (await f.Run()).State); Assert.Equal(1024, f.Remote.ResumeOffset);
            }
            using (var f = new TransferTestContext())
            {
                var j = f.Add(); f.Remote.Files[j.RemoteTemp] = new byte[1024];
                Assert.Equal("Completed", (await f.Run()).State); Assert.Equal(0, f.Remote.ResumeOffset); Assert.Equal(1, f.Remote.Removes);
            }
        }
        [Fact] public async Task ForeignFinalIsHeldWithoutOverwrite()
        {
            using (var f = new TransferTestContext())
            { var j = f.Add(); var other = new byte[j.Bytes]; f.Remote.Files[j.RemoteFinal] = other; var done = await f.Run(); Assert.Equal("Conflict", done.ErrorCode); Assert.Same(other, f.Remote.Files[j.RemoteFinal]); Assert.Equal(0, f.Remote.Uploads); }
        }
        [Fact] public async Task AmbiguousRemotePresenceNeverStartsAnUpload()
        {
            using (var f = new TransferTestContext())
            { f.Add(); f.Remote.UnknownInspection = true; var done = await f.Run(); Assert.Equal("RemoteStateUnknown", done.ErrorCode); Assert.Equal(0, f.Remote.Uploads); }
        }
        [Fact] public async Task MissingStrongVerificationKeepsTempAndOriginalProtected()
        {
            using (var f = new TransferTestContext())
            { var j = f.Add(); f.Remote.UnavailableVerification = true; var done = await f.Run(); Assert.Equal("VerificationUnavailable", done.ErrorCode); Assert.Equal(0, f.Remote.Publications); Assert.False(f.Store.CheckDeletion(j.File).Allowed); }
        }
        [Fact] public async Task SourceAuditDoesNotReuploadUnchangedContents()
        {
            using (var f = new TransferTestContext())
            { var j = f.Add(); await f.Run(); f.Store.RequestTransferRetry(j.Id, "test"); Assert.Equal("Completed", (await f.Run()).State); Assert.Equal(1, f.Remote.Uploads); }
        }
        [Fact] public async Task SameMetadataChangedContentsCreateNewGenerationAndKeepOldEvidence()
        {
            using (var f = new TransferTestContext())
            {
                var j = f.Add(); await f.Run(); f.Source.Files[j.Path].Bytes[100] ^= 1; f.Store.RequestTransferRetry(j.Id, "test"); await f.Run();
                Assert.Equal("Superseded", f.Store.TransferJob(j.Id).State); Assert.Single(f.Store.TransferEvidenceForJob(j.Id));
                Assert.False(f.Store.CheckDeletion(j.File, TransferTestContext.Hash(f.Source.Files[j.Path].Bytes)).Allowed);
                bool more; var jobs = f.Store.TransferJobs(null, null, null, 100, out more); Assert.Equal(2, jobs.Count); Assert.Single(jobs, x => x.IsCurrent);
            }
        }
        [Fact] public async Task CancellationAtPublicationPersistsReconcileIntent()
        {
            using (var f = new TransferTestContext()) using (var cancel = new CancellationTokenSource())
            { f.Add(); f.Remote.BeforePublish = cancel.Cancel; var done = await f.Run(ct: cancel.Token); Assert.Equal("ReconcileRequired", done.State); Assert.True(done.PublicationIntent); Assert.Equal(0, f.Remote.Publications); }
        }
        [Fact] public void DuplicateDiscoveryAndConcurrentClaimDoNotDuplicateWork()
        {
            using (var f = new TransferTestContext())
            {
                var j = f.Add(); Parallel.For(0, 20, i => f.Store.ObserveTransferFile(f.Policy, j.File, DateTime.UtcNow, false));
                var claims = new System.Collections.Concurrent.ConcurrentBag<TransferJob>(); Parallel.For(0, 8, i => { var c = f.Store.ClaimTransfer("epoch", Array.Empty<string>(), DateTime.UtcNow); if (c != null) claims.Add(c); });
                Assert.Single(claims); Assert.Equal(j.Id, claims.Single().Id);
            }
        }
        [Fact] public void ChangingFileWhileOldWorkerOwnsLeaseCannotResurrectCurrentGeneration()
        {
            using (var f = new TransferTestContext())
            {
                var j = f.Add(); var old = f.Store.ClaimTransfer("epoch", Array.Empty<string>(), DateTime.UtcNow);
                var changed = new FileSnapshot { Path = j.Path, Root = j.File.Root, FileId = "replacement", VolumeId = j.File.VolumeId, Length = j.Bytes, CreationUtc = j.File.CreationUtc, LastWriteUtc = j.File.LastWriteUtc };
                var newer = f.Store.ObserveTransferFile(f.Policy, changed, DateTime.UtcNow, false); old.State = "Hashing"; Assert.True(f.Store.SaveTransferJob(old));
                Assert.False(f.Store.TransferJob(old.Id).IsCurrent); Assert.True(f.Store.TransferJob(newer.Id).IsCurrent);
            }
        }
        [Fact] public void PauseArchiveAndRemovingExtensionDoNotSilentlyReleaseRegisteredGuard()
        {
            using (var f = new TransferTestContext())
            {
                var j = f.Add(); f.Policy.Enabled = false; f.Policy.Extensions = new List<string> { ".jpg" }; f.Policy = f.Store.SaveTransferPolicy(f.Policy, "test");
                Assert.False(f.Store.CheckDeletion(j.File).Allowed); f.Store.ArchiveTransferPolicy(f.Policy.Id, "test"); Assert.False(f.Store.CheckDeletion(j.File).Allowed);
                var p = f.Store.TransferPolicy(f.Policy.Id); p.RequireVerifiedUpload = false; Assert.Throws<InvalidOperationException>(() => f.Store.SaveTransferPolicy(p, "test"));
            }
        }
        [Fact] public async Task FinalCommitPermitSerializesNewGuardPublication()
        {
            using (var f = new TransferTestContext()) using (var entered = new ManualResetEventSlim())
            {
                var p = f.Store.TransferPolicy(f.Policy.Id); p.Priority = 5; Task save;
                using (f.Store.AcquireCommitPermit()) { save = Task.Run(() => { entered.Set(); f.Store.SaveTransferPolicy(p, "test"); }); Assert.True(entered.Wait(1000)); Assert.False(save.IsCompleted); }
                await save; Assert.Equal(5, f.Store.TransferPolicy(p.Id).Priority);
            }
        }
        [Fact] public void PagingIsBoundedAndUsesStableCreationCursor()
        {
            using (var f = new TransferTestContext())
            {
                for (int i = 0; i < 105; i++) f.Add(i + ".png"); bool more;
                var first = f.Store.TransferJobs(null, null, null, 100, out more); Assert.True(more); Assert.Equal(100, first.Count);
                var last = first.Last(); var second = f.Store.TransferJobs(null, null, last.CreatedUtc.ToString("O") + "|" + last.Id, 100, out more);
                Assert.False(more); Assert.Equal(5, second.Count); Assert.Empty(first.Select(x => x.Id).Intersect(second.Select(x => x.Id)));
            }
        }
        [Fact] public async Task QueueTotalsFollowUpdatesCompletionAndSourceGenerationAcrossRestart()
        {
            using (var f = new TransferTestContext())
            {
                var j = f.Add(); Assert.Equal(1, f.Store.TransferCounts()["WaitingForReady"]); Assert.Equal(j.Bytes, f.Store.TransferCounts()["WaitingForReadyBytes"]);
                var file = f.Source.Files[j.Path].File; file.Length += 128;
                f.Store.ObserveTransferFile(f.Policy, file, DateTime.UtcNow.AddSeconds(-10), false);
                Assert.Equal(file.Length, f.Store.TransferCounts()["WaitingForReadyBytes"]);
                file.Length -= 128; f.Store.ObserveTransferFile(f.Policy, file, DateTime.UtcNow.AddSeconds(-10), false);
                await f.Run(); Assert.Equal(1, f.Store.TransferCounts()["Completed"]); Assert.False(f.Store.TransferCounts().ContainsKey("WaitingForReady"));
                f.Source.Files[j.Path].Bytes[100] ^= 1; f.Store.RequestTransferRetry(j.Id, "test"); await f.Run();
                Assert.Equal(1, f.Store.TransferCounts().Where(x => !x.Key.EndsWith("Bytes", StringComparison.Ordinal)).Sum(x => x.Value));
                Assert.Equal(j.Bytes, f.Store.TransferCounts().Where(x => x.Key.EndsWith("Bytes", StringComparison.Ordinal)).Sum(x => x.Value));
                var before = f.Store.TransferCounts().OrderBy(x => x.Key).ToArray();
                f.Store.Dispose(); using (var reopened = new SqliteJournal(Path.Combine(f.DirectoryPath, "manager.db"))) Assert.Equal(before, reopened.TransferCounts().OrderBy(x => x.Key).ToArray());
            }
        }
        [Fact] public void MigrationBacksUpLegacyMetadataAndRejectsNewerSchemaWithoutDowngrade()
        {
            string folder = Path.Combine(Path.GetTempPath(), "explorer-migration-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
            string path = Path.Combine(folder, "manager.db");
            try
            {
                using (var legacy = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
                {
                    legacy.Open(); using (var cmd = legacy.CreateCommand()) { cmd.CommandText = "PRAGMA user_version=1; CREATE TABLE settings(id TEXT PRIMARY KEY,json TEXT NOT NULL); INSERT INTO settings VALUES('legacy-proof','{\"kept\":true}')"; cmd.ExecuteNonQuery(); }
                }
                using (var upgraded = new SqliteJournal(path)) Assert.Empty(upgraded.TransferConnections());
                using (var backup = new SqliteConnection("Data Source=" + path + ".pre-v2.bak;Pooling=False"))
                {
                    backup.Open(); using (var cmd = backup.CreateCommand()) { cmd.CommandText = "PRAGMA user_version"; Assert.Equal(1L, cmd.ExecuteScalar()); cmd.CommandText = "SELECT json FROM settings WHERE id='legacy-proof'"; Assert.Equal("{\"kept\":true}", cmd.ExecuteScalar()); }
                }
                using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
                { db.Open(); using (var cmd = db.CreateCommand()) { cmd.CommandText = "PRAGMA user_version=3"; cmd.ExecuteNonQuery(); } }
                Assert.Throws<InvalidOperationException>(() => new SqliteJournal(path));
                using (var db = new SqliteConnection("Data Source=" + path + ";Pooling=False"))
                { db.Open(); using (var cmd = db.CreateCommand()) { cmd.CommandText = "PRAGMA user_version"; Assert.Equal(3L, cmd.ExecuteScalar()); } }
            }
            finally { Directory.Delete(folder, true); }
        }
        [Fact] public void MalformedOrOversizedConfigurationIsRejectedBeforePublication()
        {
            using (var f = new TransferTestContext())
            {
                var bad = f.Store.TransferPolicy(f.Policy.Id); bad.Extensions.Add(null);
                Assert.Throws<ArgumentException>(() => f.Store.SaveTransferPolicy(bad, "test"));
                bad = f.Store.TransferPolicy(f.Policy.Id); bad.Exclusions = Enumerable.Range(0, 100).Select(i => bad.LocalRoot + "\\" + new string('x', 200) + i).ToList();
                Assert.Throws<ArgumentException>(() => f.Store.SaveTransferPolicy(bad, "test")); Assert.Empty(f.Store.TransferPolicy(bad.Id).Exclusions);
                var connection = f.Store.TransferConnection(f.Connection.Id); connection.EncodingName = null;
                Assert.Throws<ArgumentException>(() => f.Store.SaveTransferConnection(connection, "test"));
            }
        }
        [Theory]
        [InlineData("../bad")][InlineData("/a/../b")][InlineData("/a\r\nDELE x")][InlineData("/a\\b")]
        public void RemotePathRejectsTraversalAndCommandInjection(string path) => Assert.Throws<ArgumentException>(() => TransferRules.RemotePath(path));
        [Fact] public void ExecutionAndPasswordRotationDoNotChangeDestinationIdentity()
        {
            using (var f = new TransferTestContext())
            {
                string before = TransferRules.DestinationFingerprint(f.Policy, f.Connection); f.Policy.Priority = 5; f.Policy.StartHour = 5;
                f.Connection.ConnectTimeoutSeconds = 99; f.Connection.HasSecret = true;
                Assert.Equal(before, TransferRules.DestinationFingerprint(f.Policy, f.Connection));
                f.Policy.RemoteRoot = "/different"; Assert.NotEqual(before, TransferRules.DestinationFingerprint(f.Policy, f.Connection));
            }
        }
        [Fact] public async Task EngineSkipsUnverifiedImageAndComparesRecoveryHashBeforeDeletion()
        {
            using (var f = new Fixture())
            {
                var file = f.Add(@"D:\a\old.png", 60); var c = f.Store.SaveTransferConnection(new TransferConnection { Host = "localhost", UserName = "images" }, "test");
                var p = f.Store.SaveTransferPolicy(new TransferPolicy { LocalRoot = @"D:\a", VolumeId = "volume", ConnectionId = c.Id, Enabled = true, StableSeconds = 2,
                    PublishCompatibilityConfirmed = true, ExclusiveDestinationConfirmed = true }, "test");
                var run = await f.Engine().RunAsync(f.Policy, false, "test", CancellationToken.None); Assert.Empty(f.Deleted); Assert.Equal(1, run.Skipped);
                var j = f.Store.ObserveTransferFile(p, file, DateTime.UtcNow.AddSeconds(-10), false); j = f.Store.ClaimTransfer("test", Array.Empty<string>(), DateTime.UtcNow);
                f.Store.CompleteTransfer(j, new TransferEvidence { JobId = j.Id, File = file, SourceSha256 = new string('a', 64), RemoteSha256 = new string('a', 64),
                    DestinationFingerprint = j.DestinationFingerprint, Level = TransferEvidenceLevel.PublishedVerified }); f.Store.ReleaseTransferJob(j);
                Assert.True(f.Store.CheckDeletion(file).Allowed);
                run = await f.Engine().RunAsync(f.Policy, false, "test", CancellationToken.None); Assert.Empty(f.Deleted); Assert.Equal(0, f.Cache.Used);
                Assert.Contains(f.Store.History(null, 0).Items, x => x.Reason == "TransferContentChanged");
            }
        }
    }
}
