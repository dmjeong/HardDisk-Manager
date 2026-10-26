using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Contracts;
using ExplorerManager.Domain;
using Microsoft.Data.Sqlite;

namespace ExplorerManager.Infrastructure
{
    public sealed partial class ServiceRuntime
    {
        private TransferSupervisor _transfers;
        private WindowsTransferSource _transferSource;
        private WindowsTransferSecrets _transferSecrets;
        private DiskWorkCoordinator _diskWork;
        private void InitializeTransferRuntime()
        {
            _transferSource = new WindowsTransferSource(_volumes); _transferSecrets = new WindowsTransferSecrets(_store);
            _diskWork = new DiskWorkCoordinator(_volumes, () => _transfers?.Options.DiskReadBytesPerSecond ?? 10 * 1048576L);
            _transfers = new TransferSupervisor(_store, _transferSource, _diskWork, TransferStopReason, TransferFault);
        }
        private void TransferFault(Exception error)
        {
            if (error is SqliteException || error is InvalidOperationException && error.Message.IndexOf("database", StringComparison.OrdinalIgnoreCase) >= 0)
                _health = "AuditUnavailable: 전송 상태/증빙 DB를 확인할 수 없습니다.";
            LogError(error);
        }
        private string TransferStopReason(TransferPolicy policy)
        {
            lock (_sync) if (_busy && !_activeSafetyChecks) return "Restore in progress";
            var all = Interlocks(new DrivePolicy { VolumeId = policy.VolumeId });
            var blocked = all.FirstOrDefault(x => x.Blocking && x.Code != "ManualPause" && x.Code != "ProcessRunning" && x.Code != "ProcessInspectionUnavailable");
            return blocked == null ? null : blocked.Code + ": " + blocked.Detail;
        }
        private object DispatchTransfer(Request request, Caller caller)
        {
            var d = request.Data;
            switch (request.Command)
            {
                case "TransferOverview":
                    return new TransferOverview { Available = true, Options = _store.TransferOptions(), Connections = _store.TransferConnections(),
                        Policies = _store.TransferPolicies().ToList(), Counts = _store.TransferCounts(), Active = _transfers.Active(),
                        InterlockReason = _monitor.Interlocks.FirstOrDefault(x => x.Blocking)?.Detail };
                case "TransferJobs":
                    bool more;
                    var jobs = _store.TransferJobs((string)d["State"], (string)d["Search"], (string)d["Cursor"], (int?)d["Limit"] ?? 100, out more);
                    var rows = jobs.Select(TransferJobRow.From).ToList(); var active = _transfers.Active();
                    foreach (var row in rows) { var progress = active.FirstOrDefault(x => x.JobId == row.Id); if (progress != null) { row.State = progress.Stage; row.Progress = progress.Percent; } }
                    var last = jobs.LastOrDefault();
                    return new TransferJobPage { Items = rows, HasMore = more, NextCursor = more && last != null ? last.CreatedUtc.ToString("O") + "|" + last.Id : null };
                case "TransferDetail":
                    string detailId = Required(d, "Id");
                    return new TransferDetail { Job = _store.TransferJob(detailId) ?? throw new ArgumentException("작업이 없습니다."), Attempts = _store.TransferAttempts(detailId), Evidence = _store.TransferEvidenceForJob(detailId) };
                case "TransferOperation": return _store.TransferOperation(Required(d, "Id")) ?? throw new ArgumentException("작업 결과가 없거나 보관 기간이 지났습니다.");
                case "SaveTransferConnection":
                    var connection = d.ToObject<TransferConnection>();
                    if (_store.TransferConnection(connection.Id) == null && _store.TransferConnections().Count >= 32) throw new ArgumentException("Connection은 최대 32개입니다.");
                    var savedConnection = _store.SaveTransferConnection(connection, caller.Sid); _transfers.ClearConnectionBlock(connection.Id); return savedConnection;
                case "SetTransferSecret":
                    string secretId = Required(d, "Id");
                    _transferSecrets.Save(secretId, (string)d["Password"], caller.Sid); _transfers.ClearConnectionBlock(secretId);
                    _store.RetryTransferCredentials(secretId); return true;
                case "SaveTransferPolicy":
                    var policy = d.ToObject<TransferPolicy>(); _transferSource.ValidatePolicy(policy);
                    if (_store.TransferPolicy(policy.Id) == null && _store.TransferPolicies().Count >= 64) throw new ArgumentException("전송 정책은 최대 64개입니다.");
                    var target = _store.TransferConnection(policy.ConnectionId) ?? throw new ArgumentException("Connection을 먼저 저장하세요.");
                    Encoding.GetEncoding(target.EncodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetByteCount(policy.RemoteRoot + policy.StagingRoot);
                    if (policy.Enabled && !target.HasSecret) throw new ArgumentException("FTP 비밀번호를 먼저 서비스에 등록하세요.");
                    var savedPolicy = _store.SaveTransferPolicy(policy, caller.Sid); _transfers.CancelActive(policy.Id); _transfers.RequestScan(policy.Id); return savedPolicy;
                case "ArchiveTransferPolicy":
                    string archiveId = Required(d, "Id"); _store.ArchiveTransferPolicy(archiveId, caller.Sid); _transfers.CancelActive(archiveId); return true;
                case "SaveTransferOptions":
                    var options = d.ToObject<TransferOptions>(); options.Paused = _store.TransferOptions().Paused;
                    _store.SaveTransferOptions(options, caller.Sid); _transfers.RefreshOptions(); return true;
                case "PauseTransfers":
                    var paused = _store.TransferOptions(); paused.Paused = true; _store.SaveTransferOptions(paused, caller.Sid); _transfers.RefreshOptions();
                    if ((bool?)d["Now"] == true) _transfers.CancelActive(); return true;
                case "ResumeTransfers":
                    var resumed = _store.TransferOptions(); resumed.Paused = false; _store.SaveTransferOptions(resumed, caller.Sid); _transfers.RefreshOptions(); return true;
                case "RetryTransfer": _store.RequestTransferRetry(Required(d, "Id"), caller.Sid); return true;
                case "BackfillTransfers":
                    string backfillId = Required(d, "Id"); _store.RequestTransferBackfill(backfillId, caller.Sid); _transfers.RequestScan(backfillId); return true;
                case "TestTransferConnection":
                    var probeConnection = _store.TransferConnection(Required(d, "Id")) ?? throw new ArgumentException("Connection을 먼저 저장하세요.");
                    return _transfers.StartControl("Test connection", async (operationId, ct) => {
                        using (var transport = _transfers.CreateTransport(probeConnection)) {
                            var caps = await transport.ProbeAsync(ct).ConfigureAwait(false); _store.SaveTransferCapabilities(probeConnection.Id, caps); _transfers.ClearConnectionBlock(probeConnection.Id); return caps;
                        }
                    }, caller.Sid);
                case "TestTransferUpload":
                    var uploadConnection = _store.TransferConnection(Required(d, "Id")) ?? throw new ArgumentException("Connection을 먼저 저장하세요.");
                    string testRoot = TransferRules.RemotePath(Required(d, "Directory"));
                    return _transfers.StartControl("Test upload", async (operationId, ct) => {
                        using (var transport = (FtpTransferTransport)_transfers.CreateTransport(uploadConnection)) {
                            var caps = await transport.TestUploadAsync(testRoot, operationId, ct).ConfigureAwait(false); _store.SaveTransferCapabilities(uploadConnection.Id, caps); _transfers.ClearConnectionBlock(uploadConnection.Id); return caps;
                        }
                    }, caller.Sid);
                case "PreviewTransfers":
                    var previewPolicy = d.ToObject<TransferPolicy>(); _transferSource.ValidatePolicy(previewPolicy);
                    return _transfers.StartControl("Preview", async (operationId, ct) => await _transfers.PreviewAsync(previewPolicy, ct).ConfigureAwait(false), caller.Sid);
                default: throw new ArgumentException("지원하지 않는 명령입니다.");
            }
        }
    }
}
