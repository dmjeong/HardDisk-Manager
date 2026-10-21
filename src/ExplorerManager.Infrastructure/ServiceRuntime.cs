using System;
using System.Collections.Generic;
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
    public sealed class ServiceRuntime : IExecutionGate, IDisposable
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
        private readonly Dictionary<string, UnlockTicket> _tickets = new Dictionary<string, UnlockTicket>();
        private readonly HashSet<string> _executingTickets = new HashSet<string>();
        private bool _busy, _paused;
        private string _health = "Ready";
        private readonly string _dataDirectory;
        public ServiceRuntime(string dataDirectory)
        {
            _dataDirectory = Path.GetFullPath(dataDirectory); Directory.CreateDirectory(_dataDirectory);
            _store = new SqliteJournal(Path.Combine(_dataDirectory, "manager.db")); _store.Reconcile();
            _cache = new RecoveryCache(_store.Settings().CacheBytes, _store.SetRecoveryState);
            _files = new WindowsFileSystem(_volumes);
            _monitor = new Monitoring(_store, LogError);
            _engine = new CleanupEngine(_volumes, _files, _store, _cache, new SystemClock(), this, _locks);
        }
        public void Start()
        {
            _background = new[]
            {
                Guard(() => new PipeServer(Execute, LogError).RunAsync(_stop.Token), "IPC"),
                Guard(() => _monitor.RunAsync(_stop.Token), "Monitoring"),
                Guard(ScheduleAsync, "Scheduler")
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
            if (_paused) return "UserPaused";
            if (_health.StartsWith("AuditUnavailable", StringComparison.Ordinal)) return "AuditUnavailable";
            var current = _store.Policy(p.Id);
            if (current == null || current.Version != p.Version) return "PolicyChanged";
            if (_monitor.LoadStopReason != null) return _monitor.LoadStopReason;
            var settings = _store.Settings();
            if (settings.PauseOnHealthCritical)
            {
                var volume = _volumes.Get(p.VolumeId);
                var entities = volume.PhysicalDisks.Split(',').Select(x => "Disk" + x).ToList();
                if (_store.CriticalAlerts().Any(a => !a.Acknowledged && a.Severity == "Critical" &&
                    (a.Entity == p.VolumeId || entities.Contains(a.Entity) || a.Entity == "Storage"))) return "PausedByHealth";
            }
            return null;
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
                        DateTime last;
                        lock (_sync) { if (_busy || _paused) break; _lastRun.TryGetValue(p.Id, out last); }
                        if ((DateTime.UtcNow - last).TotalSeconds < Math.Max(p.IntervalSeconds, p.CooldownSeconds) || !PolicyRules.InWindow(p, DateTime.Now)) continue;
                        try
                        {
                            if (StopReason(p) == null && PolicyRules.ShouldStart(p, _volumes.Get(p.VolumeId))) StartCleanup(p.Id, false, "Scheduler");
                            else lock (_sync) _lastRun[p.Id] = DateTime.UtcNow;
                        }
                        catch (Exception ex) { LogError(ex); lock (_sync) _lastRun[p.Id] = DateTime.UtcNow; }
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
            return StartOperation(async ct => { await _engine.RunAsync(p, preview, actor, ct, onlyPath).ConfigureAwait(false); }, p.Id);
        }
        private string StartOperation(Func<CancellationToken, Task> action, string policyId, bool allowPaused = false)
        {
            lock (_sync)
            {
                if (_busy) throw new InvalidOperationException("현재 작업이 진행 중입니다. 완료 또는 취소 후 다시 시도하세요.");
                if (_paused && !allowPaused) throw new InvalidOperationException("전체 정리가 일시정지되어 있습니다.");
                _busy = true; _active = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
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
                    finally { lock (_sync) { _lastRun[policyId ?? "restore"] = DateTime.UtcNow; _busy = false; _active.Dispose(); _active = null; } }
                });
                return operationId;
            }
        }
        public Response Execute(Request request, Caller caller)
        {
            // Serializes command authorization/version checks and durable idempotency receipts.
            lock (_commands)
            {
                bool query = new[] { "Dashboard", "History", "Folders", "Recovery", "Settings" }.Contains(request.Command);
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
                        Policies = _store.Policies(), Metrics = _monitor.Snapshot().ToList(), Runs = _store.Runs(), Alerts = _store.Alerts() };
                case "SavePolicy":
                    var p = d.ToObject<DrivePolicy>(); PathGuard.ValidatePolicy(p, _volumes);
                    foreach (var other in _store.Policies().Where(x => x.Id != p.Id))
                        if (other.Roots.Any(a => p.Roots.Any(b => PathGuard.Within(a, b) || PathGuard.Within(b, a)))) throw new ArgumentException("다른 정책과 관리 경로가 중복됩니다.");
                    _store.RecordEvent("PolicyChanged", caller.Sid, p.Id, JsonConvert.SerializeObject(p, Protocol.Json));
                    lock (_sync) _active?.Cancel();
                    return _store.SavePolicy(p);
                case "DeletePolicy":
                    string id = Required(d, "Id"); lock (_sync) { if (_busy) throw new InvalidOperationException("작업 종료 후 정책을 삭제하세요."); }
                    _store.RecordEvent("PolicyDeleted", caller.Sid, id, ""); _store.DeletePolicy(id); return true;
                case "Preview": return StartCleanup(Required(d, "Id"), true, caller.Sid);
                case "Run": return StartCleanup(Required(d, "Id"), false, caller.Sid);
                case "Cancel": lock (_sync) _active?.Cancel(); return true;
                case "Pause": _paused = true; lock (_sync) _active?.Cancel(); _store.RecordEvent("Paused", caller.Sid, "", ""); return true;
                case "Resume": _paused = false; _health = "Ready"; _store.RecordEvent("Resumed", caller.Sid, "", ""); return true;
                case "History": return _store.History((string)d["Filter"], Math.Min(1000000, Math.Max(0, (int?)d["Offset"] ?? 0)), (string)d["RunId"]);
                case "Folders": return _store.Folders((string)d["Filter"], Math.Min(1000000, Math.Max(0, (int?)d["Offset"] ?? 0)));
                case "Recovery": return _cache.List();
                case "Settings": return _store.Settings();
                case "SaveSettings":
                    var settings = d.ToObject<AppSettings>(); ValidateSettings(settings);
                    if (settings.CacheBytes != _cache.Capacity) throw new ArgumentException("실행 중 캐시 크기를 변경할 수 없습니다. 서비스 중지 후 설정 마이그레이션이 필요합니다.");
                    _store.SaveSettings(settings); _store.RecordEvent("SettingsChanged", caller.Sid, "", ""); return true;
                case "Restore":
                    string cacheId = Required(d, "Id"); string directory = (string)d["Directory"];
                    var entry = _cache.List().FirstOrDefault(x => x.Id == cacheId) ?? throw new InvalidOperationException("복구본이 퇴출되었습니다.");
                    return StartOperation(async ct => { await _engine.RestoreAsync(cacheId, directory, _store.Policy(entry.PolicyId), caller.Sid, ct).ConfigureAwait(false); }, entry.PolicyId, true);
                case "RestoreFolder":
                    string folder = PathGuard.Normalize(Required(d, "Path"));
                    var entries = _cache.List().Where(x => PathGuard.Within(x.Path, folder)).OrderBy(x => x.Path.Length).ToList();
                    if (entries.Count == 0) throw new InvalidOperationException("남아 있는 복구본이 없습니다.");
                    return StartOperation(async ct =>
                    {
                        foreach (var e in entries)
                        {
                            ct.ThrowIfCancellationRequested();
                            await _engine.RestoreAsync(e.Id, null, _store.Policy(e.PolicyId), caller.Sid, ct).ConfigureAwait(false);
                        }
                    }, entries[0].PolicyId, true);
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
                default: throw new ArgumentException("지원하지 않는 명령입니다.");
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
            if (s == null || s.CacheBytes < 1048576 || s.CacheBytes > 1073741824 || s.HistoryDays < 1 || s.HistoryDays > 3650 ||
                s.HistoryMaxBytes < 10485760 || s.HistoryMaxBytes > 100L * 1073741824 || s.AnomalyDurationSeconds < 10 || s.AnomalyDurationSeconds > 3600 ||
                s.CpuWarningPercent < 1 || s.CpuWarningPercent > 100 || !Finite(s.CpuWarningPercent) ||
                !Finite(s.CpuTemperatureWarning) || s.CpuTemperatureWarning < 1 || s.CpuTemperatureWarning > 150 ||
                !Finite(s.DiskTemperatureWarning) || s.DiskTemperatureWarning < 1 || s.DiskTemperatureWarning > 100 ||
                !Finite(s.DiskLatencyWarningMs) || s.DiskLatencyWarningMs < 1 || s.MinimumAvailableRam < 0 ||
                s.AllowedProcessNames == null || s.AllowedProcessNames.Count > 100 || s.AllowedProcessNames.Any(x => string.IsNullOrWhiteSpace(x) || x.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || !x.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("환경설정 범위를 확인하세요.");
        }
        private static bool Finite(double n) => !double.IsNaN(n) && !double.IsInfinity(n);
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
            _stop.Cancel(); lock (_sync) _active?.Cancel(); _monitor.Dispose();
            try { await Task.WhenAll(_background.Concat(new[] { _operation })).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        public void Dispose() { _monitor.Dispose(); _cache.Dispose(); _store.Dispose(); _stop.Dispose(); }
    }
}
