using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ExplorerManager.Contracts;
using ExplorerManager.Domain;

namespace ExplorerManager.Presentation
{
    public interface ITransferUiServices { Task<string> RequestTransferPasswordAsync(); }
    public sealed class TransferViewModel : ObservableObject
    {
        private readonly IExplorerClient _client;
        private readonly IUiServices _ui;
        private TransferOverview _live = new TransferOverview();
        private TransferOptions _options = new TransferOptions();
        private bool _optionsDirty, _refreshing, _available;
        private long _optionsGeneration;
        private TransferConnectionEditor _selectedConnection;
        private TransferPolicyEditor _selectedPolicy;
        private TransferJobRow _selectedJob;
        private TransferDetail _detail;
        private string _message = "Connection과 전송 경로를 등록하세요.", _search = "", _state = "All", _cursor, _nextCursor, _probeDirectory = "/transfer-test";
        private bool _hasMore;
        private int _selectedPanel;
        private readonly Stack<string> _previous = new Stack<string>();
        public TransferViewModel(IExplorerClient client, IUiServices ui, ObservableCollection<VolumeRow> volumes)
        {
            _client = client; _ui = ui; Volumes = volumes;
            NewConnectionCommand = Command(() => { SelectedConnection = new TransferConnectionEditor(new TransferConnection()); Connections.Add(SelectedConnection); SelectedPanel = 2; return Task.CompletedTask; });
            SaveConnectionCommand = Command(async () => {
                RequireInputs(); var editor = SelectedConnection ?? throw new InvalidOperationException("Connection을 선택하세요.");
                var snapshot = editor.Snapshot(); TransferRules.ValidateConnection(snapshot); long revision = editor.EditVersion;
                var saved = await _client.SendAsync<TransferConnection>("SaveTransferConnection", snapshot); editor.Accept(saved, revision); Message = "Connection을 저장했습니다. Set password로 자격 증명을 등록하세요.";
            });
            ReloadConnectionCommand = Command(async () => { if (SelectedConnection == null) return; string id = SelectedConnection.Id; var state = await _client.SendAsync<TransferOverview>("TransferOverview"); var saved = state.Connections.FirstOrDefault(x => x.Id == id); if (saved != null && SelectedConnection?.Id == id) SelectedConnection.Reload(saved); });
            SaveSecretCommand = Command(async () => {
                var c = SavedConnection(); var secrets = _ui as ITransferUiServices ?? throw new InvalidOperationException("이 화면에서는 password 입력을 지원하지 않습니다.");
                string password = await secrets.RequestTransferPasswordAsync(); if (password == null) return;
                await _client.SendAsync<object>("SetTransferSecret", new { c.Id, Password = password }); password = null;
                Message = "비밀번호를 서비스에 등록했습니다."; await RefreshAsync(true);
            });
            TestConnectionCommand = Command(async () => { var c = SavedConnection(); var caps = await ControlAsync<TransferCapabilities>("TestTransferConnection", new { c.Id }); Message = caps.Detail; await RefreshAsync(true); });
            TestUploadCommand = Command(async () => { var c = SavedConnection(); TransferRules.RemotePath(ProbeDirectory); var caps = await ControlAsync<TransferCapabilities>("TestTransferUpload", new { c.Id, Directory = ProbeDirectory }); Message = caps.Detail; await RefreshAsync(true); });
            NewPolicyCommand = Command(() => { SelectedPolicy = new TransferPolicyEditor(new TransferPolicy { ConnectionId = SelectedConnection?.Id ?? Connections.FirstOrDefault()?.Id ?? "", VolumeId = Volumes.FirstOrDefault()?.Id ?? "" }); Policies.Add(SelectedPolicy); SelectedPanel = 1; return Task.CompletedTask; });
            SavePolicyCommand = Command(async () => {
                RequireInputs(); var editor = SelectedPolicy ?? throw new InvalidOperationException("전송 정책을 선택하세요.");
                var snapshot = editor.Snapshot(); TransferRules.ValidatePolicy(snapshot); long revision = editor.EditVersion;
                var saved = await _client.SendAsync<TransferPolicy>("SaveTransferPolicy", snapshot); editor.Accept(saved, revision);
                Message = saved.Enabled ? "자동 전송을 활성화했습니다. 삭제 연동과 상태를 확인하세요." : "전송 정책을 저장했습니다. Preview 후 활성화하세요.";
            });
            ReloadPolicyCommand = Command(async () => { if (SelectedPolicy == null) return; string id = SelectedPolicy.Id; var state = await _client.SendAsync<TransferOverview>("TransferOverview"); var saved = state.Policies.FirstOrDefault(x => x.Id == id); if (saved != null && SelectedPolicy?.Id == id) SelectedPolicy.Reload(saved); });
            PickRootCommand = Command(async () => { if (SelectedPolicy == null) return; string path = await _ui.PickFolderAsync(); if (path == null) return; SelectedPolicy.LocalRoot = path;
                var volume = Volumes.FirstOrDefault(x => path.StartsWith(x.Mount ?? "\0", StringComparison.OrdinalIgnoreCase)); if (volume != null) SelectedPolicy.VolumeId = volume.Id; });
            PreviewCommand = Command(async () => { RequireInputs(); var p = SelectedPolicy?.Snapshot() ?? throw new InvalidOperationException("전송 정책을 선택하세요.");
                var result = await ControlAsync<TransferPreview>("PreviewTransfers", p); Message = result.Files + " files · " + DisplayText.Bytes(result.Bytes) + " · unsupported " + result.Unsupported + Environment.NewLine + string.Join(Environment.NewLine, result.Samples.Take(5)); });
            BackfillCommand = Command(async () => { var p = SavedPolicy(); await _client.SendAsync<object>("BackfillTransfers", new { p.Id }); Message = "기존/대기 이미지를 전송 대상으로 선택했습니다. 활성화된 정책에서 원본 확인 후 시작합니다."; });
            ArchivePolicyCommand = Command(async () => { var p = SavedPolicy(); if (!await _ui.ConfirmAsync("Archive transfer policy", p.Name + " 전송을 중단하고 Archive합니다. 미전송 이미지의 삭제 보호는 유지됩니다.")) return;
                await _client.SendAsync<object>("ArchiveTransferPolicy", new { p.Id }); await RefreshAsync(true); Message = "정책을 Archive했습니다. 삭제 보호는 유지됩니다."; });
            PauseCommand = Command(async () => { await _client.SendAsync<object>("PauseTransfers", new { Now = false }); Message = "현재 파일을 마친 뒤 신규 전송을 일시정지합니다."; await RefreshAsync(true); });
            StopCommand = Command(async () => { await _client.SendAsync<object>("PauseTransfers", new { Now = true }); Message = "현재 I/O를 중단합니다. 다음 실행 시 원격 상태를 확인합니다."; await RefreshAsync(true); });
            ResumeCommand = Command(async () => { await _client.SendAsync<object>("ResumeTransfers"); await RefreshAsync(true); });
            SaveOptionsCommand = Command(async () => { RequireInputs(); var saved = TransferEditor<TransferOptions>.Clone(_options); saved.Paused = _live.Options.Paused; TransferRules.ValidateOptions(saved); long generation = _optionsGeneration;
                await _client.SendAsync<object>("SaveTransferOptions", saved); if (_optionsGeneration == generation) _optionsDirty = false; _optionsGeneration++; Message = "전송 실행 옵션을 저장했습니다."; });
            SearchCommand = Command(async () => { _cursor = null; _previous.Clear(); await LoadJobsAsync(); });
            NextCommand = Command(async () => { if (!_hasMore) return; _previous.Push(_cursor); _cursor = _nextCursor; await LoadJobsAsync(); });
            PreviousCommand = Command(async () => { if (_previous.Count == 0) return; _cursor = _previous.Pop(); await LoadJobsAsync(); });
            RetryCommand = Command(async () => { if (SelectedJob == null) throw new InvalidOperationException("작업을 선택하세요."); await _client.SendAsync<object>("RetryTransfer", new { SelectedJob.Id }); Message = "원본/원격 상태를 확인한 뒤 재시도합니다. 결과 불명은 자동 재게시하지 않습니다."; await LoadJobsAsync(); });
            DetailCommand = Command(async () => { if (SelectedJob == null) return; string id = SelectedJob.Id; var detail = await _client.SendAsync<TransferDetail>("TransferDetail", new { Id = id }); if (SelectedJob?.Id == id) { _detail = detail; Changed(nameof(DetailText)); } });
            ExportCommand = Command(async () => { var csv = new StringBuilder("Created,State,Path,RemotePath,Bytes,Verification,Attempts,Error\r\n"); foreach (var j in Jobs)
                    csv.AppendLine(string.Join(",", new[] { j.CreatedUtc.ToString("O"), j.State, j.Path, j.RemoteFinal, j.Bytes.ToString(), j.EvidenceLevel.ToString(), j.Attempts.ToString(), j.ErrorCode }.Select(Csv)));
                await _ui.SaveTextAsync("transfer-history-page.csv", csv.ToString()); });
        }
        private AsyncCommand Command(Func<Task> action) => new AsyncCommand(action, ex => { Message = ex.Message; _ui.Notify("Transfers", ex.Message); });
        private void RequireInputs() { if (!_ui.InputsValid) throw new InvalidOperationException("입력 오류를 먼저 수정하세요."); }
        private TransferConnectionEditor SavedConnection() { var c = SelectedConnection ?? throw new InvalidOperationException("Connection을 선택하세요."); if (c.Version == 0 || c.IsDirty) throw new InvalidOperationException("Connection 변경 내용을 먼저 저장하세요."); return c; }
        private TransferPolicyEditor SavedPolicy() { var p = SelectedPolicy ?? throw new InvalidOperationException("전송 정책을 선택하세요."); if (p.Version == 0 || p.IsDirty) throw new InvalidOperationException("전송 정책을 먼저 저장하세요."); return p; }
        private async Task<T> ControlAsync<T>(string command, object payload)
        {
            string id = await _client.SendAsync<string>(command, payload); Message = "백그라운드 작업 실행 중…";
            for (int i = 0; i < 7200; i++)
            {
                var op = await _client.SendAsync<TransferOperation<T>>("TransferOperation", new { Id = id });
                if (op.State == "Completed") return op.Result;
                if (op.State != "Running") throw new InvalidOperationException(op.Error ?? op.State);
                await Task.Delay(500);
            }
            throw new TimeoutException("작업 결과 대기 시간이 초과되었습니다. 서비스 상태를 확인하세요.");
        }
        public ObservableCollection<VolumeRow> Volumes { get; }
        public ObservableCollection<TransferConnectionEditor> Connections { get; } = new ObservableCollection<TransferConnectionEditor>();
        public ObservableCollection<TransferPolicyEditor> Policies { get; } = new ObservableCollection<TransferPolicyEditor>();
        public ObservableCollection<TransferJobRow> Jobs { get; } = new ObservableCollection<TransferJobRow>();
        public ObservableCollection<TransferProgress> Active { get; } = new ObservableCollection<TransferProgress>();
        public TransferConnectionEditor SelectedConnection { get => _selectedConnection; set => Set(ref _selectedConnection, value); }
        public TransferPolicyEditor SelectedPolicy { get => _selectedPolicy; set => Set(ref _selectedPolicy, value); }
        public TransferJobRow SelectedJob { get => _selectedJob; set { if (Set(ref _selectedJob, value)) { _detail = null; Changed(nameof(DetailText)); } } }
        public bool IsActive { get; set; }
        public bool Available { get => _available; private set => Set(ref _available, value); }
        public int SelectedPanel { get => _selectedPanel; set => Set(ref _selectedPanel, value); }
        public string Message { get => _message; set => Set(ref _message, value); }
        public string Search { get => _search; set => Set(ref _search, value); }
        public string StateFilter { get => _state; set => Set(ref _state, value); }
        public string ProbeDirectory { get => _probeDirectory; set => Set(ref _probeDirectory, value); }
        public string[] States => new[] { "All", "WaitingForReady", "Ready", "Hashing", "UploadingTemp", "VerifyingRemote", "Publishing", "Completed", "RetryWaiting", "PublicationUnknown", "NeedsAttention", "NotSelectedForBackfill", "SourceMissing", "Superseded" };
        public TransferEncryption[] Encryptions => (TransferEncryption[])Enum.GetValues(typeof(TransferEncryption));
        public TransferPublishMode[] PublishModes => (TransferPublishMode[])Enum.GetValues(typeof(TransferPublishMode));
        public TransferVerification[] Verifications => (TransferVerification[])Enum.GetValues(typeof(TransferVerification));
        public TransferTrigger[] Triggers => (TransferTrigger[])Enum.GetValues(typeof(TransferTrigger));
        private long Count(string state) { long n; return _live.Counts.TryGetValue(state, out n) ? n : 0; }
        public string Status => !Available ? "Service connection required" : _live.Options.Paused ? "Paused · 현재 파일 완료 후 대기" : string.IsNullOrEmpty(_live.InterlockReason) ? "Running · background service" : "Interlock · " + _live.InterlockReason;
        public string PendingCount => _live.Counts.Where(x => !x.Key.EndsWith("Bytes") && x.Key != "Completed" && x.Key != "Superseded" && x.Key != "SourceMissing").Sum(x => x.Value).ToString("N0");
        public string CompletedCount => Count("Completed").ToString("N0");
        public string AttentionCount => (Count("NeedsAttention") + Count("PublicationUnknown")).ToString("N0");
        public string PendingBytes => DisplayText.Bytes(_live.Counts.Where(x => x.Key.EndsWith("Bytes") && x.Key != "CompletedBytes" && x.Key != "SupersededBytes" && x.Key != "SourceMissingBytes").Sum(x => x.Value));
        public string RateText => DisplayText.Bytes(_live.Active.Sum(x => x.BytesPerSecond)) + "/s";
        public string PageText => "Page " + (_previous.Count + 1) + " · " + Jobs.Count + " items";
        public string DetailText => _detail == null ? SelectedJob == null ? "작업을 선택하고 Details에서 증빙과 시도 이력을 확인하세요." : SelectedJob.Detail :
            _detail.Job.Path + Environment.NewLine + _detail.Job.RemoteFinal + Environment.NewLine + "SHA-256: " + (_detail.Job.SourceSha256 ?? "not calculated") + Environment.NewLine +
            "Evidence: " + string.Join(", ", _detail.Evidence.Select(x => x.Level + " / " + x.VerificationMethod + " / " + x.Utc.ToLocalTime().ToString("g"))) + Environment.NewLine +
            string.Join(Environment.NewLine, _detail.Attempts.Take(6).Select(x => x.FinishedUtc.ToLocalTime().ToString("g") + " · " + x.State + " · " + x.ErrorCode + " " + x.Detail));
        private void EditOption(Action action, string property) { _optionsDirty = true; _optionsGeneration++; action(); Changed(property); }
        private static int Range(int n, int min, int max) { if (n < min || n > max) throw new ArgumentOutOfRangeException(nameof(n), min + "–" + max); return n; }
        public int Workers { get => _options.Workers; set => EditOption(() => _options.Workers = Range(value, 1, 8), nameof(Workers)); }
        public int UploadMiB { get => (int)(_options.UploadBytesPerSecond / 1048576); set => EditOption(() => _options.UploadBytesPerSecond = Range(value, 0, 1024) * 1048576L, nameof(UploadMiB)); }
        public int DownloadMiB { get => (int)(_options.DownloadBytesPerSecond / 1048576); set => EditOption(() => _options.DownloadBytesPerSecond = Range(value, 0, 1024) * 1048576L, nameof(DownloadMiB)); }
        public int ReadMiB { get => (int)(_options.DiskReadBytesPerSecond / 1048576); set => EditOption(() => _options.DiskReadBytesPerSecond = Range(value, 0, 1024) * 1048576L, nameof(ReadMiB)); }
        public int MaxPendingJobs { get => _options.MaxPendingJobs; set => EditOption(() => _options.MaxPendingJobs = Range(value, 1, 2000000), nameof(MaxPendingJobs)); }
        public int MaxAttemptSeconds { get => _options.MaxAttemptSeconds; set => EditOption(() => _options.MaxAttemptSeconds = Range(value, 30, 86400), nameof(MaxAttemptSeconds)); }
        public async Task RefreshAsync(bool available)
        {
            Available = available; if (!available || _refreshing) return; _refreshing = true;
            try
            {
                long generation = _optionsGeneration;
                var live = await _client.SendAsync<TransferOverview>("TransferOverview"); _live = live;
                foreach (var c in live.Connections) { var editor = Connections.FirstOrDefault(x => x.Id == c.Id); if (editor == null) Connections.Add(new TransferConnectionEditor(c)); else editor.Refresh(c); }
                foreach (var p in live.Policies) { var editor = Policies.FirstOrDefault(x => x.Id == p.Id); if (editor == null) Policies.Add(new TransferPolicyEditor(p)); else editor.Refresh(p); }
                if (SelectedConnection == null) SelectedConnection = Connections.FirstOrDefault(); if (SelectedPolicy == null) SelectedPolicy = Policies.FirstOrDefault();
                if (!_optionsDirty && generation == _optionsGeneration) _options = TransferEditor<TransferOptions>.Clone(live.Options);
                Active.Clear(); foreach (var progress in live.Active) Active.Add(progress);
                foreach (string name in new[] { nameof(Status), nameof(PendingCount), nameof(CompletedCount), nameof(AttentionCount), nameof(PendingBytes), nameof(RateText), nameof(Workers), nameof(UploadMiB), nameof(DownloadMiB), nameof(ReadMiB), nameof(MaxPendingJobs), nameof(MaxAttemptSeconds) }) Changed(name);
                if (IsActive && SelectedPanel == 0) await LoadJobsAsync();
            }
            catch (Exception ex) { Message = ex.Message; }
            finally { _refreshing = false; }
        }
        public async Task LoadJobsAsync()
        {
            string search = Search, state = StateFilter, cursor = _cursor;
            var page = await _client.SendAsync<TransferJobPage>("TransferJobs", new { Search = search, State = state == "All" ? "" : state, Cursor = cursor, Limit = 100 });
            if (search != Search || state != StateFilter || cursor != _cursor) return;
            string selected = SelectedJob?.Id; Jobs.Clear(); foreach (var row in page.Items) Jobs.Add(row);
            SelectedJob = Jobs.FirstOrDefault(x => x.Id == selected); _nextCursor = page.NextCursor; _hasMore = page.HasMore; Changed(nameof(PageText));
        }
        private static string Csv(string value) { value = value ?? ""; if (value.Length > 0 && "=+-@".IndexOf(value[0]) >= 0) value = "'" + value; return "\"" + value.Replace("\"", "\"\"") + "\""; }
        public AsyncCommand NewConnectionCommand { get; }
        public AsyncCommand SaveConnectionCommand { get; }
        public AsyncCommand ReloadConnectionCommand { get; }
        public AsyncCommand SaveSecretCommand { get; }
        public AsyncCommand TestConnectionCommand { get; }
        public AsyncCommand TestUploadCommand { get; }
        public AsyncCommand NewPolicyCommand { get; }
        public AsyncCommand SavePolicyCommand { get; }
        public AsyncCommand ReloadPolicyCommand { get; }
        public AsyncCommand PickRootCommand { get; }
        public AsyncCommand PreviewCommand { get; }
        public AsyncCommand BackfillCommand { get; }
        public AsyncCommand ArchivePolicyCommand { get; }
        public AsyncCommand PauseCommand { get; }
        public AsyncCommand StopCommand { get; }
        public AsyncCommand ResumeCommand { get; }
        public AsyncCommand SaveOptionsCommand { get; }
        public AsyncCommand SearchCommand { get; }
        public AsyncCommand NextCommand { get; }
        public AsyncCommand PreviousCommand { get; }
        public AsyncCommand RetryCommand { get; }
        public AsyncCommand DetailCommand { get; }
        public AsyncCommand ExportCommand { get; }
    }
}
