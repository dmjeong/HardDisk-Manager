using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Application;
using ExplorerManager.Contracts;
using ExplorerManager.Domain;
using ExplorerManager.Infrastructure;
using Newtonsoft.Json.Linq;
using Xunit;
using Microsoft.Win32.SafeHandles;
using System.Security.Principal;
using ExplorerManager.Client;

namespace ExplorerManager.Tests
{
    public sealed class WindowsSafetyTests
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateHardLinkW(string name, string existing, IntPtr security);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [WindowsFact] public async Task NativeLockedFileIsSkippedWithAlert()
        {
            using (var f = new NativeFixture())
            {
                string path = f.OldFile("locked.bin");
                using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var result = await f.Engine.RunAsync(f.Policy, false, "test", CancellationToken.None);
                    Assert.Equal(0, result.DeletedFiles); Assert.True(File.Exists(path));
                    Assert.Contains(f.Store.Alerts(), a => a.Category == "LockedFile" && a.Path == path);
                }
            }
        }
        [WindowsFact] public async Task PdhReportsValidCpuAfterTwoSamples()
        {
            using (var counters = new PdhReader())
            {
                counters.Read(); await Task.Delay(1500);
                var cpu = counters.Read().Single(m => m.Name == "CPU");
                Assert.Equal("Valid", cpu.Quality); Assert.True(cpu.Value.HasValue); Assert.InRange(cpu.Value.Value, 0, 100);
            }
        }
        [WindowsFact] public async Task AlternateDataStreamsAndHardlinksAreNeverDeleted()
        {
            using (var f = new NativeFixture())
            {
                string ads = f.OldFile("ads.bin");
                using (var stream = new FileStream(CreateFileW(ads + ":Zone.Identifier", 0x40000000, 0, IntPtr.Zero, 2, 0x80, IntPtr.Zero), FileAccess.Write))
                    stream.Write(new byte[] { 1, 2, 3, 4 }, 0, 4);
                File.SetLastWriteTimeUtc(ads, DateTime.UtcNow.AddDays(-60));
                string hard = f.OldFile("hard.bin");
                string outside = Path.Combine(f.Base, "other-link");
                Assert.True(CreateHardLinkW(outside, hard, IntPtr.Zero));
                var run = await f.Engine.RunAsync(f.Policy, false, "test", CancellationToken.None);
                Assert.Equal(0, run.DeletedFiles); Assert.True(File.Exists(ads)); Assert.True(File.Exists(hard));
                using (var stream = new FileStream(CreateFileW(ads + ":Zone.Identifier", 0x80000000, 7, IntPtr.Zero, 3, 0x80, IntPtr.Zero), FileAccess.Read))
                    Assert.Equal(4, stream.Length);
            }
        }
        [WindowsFact] public async Task JunctionDoesNotEscapeManagedRoot()
        {
            using (var f = new NativeFixture())
            {
                string outside = Path.Combine(f.Base, "outside"); Directory.CreateDirectory(outside);
                string file = Path.Combine(outside, "keep.bin"); File.WriteAllText(file, "keep"); File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-60));
                string link = Path.Combine(f.Root, "junction");
                using (var cmd = Process.Start(new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + outside + "\"") { UseShellExecute = false, CreateNoWindow = true }))
                { cmd.WaitForExit(); Assert.Equal(0, cmd.ExitCode); }
                try { await f.Engine.RunAsync(f.Policy, false, "test", CancellationToken.None); Assert.True(File.Exists(file)); }
                finally { Directory.Delete(link, false); }
            }
        }
        [WindowsFact] public async Task DirectoryCannotBeReplacedWhileDeletionLeaseIsHeld()
        {
            using (var f = new NativeFixture())
            {
                f.OldFile("file.bin");
                await f.Files.ScanAsync(f.Policy, DateTime.UtcNow.AddDays(-30), "test", f.Store, CancellationToken.None);
                var candidate = f.Store.TakeNextCandidate("test");
                using (var lease = f.Files.OpenVerified(candidate, f.Policy))
                    Assert.ThrowsAny<IOException>(() => Directory.Move(f.Root, f.Root + "-moved"));
            }
        }
        [WindowsFact] public void RuntimeRejectsUnprivilegedCallerAndDuplicatePolicySaveIsIdempotent()
        {
            using (var f = new NativeFixture())
            using (var runtime = new ServiceRuntime(Path.Combine(f.Base, "runtime")))
            {
                Assert.Throws<InvalidOperationException>(() => new ServiceRuntime(Path.Combine(f.Base, "duplicate")));
                Assert.Throws<UnauthorizedAccessException>(() => runtime.Execute(new Request { Command = "Dashboard" }, new Caller { Sid = "fake", Operator = false }));
                var request = new Request { Command = "SavePolicy", Data = JObject.FromObject(f.Policy) };
                var caller = new Caller { Sid = "test-operator", Operator = true };
                var first = runtime.Execute(request, caller); var again = runtime.Execute(request, caller);
                Assert.True(first.Success, first.Error); Assert.True(again.Success, again.Error);
                Assert.Equal(1, first.Data.ToObject<DrivePolicy>().Version); Assert.Equal(first.Data.ToString(), again.Data.ToString());
            }
        }
        [WindowsFact] public void CacheCapacitySettingsApplyImmediatelyAndSurviveServiceRecreation()
        {
            using (var f = new NativeFixture())
            {
                string data = Path.Combine(f.Base, "resize-runtime");
                var caller = new Caller { Sid = "test-operator", Operator = true };
                using (var runtime = new ServiceRuntime(data))
                {
                    foreach (long capacity in new[] { 2L * 1073741824, 256L * 1048576, AppSettings.MaximumCacheBytes })
                    {
                        var settings = new AppSettings { CacheBytes = capacity };
                        var response = runtime.Execute(new Request { Command = "SaveSettings", Data = JObject.FromObject(settings) }, caller);
                        Assert.True(response.Success, response.Error);
                        var dashboard = runtime.Execute(new Request { Command = "Dashboard" }, caller).Data.ToObject<Dashboard>();
                        Assert.Equal(capacity, dashboard.CacheCapacity); Assert.Equal(0, dashboard.CacheUsed);
                        Assert.Equal(capacity, runtime.Execute(new Request { Command = "Settings" }, caller).Data.ToObject<AppSettings>().CacheBytes);
                    }
                }
                using (var restarted = new ServiceRuntime(data))
                    Assert.Equal(AppSettings.MaximumCacheBytes, restarted.Execute(new Request { Command = "Dashboard" }, caller).Data.ToObject<Dashboard>().CacheCapacity);
            }
        }
        [WindowsFact] public void InvalidCacheCapacityDoesNotAlterPersistedOrRunningSettings()
        {
            using (var f = new NativeFixture())
            using (var runtime = new ServiceRuntime(Path.Combine(f.Base, "invalid-resize-runtime")))
            {
                var caller = new Caller { Sid = "test-operator", Operator = true };
                foreach (long capacity in new[] { 0L, AppSettings.MinimumCacheBytes - 1, AppSettings.MaximumCacheBytes + 1, long.MaxValue })
                {
                    var response = runtime.Execute(new Request { Command = "SaveSettings", Data = JObject.FromObject(new AppSettings { CacheBytes = capacity }) }, caller);
                    Assert.False(response.Success); Assert.Contains("RAM", response.Error);
                    Assert.Equal(AppSettings.DefaultCacheBytes, runtime.Execute(new Request { Command = "Settings" }, caller).Data.ToObject<AppSettings>().CacheBytes);
                    Assert.Equal(AppSettings.DefaultCacheBytes, runtime.Execute(new Request { Command = "Dashboard" }, caller).Data.ToObject<Dashboard>().CacheCapacity);
                }
            }
        }
        [WindowsFact] public async Task CacheResizeIsRejectedDuringCleanupButOtherSettingsRemainEditable()
        {
            using (var f = new NativeFixture())
            using (var runtime = new ServiceRuntime(Path.Combine(f.Base, "busy-resize-runtime")))
            {
                string path = f.OldFile("slow.bin"); File.WriteAllBytes(path, new byte[2 * 1048576]);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-60)); f.Policy.ReadBytesPerSecond = 65536;
                var caller = new Caller { Sid = "test-operator", Operator = true };
                var saved = runtime.Execute(new Request { Command = "SavePolicy", Data = JObject.FromObject(f.Policy) }, caller);
                Assert.True(saved.Success, saved.Error);
                try
                {
                    var started = runtime.Execute(new Request { Command = "Run", Data = JObject.FromObject(new { f.Policy.Id }) }, caller);
                    Assert.True(started.Success, started.Error);
                    var resized = runtime.Execute(new Request { Command = "SaveSettings", Data = JObject.FromObject(new AppSettings { CacheBytes = 2L * 1073741824 }) }, caller);
                    Assert.False(resized.Success); Assert.Contains("현재 작업", resized.Error);
                    var unchangedCapacity = new AppSettings { HistoryDays = 120 };
                    var other = runtime.Execute(new Request { Command = "SaveSettings", Data = JObject.FromObject(unchangedCapacity) }, caller);
                    Assert.True(other.Success, other.Error);
                    var actual = runtime.Execute(new Request { Command = "Settings" }, caller).Data.ToObject<AppSettings>();
                    Assert.Equal(120, actual.HistoryDays); Assert.Equal(AppSettings.DefaultCacheBytes, actual.CacheBytes);
                }
                finally { await runtime.StopAsync(); }
                var afterStop = runtime.Execute(new Request { Command = "SaveSettings", Data = JObject.FromObject(new AppSettings { CacheBytes = 2L * 1073741824 }) }, caller);
                Assert.True(afterStop.Success, afterStop.Error);
            }
        }
        [WindowsFact] public async Task ForceCloseBrokerRejectsChangedIdentityAndTerminatesOnlyOwnedTestChild()
        {
            using (var child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 30 127.0.0.1")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true }))
            {
                try
                {
                    var identity = ProcessIdentityReader.Read(child.Id);
                    identity.CanTerminate = true; // Test broker with an already approved, owned disposable child.
                    var ticket = new UnlockTicket { OperatorSid = WindowsIdentity.GetCurrent().User.Value,
                        SessionId = Process.GetCurrentProcess().SessionId, ExpiresUtc = DateTime.UtcNow.AddSeconds(30),
                        Processes = new List<ProcessIdentity> { identity } };
                    identity.StartTimeUtcTicks++;
                    await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessTermination.ExecuteAsync(ticket, true, CancellationToken.None));
                    Assert.False(child.HasExited);
                    identity.StartTimeUtcTicks--;
                    await ProcessTermination.ExecuteAsync(ticket, true, CancellationToken.None);
                    Assert.True(child.WaitForExit(1000));
                }
                finally { if (!child.HasExited) child.Kill(); }
            }
        }
        private sealed class NativeFixture : IDisposable
        {
            public string Base { get; } = Path.Combine(Path.GetTempPath(), "ExplorerSafety-" + Guid.NewGuid().ToString("N"));
            public string Root { get; }
            public DrivePolicy Policy { get; }
            public SqliteJournal Store { get; }
            public RecoveryCache Cache { get; }
            public WindowsFileSystem Files { get; }
            public CleanupEngine Engine { get; }
            public NativeFixture()
            {
                Root = Path.Combine(Base, "managed"); Directory.CreateDirectory(Root);
                var volumes = new WindowsVolumeCatalog(); var volume = WindowsVolumeCatalog.Read(Path.GetPathRoot(Root));
                Policy = new DrivePolicy { VolumeId = volume.Id, Roots = new List<string> { Root }, StartThreshold = 0.000001, StopThreshold = 0 };
                Store = new SqliteJournal(Path.Combine(Base, "audit.db")); Cache = new RecoveryCache(1024 * 1024, Store.SetRecoveryState);
                Files = new WindowsFileSystem(volumes);
                Engine = new CleanupEngine(volumes, Files, Store, Cache, new SystemClock(), new AlwaysRun(), new RestartManagerInspector());
            }
            public string OldFile(string name)
            { string path = Path.Combine(Root, name); File.WriteAllText(path, "test contents"); File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-60)); return path; }
            public void Dispose() { Cache.Dispose(); Store.Dispose(); Directory.Delete(Base, true); }
        }
        private sealed class AlwaysRun : IExecutionGate { public string StopReason(DrivePolicy p) => null; }
    }
}
