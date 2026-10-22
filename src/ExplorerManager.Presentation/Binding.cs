using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using ExplorerManager.Domain;

namespace ExplorerManager.Presentation
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Changed([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(name); return true; }
    }
    public sealed class AsyncCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Action<Exception> _error;
        private bool _running;
        public AsyncCommand(Func<Task> execute, Action<Exception> error) { _execute = execute; _error = error; }
        public bool CanExecute(object parameter) => !_running;
        public event EventHandler CanExecuteChanged;
        public async void Execute(object parameter)
        {
            if (_running) return; _running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try { await _execute(); } catch (Exception ex) { _error(ex); }
            finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
        }
    }
    public interface IUiServices
    {
        bool InputsValid { get; }
        Task<bool> ConfirmAsync(string title, string text);
        Task<int> ShowLockAlertAsync(AlertRecord alert);
        Task<string> PickFolderAsync();
        Task SaveTextAsync(string name, string content);
        void Notify(string title, string message);
        void RunElevated();
    }
    public sealed class ActionCommand : ICommand
    {
        private readonly Action _action;
        public ActionCommand(Action action) { _action = action; }
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter) => _action();
        public event EventHandler CanExecuteChanged { add { } remove { } }
    }
    public static class DisplayText
    {
        private static readonly string[] ByteUnits = { "B", "KB", "MB", "GB", "TB", "PB", "EB" };
        private static readonly Dictionary<string, string> States = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "Ready", "정상" }, { "Scanning", "탐색 중" }, { "Preview", "미리보기 중" }, { "Deleting", "삭제 중" },
            { "BelowThreshold", "정리 기준 미도달" }, { "OutsideWindow", "실행 시간 외" }, { "ClockChanged", "시간 변경으로 중단" },
            { "LimitReached", "작업 상한 도달" }, { "TargetReached", "목표 용량 확보" }, { "PreviewComplete", "미리보기 완료" },
            { "NoEligibleCandidates", "정리 대상 없음" }, { "PausedByHealth", "장치 상태로 일시정지" }, { "PausedByLoad", "부하로 일시정지" },
            { "Failed", "실패" }, { "Interrupted", "중단됨" }, { "UserCancelled", "사용자 취소" }, { "ScanIncomplete", "탐색 미완료" },
            { "Candidate", "정리 대상" }, { "WouldDelete", "삭제 예정" }, { "CacheReady", "캐시 보관" }, { "Preparing", "준비 중" },
            { "Restoring", "복구 중" }, { "Restored", "복구 완료" }, { "DeletedConfirmed", "삭제 완료" }, { "DeleteIntent", "삭제 준비" },
            { "DeleteRequested", "삭제 요청됨" }, { "OutcomeUnknown", "결과 확인 필요" }, { "Recoverable", "복구 가능" },
            { "Unavailable", "사용 불가" }, { "UnavailableAfterRestart", "재시작으로 복구 불가" }, { "Cancelled", "취소됨" },
            { "Skipped", "건너뜀" }, { "Evicted", "캐시에서 제거됨" }, { "Expired", "기한 만료" },
            { "Warning", "주의" }, { "Critical", "위험" }, { "Info", "안내" }, { "Valid", "정상" },
            { "WarmingUp", "측정 대기" }, { "Unsupported", "미지원" }, { "Stale", "측정 대기" }, { "Error", "측정 오류" },
            { "LockedFile", "파일 사용 중" }, { "DiskHealth", "디스크 상태" }, { "DiskPerformance", "디스크 성능" },
            { "Temperature", "온도 알림" }, { "AuditUnavailable", "기록 실패" },
            { "Clear", "정상" }, { "Pending", "차단 전 대기" }, { "Tripped", "차단" }, { "Recovering", "복구 조건 확인 중" },
            { "AwaitingReset", "수동 해제 대기" }, { "Unknown", "측정 대기" }, { "LowMemory", "시스템 RAM 부족" },
            { "CpuLoad", "CPU 부하" }, { "CpuTemperature", "CPU 온도" }, { "DiskTemperature", "디스크 온도" },
            { "DiskReadLatency", "디스크 읽기 지연" }, { "DiskWriteLatency", "디스크 쓰기 지연" },
            { "ManualPause", "사용자 일시정지" }, { "TelemetryUnavailable", "모니터링 상태 확인 불가" },
            { "ProcessRunning", "지정 앱 실행 중" }, { "ProcessInspectionUnavailable", "앱 상태 확인 불가" },
            { "PausedByInterlock", "인터록으로 일시정지" }, { "UserPaused", "사용자 일시정지" }, { "PolicyChanged", "정책 변경으로 중단" }
        };

        // Storage capacities use decimal units; sensor metrics retain their reported units.
        public static string Bytes(long value) => Bytes((double)value);
        public static string Bytes(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value)) return "측정 대기";
            int unit = 0;
            while (Math.Abs(value) >= 1000 && unit < ByteUnits.Length - 1) { value /= 1000; unit++; }
            return value.ToString(unit == 0 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + ByteUnits[unit];
        }

        public static string State(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return "상태 확인 중";
            string text;
            if (States.TryGetValue(value, out text)) return text;
            if (value.StartsWith("Offline", StringComparison.OrdinalIgnoreCase)) return "연결 끊김 · 식별 확인 필요";
            if (value.StartsWith("Unavailable:", StringComparison.OrdinalIgnoreCase)) return "사용 불가";
            return value;
        }

        public static bool HasValue(Metric value) => value != null && value.Quality == "Valid" && value.Value.HasValue &&
            !double.IsNaN(value.Value.Value) && !double.IsInfinity(value.Value.Value);

        public static string MetricValue(Metric value)
        {
            if (!HasValue(value)) return value?.Quality == "Unsupported" ? "미지원" : value?.Quality == "Error" ? "측정 오류" : "측정 대기";
            return value.Value.Value.ToString("0.#", CultureInfo.InvariantCulture) +
                (string.IsNullOrWhiteSpace(value.Unit) ? "" : " " + value.Unit);
        }
    }
    public sealed class VolumeRow : ObservableObject
    {
        private VolumeStatus _value;
        public VolumeRow(VolumeStatus value) { _value = value; }
        public string Id => _value.Id;
        public string Mount => _value.Mount;
        public string Label => _value.Label;
        public string FileSystem => _value.FileSystem;
        public string State => _value.State;
        public long TotalBytes => _value.TotalBytes;
        public long FreeBytes => _value.FreeBytes;
        public bool IsReady => _value.State == "Ready";
        public bool HasCapacity => IsReady && _value.TotalBytes > 0 && _value.FreeBytes >= 0 && _value.FreeBytes <= _value.TotalBytes;
        public double UsedPercent => HasCapacity ? _value.UsedPercent : 0;
        public string Title => (string.IsNullOrWhiteSpace(Mount) ? "등록된 볼륨" : Mount.TrimEnd('\\', '/')) +
            (string.IsNullOrWhiteSpace(Label) ? "" : "  " + Label);
        public string StateText => DisplayText.State(State);
        public string CapacityText => HasCapacity ? DisplayText.Bytes(TotalBytes) : "확인 불가";
        public string FreeText => HasCapacity ? DisplayText.Bytes(FreeBytes) : "확인 불가";
        public string UsedText => HasCapacity ? DisplayText.Bytes(TotalBytes - FreeBytes) : "확인 불가";
        public string UsedPercentText => HasCapacity ? UsedPercent.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—";
        public string UsageText => HasCapacity ? UsedPercentText + " used" : StateText;
        public string Summary => Title + " · " + (HasCapacity ? UsedPercentText + " used / " + FreeText + " free" : StateText);
        public void Apply(VolumeStatus value)
        {
            _value = value;
            foreach (string name in new[] { nameof(Mount), nameof(Label), nameof(FileSystem), nameof(State), nameof(TotalBytes), nameof(FreeBytes),
                nameof(IsReady), nameof(HasCapacity), nameof(UsedPercent), nameof(Title), nameof(StateText), nameof(CapacityText), nameof(FreeText),
                nameof(UsedText), nameof(UsedPercentText), nameof(UsageText), nameof(Summary) }) Changed(name);
        }
    }
    public sealed class LockAlertViewModel
    {
        public LockAlertViewModel(AlertRecord alert)
        {
            Message = alert.Message + "\n\n" + alert.Path + "\n\n" +
                string.Join("\n", alert.Processes.ConvertAll(p => p.Name + " · PID " + p.Pid));
            Skip = new ActionCommand(() => Choose(0)); Retry = new ActionCommand(() => Choose(1));
            NormalClose = new ActionCommand(() => Choose(2)); ForceClose = new ActionCommand(() => Choose(3));
        }
        public string Message { get; }
        public int Choice { get; private set; }
        public ActionCommand Skip { get; }
        public ActionCommand Retry { get; }
        public ActionCommand NormalClose { get; }
        public ActionCommand ForceClose { get; }
        public event Action CloseRequested;
        private void Choose(int value) { Choice = value; CloseRequested?.Invoke(); }
    }
}
