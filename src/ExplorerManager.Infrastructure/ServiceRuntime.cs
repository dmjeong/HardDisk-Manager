using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Application;
using ExplorerManager.Contracts;
using ExplorerManager.Domain;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExplorerManager.Infrastructure
{
    public sealed partial class ServiceRuntime : IExecutionGate, IDisposable
    {
        private readonly SqliteJournal _store;
        private readonly WindowsVolumeCatalog _volumes = new WindowsVolumeCatalog();
        private readonly WindowsFileSystem _files;
        private readonly RecoveryCache _cache;
        private readonly RestartManagerInspector _locks = new RestartManagerInspector();
        private readonly CleanupEngine _engine;
        private readonly Monitoring _monitor;
        private readonly object _sync = new object(), _commands = new object(), _logSync = new object();
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private CancellationTokenSource _active;
        private Task _operation = Task.CompletedTask;
        private Task[] _background = Array.Empty<Task>();
        private readonly Dictionary<string, DateTime> _lastRun = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, DateTime> _lastCheck = new Dictionary<string, DateTime>();
        private string _activePolicyId;
        private readonly Dictionary<string, UnlockTicket> _tickets = new Dictionary<string, UnlockTicket>();
        private readonly HashSet<string> _executingTickets = new HashSet<string>();
        private bool _busy, _paused, _started, _activeSafetyChecks, _activeRespectSchedule;
        private volatile string _activeStopReason;
        private readonly object _interlockLogSync = new object();
        private readonly Dictionary<string, InterlockStatus> _runtimeInterlocks = new Dictionary<string, InterlockStatus>(StringComparer.OrdinalIgnoreCase);
        private string _health = "Ready";
        private readonly string _dataDirectory;
        private readonly Mutex _instance;
        public ServiceRuntime(string dataDirectory)
        {
            bool created;
            _instance = new Mutex(false, @"Global\ExplorerManager.ServiceHost.v1", out created);
            if (!created) { _instance.Dispose(); throw new InvalidOperationException("Explorer Manager 서비스 호스트가 이미 실행 중입니다."); }
            try
            {
                _dataDirectory = Path.GetFullPath(dataDirectory); Directory.CreateDirectory(_dataDirectory);
                _store = new SqliteJournal(Path.Combine(_dataDirectory, "manager.db")); _store.Reconcile();
                _cache = new RecoveryCache(_store.Settings().CacheBytes, _store.SetRecoveryState);
                _files = new WindowsFileSystem(_volumes);
                _monitor = new Monitoring(_store, LogError);
                InitializeTransferRuntime();
                _engine = new CleanupEngine(_volumes, _files, _store, _cache, new SystemClock(), this, _locks, _store, _diskWork);
            }
            catch { _instance.Dispose(); throw; }
        }
        public void Start()
        {
            _started = true;
            _background = new[]
            {
                Guard(() => new PipeServer(Execute, LogError).RunAsync(_stop.Token), "IPC"),
                Guard(() => _monitor.RunAsync(_stop.Token), "Monitoring"),
                Guard(ScheduleAsync, "Scheduler"),
                Guard(WatchInterlocksAsync, "Interlocks"),
                Guard(() => _transfers.RunAsync(_stop.Token), "Transfers")
            };
            _store.RecordEvent("ServiceStarted", "Service", _cache.Epoch, "v1.0.0");
        }
        private async Task Guard(Func<Task> run, string name)
        {
            while (!_stop.IsCancellationRequested)
            {
                try { await run().ConfigureAwait(false); if (!_stop.IsCancellationRequested) throw new IOException(name + " stopped unexpectedly"); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
                catch (Exception ex) { LogError(ex); _health = name + ": " + ex.Message; }
                try { await Task.Delay(10000, _stop.Token).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        }
        public string StopReason(DrivePolicy p)
        {
            if (_activePolicyId == p.Id && _activeStopReason != null) return _activeStopReason;
            var current = _store.Policy(p.Id);
            if (current == null || current.Version != p.Version) return "PolicyChanged";
            var blocked = Interlocks(p).FirstOrDefault(x => x.Blocking);
            if (blocked == null) return null;
            if (blocked.Code == "ManualPause") return "UserPaused";
            if (blocked.Code == "AuditUnavailable") return "AuditUnavailable";
            if (blocked.Code == "DiskHealth") return "PausedByHealth";
            return "PausedByInterlock";
        }
        private List<InterlockStatus> Interlocks(DrivePolicy policy = null, bool includeResources = true)
        {
            var now = DateTime.UtcNow;
            var settings = _store.Settings();
            var result = includeResources ? _monitor.Interlocks.ToList() : new List<InterlockStatus>();
            if (_paused) result.Add(Block("ManualPause", "Service", "사용자가 정리를 일시정지했습니다.", now));
            if (_health.StartsWith("AuditUnavailable", StringComparison.Ordinal) || settings.CacheBytes != _cache.Capacity)
                result.Add(Block("AuditUnavailable", "Service", "감사 기록 또는 복구 캐시 설정 일치를 확인해야 합니다. " + _health, now));
            var age = now - _monitor.LastSampleUtc;
            if (_started && (_monitor.LastSampleUtc == default(DateTime) || age.TotalSeconds > 20 || age.TotalSeconds < -5))
                result.Add(Block("TelemetryUnavailable", "Monitoring", "최신 리소스 측정값이 없습니다. 정상 측정이 재개될 때까지 정리를 차단합니다.", now));
            foreach (string name in settings.InterlockProcessNames ?? new List<string>())
            {
                try
                {
                    var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(name));
                    int count = processes.Length;
                    foreach (var process in processes) process.Dispose();
                    result.Add(new InterlockStatus { Code = "ProcessRunning", Entity = name, SinceUtc = now,
                        Blocking = count > 0, State = count > 0 ? "Tripped" : "Clear",
                        Detail = count > 0 ? name + " 실행 중 (" + count + "개): 정리 차단" : name + " 실행되지 않음" });
                }
                catch (Exception ex)
                { result.Add(Block("ProcessInspectionUnavailable", name, "프로세스 상태를 확인할 수 없습니다: " + ex.Message, now)); }
            }
            TrackRuntimeInterlocks(result);
            if (settings.PauseOnHealthCritical)
            {
                var entities = new List<string>();
                bool unknownTopology = false;
                if (policy != null)
                {
                    string topology = _volumes.Get(policy.VolumeId).PhysicalDisks;
                    unknownTopology = string.IsNullOrWhiteSpace(topology) || topology == "UnknownTopology";
                    entities = (topology ?? "").Split(',').Select(x => "Disk" + x).ToList();
                    entities.Add(policy.VolumeId); entities.Add("Storage");
                }
                foreach (var alert in _store.CriticalAlerts().Where(a => !a.Acknowledged && StorageAlert(a) &&
                    (policy == null || entities.Contains(a.Entity) || (unknownTopology && (a.Entity ?? "").StartsWith("Disk", StringComparison.OrdinalIgnoreCase)))))
                    result.Add(new InterlockStatus { Code = "DiskHealth", Entity = alert.Entity ?? "Storage", State = "Tripped",
                        Blocking = true, Latched = true, SinceUtc = alert.Utc, Detail = alert.Message });
            }
            return result.OrderByDescending(x => x.Blocking).ThenBy(x => x.Code).ThenBy(x => x.Entity).ToList();
        }
        private static InterlockStatus Block(string code, string entity, string detail, DateTime now) =>
            new InterlockStatus { Code = code, Entity = entity, Detail = detail, Blocking = true, State = "Tripped", SinceUtc = now };
        private static bool StorageAlert(AlertRecord alert) => alert.Entity == "Storage" ||
            (alert.Entity ?? "").StartsWith("Disk", StringComparison.OrdinalIgnoreCase) || alert.Category == "DiskIO" ||
            alert.Category == "DiskHealth" || alert.Category == "DiskPerformance" || alert.Category == "WindowsStorageEvent";
        private void TrackRuntimeInterlocks(List<InterlockStatus> all)
        {
            lock (_interlockLogSync)
            {
                var states = all.Where(x => x.Code == "ProcessRunning" || x.Code == "ProcessInspectionUnavailable" || x.Code == "TelemetryUnavailable" || x.Code == "AuditUnavailable").ToList();
                foreach (var state in states)
                {
                    string key = state.Code + "/" + state.Entity;
                    InterlockStatus previous;
                    if (_runtimeInterlocks.TryGetValue(key, out previous) && previous.Blocking == state.Blocking && previous.State == state.State)
                        state.SinceUtc = previous.SinceUtc;
                    else if (state.Blocking || (previous != null && previous.Blocking))
                        _store.RecordEvent(state.Blocking ? "InterlockTripped" : "InterlockCleared", "Service", key, state.Detail);
                }
                foreach (var old in _runtimeInterlocks.Where(x => x.Value.Blocking && !states.Any(n => string.Equals(n.Code + "/" + n.Entity, x.Key, StringComparison.OrdinalIgnoreCase))))
                    _store.RecordEvent("InterlockCleared", "Service", old.Key, "차단 조건 해소 또는 설정 변경");
                _runtimeInterlocks.Clear();
                foreach (var state in states) _runtimeInterlocks[state.Code + "/" + state.Entity] = state;
            }
        }
        private async Task WatchInterlocksAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                string policyId; bool check, respectSchedule; CancellationTokenSource active;
                lock (_sync) { policyId = _activePolicyId; active = _active; check = _busy && _activeSafetyChecks; respectSchedule = _activeRespectSchedule; }
                if (check)
                {
                    string reason;
                    try
                    {
                        var policy = _store.Policy(policyId);
                        reason = policy == null ? "PolicyChanged" : StopReason(policy);
                        if (reason == null && respectSchedule && !PolicyRules.InWindow(policy, DateTime.Now)) reason = "OutsideWindow";
                    }
                    catch (Exception ex) { LogError(ex); _health = "AuditUnavailable: " + ex.Message; reason = "AuditUnavailable"; }
                    if (reason != null)
                        lock (_sync)
                            if (_busy && _activeSafetyChecks && _activePolicyId == policyId && ReferenceEquals(_active, active))
                            { _activeStopReason = reason; _active?.Cancel(); }
                }
                await Task.Delay(1000, _stop.Token).ConfigureAwait(false);
            }
        }
        private async Task ScheduleAsync()
        {
            int tick = 0;
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    foreach (var p in _store.Policies().Where(x => x.Enabled))
                    {
                        DateTime last, checkedAt;
                        lock (_sync) { if (_busy || _paused) break; _lastRun.TryGetValue(p.Id, out last); _lastCheck.TryGetValue(p.Id, out checkedAt); }
                        if ((DateTime.UtcNow - checkedAt).TotalSeconds < p.IntervalSeconds || (DateTime.UtcNow - last).TotalSeconds < p.CooldownSeconds || !PolicyRules.InWindow(p, DateTime.Now)) continue;
                        lock (_sync) _lastCheck[p.Id] = DateTime.UtcNow;
                        try
                        {
                            if (StopReason(p) == null && PolicyRules.ShouldStart(p, _volumes.Get(p.VolumeId))) StartCleanup(p.Id, false, "Scheduler");
                        }
                        catch (Exception ex) { LogError(ex); }
                    }
                    if (++tick % 360 == 0) _store.Maintain(_store.Settings());
                }
                catch (Exception ex) { _health = "AuditUnavailable: " + ex.Message; LogError(ex); }
                await Task.Delay(10000, _stop.Token).ConfigureAwait(false);
            }
        }
        private string StartCleanup(string policyId, bool preview, string actor, string onlyPath = null)
        {
            var p = _store.Policy(policyId) ?? throw new InvalidOperationException("정책을 찾을 수 없습니다.");
            PathGuard.ValidatePolicy(p, _volumes);
            return StartOperation(async ct => { await _engine.RunAsync(p, preview, actor, ct, onlyPath).ConfigureAwait(false); }, p.Id, false, true, !preview);
        }
        private string StartOperation(Func<CancellationToken, Task> action, string policyId, bool allowPaused = false, bool safetyChecks = false, bool respectSchedule = false)
        {
            lock (_sync)
            {
                if (_busy) throw new InvalidOperationException("현재 작업이 진행 중입니다. 완료 또는 취소 후 다시 시도하세요.");
                if (_paused && !allowPaused) throw new InvalidOperationException("전체 정리가 일시정지되어 있습니다.");
                _busy = true; _active = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                _activePolicyId = policyId; _activeStopReason = null; _activeSafetyChecks = safetyChecks; _activeRespectSchedule = respectSchedule;
                string operationId = Guid.NewGuid().ToString("N");
                _operation = Task.Run(async () =>
                {
                    try { await action(_active.Token).ConfigureAwait(false); _health = "Ready"; }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        _health = "AuditUnavailable: " + ex.Message; LogError(ex);
                        try { _store.SaveAlert(new AlertRecord { Utc = DateTime.UtcNow, Category = "OperationError", Severity = "Critical", Message = ex.Message, PolicyId = policyId }); } catch { }
                    }
                    finally { lock (_sync) { _lastRun[policyId ?? "restore"] = DateTime.UtcNow; _busy = false; _active.Dispose(); _active = null; _activePolicyId = null; _activeStopReason = null; _activeSafetyChecks = false; } }
                });
                return operationId;
            }
        }
        public Response Execute(Request request, Caller caller)
        {
            // Serializes command authorization/version checks and durable idempotency receipts.
            lock (_commands)
            {
                bool query = new[] { "Dashboard", "History", "Folders", "Recovery", "Settings", "TransferOverview", "TransferJobs", "TransferDetail", "TransferOperation" }.Contains(request.Command);
                if (!caller.Operator) throw new UnauthorizedAccessException("관리자 권한으로 Explorer Manager를 실행하세요.");
                string fingerprint;
                using (var sha = SHA256.Create()) fingerprint = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(request.Command + request.Data.ToString(Formatting.None))));
                if (!query)
                {
                    var previous = _store.RequestReceipt(caller.Sid, request.Id, fingerprint);
                    if (previous != null) return previous;
                    _store.BeginRequest(caller.Sid, request.Id, fingerprint);
                }
                Response response;
                try { response = Response.Ok(request, Dispatch(request, caller)); }
                catch (Exception ex) { response = new Response { Id = request.Id, Error = ex.Message }; LogError(ex); }
                if (!query) _store.EndRequest(caller.Sid, request.Id, response);
                return response;
            }
        }
        private object Dispatch(Request r, Caller caller)
        {
            var d = r.Data;
            switch (r.Command)
            {
                case "Dashboard":
                    return new Dashboard { Utc = DateTime.UtcNow, IsOperator = caller.Operator, Paused = _paused, Health = _health,
                        CacheEpoch = _cache.Epoch, CacheCapacity = _cache.Capacity, CacheUsed = _cache.Used, Volumes = _volumes.List().ToList(),
                        Policies = _store.Policies(), Metrics = _monitor.Snapshot().ToList(), Runs = _store.Runs(), Alerts = _store.Alerts(),
                        Interlocks = Interlocks(), OperatorSettings = _store.Settings(), TransfersAvailable = true };
                case "SavePolicy":
                    var p = d.ToObject<DrivePolicy>(); PathGuard.ValidatePolicy(p, _volumes);
                    foreach (var other in _store.Policies().Where(x => x.Id != p.Id))
                        if (other.Roots.Any(a => p.Roots.Any(b => PathGuard.Within(a, b) || PathGuard.Within(b, a)))) throw new ArgumentException("다른 정책과 관리 경로가 중복됩니다.");
                    _store.RecordEvent("PolicyChanged", caller.Sid, p.Id, JsonConvert.SerializeObject(p, Protocol.Json));
                    var savedPolicy = _store.SavePolicy(p);
                    lock (_sync) { if (_activePolicyId == p.Id) { _activeStopReason = "PolicyChanged"; _active?.Cancel(); } }
                    return savedPolicy;
                case "DeletePolicy":
                    string id = Required(d, "Id"); lock (_sync) { if (_busy) throw new InvalidOperationException("작업 종료 후 정책을 삭제하세요."); }
                    _store.RecordEvent("PolicyDeleted", caller.Sid, id, ""); _store.DeletePolicy(id); return true;
                case "Preview": return StartCleanup(Required(d, "Id"), true, caller.Sid);
                case "Run": return StartCleanup(Required(d, "Id"), false, caller.Sid);
                case "Cancel": lock (_sync) _active?.Cancel(); return true;
                case "Pause": _paused = true; lock (_sync) { _activeStopReason = "UserPaused"; _active?.Cancel(); } _store.RecordEvent("Paused", caller.Sid, "", ""); return true;
                case "Resume":
                    if (_health.StartsWith("AuditUnavailable", StringComparison.Ordinal) || _store.Settings().CacheBytes != _cache.Capacity)
                        throw new InvalidOperationException("감사 기록/설정 오류를 해결한 뒤 Reset interlocks로 확인하세요. Resume는 이 차단을 해제하지 않습니다.");
                    _store.RecordEvent("Resumed", caller.Sid, "", ""); _paused = false; return true;
                case "ResetInterlocks":
                    if (_store.Settings().CacheBytes != _cache.Capacity)
                        throw new InvalidOperationException("복구 캐시 설정과 실행 용량이 다릅니다. 서비스 재시작이 필요합니다.");
                    if (_health.StartsWith("AuditUnavailable", StringComparison.Ordinal))
                    {
                        _store.RecordEvent("AuditWriteVerified", caller.Sid, "Service", "Interlock Reset 전 감사 기록 쓰기 확인");
                        _health = "Ready";
                    }
                    var hardBlocks = Interlocks(null, false).Where(x => x.Blocking && x.Code != "ManualPause").ToList();
                    if (hardBlocks.Count > 0) throw new InvalidOperationException("차단 조건이 남아 있습니다: " + string.Join("; ", hardBlocks.Select(x => x.Detail)));
                    if (!_monitor.ResetInterlocks()) throw new InvalidOperationException("위험 조건 또는 안정 회복 대기 중에는 Interlock을 Reset할 수 없습니다.");
                    _store.RecordEvent("InterlocksReset", caller.Sid, "Monitoring", "안전 조건 재확인 완료"); return true;
                case "History": return _store.History((string)d["Filter"], Math.Min(1000000, Math.Max(0, (int?)d["Offset"] ?? 0)), (string)d["RunId"]);
                case "Folders": return _store.Folders((string)d["Filter"], Math.Min(1000000, Math.Max(0, (int?)d["Offset"] ?? 0)));
                case "Recovery": return _cache.List();
                case "Settings": return _store.Settings();
                case "SaveSettings":
                    var settings = d.ToObject<AppSettings>(); ValidateSettings(settings);
                    settings.InterlockProcessNames = settings.InterlockProcessNames.Select(x => x.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    var oldSettings = _store.Settings();
                    lock (_sync)
                    {
                        if (settings.CacheBytes == _cache.Capacity) _store.SaveSettings(settings, caller.Sid);
                        else
                        {
                            if (_busy) throw new InvalidOperationException("현재 작업이 진행 중입니다. 완료하거나 취소한 뒤 RAM 보관 용량을 변경하세요.");
                            try { _cache.Resize(settings.CacheBytes, () => _store.SaveSettings(settings, caller.Sid)); }
                            catch
                            {
                                try
                                {
                                    if (_store.Settings().CacheBytes != _cache.Capacity) _health = "AuditUnavailable: 복구 캐시 설정 적용이 완료되지 않았습니다. 서비스를 다시 시작하세요.";
                                }
                                catch { _health = "AuditUnavailable: 복구 캐시 설정 상태를 확인할 수 없습니다."; }
                                throw;
                            }
                        }
                    }
                    if (settings.HistoryDays < oldSettings.HistoryDays || settings.HistoryMaxBytes < oldSettings.HistoryMaxBytes)
                    {
                        try { _store.Maintain(settings); }
                        catch (Exception ex) { _health = "AuditUnavailable: " + ex.Message; throw; }
                    }
                    return true;
                case "Restore":
                    string cacheId = Required(d, "Id"); string directory = (string)d["Directory"];
                    var entry = _cache.List().FirstOrDefault(x => x.Id == cacheId) ?? throw new InvalidOperationException("복구본이 퇴출되었습니다.");
                    string restoreOperation = StartOperation(async ct => { await _engine.RestoreAsync(cacheId, directory, _store.Policy(entry.PolicyId), caller.Sid, ct).ConfigureAwait(false); }, entry.PolicyId, true);
                    _transfers.CancelActive(); return restoreOperation;
                case "RestoreFolder":
                    string folder = PathGuard.Normalize(Required(d, "Path"));
                    var entries = _cache.List().Where(x => PathGuard.Within(x.Path, folder)).OrderBy(x => x.Path.Length).ToList();
                    if (entries.Count == 0) throw new InvalidOperationException("남아 있는 복구본이 없습니다.");
                    string folderOperation = StartOperation(async ct =>
                    {
                        foreach (var e in entries)
                        {
                            ct.ThrowIfCancellationRequested();
                            await _engine.RestoreAsync(e.Id, null, _store.Policy(e.PolicyId), caller.Sid, ct).ConfigureAwait(false);
                        }
                    }, entries[0].PolicyId, true);
                    _transfers.CancelActive(); return folderOperation;
                case "Acknowledge":
                    var alert = _store.Alert(Required(d, "Id")) ?? throw new InvalidOperationException("알람이 없습니다.");
                    alert.Acknowledged = true; _store.SaveAlert(alert); _store.RecordEvent("AlertAcknowledged", caller.Sid, alert.Id, ""); return true;
                case "RetryAlert":
                    var retry = _store.Alert(Required(d, "Id")) ?? throw new InvalidOperationException("알람이 없습니다.");
                    if (retry.Category != "LockedFile") throw new InvalidOperationException("잠금 알람이 아닙니다.");
                    return StartCleanup(retry.PolicyId, false, caller.Sid, retry.Path);
                case "PrepareUnlock": return PrepareUnlock(Required(d, "Id"), caller);
                case "ValidateUnlock":
                    var ticket = GetTicket(Required(d, "Id"), caller);
                    if (_executingTickets.Contains(ticket.Id)) throw new InvalidOperationException("이미 사용한 승인 티켓입니다.");
                    ValidateTicket(ticket, caller); _executingTickets.Add(ticket.Id);
                    _store.RecordEvent("ProcessConsent", caller.Sid, ticket.Id, JsonConvert.SerializeObject(ticket, Protocol.Json)); return ticket;
                case "CompleteUnlock":
                    var completed = GetTicket(Required(d, "Id"), caller);
                    if (!_executingTickets.Remove(completed.Id)) throw new InvalidOperationException("실행 승인되지 않은 티켓입니다.");
                    _tickets.Remove(completed.Id);
                    _store.RecordEvent((bool?)d["Force"] == true ? "ForceCloseResult" : "NormalCloseResult", caller.Sid, completed.Id, (string)d["Result"]);
                    if ((bool?)d["Retry"] == true) return StartCleanup(completed.PolicyId, false, caller.Sid, completed.File.Path);
                    return true;
                default: return DispatchTransfer(r, caller);
            }
        }
        private UnlockTicket PrepareUnlock(string alertId, Caller caller)
        {
            var alert = _store.Alert(alertId);
            if (alert == null || alert.Category != "LockedFile") throw new InvalidOperationException("잠금 알람을 선택하세요.");
            var item = _store.LatestItem(alert.Path, alert.PolicyId) ?? throw new InvalidOperationException("파일 이력이 없습니다.");
            if (item.File.IsDirectory) throw new InvalidOperationException("빈 폴더 잠금은 프로세스를 확정할 수 없습니다.");
            var policy = _store.Policy(alert.PolicyId) ?? throw new InvalidOperationException("정책이 없습니다.");
            var ticket = new UnlockTicket { AlertId = alertId, PolicyId = policy.Id, PolicyVersion = policy.Version, File = item.File,
                OperatorSid = caller.Sid, SessionId = caller.Session, ExpiresUtc = DateTime.UtcNow.AddSeconds(60), Processes = _locks.Inspect(alert.Path).ToList() };
            ValidateTicket(ticket, caller);
            foreach (var old in _tickets.Values.Where(x => x.ExpiresUtc < DateTime.UtcNow).Select(x => x.Id).ToList()) { _tickets.Remove(old); _executingTickets.Remove(old); }
            if (_tickets.Count >= 100) throw new InvalidOperationException("승인 요청이 너무 많습니다.");
            _tickets[ticket.Id] = ticket; return ticket;
        }
        private void ValidateTicket(UnlockTicket t, Caller caller)
        {
            var p = _store.Policy(t.PolicyId) ?? throw new InvalidOperationException("정책이 없습니다.");
            if (t.OperatorSid != caller.Sid || t.SessionId != caller.Session || t.ExpiresUtc <= DateTime.UtcNow || p.Version != t.PolicyVersion) throw new InvalidOperationException("승인 조건이 변경되었거나 만료되었습니다.");
            if (StopReason(p) != null || PolicyRules.TargetReached(p, _volumes.Get(p.VolumeId)) || !PolicyRules.ShouldStart(p, _volumes.Get(p.VolumeId))) throw new InvalidOperationException("정리가 필요하지 않거나 중지되었습니다.");
            var current = _files.Inspect(t.File, p);
            if (current.FileId != t.File.FileId || current.LastWriteUtc != t.File.LastWriteUtc || current.Length != t.File.Length ||
                !PolicyRules.IsExpired(current, p, DateTime.UtcNow.AddDays(-p.RetentionDays))) throw new InvalidOperationException("파일 조건이 변경되었습니다.");
            var processes = _locks.Inspect(t.File.Path).ToList();
            var allowed = _store.Settings().AllowedProcessNames;
            if (processes.Count == 0 || processes.Count != t.Processes.Count || processes.Any(a => !t.Processes.Any(b => a.Pid == b.Pid && a.StartTimeUtcTicks == b.StartTimeUtcTicks)))
                throw new InvalidOperationException("잠금 프로세스 목록이 변경되었거나 확인할 수 없습니다.");
            if (processes.Any(x => !x.CanTerminate || x.OwnerSid != caller.Sid || x.SessionId != caller.Session || !allowed.Any(n => n.Equals(x.Name, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidOperationException("현재 사용자 세션의 종료 허용 앱만 종료할 수 있습니다. 환경설정에서 실행 파일 이름을 등록하세요.");
        }
        private UnlockTicket GetTicket(string id, Caller caller)
        {
            UnlockTicket t;
            if (!_tickets.TryGetValue(id, out t) || t.OperatorSid != caller.Sid || t.SessionId != caller.Session || t.ExpiresUtc <= DateTime.UtcNow) throw new InvalidOperationException("티켓이 없거나 만료되었습니다.");
            return t;
        }
        private static string Required(JObject data, string field)
        { string value = (string)data[field]; if (string.IsNullOrWhiteSpace(value) || value.Length > 32000) throw new ArgumentException(field + " 값이 잘못되었습니다."); return value; }
        private static void ValidateSettings(AppSettings s)
        {
            if (s == null || s.CacheBytes < AppSettings.MinimumCacheBytes || s.CacheBytes > AppSettings.MaximumCacheBytes)
                throw new ArgumentException("RAM 보관 용량은 1~16384 MiB 범위입니다.");
            if (s.MinimumAvailableRam < 1048576L || s.MinimumAvailableRam > 1048576L * 1048576 ||
                s.InterlockTripSeconds < 0 || s.InterlockTripSeconds > 3600 ||
                s.InterlockRecoverySeconds < 0 || s.InterlockRecoverySeconds > 3600 ||
                !Enum.IsDefined(typeof(AlertDeliveryMode), s.LockedFileNotificationMode) ||
                s.LockedFileNotificationIntervalSeconds < 0 || s.LockedFileNotificationIntervalSeconds > 86400 ||
                s.InterlockProcessNames == null || s.InterlockProcessNames.Count > 100 ||
                s.InterlockProcessNames.Any(x => !ValidProcessName(x)))
                throw new ArgumentException("Interlock RAM·시간·프로세스 목록 또는 알림 설정 범위를 확인하세요.");
            if (s.HistoryDays < 1 || s.HistoryDays > 3650 ||
                s.HistoryMaxBytes < 10485760 || s.HistoryMaxBytes > 100L * 1073741824 || s.AnomalyDurationSeconds < 10 || s.AnomalyDurationSeconds > 3600 ||
                s.CpuWarningPercent < 1 || s.CpuWarningPercent > 100 || !Finite(s.CpuWarningPercent) ||
                !Finite(s.CpuTemperatureWarning) || s.CpuTemperatureWarning < 1 || s.CpuTemperatureWarning > 150 ||
                !Finite(s.DiskTemperatureWarning) || s.DiskTemperatureWarning < 1 || s.DiskTemperatureWarning > 100 ||
                !Finite(s.DiskLatencyWarningMs) || s.DiskLatencyWarningMs < 1 || s.MinimumAvailableRam < 0 ||
                s.AllowedProcessNames == null || s.AllowedProcessNames.Count > 100 || s.AllowedProcessNames.Any(x => string.IsNullOrWhiteSpace(x) || x.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || !x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("환경설정 범위를 확인하세요.");
        }
        private static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
        private static bool ValidProcessName(string name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 128 &&
            name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && name.Length > 4 &&
            name.IndexOfAny(new[] { '\\', '/', ':', '*', '?', '"', '<', '>', '|', '\0', '\n', '\r' }) < 0;
        public void LogError(Exception ex)
        {
            lock (_logSync)
                try
                {
                    string path = Path.Combine(_dataDirectory, "service.log");
                    if (File.Exists(path) && new FileInfo(path).Length > 10 * 1024 * 1024)
                    {
                        for (int i = 8; i >= 1; i--) { string from = path + "." + i, to = path + "." + (i + 1); if (File.Exists(to)) File.Delete(to); if (File.Exists(from)) File.Move(from, to); }
                        if (File.Exists(path + ".1")) File.Delete(path + ".1"); File.Move(path, path + ".1");
                    }
                    File.AppendAllText(path, JsonConvert.SerializeObject(new { utc = DateTime.UtcNow, error = ex.ToString() }) + Environment.NewLine);
                }
                catch
                {
#if NETFRAMEWORK
                    try { System.Diagnostics.EventLog.WriteEntry("ExplorerManager", ex.Message, System.Diagnostics.EventLogEntryType.Error); } catch { }
#endif
                }
        }
        public async Task StopAsync()
        {
            _stop.Cancel(); lock (_sync) _active?.Cancel(); _monitor.Dispose(); _transfers.StopControls(); _transfers.CancelActive();
            try { await Task.WhenAll(_background.Concat(new[] { _operation }).Concat(_transfers.ControlTasks)).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        public void Dispose()
        {
            try { if (_started) StopAsync().GetAwaiter().GetResult(); _transfers.Dispose(); _monitor.Dispose(); _cache.Dispose(); _store.Dispose(); _stop.Dispose(); }
            finally { _instance.Dispose(); }
        }
    }
}
