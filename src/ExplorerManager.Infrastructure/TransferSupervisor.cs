using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Application;
using ExplorerManager.Contracts;
using ExplorerManager.Domain;

namespace ExplorerManager.Infrastructure
{
    public sealed class TransferSupervisor : IDisposable
    {
        private readonly SqliteJournal _store;
        private readonly WindowsTransferSource _source;
        private readonly WindowsTransferSecrets _secrets;
        private readonly IFileWorkCoordinator _work;
        private readonly Func<TransferPolicy, string> _stopReason;
        private readonly Action<Exception> _fault;
        private readonly string _epoch = Guid.NewGuid().ToString("N");
        private readonly object _sync = new object();
        private readonly Dictionary<string, ActiveTransfer> _active = new Dictionary<string, ActiveTransfer>();
        private readonly Dictionary<string, DateTime> _blocked = new Dictionary<string, DateTime>();
        private readonly Dictionary<string, int> _failures = new Dictionary<string, int>();
        private readonly ConcurrentQueue<SourceChange> _changes = new ConcurrentQueue<SourceChange>();
        private readonly ConcurrentDictionary<string, bool> _dirtyRoots = new ConcurrentDictionary<string, bool>();
        private readonly Dictionary<string, FileSystemWatcher> _watchers = new Dictionary<string, FileSystemWatcher>();
        private readonly Dictionary<string, int> _watchVersions = new Dictionary<string, int>();
        private readonly Dictionary<string, DateTime> _nextScan = new Dictionary<string, DateTime>();
        private readonly AsyncByteLimiter _upload = new AsyncByteLimiter(), _download = new AsyncByteLimiter();
        private readonly CancellationTokenSource _controlsStop = new CancellationTokenSource();
        private readonly Dictionary<string, Task> _controls = new Dictionary<string, Task>();
        private int _changeCount;
        private volatile TransferOptions _options;
        private sealed class SourceChange { public string PolicyId; public string Path; }
        private sealed class ActiveTransfer { public TransferJob Job; public CancellationTokenSource Cancellation; public TransferProgress Progress; }
        public TransferSupervisor(SqliteJournal store, WindowsTransferSource source, IFileWorkCoordinator work,
            Func<TransferPolicy, string> stopReason, Action<Exception> fault)
        {
            _store = store; _source = source; _secrets = new WindowsTransferSecrets(store); _work = work; _stopReason = stopReason; _fault = fault;
            _options = store.TransferOptions();
            store.RecoverTransferOperations();
        }
        public TransferOptions Options => _options;
        public void RefreshOptions() { _options = _store.TransferOptions(); }
        public ITransferTransport CreateTransport(TransferConnection connection) => new FtpTransferTransport(connection, _secrets.Read(connection.Id), () => _options, _upload, _download);
        public async Task RunAsync(CancellationToken ct)
        {
            _store.RecoverTransfers(_epoch);
            var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() => WorkerLoopAsync(i, ct), ct)).ToList();
            tasks.Add(Task.Run(() => DiscoveryLoopAsync(ct), ct)); tasks.Add(Task.Run(() => WatchLoopAsync(ct), ct));
            try { await Task.WhenAll(tasks).ConfigureAwait(false); }
            finally { DisposeWatchers(); }
        }
        private async Task WorkerLoopAsync(int worker, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TransferJob job = null; CancellationTokenSource operation = null;
                try
                {
                    if (worker >= _options.Workers || _options.Paused) { await Task.Delay(1000, ct).ConfigureAwait(false); continue; }
                    List<string> excluded;
                    lock (_sync)
                    {
                        excluded = _blocked.Where(x => x.Value > DateTime.UtcNow).Select(x => x.Key).ToList();
                        foreach (var g in _active.Values.GroupBy(x => x.Job.Connection.Id))
                            if (g.Count() >= g.First().Job.Connection.MaxConnections) excluded.Add(g.Key);
                        job = _store.ClaimTransfer(_epoch, excluded, DateTime.UtcNow);
                        if (job != null)
                        {
                            operation = CancellationTokenSource.CreateLinkedTokenSource(ct); operation.CancelAfter(TimeSpan.FromSeconds(_options.MaxAttemptSeconds));
                            _active[job.Id] = new ActiveTransfer { Job = job, Cancellation = operation,
                                Progress = new TransferProgress { JobId = job.Id, Stage = job.State, Total = job.Bytes } };
                        }
                    }
                    if (job == null) { await Task.Delay(1000, ct).ConfigureAwait(false); continue; }
                    string reason = _stopReason(job.Policy);
                    if (reason != null) { job.State = job.PublicationIntent ? "ReconcileRequired" : "RetryWaiting"; job.Detail = reason; job.ErrorCode = "Interlock"; job.NextAttemptUtc = DateTime.UtcNow.AddSeconds(5); _store.SaveTransferJob(job); }
                    else
                    {
                        var coordinator = new TransferCoordinator(_store, _source, CreateTransport, _work, p => {
                            lock (_sync) { ActiveTransfer active; if (_active.TryGetValue(p.JobId, out active)) active.Progress = p; }
                        });
                        await coordinator.RunAsync(job, operation.Token).ConfigureAwait(false);
                        TrackConnection(job);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
                catch (Exception ex) {
                    _fault(ex);
                    if (job != null) try { job.State = job.PublicationIntent ? "PublicationUnknown" : "NeedsAttention"; job.ErrorCode = "TransferInternalError"; job.Detail = "서비스 로그를 확인하세요."; job.NextAttemptUtc = DateTime.MaxValue; _store.SaveTransferJob(job); } catch (Exception persist) { _fault(persist); }
                    try { await Task.Delay(5000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
                }
                finally
                {
                    if (job != null)
                    {
                        try { _store.ReleaseTransferJob(job); } catch (Exception ex) { _fault(ex); }
                        lock (_sync) _active.Remove(job.Id);
                    }
                    operation?.Dispose();
                }
            }
        }
        private void TrackConnection(TransferJob job)
        {
            lock (_sync)
            {
                if (job.State == "Completed") { _failures.Remove(job.Connection.Id); _blocked.Remove(job.Connection.Id); return; }
                if (job.ErrorCode == "AuthenticationFailed" || job.ErrorCode == "CredentialMissing" || job.ErrorCode == "CredentialUnreadable" || job.ErrorCode == "TlsAuthentication")
                { _blocked[job.Connection.Id] = DateTime.MaxValue; return; }
                if (job.ErrorCode == "DestinationFull") { _blocked[job.Connection.Id] = DateTime.UtcNow.AddMinutes(5); return; }
                if (job.ErrorCode == "ConnectionUnavailable" || job.ErrorCode == "ConnectionTimeout" || job.ErrorCode == "ConnectionInterrupted")
                {
                    int n; _failures.TryGetValue(job.Connection.Id, out n); _failures[job.Connection.Id] = ++n;
                    if (n >= 5) _blocked[job.Connection.Id] = DateTime.UtcNow.AddSeconds(Math.Min(900, 60 * Math.Pow(2, Math.Min(4, n - 5))));
                }
            }
        }
        public void ClearConnectionBlock(string id)
        { lock (_sync) { _blocked.Remove(id); _failures.Remove(id); } }
        public List<TransferProgress> Active()
        { lock (_sync) return _active.Values.Select(x => x.Progress).ToList(); }
        public void CancelActive(string policyId = null)
        {
            CancellationTokenSource[] all;
            lock (_sync) all = _active.Values.Where(x => policyId == null || x.Job.Policy.Id == policyId).Select(x => x.Cancellation).ToArray();
            foreach (var c in all) try { c.Cancel(); } catch (ObjectDisposedException) { }
        }
        private async Task WatchLoopAsync(CancellationToken ct)
        {
            int tick = 0;
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    RefreshOptions();
                    ActiveTransfer[] all; lock (_sync) all = _active.Values.ToArray();
                    foreach (var active in all)
                    {
                        var policy = _store.TransferPolicy(active.Job.Policy.Id);
                        if (policy == null || !policy.Enabled || policy.Archived || _stopReason(active.Job.Policy) != null)
                            try { active.Cancellation.Cancel(); } catch (ObjectDisposedException) { }
                    }
                    if (++tick % 60 == 0) _store.ScheduleTransferAudits(DateTime.UtcNow);
                }
                catch (Exception ex) { _fault(ex); CancelActive(); }
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        private void Changed(string policyId, string path)
        {
            if (Interlocked.Increment(ref _changeCount) > 4096) { Interlocked.Decrement(ref _changeCount); _dirtyRoots[policyId] = true; return; }
            _changes.Enqueue(new SourceChange { PolicyId = policyId, Path = path });
        }
        public void RequestScan(string policyId) { _dirtyRoots[policyId] = true; }
        public string StartControl(string kind, Func<string, CancellationToken, Task<object>> action, string actor)
        {
            lock (_sync)
            {
                if (_controlsStop.IsCancellationRequested) throw new InvalidOperationException("서비스 종료 중입니다.");
                foreach (string id in _controls.Where(x => x.Value.IsCompleted).Select(x => x.Key).ToArray()) _controls.Remove(id);
                if (_controls.Count >= 2) throw new InvalidOperationException("연결 시험/Preview 작업이 이미 진행 중입니다.");
                var operation = new TransferOperation<object> { Id = Guid.NewGuid().ToString("N"), Kind = kind, State = "Running", Utc = DateTime.UtcNow };
                _store.SaveTransferOperation(operation); _store.RecordEvent("TransferControlStarted", actor, operation.Id, kind);
                _controls[operation.Id] = Task.Run(async () => {
                    try { operation.Result = await action(operation.Id, _controlsStop.Token).ConfigureAwait(false); operation.State = "Completed"; }
                    catch (OperationCanceledException) { operation.State = "Interrupted"; operation.Error = "서비스 종료/취소로 중단되었습니다."; }
                    catch (TransferFailure ex) { operation.State = "Failed"; operation.Error = ex.Code + ": " + ex.Message; }
                    catch (Exception ex) { operation.State = "Failed"; operation.Error = "작업에 실패했습니다. 서비스 로그와 경로/권한을 확인하세요."; _fault(ex); }
                    finally { try { _store.SaveTransferOperation(operation); } catch (Exception ex) { _fault(ex); } }
                });
                return operation.Id;
            }
        }
        public Task[] ControlTasks { get { lock (_sync) return _controls.Values.ToArray(); } }
        public void StopControls() { _controlsStop.Cancel(); }
        private void EnsureWatcher(TransferPolicy p)
        {
            int version;
            if (_watchVersions.TryGetValue(p.Id, out version) && version == p.Version) return;
            FileSystemWatcher old; if (_watchers.TryGetValue(p.Id, out old)) old.Dispose();
            var watcher = new FileSystemWatcher(p.LocalRoot) { IncludeSubdirectories = p.IncludeSubdirectories,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size, InternalBufferSize = 16384 };
            watcher.Created += (s, e) => Changed(p.Id, e.FullPath); watcher.Changed += (s, e) => Changed(p.Id, e.FullPath);
            watcher.Renamed += (s, e) => { Changed(p.Id, e.FullPath); _dirtyRoots[p.Id] = true; };
            watcher.Error += (s, e) => _dirtyRoots[p.Id] = true;
            _watchers[p.Id] = watcher; _watchVersions[p.Id] = p.Version; watcher.EnableRaisingEvents = true; _dirtyRoots[p.Id] = true;
        }
        private async Task DiscoveryLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    var policies = _store.TransferPolicies().Where(x => x.Enabled && !x.Archived).ToList();
                    foreach (string old in _watchers.Keys.Where(x => !policies.Any(p => p.Id == x)).ToArray())
                    { _watchers[old].Dispose(); _watchers.Remove(old); _watchVersions.Remove(old); }
                    foreach (var p in policies)
                    {
                        try { EnsureWatcher(p); }
                        catch (Exception ex) when (ex is IOException || ex is ArgumentException || ex is UnauthorizedAccessException)
                        { _store.TransferAlert(p.ConnectionId, "WatcherUnavailable", "전송 감시 경로를 확인하세요: " + p.LocalRoot); }
                    }
                    SourceChange change; int drained = 0;
                    while (drained++ < 4096 && _changes.TryDequeue(out change))
                    {
                        Interlocked.Decrement(ref _changeCount); var p = policies.FirstOrDefault(x => x.Id == change.PolicyId); if (p == null) continue;
                        try {
                            var file = _source.Inspect(p, change.Path);
                            if (file?.IsDirectory == true) _dirtyRoots[p.Id] = true;
                            else if (file != null && TransferRules.Matches(p, file)) _store.ObserveTransferFile(p, file, DateTime.UtcNow, false, true);
                        }
                        catch (Exception ex) when (ex is IOException || ex is System.ComponentModel.Win32Exception || ex is UnauthorizedAccessException || ex is ArgumentException)
                        { _dirtyRoots[p.Id] = true; }
                    }
                    foreach (var p in policies)
                    {
                        ct.ThrowIfCancellationRequested(); DateTime next; bool dirty;
                        bool force = _dirtyRoots.TryRemove(p.Id, out dirty);
                        if (!force && _nextScan.TryGetValue(p.Id, out next) && next > DateTime.UtcNow) continue;
                        if (_stopReason(p) != null) { _dirtyRoots[p.Id] = true; continue; }
                        try
                        {
                            foreach (var file in _source.Enumerate(p, ct)) _store.ObserveTransferFile(p, file, DateTime.UtcNow, true);
                            _store.CompleteTransferScan(p.Id, p.Version, DateTime.UtcNow);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex) when (ex is IOException || ex is System.ComponentModel.Win32Exception || ex is UnauthorizedAccessException || ex is ArgumentException)
                        { _store.TransferAlert(p.ConnectionId, "ScanIncomplete", "전송 경로 탐색이 완료되지 않았습니다. 원본 보호를 유지합니다: " + p.LocalRoot); }
                        _nextScan[p.Id] = DateTime.UtcNow.AddSeconds(p.ReconcileSeconds);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                catch (Exception ex) { _fault(ex); }
                await Task.Delay(1000, ct).ConfigureAwait(false);
            }
        }
        public async Task<TransferPreview> PreviewAsync(TransferPolicy policy, CancellationToken ct)
        {
            return await Task.Run(() => {
                var result = new TransferPreview();
                foreach (var file in _source.Enumerate(policy, ct))
                { result.Files++; result.Bytes += file.Length; if (file.UnsupportedReason != null) result.Unsupported++; if (result.Samples.Count < 20) result.Samples.Add(file.Path); }
                result.Complete = true; result.Detail = "Metadata preview only. Final source/remote verification runs before publication and cleanup."; return result;
            }, ct).ConfigureAwait(false);
        }
        private void DisposeWatchers() { foreach (var watcher in _watchers.Values) watcher.Dispose(); _watchers.Clear(); _watchVersions.Clear(); }
        public void Dispose() { StopControls(); CancelActive(); DisposeWatchers(); _controlsStop.Dispose(); }
    }
}
