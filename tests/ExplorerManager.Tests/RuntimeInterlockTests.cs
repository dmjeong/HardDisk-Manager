using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading.Tasks;
using ExplorerManager.Contracts;
using ExplorerManager.Domain;
using ExplorerManager.Infrastructure;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json.Linq;
using Xunit;

namespace ExplorerManager.Tests
{
    [Collection("Service runtime")]
    public sealed class RuntimeInterlockTests
    {
        [WindowsFact] public async Task ConfiguredProcessBlocksDeletionAndResumeOrResetCannotBypassIt()
        {
            using (var fixture = new Fixture())
            using (var runtime = new ServiceRuntime(fixture.Data))
            using (var child = StartOwnedPing())
            {
                try
                {
                    string path = fixture.OldFile("keep.bin");
                    var settings = QuietSettings(); settings.InterlockProcessNames.Add("ping.exe");
                    AssertSuccess(Send(runtime, "SaveSettings", settings));
                    var policy = SavePolicy(runtime, fixture.Policy);
                    var dashboard = ReadDashboard(runtime);
                    Assert.Contains(dashboard.Interlocks, i => i.Code == "ProcessRunning" && i.Entity == "ping.exe" && i.Blocking);
                    Assert.Equal(new[] { "ping.exe" }, dashboard.OperatorSettings.InterlockProcessNames);
                    Assert.Equal("PausedByInterlock", runtime.StopReason(policy));

                    AssertSuccess(Send(runtime, "Run", new { policy.Id }));
                    dashboard = await WaitForDashboardAsync(runtime, d => d.Runs.Any(r => r.PolicyId == policy.Id && r.FinishedUtc.HasValue));
                    var run = dashboard.Runs.Single(r => r.PolicyId == policy.Id);
                    Assert.Equal("PausedByInterlock", run.State); Assert.Equal(0, run.DeletedFiles);
                    Assert.True(File.Exists(path)); Assert.Equal(0, dashboard.CacheUsed);
                    Assert.False(Send(runtime, "ResetInterlocks").Success);
                    AssertSuccess(Send(runtime, "Resume"));
                    Assert.Equal("PausedByInterlock", runtime.StopReason(policy));
                    Assert.Contains(ReadDashboard(runtime).Interlocks, i => i.Code == "ProcessRunning" && i.Blocking);
                    Assert.False(child.HasExited);

                    StopOwnedChild(child);
                    Assert.DoesNotContain(ReadDashboard(runtime).Interlocks, i => i.Code == "ProcessRunning" && i.Blocking);
                    AssertSuccess(Send(runtime, "ResetInterlocks"));
                    Assert.Null(runtime.StopReason(policy)); Assert.True(File.Exists(path));
                }
                finally { StopOwnedChild(child); await runtime.StopAsync(); }
            }
        }

        [WindowsFact] public async Task ResetInterlocksDoesNotClearManualPause()
        {
            using (var fixture = new Fixture())
            using (var runtime = new ServiceRuntime(fixture.Data))
            {
                try
                {
                    var policy = SavePolicy(runtime, fixture.Policy);
                    AssertSuccess(Send(runtime, "Pause"));
                    AssertSuccess(Send(runtime, "ResetInterlocks"));
                    var dashboard = ReadDashboard(runtime);
                    Assert.True(dashboard.Paused);
                    Assert.Contains(dashboard.Interlocks, i => i.Code == "ManualPause" && i.Blocking);
                    Assert.Equal("UserPaused", runtime.StopReason(policy));
                    Assert.False(Send(runtime, "Run", new { policy.Id }).Success);
                    AssertSuccess(Send(runtime, "Resume"));
                    Assert.False(ReadDashboard(runtime).Paused); Assert.Null(runtime.StopReason(policy));
                }
                finally { await runtime.StopAsync(); }
            }
        }

        [WindowsFact] public async Task AuditFailureKeepsResumeBlockedUntilResetVerifiesDurableWrites()
        {
            using (var fixture = new Fixture())
            using (var runtime = new ServiceRuntime(fixture.Data))
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = Path.Combine(fixture.Data, "manager.db"), Pooling = false }.ToString()))
            {
                try
                {
                    string path = fixture.OldFile("keep-audit.bin");
                    AssertSuccess(Send(runtime, "SaveSettings", QuietSettings()));
                    var policy = SavePolicy(runtime, fixture.Policy);
                    connection.Open();
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "CREATE TRIGGER owned_fail_delete_intent BEFORE INSERT ON events " +
                            "WHEN NEW.action='DeleteIntent' BEGIN SELECT RAISE(ABORT,'owned audit failure'); END;";
                        command.ExecuteNonQuery();
                    }
                    AssertSuccess(Send(runtime, "Run", new { policy.Id }));
                    var failed = await WaitForDashboardAsync(runtime, d => d.Health.StartsWith("AuditUnavailable", StringComparison.Ordinal) &&
                        d.Runs.Any(r => r.PolicyId == policy.Id && r.FinishedUtc.HasValue));
                    Assert.Equal("Failed", failed.Runs.Single(r => r.PolicyId == policy.Id).State);
                    Assert.True(File.Exists(path)); Assert.Equal(0, failed.CacheUsed);
                    Assert.Contains(failed.Interlocks, i => i.Code == "AuditUnavailable" && i.Blocking);

                    using (var command = connection.CreateCommand())
                    { command.CommandText = "DROP TRIGGER owned_fail_delete_intent"; command.ExecuteNonQuery(); }
                    Assert.False(Send(runtime, "Resume").Success);
                    Assert.Equal("AuditUnavailable", runtime.StopReason(policy));
                    AssertSuccess(Send(runtime, "ResetInterlocks"));
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = "SELECT count(*) FROM events WHERE action='AuditWriteVerified'";
                        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
                    }
                    Assert.Null(runtime.StopReason(policy));
                    Assert.DoesNotContain(ReadDashboard(runtime).Interlocks, i => i.Code == "AuditUnavailable" && i.Blocking);
                    Assert.True(File.Exists(path)); Assert.Empty(Send(runtime, "Recovery").Data.ToObject<RecoveryInfo[]>());
                }
                finally { await runtime.StopAsync(); }
            }
        }

        [WindowsFact] public async Task CriticalDiskAlertCannotBeClearedByInterlockResetOrResume()
        {
            using (var fixture = new Fixture())
            {
                using (var journal = new SqliteJournal(Path.Combine(fixture.Data, "manager.db")))
                    journal.SaveAlert(new AlertRecord { Utc = DateTime.UtcNow, Category = "DiskHealth", Severity = "Critical",
                        Entity = "Storage", Message = "Owned integration test: disk health failure" });
                using (var runtime = new ServiceRuntime(fixture.Data))
                {
                    try
                    {
                        string path = fixture.OldFile("keep-critical.bin");
                        var policy = SavePolicy(runtime, fixture.Policy);
                        Assert.Contains(ReadDashboard(runtime).Interlocks, i => i.Code == "DiskHealth" && i.Blocking && i.Latched);
                        Assert.False(Send(runtime, "ResetInterlocks").Success);
                        AssertSuccess(Send(runtime, "Resume"));
                        Assert.Equal("PausedByHealth", runtime.StopReason(policy));
                        AssertSuccess(Send(runtime, "Run", new { policy.Id }));
                        var dashboard = await WaitForDashboardAsync(runtime, d => d.Runs.Any(r => r.PolicyId == policy.Id && r.FinishedUtc.HasValue));
                        Assert.Equal("PausedByHealth", dashboard.Runs.Single(r => r.PolicyId == policy.Id).State);
                        Assert.True(File.Exists(path)); Assert.Equal(0, dashboard.CacheUsed);
                        Assert.Contains(dashboard.Alerts, a => a.Category == "DiskHealth" && !a.Acknowledged);
                    }
                    finally { await runtime.StopAsync(); }
                }
            }
        }

        [WindowsFact] public async Task NewOperatorOptionsAcceptBoundariesAndPersistAcrossServiceRecreation()
        {
            using (var fixture = new Fixture())
            {
                var minimum = QuietSettings(); minimum.HistoryDays = 1; minimum.HistoryMaxBytes = 10L * 1048576;
                minimum.InterlockTripSeconds = 0; minimum.InterlockRecoverySeconds = 0;
                minimum.LockedFileNotificationIntervalSeconds = 0; minimum.LockedFileNotificationMode = AlertDeliveryMode.LogOnly;
                var maximum = QuietSettings(); maximum.MinimumAvailableRam = 1048576L * 1048576;
                maximum.HistoryDays = 3650; maximum.HistoryMaxBytes = 100L * 1073741824;
                maximum.InterlockTripSeconds = 3600; maximum.InterlockRecoverySeconds = 3600;
                maximum.LockedFileNotificationIntervalSeconds = 86400; maximum.LockedFileNotificationMode = AlertDeliveryMode.Tray;
                maximum.InterlockManualReset = true;
                maximum.InterlockProcessNames = Enumerable.Range(0, 100).Select(i => "explorer-test-unused-" + i + ".exe").ToList();
                using (var runtime = new ServiceRuntime(fixture.Data))
                {
                    try
                    {
                        foreach (var settings in new[] { minimum, maximum })
                        {
                            AssertSuccess(Send(runtime, "SaveSettings", settings));
                            AssertSettings(settings, Send(runtime, "Settings").Data.ToObject<AppSettings>());
                            AssertSettings(settings, ReadDashboard(runtime).OperatorSettings);
                        }
                    }
                    finally { await runtime.StopAsync(); }
                }
                using (var restarted = new ServiceRuntime(fixture.Data))
                {
                    try
                    {
                        AssertSettings(maximum, Send(restarted, "Settings").Data.ToObject<AppSettings>());
                        AssertSettings(maximum, ReadDashboard(restarted).OperatorSettings);
                    }
                    finally { await restarted.StopAsync(); }
                }
            }
        }

        [WindowsFact] public async Task InvalidOperatorOptionsNeverReplacePersistedOrDashboardSettings()
        {
            var invalid = new Dictionary<string, Action<AppSettings>>
            {
                { "RAM below minimum", s => s.MinimumAvailableRam = 1048575 },
                { "RAM above maximum", s => s.MinimumAvailableRam = 1048576L * 1048576 + 1 },
                { "Negative trip", s => s.InterlockTripSeconds = -1 },
                { "Trip above maximum", s => s.InterlockTripSeconds = 3601 },
                { "Negative recovery", s => s.InterlockRecoverySeconds = -1 },
                { "Recovery above maximum", s => s.InterlockRecoverySeconds = 3601 },
                { "Negative notification interval", s => s.LockedFileNotificationIntervalSeconds = -1 },
                { "Notification interval above maximum", s => s.LockedFileNotificationIntervalSeconds = 86401 },
                { "Unknown notification mode", s => s.LockedFileNotificationMode = (AlertDeliveryMode)99 },
                { "History size below minimum", s => s.HistoryMaxBytes = 10L * 1048576 - 1 },
                { "History size above maximum", s => s.HistoryMaxBytes = 100L * 1073741824 + 1 },
                { "Null blockers", s => s.InterlockProcessNames = null },
                { "Too many blockers", s => s.InterlockProcessNames = Enumerable.Repeat("unused.exe", 101).ToList() },
                { "Empty blocker", s => s.InterlockProcessNames.Add("") },
                { "Whitespace blocker", s => s.InterlockProcessNames.Add(" ") },
                { "Path blocker", s => s.InterlockProcessNames.Add(@"C:\Windows\ping.exe") },
                { "Wildcard blocker", s => s.InterlockProcessNames.Add("*.exe") },
                { "Missing extension", s => s.InterlockProcessNames.Add("ping") },
                { "Empty process basename", s => s.InterlockProcessNames.Add(".exe") },
                { "Line break blocker", s => s.InterlockProcessNames.Add("ping\n.exe") },
                { "Oversized process name", s => s.InterlockProcessNames.Add(new string('a', 129) + ".exe") }
            };
            using (var fixture = new Fixture())
            using (var runtime = new ServiceRuntime(fixture.Data))
            {
                try
                {
                    var settings = QuietSettings(); settings.InterlockManualReset = true;
                    settings.LockedFileNotificationMode = AlertDeliveryMode.Tray;
                    AssertSuccess(Send(runtime, "SaveSettings", settings));
                    foreach (var change in invalid)
                    {
                        var draft = JObject.FromObject(settings).ToObject<AppSettings>(); change.Value(draft);
                        var response = Send(runtime, "SaveSettings", draft);
                        Assert.False(response.Success, change.Key); Assert.False(string.IsNullOrWhiteSpace(response.Error));
                        AssertSettings(settings, Send(runtime, "Settings").Data.ToObject<AppSettings>());
                        AssertSettings(settings, ReadDashboard(runtime).OperatorSettings);
                    }
                }
                finally { await runtime.StopAsync(); }
            }
        }

        [WindowsFact] public async Task RunningWatchdogCancelsInProgressCacheCopyWhenProcessInterlockTrips()
        {
            using (var fixture = new Fixture())
            using (var runtime = new ServiceRuntime(fixture.Data))
            {
                Process child = null;
                try
                {
                    byte[] contents = new byte[2 * 1048576]; new Random(173).NextBytes(contents);
                    string path = fixture.OldFile("slow-copy.bin", contents);
                    fixture.Policy.ReadBytesPerSecond = 65536;
                    var policy = SavePolicy(runtime, fixture.Policy);
                    var settings = QuietSettings();
                    AssertSuccess(Send(runtime, "SaveSettings", settings));
                    runtime.Start();
                    await WaitForDashboardAsync(runtime, d => d.Metrics.Any(m => m.Entity == "RAM" && m.Name == "AvailableRAM" &&
                        m.Quality == "Valid" && m.Value > 0 && DateTime.UtcNow - m.Utc < TimeSpan.FromSeconds(10)) && !d.Interlocks.Any(i => i.Blocking));
                    AssertSuccess(Send(runtime, "Run", new { policy.Id }));
                    await WaitForDashboardAsync(runtime, d => d.CacheUsed > 0 && d.Runs.Any(r => r.PolicyId == policy.Id && !r.FinishedUtc.HasValue));

                    child = StartOwnedPing(); settings.InterlockProcessNames.Add("ping.exe");
                    AssertSuccess(Send(runtime, "SaveSettings", settings));
                    Assert.False(Send(runtime, "ResetInterlocks").Success);
                    AssertSuccess(Send(runtime, "Resume"));
                    var final = await WaitForDashboardAsync(runtime, d => d.Runs.Any(r => r.PolicyId == policy.Id && r.FinishedUtc.HasValue));
                    var run = final.Runs.Single(r => r.PolicyId == policy.Id);
                    Assert.Equal("PausedByInterlock", run.State); Assert.Equal(0, run.DeletedFiles);
                    Assert.Equal(0, final.CacheUsed); Assert.Empty(Send(runtime, "Recovery").Data.ToObject<RecoveryInfo[]>());
                    Assert.Contains(final.Interlocks, i => i.Code == "ProcessRunning" && i.Blocking);
                    Assert.True(File.Exists(path)); Assert.Equal(contents, File.ReadAllBytes(path));
                }
                finally
                {
                    if (child != null) { StopOwnedChild(child); child.Dispose(); }
                    await runtime.StopAsync();
                }
            }
        }

        private static AppSettings QuietSettings() => new AppSettings
        {
            CacheBytes = 8L * 1048576, MinimumAvailableRam = 1048576,
            CpuWarningPercent = 100, CpuTemperatureWarning = 150, DiskTemperatureWarning = 100,
            DiskLatencyWarningMs = 1000000, InterlockTripSeconds = 0, InterlockRecoverySeconds = 0,
            PauseOnHealthCritical = false
        };
        private static Response Send(ServiceRuntime runtime, string command, object data = null)
        {
            using (var identity = WindowsIdentity.GetCurrent())
            using (var process = Process.GetCurrentProcess())
                return runtime.Execute(new Request { Command = command, Data = data == null ? new JObject() : JObject.FromObject(data) },
                    new Caller { Sid = identity.User.Value, Session = process.SessionId, Operator = true });
        }
        private static void AssertSuccess(Response response) => Assert.True(response.Success, response.Error);
        private static DrivePolicy SavePolicy(ServiceRuntime runtime, DrivePolicy policy)
        {
            var response = Send(runtime, "SavePolicy", policy); AssertSuccess(response); return response.Data.ToObject<DrivePolicy>();
        }
        private static Dashboard ReadDashboard(ServiceRuntime runtime)
        {
            var response = Send(runtime, "Dashboard"); AssertSuccess(response); return response.Data.ToObject<Dashboard>();
        }
        private static void AssertSettings(AppSettings expected, AppSettings actual) =>
            Assert.True(JToken.DeepEquals(JToken.FromObject(expected), JToken.FromObject(actual)), "Settings changed or were not persisted completely.");
        private static async Task<Dashboard> WaitForDashboardAsync(ServiceRuntime runtime, Func<Dashboard, bool> predicate)
        {
            var watch = Stopwatch.StartNew(); Dashboard state;
            do
            {
                state = ReadDashboard(runtime);
                if (predicate(state)) return state;
                await Task.Delay(50);
            } while (watch.Elapsed < TimeSpan.FromSeconds(10));
            throw new TimeoutException("Service condition did not settle within 10 seconds. " +
                string.Join("; ", state.Interlocks.Where(i => i.Blocking).Select(i => i.Code + ": " + i.Detail)) +
                " Runs: " + string.Join(", ", state.Runs.Select(r => r.State)));
        }
        private static Process StartOwnedPing() => Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), "-n 60 127.0.0.1")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
        private static void StopOwnedChild(Process child)
        {
            if (child.HasExited) return;
            child.Kill(); Assert.True(child.WaitForExit(3000), "Owned ping child did not terminate.");
        }
        private sealed class Fixture : IDisposable
        {
            public string Base { get; } = Path.Combine(Path.GetTempPath(), "ExplorerRuntimeInterlock-" + Guid.NewGuid().ToString("N"));
            public string Root { get; }
            public string Data { get; }
            public DrivePolicy Policy { get; }
            public Fixture()
            {
                Root = Path.Combine(Base, "managed"); Data = Path.Combine(Base, "runtime");
                Directory.CreateDirectory(Root); Directory.CreateDirectory(Data);
                var volume = WindowsVolumeCatalog.Read(Path.GetPathRoot(Root));
                Policy = new DrivePolicy { VolumeId = volume.Id, Roots = new List<string> { Root }, StartThreshold = 0.000001, StopThreshold = 0 };
            }
            public string OldFile(string name, byte[] contents = null)
            {
                string path = Path.Combine(Root, name); File.WriteAllBytes(path, contents ?? new byte[] { 1, 2, 3, 4 });
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-60)); return path;
            }
            public void Dispose() { Directory.Delete(Base, true); }
        }
    }
}
