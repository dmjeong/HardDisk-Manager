using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Contracts;
using ExplorerManager.Domain;
using ExplorerManager.Presentation;

namespace ExplorerManager.Desktop
{
    // Constructed only by the explicit --smoke --ui-preview capture path. No service is used.
    internal sealed class UiPreviewClient : IExplorerClient
    {
        private const string MediaPolicy = "ui-preview-media";
        private const string BackupPolicy = "ui-preview-backup";
        private const string MediaVolume = "ui-preview-volume-d";
        private const string BackupVolume = "ui-preview-volume-e";
        private readonly DateTime _now = DateTime.UtcNow;
        private readonly Dashboard _dashboard;
        private readonly HistoryPage _history;
        private readonly RecoveryInfo[] _recovery;
        private readonly FolderSummary[] _folders;
        private readonly AppSettings _settings;

        private UiPreviewClient()
        {
            _dashboard = new Dashboard
            {
                Utc = _now, Health = "Ready", Paused = false, IsOperator = false,
                CacheUsed = 512000000, CacheCapacity = 1073741824, CacheEpoch = "ui-preview-only",
                Volumes = new List<VolumeStatus>
                {
                    Volume(MediaVolume, @"D:\", "Media Archive", 2000000000000L, 320000000000L, "Disk 1"),
                    Volume(BackupVolume, @"E:\", "Backup", 4000000000000L, 1600000000000L, "Disk 2")
                },
                Policies = new List<DrivePolicy>
                {
                    Policy(MediaPolicy, MediaVolume, "미디어 아카이브 정리", @"D:\Media\Captures", 30, 85),
                    Policy(BackupPolicy, BackupVolume, "일별 백업 보존", @"E:\Backups\Daily", 60, 80)
                },
                Runs = new List<RunRecord>
                {
                    Run("ui-run-1", MediaPolicy, "미디어 아카이브 정리", 16, "TargetReached", 12400000000L, 64, 7, false),
                    Run("ui-run-2", BackupPolicy, "일별 백업 보존", 85, "PreviewComplete", 0, 0, 0, true),
                    Run("ui-run-3", MediaPolicy, "미디어 아카이브 정리", 1440, "TargetReached", 10600000000L, 42, 4, false),
                    Run("ui-run-4", BackupPolicy, "일별 백업 보존", 2880, "NoEligibleCandidates", 0, 0, 0, false)
                },
                Metrics = new List<Metric>
                {
                    Metric("CPU", "CPU", Cpu(89), "%"),
                    Metric("CPU", "Temperature", 52.4, "°C"),
                    Metric("RAM", "AvailableRAM", 0.8, "GiB"),
                    Metric("Disk 1", "ReadBytes", Read(89), "MiB/s"),
                    Metric("Disk 1", "WriteBytes", Write(89), "MiB/s"),
                    Metric("Disk 1", "ReadLatency", Latency(89), "ms"),
                    Metric("Disk 1", "WriteLatency", 3.1, "ms"),
                    Metric("Disk 1", "Temperature", 37, "°C"),
                    Metric("Disk 1", "Queue", 0.4, ""),
                    Metric("Disk 2", "ReadBytes", 4.8, "MiB/s"),
                    Metric("Disk 2", "WriteBytes", 1.2, "MiB/s"),
                    Metric("Disk 2", "ReadLatency", 1.9, "ms"),
                    Metric("Disk 2", "WriteLatency", 2.3, "ms"),
                    Metric("Disk 2", "Temperature", 35, "°C")
                },
                Alerts = new List<AlertRecord>
                {
                    new AlertRecord
                    {
                        Id = "ui-alert-locked", Utc = _now.AddMinutes(-16), Category = "LockedFile", Severity = "Warning",
                        Message = "[예시] 편집 중인 파일을 건너뛰었습니다. 프로그램 종료 후 다시 시도할 수 있습니다.",
                        Path = @"D:\Media\Captures\review\session-recording.mp4", PolicyId = MediaPolicy, Acknowledged = true,
                        Processes = new List<ProcessIdentity>
                        {
                            new ProcessIdentity { Name = "Video Editor (예시)", Pid = 4200, CanTerminate = false, Reason = "UI 미리보기: 실제 프로세스가 아닙니다." }
                        }
                    },
                    new AlertRecord
                    {
                        Id = "ui-alert-disk", Utc = _now.AddHours(-2), Category = "DiskPerformance", Severity = "Warning",
                        Message = "[예시] Disk 2 읽기 지연이 기준을 초과했습니다. 현재 측정값은 정상 범위입니다.",
                        Entity = "Disk 2", Acknowledged = false
                    }
                }
            };
            _recovery = new[]
            {
                Recovery("ui-recovery-1", "ui-item-1", @"D:\Media\Captures\2026-08\interview-b-roll.mp4", 348000000),
                Recovery("ui-recovery-2", "ui-item-2", @"D:\Media\Captures\2026-08\reference-take.mov", 164000000)
            };
            _history = new HistoryPage
            {
                Items = new List<ItemRecord>
                {
                    Item("ui-item-1", @"D:\Media\Captures\2026-08\interview-b-roll.mp4", 348000000, "DeletedConfirmed", "Recoverable", "[예시] 보존 기간 경과 · 복구 캐시 보관"),
                    Item("ui-item-2", @"D:\Media\Captures\2026-08\reference-take.mov", 164000000, "DeletedConfirmed", "Recoverable", "[예시] 보존 기간 경과 · 복구 캐시 보관"),
                    Item("ui-item-3", @"D:\Media\Captures\2026-08\camera-a-001.mp4", 480000000, "DeletedConfirmed", "Evicted", "[예시] 이전 보관본 캐시 퇴출"),
                    Item("ui-item-4", @"D:\Media\Captures\2026-08\camera-b-001.mp4", 360000000, "DeletedConfirmed", "Evicted", "[예시] 이전 보관본 캐시 퇴출"),
                    Item("ui-item-5", @"D:\Media\Captures\review\session-recording.mp4", 780000000, "Skipped", "Unavailable", "[예시] 다른 프로그램에서 파일 사용 중"),
                    Item("ui-item-6", @"D:\Media\Captures\2026-08\cache", 0, "DeletedConfirmed", "Unavailable", "[예시] 빈 폴더 정리", true)
                }
            };
            _folders = new[]
            {
                new FolderSummary { Path = @"D:\Media\Captures\2026-08", RunId = "ui-run-1", DeletedFiles = 64, DeletedDirectories = 7, LogicalBytes = 12400000000L, ObservedFreeDelta = 12360000000L },
                new FolderSummary { Path = @"D:\Media\Captures\2026-07", RunId = "ui-run-3", DeletedFiles = 42, DeletedDirectories = 4, LogicalBytes = 10600000000L, ObservedFreeDelta = 10580000000L }
            };
            _settings = new AppSettings { AllowedProcessNames = new List<string> { "VideoEditor.exe", "MediaPlayer.exe" },
                InterlockProcessNames = new List<string> { "VideoEncoder.exe" } };
            _dashboard.OperatorSettings = new AppSettings { LockedFileNotificationMode = AlertDeliveryMode.Popup, LockedFileNotificationIntervalSeconds = 300 };
            _dashboard.Interlocks = new List<InterlockStatus>
            {
                new InterlockStatus { Code = "LowMemory", Entity = "RAM", State = "Tripped", Blocking = true, Latched = true,
                    SinceUtc = _now.AddSeconds(-12), Detail = "[예시] 여유 RAM 0.8 GiB / 한도 1 GiB · 메모리 부족으로 즉시 차단" },
                new InterlockStatus { Code = "CpuLoad", Entity = "CPU", State = "Clear", Blocking = false, Latched = false,
                    SinceUtc = _now.AddMinutes(-4), Detail = "[예시] CPU 사용률이 90% 미만입니다." }
            };
        }

        public static async Task<MainViewModel> CreateAsync(IUiServices ui)
        {
            if (ui == null) throw new ArgumentNullException(nameof(ui));
            var client = new UiPreviewClient();
            var model = new MainViewModel(client, new PreviewUiServices(ui));
            await model.RefreshAsync();
            Fill(model.CpuSeries, Cpu); Fill(model.ReadSeries, Read);
            Fill(model.WriteSeries, Write); Fill(model.LatencySeries, Latency);
            model.SelectedAlert = model.Alerts[0];
            model.SelectedRecovery = model.Recovery[0];
            model.SelectedItem = model.History[0];
            model.Message = "UI 미리보기 · 예시 데이터입니다. 실제 드라이브 연결 및 삭제·복구·저장 등 실제 작업 불가";
            return model;
        }

        public Task<T> SendAsync<T>(string command, object data = null, CancellationToken cancellation = default(CancellationToken))
        {
            cancellation.ThrowIfCancellationRequested();
            object value;
            switch (command)
            {
                case "Dashboard": value = _dashboard; break;
                case "Recovery": value = _recovery; break;
                case "History": value = _history; break;
                case "Folders": value = _folders; break;
                case "Settings": value = _settings; break;
                default: throw Blocked("삭제·복구·정책 저장·서비스 제어를 실행할 수 없습니다.");
            }
            return Task.FromResult((T)value);
        }

        public Task CloseProcessesAsync(UnlockTicket ticket, bool force, CancellationToken cancellation)
        { throw Blocked("프로세스를 종료할 수 없습니다."); }

        private static InvalidOperationException Blocked(string action)
        { return new InvalidOperationException("UI 미리보기에서는 " + action); }

        private static void Fill(ObservableCollection<double> values, Func<int, double> sample)
        { values.Clear(); for (int i = 0; i < 90; i++) values.Add(sample(i)); }
        private static double Cpu(int i) => 23 + 6 * Math.Sin(i * .13) + 2.4 * Math.Sin(i * .61);
        private static double Read(int i) => 48 + 19 * Math.Sin(i * .12) + 5 * Math.Sin(i * .53);
        private static double Write(int i) => 17 + 8 * Math.Sin(i * .17 + .8) + 2 * Math.Sin(i * .71);
        private static double Latency(int i) => 3.5 + .9 * Math.Sin(i * .14) + .3 * Math.Sin(i * .57);

        private static VolumeStatus Volume(string id, string mount, string label, long total, long free, string disk)
        { return new VolumeStatus { Id = id, Mount = mount, Label = label, TotalBytes = total, FreeBytes = free, AvailableBytes = free, FileSystem = "NTFS", State = "Ready", PhysicalDisks = disk }; }

        private static DrivePolicy Policy(string id, string volume, string name, string root, int days, double stop)
        {
            return new DrivePolicy
            {
                Id = id, Version = 1, Name = name, VolumeId = volume, Enabled = false,
                Roots = new List<string> { root }, Exclusions = new List<string> { root + @"\Protected", root + @"\review" },
                ExcludedExtensions = new List<string> { ".project", ".prproj" }, RetentionDays = days,
                ThresholdMode = ThresholdMode.UsedPercent, StartThreshold = 90, StopThreshold = stop,
                StartHour = 1, EndHour = 6, IntervalSeconds = 300
            };
        }

        private RunRecord Run(string id, string policy, string name, int minutes, string state, long bytes, int files, int directories, bool preview)
        {
            return new RunRecord
            {
                Id = id, PolicyId = policy, PolicyVersion = 1, PolicyName = name, Actor = "UI 미리보기 예시",
                StartedUtc = _now.AddMinutes(-minutes - 2), FinishedUtc = _now.AddMinutes(-minutes), CutoffUtc = _now.AddDays(-30),
                State = state, DryRun = preview, DeletedBytes = bytes, DeletedFiles = files, DeletedDirectories = directories,
                Candidates = preview ? 38 : files, CandidateBytes = preview ? 7800000000L : bytes,
                FreeBefore = 320000000000L - bytes, FreeAfter = 320000000000L,
                Skipped = id == "ui-run-1" ? 1 : 0, Detail = "[예시] UI 검증용 기록이며 실제 작업이 아닙니다."
            };
        }

        private Metric Metric(string entity, string name, double value, string unit)
        { return new Metric { Utc = _now, Entity = entity, Name = name, Value = value, Unit = unit, Quality = "Valid", Provider = "UI 미리보기", Detail = "실제 센서 값이 아닌 예시 데이터" }; }

        private RecoveryInfo Recovery(string id, string item, string path, long bytes)
        { return new RecoveryInfo { Id = id, ItemId = item, RunId = "ui-run-1", PolicyId = MediaPolicy, Epoch = "ui-preview-only", Path = path, Length = bytes, ChargedBytes = bytes, DeletedUtc = _now.AddMinutes(-16), State = "Recoverable" }; }

        private ItemRecord Item(string id, string path, long bytes, string state, string recovery, string reason, bool directory = false)
        {
            return new ItemRecord
            {
                Id = id, RunId = "ui-run-1", PolicyId = MediaPolicy, Utc = _now.AddMinutes(-16), State = state,
                RecoveryState = recovery, Reason = reason, Actor = "UI 미리보기 예시", CacheId = recovery == "Recoverable" ? id.Replace("item", "recovery") : null,
                File = new FileSnapshot { Path = path, Root = @"D:\Media\Captures", VolumeId = MediaVolume, Length = bytes, IsDirectory = directory, CreationUtc = _now.AddDays(-65), LastWriteUtc = _now.AddDays(-45), AgeUtc = _now.AddDays(-45) },
                FreeBefore = state == "DeletedConfirmed" ? (long?)(320000000000L - bytes) : null,
                FreeAfter = state == "DeletedConfirmed" ? (long?)320000000000L : null
            };
        }

        // Commands that bypass the client (export, folder picker, elevation) must also stay in preview.
        private sealed class PreviewUiServices : IUiServices
        {
            private readonly IUiServices _notifications;
            public PreviewUiServices(IUiServices notifications) { _notifications = notifications; }
            public bool InputsValid => true;
            public Task<bool> ConfirmAsync(string title, string text) { throw Blocked("실제 작업을 확인하거나 실행할 수 없습니다."); }
            public Task<int> ShowLockAlertAsync(AlertRecord alert) { throw Blocked("잠금 파일 작업을 실행할 수 없습니다."); }
            public Task<string> PickFolderAsync() { throw Blocked("실제 폴더를 선택할 수 없습니다."); }
            public Task SaveTextAsync(string name, string content) { throw Blocked("파일을 저장할 수 없습니다."); }
            public void RunElevated() { throw Blocked("관리자 권한으로 실행할 수 없습니다."); }
            public void Notify(string title, string message) { _notifications.Notify(title, message); }
        }
    }
}
