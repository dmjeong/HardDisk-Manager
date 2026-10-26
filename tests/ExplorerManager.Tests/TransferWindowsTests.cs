using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Application;
using ExplorerManager.Contracts;
using ExplorerManager.Domain;
using ExplorerManager.Infrastructure;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExplorerManager.Tests
{
    [Collection("Service runtime")]
    public sealed class TransferWindowsTests
    {
        private sealed class NativeTransferFixture : IDisposable
        {
            public string Base { get; } = Path.Combine(Path.GetTempPath(), "ExplorerTransfer-" + Guid.NewGuid().ToString("N"));
            public string Root { get; }
            public string Data { get; }
            public string VolumeId { get; }
            public NativeTransferFixture() { Root = Path.Combine(Base, "Images"); Data = Path.Combine(Base, "Data"); Directory.CreateDirectory(Root); VolumeId = WindowsVolumeCatalog.Read(Path.GetPathRoot(Root)).Id; }
            public string Image(string name = "camera-한글.png", int length = 65536)
            {
                byte[] bytes = new byte[length]; new Random(42).NextBytes(bytes); Array.Copy(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, bytes, 8);
                string path = Path.Combine(Root, name); File.WriteAllBytes(path, bytes); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-60)); return path;
            }
            public TransferPolicy Policy(string connection) => new TransferPolicy { ConnectionId = connection, VolumeId = VolumeId, LocalRoot = Root, Enabled = true,
                StableSeconds = 2, PublishCompatibilityConfirmed = true, ExclusiveDestinationConfirmed = true };
            public void Dispose() { if (Directory.Exists(Base)) Directory.Delete(Base, true); }
        }
        private static Response Send(ServiceRuntime runtime, string command, object data = null) => runtime.Execute(new Request { Command = command, Data = data == null ? new JObject() : JObject.FromObject(data) },
            new Caller { Sid = "transfer-test-operator", Operator = true, Session = Process.GetCurrentProcess().SessionId });
        private static T Read<T>(ServiceRuntime runtime, string command, object data = null)
        { var response = Send(runtime, command, data); Assert.True(response.Success, response.Error); return response.Data.ToObject<T>(); }
        private static AppSettings Quiet() => new AppSettings { CacheBytes = 1048576, MinimumAvailableRam = 1048576, CpuWarningPercent = 100,
            CpuTemperatureWarning = 150, DiskTemperatureWarning = 100, DiskLatencyWarningMs = 1000000, InterlockTripSeconds = 3600, PauseOnHealthCritical = false };
        private static async Task WaitAsync(Func<bool> ready, Func<string> diagnostic = null, int seconds = 30)
        {
            var watch = Stopwatch.StartNew(); while (watch.Elapsed.TotalSeconds < seconds) { if (ready()) return; await Task.Delay(100); }
            throw new TimeoutException(diagnostic?.Invoke() ?? "Native transfer did not settle");
        }
        private static TransferConnection Register(ServiceRuntime runtime, TinyFtpServer server)
        {
            var c = Read<TransferConnection>(runtime, "SaveTransferConnection", server.Connection());
            Assert.True(Send(runtime, "SetTransferSecret", new { c.Id, Password = "test-password" }).Success); return c;
        }
        [WindowsFact] public void NativeReadLeaseBlocksWritesAndRenameWithoutDeleteAccess()
        {
            using (var f = new NativeTransferFixture())
            {
                string path = f.Image(); var source = new WindowsTransferSource(new WindowsVolumeCatalog()); var p = f.Policy(Guid.NewGuid().ToString("N"));
                var snapshot = source.Enumerate(p, CancellationToken.None).Single();
                using (var lease = source.OpenVerified(snapshot))
                {
                    Assert.ThrowsAny<IOException>(() => File.WriteAllText(path, "replace"));
                    Assert.ThrowsAny<IOException>(() => File.Move(path, path + ".moved"));
                    using (var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)) Assert.Equal(snapshot.Length, read.Length);
                }
                File.WriteAllText(path, "writer resumes"); Assert.Equal("writer resumes", File.ReadAllText(path));
            }
        }
        [WindowsFact] public void ExistingWriterIsNotReadUntilItsWriteHandleCloses()
        {
            using (var f = new NativeTransferFixture())
            {
                string path = f.Image(); var source = new WindowsTransferSource(new WindowsVolumeCatalog()); var snapshot = source.Enumerate(f.Policy(Guid.NewGuid().ToString("N")), CancellationToken.None).Single();
                using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.Read))
                    Assert.Equal("SourceLocked", Assert.Throws<TransferFailure>(() => source.OpenVerified(snapshot)).Code);
                using (var read = source.OpenVerified(snapshot)) Assert.Equal(snapshot.Length, read.Content.Length);
            }
        }
        [WindowsFact] public async Task RealBackgroundDiscoveryUploadsAndVerifiedCleanupCanRestoreOriginalBytes()
        {
            using (var f = new NativeTransferFixture()) using (var server = new TinyFtpServer()) using (var runtime = new ServiceRuntime(f.Data))
            {
                try
                {
                    string path = f.Image(); byte[] original = File.ReadAllBytes(path); Read<object>(runtime, "SaveSettings", Quiet());
                    var c = Register(runtime, server); var p = Read<TransferPolicy>(runtime, "SaveTransferPolicy", f.Policy(c.Id)); runtime.Start();
                    await WaitAsync(() => Read<TransferOverview>(runtime, "TransferOverview").Counts.ContainsKey("Completed"),
                        () => Read<Dashboard>(runtime, "Dashboard").Health + " / " + string.Join(";", Read<TransferJobPage>(runtime, "TransferJobs").Items.Select(x => x.State + ": " + x.ErrorCode + " " + x.Detail)));
                    Assert.Equal(original, server.Files["/incoming/" + Path.GetFileName(path)]); Assert.True(File.Exists(path));
                    var cleanup = Read<DrivePolicy>(runtime, "SavePolicy", new DrivePolicy { VolumeId = f.VolumeId, Roots = new List<string> { f.Root }, StartThreshold = 0.000001, StopThreshold = 0 });
                    Read<object>(runtime, "Run", new { cleanup.Id });
                    await WaitAsync(() => Read<Dashboard>(runtime, "Dashboard").Runs.Any(x => x.FinishedUtc.HasValue)); Assert.False(File.Exists(path));
                    var item = Read<HistoryPage>(runtime, "History").Items.Single(x => x.Path == path && x.State == "DeletedConfirmed"); Assert.Single(item.TransferEvidenceIds); Assert.True(TransferRules.ValidSha256(item.VerifiedSourceHash));
                    var cached = Read<RecoveryInfo[]>(runtime, "Recovery").Single(); Read<object>(runtime, "Restore", new { cached.Id });
                    await WaitAsync(() => File.Exists(path)); Assert.Equal(original, File.ReadAllBytes(path));
                }
                finally { await runtime.StopAsync(); }
            }
        }
        [WindowsFact] public async Task PausedTransferGuardSurvivesServiceRecreationAndBlocksCleanup()
        {
            using (var f = new NativeTransferFixture()) using (var server = new TinyFtpServer())
            {
                string path = f.Image(); TransferPolicy p;
                using (var runtime = new ServiceRuntime(f.Data))
                {
                    var c = Register(runtime, server); p = Read<TransferPolicy>(runtime, "SaveTransferPolicy", f.Policy(c.Id));
                    p.Enabled = false; p = Read<TransferPolicy>(runtime, "SaveTransferPolicy", p); await runtime.StopAsync();
                }
                using (var runtime = new ServiceRuntime(f.Data))
                {
                    try
                    {
                        Read<object>(runtime, "SaveSettings", Quiet()); Assert.True(Read<TransferOverview>(runtime, "TransferOverview").Policies.Single().ProtectionRegistered);
                        var cleanup = Read<DrivePolicy>(runtime, "SavePolicy", new DrivePolicy { VolumeId = f.VolumeId, Roots = new List<string> { f.Root }, StartThreshold = 0.000001, StopThreshold = 0 });
                        Read<object>(runtime, "Run", new { cleanup.Id }); await WaitAsync(() => Read<Dashboard>(runtime, "Dashboard").Runs.Any(x => x.FinishedUtc.HasValue));
                        Assert.True(File.Exists(path)); Assert.Contains(Read<HistoryPage>(runtime, "History").Items, x => x.Path == path && x.Reason == "AwaitingVerifiedUpload");
                    }
                    finally { await runtime.StopAsync(); }
                }
            }
        }
        [WindowsFact] public async Task BackgroundWatcherDetectsNewFinalImageAfterInitialScan()
        {
            using (var f = new NativeTransferFixture()) using (var server = new TinyFtpServer()) using (var runtime = new ServiceRuntime(f.Data))
            {
                try
                {
                    Read<object>(runtime, "SaveSettings", Quiet()); var c = Register(runtime, server); var p = Read<TransferPolicy>(runtime, "SaveTransferPolicy", f.Policy(c.Id)); runtime.Start();
                    await WaitAsync(() => Read<TransferOverview>(runtime, "TransferOverview").Policies.Single().LastCompleteScanUtc.HasValue);
                    string path = f.Image("late.png");
                    await WaitAsync(() => server.Files.ContainsKey("/incoming/late.png")); Assert.Equal(File.ReadAllBytes(path), server.Files["/incoming/late.png"]);
                }
                finally { await runtime.StopAsync(); }
            }
        }
        [WindowsFact] public async Task ConnectionTestDoesNotHoldCommandLockOrBlockDashboard()
        {
            using (var f = new NativeTransferFixture()) using (var server = new TinyFtpServer()) using (var runtime = new ServiceRuntime(f.Data))
            {
                var c = Register(runtime, server); var request = new Request { Command = "TestTransferConnection", Data = JObject.FromObject(new { c.Id }) };
                var caller = new Caller { Sid = "transfer-test", Operator = true };
                var response = runtime.Execute(request, caller); Assert.True(response.Success, response.Error); string id = response.Data.ToObject<string>();
                Assert.Equal(id, runtime.Execute(request, caller).Data.ToObject<string>());
                Assert.True(Read<Dashboard>(runtime, "Dashboard").TransfersAvailable);
                await WaitAsync(() => Read<TransferOperation<TransferCapabilities>>(runtime, "TransferOperation", new { Id = id }).State != "Running");
                Assert.Equal("Completed", Read<TransferOperation<TransferCapabilities>>(runtime, "TransferOperation", new { Id = id }).State); await runtime.StopAsync();
            }
        }
        [WindowsFact] public void CredentialsAreProtectedAndUnprivilegedTransferCommandsAreRejected()
        {
            using (var f = new NativeTransferFixture()) using (var server = new TinyFtpServer())
            {
                string id;
                using (var runtime = new ServiceRuntime(f.Data))
                {
                    id = Register(runtime, server).Id;
                    Assert.Throws<UnauthorizedAccessException>(() => runtime.Execute(new Request { Command = "TransferOverview" }, new Caller { Operator = false, Sid = "untrusted" }));
                }
                using (var db = new SqliteConnection("Data Source=" + Path.Combine(f.Data, "manager.db") + ";Pooling=False"))
                {
                    db.Open(); using (var cmd = db.CreateCommand()) {
                        cmd.CommandText = "SELECT value FROM transfer_secrets WHERE id=$id"; cmd.Parameters.AddWithValue("$id", id); var cipher = (byte[])cmd.ExecuteScalar();
                        Assert.DoesNotContain("test-password", Encoding.UTF8.GetString(cipher)); byte[] plain = ProtectedData.Unprotect(cipher, Encoding.UTF8.GetBytes(id), DataProtectionScope.LocalMachine);
                        try { Assert.Equal("test-password", Encoding.UTF8.GetString(plain)); } finally { Array.Clear(plain, 0, plain.Length); }
                    }
                }
                Assert.DoesNotContain("test-password", Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(f.Data, "manager.db"))));
            }
        }
        [WindowsFact] public async Task ExplicitFtpsProtectsBothControlAndImageDataWithTrustedCertificate()
        {
            using (var rsa = RSA.Create())
            {
                rsa.KeySize = 2048; var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); request.CertificateExtensions.Add(san.Build());
                request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
                request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.KeyCertSign, true));
                using (var signed = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1)))
                using (var certificate = new X509Certificate2(signed.Export(X509ContentType.Pfx), "", X509KeyStorageFlags.Exportable))
                using (var trust = new X509Store(StoreName.Root, StoreLocation.CurrentUser))
                {
                    trust.Open(OpenFlags.ReadWrite); trust.Add(certificate);
                    try
                    {
                        string diagnostics = Path.Combine(Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") ?? Path.GetTempPath(), "artifacts"); Directory.CreateDirectory(diagnostics);
                        using (var server = new TinyFtpServer { Certificate = certificate, Trace = message => File.AppendAllText(Path.Combine(diagnostics, "ftps-test.log"), DateTime.UtcNow.ToString("O") + " " + message + Environment.NewLine) })
                        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
                        {
                            var c = server.Connection(); c.Encryption = TransferEncryption.ExplicitTls;
                            using (var ftp = new FtpTransferTransport(c, "test-password", () => new TransferOptions { UploadBytesPerSecond = 0, DownloadBytesPerSecond = 0 }))
                            { var caps = await ftp.TestUploadAsync("/test", Guid.NewGuid().ToString("N"), deadline.Token); Assert.True(caps.ReadBackVerified); Assert.True(caps.RenameVerified); }
                        }
                    }
                    finally { trust.Remove(certificate); }
                }
            }
        }
    }
}
