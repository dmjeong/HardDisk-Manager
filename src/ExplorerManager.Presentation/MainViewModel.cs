using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Contracts;
using ExplorerManager.Domain;

namespace ExplorerManager.Presentation
{
    public sealed class MainViewModel : ObservableObject
    {
        private readonly IExplorerClient _client;
        private readonly IUiServices _ui;
        private bool _refreshing, _historyLoaded, _showingAlarm;
        private readonly System.Collections.Generic.HashSet<string> _seen = new System.Collections.Generic.HashSet<string>();
        private DateTime _lastPopup = DateTime.MinValue;
        private string _message = "서비스에 연결하는 중…", _status = "연결 대기", _cacheText = "0 / 1 GiB", _filter = "", _folder = "", _allowedApps = "";
        private PolicyEditor _selectedPolicy;
        private ItemRecord _selectedItem;
        private AlertRecord _selectedAlert;
        private RecoveryInfo _selectedRecovery;
        private AppSettings _settings = new AppSettings();
        private int _historyOffset;
        private bool _more;
        public MainViewModel(IExplorerClient client, IUiServices ui)
        {
            _client = client; _ui = ui;
            RefreshCommand = Command(async () => { await RefreshAsync(); await LoadHistoryAsync(); });
            NewCommand = Command(() => { SelectedPolicy = new PolicyEditor(new DrivePolicy { VolumeId = Volumes.FirstOrDefault()?.Id ?? "" }); Policies.Add(SelectedPolicy); return Task.CompletedTask; });
            SaveCommand = Command(SavePolicyAsync);
            DeleteCommand = Command(async () =>
            {
                if (SelectedPolicy != null && await _ui.ConfirmAsync("정책 삭제", SelectedPolicy.Name + " 정책을 삭제하시겠습니까?"))
                { await _client.SendAsync<object>("DeletePolicy", new { SelectedPolicy.Id }); SelectedPolicy = null; await RefreshAsync(true); }
            });
            AddRootCommand = Command(async () => { string path = await _ui.PickFolderAsync(); if (path != null && SelectedPolicy != null) SelectedPolicy.Roots += (string.IsNullOrWhiteSpace(SelectedPolicy.Roots) ? "" : Environment.NewLine) + path; });
            PreviewCommand = Command(() => StartAsync(true));
            RunCommand = Command(() => StartAsync(false));
            CancelCommand = Command(async () => { await _client.SendAsync<object>("Cancel"); Message = "안전한 지점에서 현재 작업을 취소합니다."; });
            PauseCommand = Command(async () => { await _client.SendAsync<object>("Pause"); await RefreshAsync(); });
            ResumeCommand = Command(async () => { await _client.SendAsync<object>("Resume"); await RefreshAsync(); });
            SearchCommand = Command(async () => { _historyOffset = 0; await LoadHistoryAsync(); });
            NextCommand = Command(async () => { if (_more) { _historyOffset += 100; await LoadHistoryAsync(); } });
            PreviousCommand = Command(async () => { _historyOffset = Math.Max(0, _historyOffset - 100); await LoadHistoryAsync(); });
            ExportCommand = Command(ExportAsync);
            RestoreCommand = Command(async () =>
            {
                if (SelectedRecovery == null) throw new InvalidOperationException("복구 항목을 선택하세요.");
                if (await _ui.ConfirmAsync("파일 복구", SelectedRecovery.Path + "\n원래 경로로 복구합니다. 같은 이름이 있으면 새 이름을 사용합니다."))
                { await _client.SendAsync<string>("Restore", new { SelectedRecovery.Id }); Message = "복구 작업을 시작했습니다."; }
            });
            RestoreElsewhereCommand = Command(async () =>
            {
                if (SelectedRecovery == null) throw new InvalidOperationException("복구 항목을 선택하세요.");
                string path = await _ui.PickFolderAsync();
                if (path != null) { await _client.SendAsync<string>("Restore", new { SelectedRecovery.Id, Directory = path }); Message = "선택한 위치로 복구합니다."; }
            });
            RestoreFolderCommand = Command(async () =>
            {
                if (string.IsNullOrWhiteSpace(RecoveryFolder)) throw new InvalidOperationException("복구할 폴더 경로를 입력하세요.");
                if (await _ui.ConfirmAsync("폴더 복구", RecoveryFolder + "\n캐시에 남아 있는 파일만 복구합니다."))
                    await _client.SendAsync<string>("RestoreFolder", new { Path = RecoveryFolder });
            });
            AcknowledgeCommand = Command(async () => { if (SelectedAlert != null) { await _client.SendAsync<object>("Acknowledge", new { SelectedAlert.Id }); await RefreshAsync(); } });
            RetryCommand = Command(async () => { if (SelectedAlert != null) await _client.SendAsync<object>("RetryAlert", new { SelectedAlert.Id }); });
            ForceCloseCommand = Command(() => CloseSelectedAsync(true));
            NormalCloseCommand = Command(() => CloseSelectedAsync(false));
            SaveSettingsCommand = Command(async () =>
            {
                Settings.AllowedProcessNames = AllowedApps.Split(new[] { '\n', '\r', ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
                await _client.SendAsync<object>("SaveSettings", Settings); Message = "환경설정을 저장했습니다.";
            });
            ElevateCommand = Command(() => { _ui.RunElevated(); return Task.CompletedTask; });
        }
        private AsyncCommand Command(Func<Task> action) => new AsyncCommand(action, Error);
        private void Error(Exception ex) { Message = ex.Message; _ui.Notify("Explorer Manager", ex.Message); }
        public ObservableCollection<VolumeStatus> Volumes { get; } = new ObservableCollection<VolumeStatus>();
        public ObservableCollection<PolicyEditor> Policies { get; } = new ObservableCollection<PolicyEditor>();
        public ObservableCollection<RunRecord> Runs { get; } = new ObservableCollection<RunRecord>();
        public ObservableCollection<Metric> Metrics { get; } = new ObservableCollection<Metric>();
        public ObservableCollection<AlertRecord> Alerts { get; } = new ObservableCollection<AlertRecord>();
        public ObservableCollection<ItemRecord> History { get; } = new ObservableCollection<ItemRecord>();
        public ObservableCollection<FolderSummary> Folders { get; } = new ObservableCollection<FolderSummary>();
        public ObservableCollection<RecoveryInfo> Recovery { get; } = new ObservableCollection<RecoveryInfo>();
        public ObservableCollection<double> CpuSeries { get; } = new ObservableCollection<double>();
        public ThresholdMode[] Modes => (ThresholdMode[])Enum.GetValues(typeof(ThresholdMode));
        public AgeBasis[] AgeOptions => (AgeBasis[])Enum.GetValues(typeof(AgeBasis));
        public string Message { get => _message; set => Set(ref _message, value); }
        public string Status { get => _status; private set => Set(ref _status, value); }
        public string CacheText { get => _cacheText; private set => Set(ref _cacheText, value); }
        public string Filter { get => _filter; set => Set(ref _filter, value); }
        public string RecoveryFolder { get => _folder; set => Set(ref _folder, value); }
        public string AllowedApps { get => _allowedApps; set => Set(ref _allowedApps, value); }
        public string HistoryPageText => "표시 위치 " + (_historyOffset + 1) + " · 최대 100건";
        public PolicyEditor SelectedPolicy { get => _selectedPolicy; set => Set(ref _selectedPolicy, value); }
        public ItemRecord SelectedItem { get => _selectedItem; set => Set(ref _selectedItem, value); }
        public AlertRecord SelectedAlert { get => _selectedAlert; set => Set(ref _selectedAlert, value); }
        public RecoveryInfo SelectedRecovery
        {
            get => _selectedRecovery;
            set { if (Set(ref _selectedRecovery, value) && value?.Path != null) { int at = value.Path.LastIndexOf('\\'); RecoveryFolder = at > 0 ? value.Path.Substring(0, at) : value.Path; } }
        }
        public AppSettings Settings { get => _settings; private set => Set(ref _settings, value); }
        public AsyncCommand RefreshCommand { get; }
        public AsyncCommand NewCommand { get; }
        public AsyncCommand SaveCommand { get; }
        public AsyncCommand DeleteCommand { get; }
        public AsyncCommand AddRootCommand { get; }
        public AsyncCommand PreviewCommand { get; }
        public AsyncCommand RunCommand { get; }
        public AsyncCommand CancelCommand { get; }
        public AsyncCommand PauseCommand { get; }
        public AsyncCommand ResumeCommand { get; }
        public AsyncCommand SearchCommand { get; }
        public AsyncCommand NextCommand { get; }
        public AsyncCommand PreviousCommand { get; }
        public AsyncCommand ExportCommand { get; }
        public AsyncCommand RestoreCommand { get; }
        public AsyncCommand RestoreElsewhereCommand { get; }
        public AsyncCommand RestoreFolderCommand { get; }
        public AsyncCommand AcknowledgeCommand { get; }
        public AsyncCommand RetryCommand { get; }
        public AsyncCommand ForceCloseCommand { get; }
        public AsyncCommand NormalCloseCommand { get; }
        public AsyncCommand SaveSettingsCommand { get; }
        public AsyncCommand ElevateCommand { get; }
        public async Task RefreshAsync(bool discardPolicyChanges = false)
        {
            if (_refreshing) return; _refreshing = true;
            try
            {
                var state = await _client.SendAsync<Dashboard>("Dashboard");
                Status = (state.Paused ? "신규 삭제 일시정지" : "서비스 연결됨") + " · " + state.Health + " · " + state.Utc.ToLocalTime().ToString("HH:mm:ss");
                CacheText = (state.CacheUsed / 1048576.0).ToString("F1") + " MiB / " + (state.CacheCapacity / 1073741824.0).ToString("F2") + " GiB";
                string alertId = SelectedAlert?.Id, recoveryId = SelectedRecovery?.Id, recoveryFolder = RecoveryFolder;
                Replace(Volumes, state.Volumes); Replace(Runs, state.Runs); Replace(Metrics, state.Metrics); Replace(Alerts, state.Alerts);
                SelectedAlert = Alerts.FirstOrDefault(x => x.Id == alertId);
                if (discardPolicyChanges || SelectedPolicy == null || !SelectedPolicy.IsDirty)
                {
                    string id = SelectedPolicy?.Id;
                    Replace(Policies, state.Policies.Select(x => new PolicyEditor(x)));
                    SelectedPolicy = Policies.FirstOrDefault(x => x.Id == id) ?? Policies.FirstOrDefault();
                }
                Replace(Recovery, await _client.SendAsync<RecoveryInfo[]>("Recovery"));
                SelectedRecovery = Recovery.FirstOrDefault(x => x.Id == recoveryId); RecoveryFolder = recoveryFolder;
                var cpu = state.Metrics.FirstOrDefault(x => x.Name == "CPU" && x.Quality == "Valid")?.Value;
                if (cpu.HasValue) { CpuSeries.Add(cpu.Value); while (CpuSeries.Count > 120) CpuSeries.RemoveAt(0); }
                if (!_historyLoaded)
                {
                    Settings = await _client.SendAsync<AppSettings>("Settings"); AllowedApps = string.Join(Environment.NewLine, Settings.AllowedProcessNames);
                    await LoadHistoryAsync(); _historyLoaded = true; Message = "준비되었습니다. 정책 저장 후 미리보기를 실행하세요.";
                }
                var newAlarm = state.Alerts.FirstOrDefault(a => !a.Acknowledged && a.Category == "LockedFile" && !_seen.Contains(a.Id));
                if (newAlarm != null && !_showingAlarm && DateTime.UtcNow - _lastPopup > TimeSpan.FromMinutes(5))
                {
                    _showingAlarm = true; _seen.Add(newAlarm.Id); _lastPopup = DateTime.UtcNow;
                    try
                    {
                        SelectedAlert = newAlarm;
                        int choice = await _ui.ShowLockAlertAsync(newAlarm);
                        if (choice == 1) await _client.SendAsync<object>("RetryAlert", new { newAlarm.Id });
                        else if (choice == 2 || choice == 3) await CloseSelectedAsync(choice == 3);
                    }
                    finally { _showingAlarm = false; }
                }
            }
            catch (Exception ex) { Status = "서비스 연결/조회 실패 · 관리자 실행과 서비스 상태를 확인하세요."; Message = ex.Message; }
            finally { _refreshing = false; }
        }
        private async Task SavePolicyAsync()
        {
            if (SelectedPolicy == null) throw new InvalidOperationException("정책을 선택하세요.");
            var p = SelectedPolicy.ToPolicy(); var errors = PolicyRules.Validate(p);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
            if (p.Enabled && !await _ui.ConfirmAsync("자동 삭제 활성화", "등록 경로:\n" + string.Join("\n", p.Roots) +
                "\n\n보관 기간이 지난 파일을 자동 삭제합니다. 복구 캐시는 전체 1GiB이며 이전 복구본은 퇴출될 수 있습니다.\n미리보기 결과와 경로를 확인하셨습니까?")) return;
            await _client.SendAsync<DrivePolicy>("SavePolicy", p); Message = "정책을 저장했습니다."; await RefreshAsync(true);
        }
        private async Task StartAsync(bool preview)
        {
            if (SelectedPolicy == null) throw new InvalidOperationException("정책을 선택하세요.");
            if (SelectedPolicy.IsDirty || SelectedPolicy.Version == 0) throw new InvalidOperationException("정책을 먼저 저장하세요.");
            await _client.SendAsync<string>(preview ? "Preview" : "Run", new { SelectedPolicy.Id });
            Message = preview ? "미리보기를 시작했습니다. 실행 이력에서 결과를 확인하세요." : "삭제 작업을 시작했습니다.";
        }
        private async Task CloseSelectedAsync(bool force)
        {
            if (SelectedAlert == null) throw new InvalidOperationException("잠금 알람을 선택하세요.");
            var ticket = await _client.SendAsync<UnlockTicket>("PrepareUnlock", new { SelectedAlert.Id });
            string names = string.Join("\n", ticket.Processes.Select(x => x.Name + " · PID " + x.Pid + " · " + x.Executable));
            if (await _ui.ConfirmAsync(force ? "강제 종료 후 삭제하시겠습니까?" : "정상 종료 후 삭제",
                ticket.File.Path + "\n\n" + names + "\n\n저장하지 않은 작업이 사라질 수 있습니다. 표시한 프로세스만 종료합니다."))
            { await _client.CloseProcessesAsync(ticket, force, CancellationToken.None); Message = "종료 결과를 기록하고 해당 파일을 재검사합니다."; }
        }
        private async Task LoadHistoryAsync()
        {
            var page = await _client.SendAsync<HistoryPage>("History", new { Filter, Offset = _historyOffset });
            Replace(History, page.Items); _more = page.HasMore; Changed(nameof(HistoryPageText));
            Replace(Folders, await _client.SendAsync<FolderSummary[]>("Folders", new { Filter, Offset = _historyOffset }));
        }
        private async Task ExportAsync()
        {
            var b = new StringBuilder("Utc,RunId,Path,Kind,State,LogicalBytes,RecoveryState,Reason\r\n");
            foreach (var i in History)
                b.AppendLine(string.Join(",", new[] { i.Utc.ToString("O"), i.RunId, i.Path, i.Kind, i.State, i.Bytes.ToString(), i.RecoveryState, i.Reason }.Select(Csv)));
            await _ui.SaveTextAsync("explorer-history.csv", b.ToString());
        }
        private static string Csv(string value)
        {
            value = value ?? "";
            if (value.Length > 0 && "=+-@".IndexOf(value[0]) >= 0) value = "'" + value;
            return "\"" + value.Replace("\"", "\"\"") + "\"";
        }
        private static void Replace<T>(ObservableCollection<T> target, System.Collections.Generic.IEnumerable<T> source)
        { target.Clear(); foreach (var value in source) target.Add(value); }
    }
}
