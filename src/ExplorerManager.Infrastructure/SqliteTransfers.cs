using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using ExplorerManager.Application;
using ExplorerManager.Domain;
using ExplorerManager.Contracts;
using Microsoft.Data.Sqlite;

namespace ExplorerManager.Infrastructure
{
    public sealed partial class SqliteJournal : ITransferStore, IFileDeletionGate
    {
        private List<StoredTransferGuard> _transferGuardCache;
        private void InitializeTransfers()
        {
            using (var tx = _db.BeginTransaction())
            using (var c = _db.CreateCommand())
            {
                c.Transaction = tx;
                c.CommandText = @"
CREATE TABLE IF NOT EXISTS transfer_connections(id TEXT PRIMARY KEY,version INTEGER NOT NULL,json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS transfer_secrets(id TEXT PRIMARY KEY,value BLOB NOT NULL);
CREATE TABLE IF NOT EXISTS transfer_policies(id TEXT PRIMARY KEY,version INTEGER NOT NULL,enabled INTEGER NOT NULL,archived INTEGER NOT NULL,json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS transfer_guards(id TEXT PRIMARY KEY,policyid TEXT NOT NULL,fingerprint TEXT NOT NULL,json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS transfer_jobs(id TEXT PRIMARY KEY,policyid TEXT NOT NULL,connectionid TEXT NOT NULL,pathkey TEXT NOT NULL,sourceid TEXT NOT NULL,fingerprint TEXT NOT NULL,state TEXT NOT NULL,current INTEGER NOT NULL,due TEXT NOT NULL,priority INTEGER NOT NULL,created TEXT NOT NULL,updated TEXT NOT NULL,lease TEXT,json TEXT NOT NULL);
CREATE UNIQUE INDEX IF NOT EXISTS transfer_source_current ON transfer_jobs(policyid,pathkey) WHERE current=1;
CREATE UNIQUE INDEX IF NOT EXISTS transfer_job_identity ON transfer_jobs(sourceid,fingerprint);
CREATE INDEX IF NOT EXISTS transfer_job_due ON transfer_jobs(state,due,priority,created,id);
CREATE INDEX IF NOT EXISTS transfer_job_page ON transfer_jobs(created DESC,id DESC);
CREATE TABLE IF NOT EXISTS transfer_evidence(id TEXT PRIMARY KEY,jobid TEXT NOT NULL,pathkey TEXT NOT NULL,fingerprint TEXT NOT NULL,sha TEXT NOT NULL,json TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS transfer_proof ON transfer_evidence(pathkey,fingerprint,sha);
CREATE TABLE IF NOT EXISTS transfer_attempts(id TEXT PRIMARY KEY,jobid TEXT NOT NULL,utc TEXT NOT NULL,json TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS transfer_attempt_job ON transfer_attempts(jobid,utc DESC);
CREATE TABLE IF NOT EXISTS transfer_revisions(id TEXT PRIMARY KEY,json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS transfer_operations(id TEXT PRIMARY KEY,utc TEXT NOT NULL,json TEXT NOT NULL);
PRAGMA user_version=2;";
                c.ExecuteNonQuery(); tx.Commit();
            }
        }
        private static string PathKey(string path) => path.Replace('/', '\\').ToUpperInvariant();
        private SqliteCommand TransferCommand(string sql, SqliteTransaction tx, params object[] args)
        {
            var c = _db.CreateCommand(); c.CommandText = sql; c.Transaction = tx;
            for (int i = 0; i < args.Length; i++) c.Parameters.AddWithValue("$" + i, args[i] ?? DBNull.Value);
            return c;
        }
        private int TransferExec(string sql, SqliteTransaction tx, params object[] args)
        { using (var c = TransferCommand(sql, tx, args)) return c.ExecuteNonQuery(); }
        private List<T> TransferRead<T>(string sql, params object[] args)
        {
            using (var c = TransferCommand(sql, null, args))
            using (var r = c.ExecuteReader()) { var list = new List<T>(); while (r.Read()) list.Add(Parse<T>(r.GetString(0))); return list; }
        }
        private void TransferEvent(SqliteTransaction tx, string action, string actor, string entity, string detail) =>
            TransferExec("INSERT INTO events(utc,action,actor,entity,detail) VALUES($0,$1,$2,$3,$4)", tx, DateTime.UtcNow.ToString("O"), action, actor, entity, detail);
        public TransferOptions TransferOptions()
        { lock (_sync) return TransferRead<TransferOptions>("SELECT json FROM settings WHERE id='transfers'").SingleOrDefault() ?? new TransferOptions(); }
        public void SaveTransferOptions(TransferOptions options, string actor)
        {
            TransferRules.ValidateOptions(options);
            lock (_sync) using (var tx = _db.BeginTransaction())
            {
                TransferExec("INSERT OR REPLACE INTO settings VALUES('transfers',$0)", tx, Json(options));
                TransferEvent(tx, "TransferOptionsChanged", actor, "Transfers", Json(options)); tx.Commit();
            }
        }
        public List<TransferConnection> TransferConnections()
        {
            lock (_sync)
            {
                var list = TransferRead<TransferConnection>("SELECT json FROM transfer_connections ORDER BY id");
                foreach (var c in list) c.HasSecret = HasTransferSecret(c.Id);
                return list;
            }
        }
        public TransferConnection TransferConnection(string id)
        { lock (_sync) return TransferConnections().SingleOrDefault(x => x.Id == id); }
        public TransferConnection SaveTransferConnection(TransferConnection connection, string actor)
        {
            TransferRules.ValidateConnection(connection);
            lock (_sync)
            {
                var old = TransferConnection(connection.Id);
                if ((old == null && connection.Version != 0) || (old != null && old.Version != connection.Version)) throw new InvalidOperationException("연결 설정이 변경되었습니다. 다시 조회하세요.");
                if (old != null && TransferRules.ConnectionFingerprint(old) != TransferRules.ConnectionFingerprint(connection))
                {
                    using (var q = TransferCommand("SELECT COUNT(*) FROM transfer_jobs WHERE connectionid=$0", null, connection.Id))
                        if (Convert.ToInt64(q.ExecuteScalar()) > 0) throw new InvalidOperationException("전송 실적이 있는 서버의 목적지는 고정됩니다. 새 Connection을 등록하세요. 비밀번호 교체는 가능합니다.");
                    if (TransferPolicies().Any(x => x.ConnectionId == connection.Id && x.ProtectionRegistered)) throw new InvalidOperationException("삭제 보호가 등록된 서버 목적지는 새 Connection으로 등록하세요.");
                }
                connection.Version++; connection.HasSecret = HasTransferSecret(connection.Id); connection.Capabilities = old?.Capabilities;
                using (var tx = _db.BeginTransaction())
                {
                    TransferExec("INSERT OR REPLACE INTO transfer_connections VALUES($0,$1,$2)", tx, connection.Id, connection.Version, Json(connection));
                    TransferEvent(tx, "TransferConnectionChanged", actor, connection.Id, Json(connection)); tx.Commit();
                }
                return Parse<TransferConnection>(Json(connection));
            }
        }
        public bool HasTransferSecret(string id)
        { lock (_sync) using (var c = TransferCommand("SELECT COUNT(*) FROM transfer_secrets WHERE id=$0", null, id)) return Convert.ToInt32(c.ExecuteScalar()) != 0; }
        public byte[] TransferSecret(string id)
        { lock (_sync) using (var c = TransferCommand("SELECT value FROM transfer_secrets WHERE id=$0", null, id)) return c.ExecuteScalar() as byte[]; }
        public void SaveTransferSecret(string id, byte[] ciphertext, string actor)
        {
            lock (_sync)
            {
                if (TransferConnection(id) == null) throw new ArgumentException("먼저 Connection을 저장하세요.");
                using (var tx = _db.BeginTransaction()) {
                    TransferExec("INSERT OR REPLACE INTO transfer_secrets VALUES($0,$1)", tx, id, ciphertext);
                    TransferEvent(tx, "TransferSecretChanged", actor, id, "Credential replaced; value redacted"); tx.Commit();
                }
            }
        }
        public void SaveTransferCapabilities(string id, TransferCapabilities capabilities)
        {
            lock (_sync)
            {
                var c = TransferConnection(id) ?? throw new ArgumentException("Connection이 없습니다.");
                if (capabilities.TargetFingerprint != TransferRules.ConnectionFingerprint(c)) throw new InvalidOperationException("연결 시험 중 목적지가 변경되었습니다.");
                c.Capabilities = capabilities;
                TransferExec("UPDATE transfer_connections SET json=$0 WHERE id=$1", null, Json(c), id);
            }
        }
        public IList<TransferPolicy> TransferPolicies()
        { lock (_sync) return TransferRead<TransferPolicy>("SELECT json FROM transfer_policies ORDER BY id"); }
        public TransferPolicy TransferPolicy(string id)
        { lock (_sync) return TransferRead<TransferPolicy>("SELECT json FROM transfer_policies WHERE id=$0", id).SingleOrDefault(); }
        private sealed class StoredTransferGuard
        {
            public TransferPolicy Policy { get; set; }
            public string Fingerprint { get; set; }
        }
        public TransferPolicy SaveTransferPolicy(TransferPolicy p, string actor)
        {
            TransferRules.ValidatePolicy(p);
            lock (_sync)
            {
                var c = TransferConnection(p.ConnectionId) ?? throw new ArgumentException("Connection을 선택하세요.");
                var old = TransferPolicy(p.Id);
                if ((old == null && p.Version != 0) || (old != null && p.Version != old.Version)) throw new InvalidOperationException("전송 정책이 변경되었습니다. 다시 조회하세요.");
                foreach (var other in TransferPolicies().Where(x => x.Id != p.Id))
                    if (TransferRules.Within(p.LocalRoot, other.LocalRoot) || TransferRules.Within(other.LocalRoot, p.LocalRoot)) throw new ArgumentException("다른 전송 정책과 원본 경로가 중첩됩니다.");
                if (old?.ProtectionRegistered == true && !p.RequireVerifiedUpload) throw new InvalidOperationException("등록된 삭제 보호는 Pause/Archive로 유지됩니다. 보호 완화는 자동으로 적용하지 않습니다.");
                if (old?.ProtectionRegistered == true && (old.LocalRoot != p.LocalRoot || old.VolumeId != p.VolumeId || old.ConnectionId != p.ConnectionId ||
                    TransferRules.DestinationFingerprint(old, c) != TransferRules.DestinationFingerprint(p, c)))
                    throw new InvalidOperationException("삭제 보호가 등록된 원본/목적지의 변경은 새 정책으로 등록하세요. 기존 정책은 Pause/Archive하고 보호를 유지하세요.");
                p.Version++; p.ProtectionRegistered = old?.ProtectionRegistered == true || (p.Enabled && p.RequireVerifiedUpload);
                p.LastCompleteScanUtc = old?.LastCompleteScanUtc; p.ActivatedUtc = old?.ActivatedUtc ?? (p.Enabled ? (DateTime?)DateTime.UtcNow : null);
                using (var tx = _db.BeginTransaction())
                {
                    TransferExec("INSERT OR REPLACE INTO transfer_policies VALUES($0,$1,$2,$3,$4)", tx, p.Id, p.Version, p.Enabled ? 1 : 0, p.Archived ? 1 : 0, Json(p));
                    TransferExec("INSERT INTO transfer_revisions VALUES($0,$1)", tx, p.Id + "/" + p.Version, Json(p));
                    if (p.ProtectionRegistered)
                        TransferExec("INSERT OR REPLACE INTO transfer_guards VALUES($0,$1,$2,$3)", tx, p.Id + "/" + TransferRules.Digest(Json(new { p.LocalRoot, p.VolumeId, p.IncludeSubdirectories, p.Extensions, p.Exclusions, Fingerprint = TransferRules.DestinationFingerprint(p, c) })), p.Id,
                            TransferRules.DestinationFingerprint(p, c), Json(new StoredTransferGuard { Policy = p, Fingerprint = TransferRules.DestinationFingerprint(p, c) }));
                    TransferEvent(tx, "TransferPolicyChanged", actor, p.Id, Json(p)); tx.Commit();
                    _transferGuardCache = null;
                }
                return Parse<TransferPolicy>(Json(p));
            }
        }
        public void ArchiveTransferPolicy(string id, string actor)
        {
            lock (_sync)
            {
                var p = TransferPolicy(id) ?? throw new ArgumentException("전송 정책이 없습니다.");
                p.Enabled = false; p.Archived = true; SaveTransferPolicy(p, actor);
            }
        }
        private void WriteTransferJob(TransferJob j, SqliteTransaction tx)
        {
            TransferExec(@"INSERT INTO transfer_jobs VALUES($0,$1,$2,$3,$4,$5,$6,$7,$8,$9,$10,$11,$12,$13)
ON CONFLICT(id) DO UPDATE SET policyid=excluded.policyid,connectionid=excluded.connectionid,pathkey=excluded.pathkey,sourceid=excluded.sourceid,
fingerprint=excluded.fingerprint,state=excluded.state,current=excluded.current,due=excluded.due,priority=excluded.priority,
created=excluded.created,updated=excluded.updated,lease=excluded.lease,json=excluded.json", tx,
                j.Id, j.Policy.Id, j.Connection.Id, PathKey(j.Path), j.SourceVersionId, j.DestinationFingerprint, j.State, j.IsCurrent ? 1 : 0,
                j.NextAttemptUtc.ToString("O"), j.Policy.Priority, j.CreatedUtc.ToString("O"), j.UpdatedUtc.ToString("O"), j.LeaseToken, Json(j));
        }
        public TransferJob ObserveTransferFile(TransferPolicy policy, FileSnapshot file, DateTime now, bool baseline, bool contentDirty = false)
        {
            lock (_sync)
            {
                var live = TransferPolicy(policy.Id);
                if (live == null || live.Version != policy.Version || live.Archived || !live.Enabled) return null;
                var old = TransferRead<TransferJob>("SELECT json FROM transfer_jobs WHERE policyid=$0 AND pathkey=$1 AND current=1", policy.Id, PathKey(file.Path)).SingleOrDefault();
                if (old != null && TransferRules.SameFile(old.File, file))
                {
                    if (contentDirty && old.State == "NotSelectedForBackfill")
                    { old.State = "WaitingForReady"; old.StableSinceUtc = now; old.NextAttemptUtc = now.AddSeconds(policy.StableSeconds); old.UpdatedUtc = now; WriteTransferJob(old, null); }
                    if (contentDirty && old.State == "Completed" && old.LeaseToken == null)
                    { old.RecheckContent = true; old.NextAttemptUtc = now; old.State = "Ready"; old.UpdatedUtc = now; WriteTransferJob(old, null); }
                    return old;
                }
                if (old != null && old.LeaseToken == null && string.IsNullOrEmpty(old.SourceSha256) && !old.PublicationIntent)
                {
                    old.File = file; old.StableSinceUtc = now; old.NextAttemptUtc = now.AddSeconds(policy.StableSeconds); old.UpdatedUtc = now;
                    old.State = file.UnsupportedReason == null ? "WaitingForReady" : "NeedsAttention"; old.Detail = file.UnsupportedReason;
                    WriteTransferJob(old, null); return old;
                }
                using (var c = TransferCommand("SELECT COUNT(*) FROM transfer_jobs WHERE current=1 AND state NOT IN ('Completed','SourceMissing','Superseded')", null))
                    if (Convert.ToInt64(c.ExecuteScalar()) >= TransferOptions().MaxPendingJobs) throw new TransferFailure("QueueFull", "전송 대기열 한도에 도달했습니다. 신규 탐색을 보류하고 기존 작업을 처리합니다.", true);
                var connection = TransferConnection(policy.ConnectionId) ?? throw new TransferFailure("ConnectionMissing", "Connection이 없습니다.");
                var job = new TransferJob { Policy = policy, Connection = connection, File = file, CreatedUtc = now, UpdatedUtc = now,
                    StableSinceUtc = now, NextAttemptUtc = now.AddSeconds(policy.StableSeconds), DestinationFingerprint = TransferRules.DestinationFingerprint(policy, connection) };
                job.RemoteFinal = TransferRules.RemoteFinal(policy, file);
                job.RemoteTemp = policy.PublishMode == TransferPublishMode.DirectWrite ? job.RemoteFinal :
                    policy.PublishMode == TransferPublishMode.SameDirectoryRename ? job.RemoteFinal + ".em-" + job.Id + ".part" :
                    TransferRules.RemotePath(policy.StagingRoot).TrimEnd('/') + "/em-" + job.Id + ".part";
                if (baseline && !policy.BackfillExisting && policy.LastCompleteScanUtc == null) { job.State = "NotSelectedForBackfill"; job.NextAttemptUtc = DateTime.MaxValue; }
                if (TransferRead<bool>("SELECT json FROM settings WHERE id=$0", "transfer-backfill-" + policy.Id).FirstOrDefault())
                { job.ManualRequested = true; job.State = "WaitingForReady"; job.NextAttemptUtc = now.AddSeconds(policy.StableSeconds); }
                if (!string.IsNullOrEmpty(file.UnsupportedReason)) { job.State = "NeedsAttention"; job.ErrorCode = "UnsupportedSource"; job.Detail = file.UnsupportedReason; }
                using (var tx = _db.BeginTransaction())
                {
                    if (old != null) { old.IsCurrent = false; if (!old.PublicationIntent && old.LeaseToken == null) old.State = "Superseded"; WriteTransferJob(old, tx); }
                    WriteTransferJob(job, tx); tx.Commit();
                }
                return job;
            }
        }
        public void CompleteTransferScan(string policyId, int version, DateTime now)
        {
            lock (_sync)
            {
                var p = TransferPolicy(policyId); if (p == null || p.Version != version) return;
                p.LastCompleteScanUtc = now; TransferExec("UPDATE transfer_policies SET json=$0 WHERE id=$1", null, Json(p), p.Id);
                TransferExec("DELETE FROM settings WHERE id=$0", null, "transfer-backfill-" + p.Id);
            }
        }
        public void RecoverTransfers(string epoch)
        {
            lock (_sync)
            {
                var jobs = TransferRead<TransferJob>("SELECT json FROM transfer_jobs WHERE lease IS NOT NULL");
                using (var tx = _db.BeginTransaction())
                {
                    foreach (var j in jobs)
                    {
                        j.LeaseToken = null; j.BootEpoch = epoch; j.State = j.PublicationIntent && j.State != "Completed" ? "ReconcileRequired" : j.State == "Completed" ? "Completed" : "RetryWaiting";
                        j.NextAttemptUtc = DateTime.UtcNow; j.UpdatedUtc = DateTime.UtcNow; j.StateVersion++; WriteTransferJob(j, tx);
                    }
                    tx.Commit();
                }
            }
        }
        public TransferJob ClaimTransfer(string epoch, IList<string> excludedConnections, DateTime now)
        {
            lock (_sync)
            {
                if (TransferOptions().Paused) return null;
                var due = TransferRead<TransferJob>(@"SELECT j.json FROM transfer_jobs j JOIN transfer_policies p ON p.id=j.policyid
WHERE j.lease IS NULL AND p.enabled=1 AND p.archived=0 AND j.due<=$0 AND j.state IN ('WaitingForReady','Ready','RetryWaiting','ReconcileRequired')
ORDER BY j.priority + CAST((julianday($0)-julianday(j.created))*24 AS INTEGER) DESC,j.created,j.id LIMIT 128", now.ToString("O"));
                foreach (var j in due)
                {
                    if (excludedConnections.Contains(j.Connection.Id)) continue;
                    var p = TransferPolicy(j.Policy.Id);
                    if (!j.PublicationIntent && !j.ManualRequested && (p.Trigger == TransferTrigger.Manual ||
                        (p.Trigger == TransferTrigger.Schedule && !TransferRules.InWindow(p, now.ToLocalTime())))) continue;
                    j.Policy.Priority = p.Priority; j.Policy.StableSeconds = p.StableSeconds; j.Policy.MaxFileBytes = p.MaxFileBytes;
                    j.Policy.ContentAuditHours = p.ContentAuditHours; j.Policy.Verification = p.Verification;
                    j.Connection = TransferConnection(j.Connection.Id) ?? j.Connection;
                    j.LeaseToken = Guid.NewGuid().ToString("N"); j.BootEpoch = epoch; j.Attempts++; j.StateVersion++; j.UpdatedUtc = now;
                    WriteTransferJob(j, null); return j;
                }
                return null;
            }
        }
        public bool SaveTransferJob(TransferJob job)
        {
            lock (_sync)
            {
                var live = TransferJob(job.Id);
                if (live == null || live.LeaseToken != job.LeaseToken) return false;
                job.IsCurrent = live.IsCurrent;
                job.UpdatedUtc = DateTime.UtcNow; job.StateVersion++; WriteTransferJob(job, null); return true;
            }
        }
        public void ReleaseTransferJob(TransferJob job)
        {
            lock (_sync)
            {
                using (var c = TransferCommand("SELECT lease FROM transfer_jobs WHERE id=$0", null, job.Id))
                    if (!string.Equals(c.ExecuteScalar() as string, job.LeaseToken, StringComparison.Ordinal)) return;
                job.LeaseToken = null; job.ManualRequested = false; job.UpdatedUtc = DateTime.UtcNow; WriteTransferJob(job, null);
            }
        }
        public void CompleteTransfer(TransferJob job, TransferEvidence evidence)
        {
            lock (_sync)
            {
                using (var c = TransferCommand("SELECT lease FROM transfer_jobs WHERE id=$0", null, job.Id))
                    if ((c.ExecuteScalar() as string) != job.LeaseToken) throw new InvalidOperationException("전송 lease가 변경되었습니다.");
                using (var tx = _db.BeginTransaction())
                {
                    job.State = "Completed"; job.EvidenceLevel = evidence.Level; job.ErrorCode = null; job.Detail = evidence.VerificationMethod;
                    job.RecheckContent = false; job.UpdatedUtc = DateTime.UtcNow;
                    job.NextAuditUtc = job.Policy.ContentAuditHours == 0 ? null : (DateTime?)DateTime.UtcNow.AddHours(job.Policy.ContentAuditHours);
                    TransferExec("INSERT OR REPLACE INTO transfer_evidence VALUES($0,$1,$2,$3,$4,$5)", tx, evidence.Id, job.Id, PathKey(job.Path), job.DestinationFingerprint, evidence.SourceSha256, Json(evidence));
                    WriteTransferJob(job, tx); TransferEvent(tx, "TransferCompleted", "Service", job.Id, Json(evidence)); tx.Commit();
                }
            }
        }
        public TransferJob ReplaceTransferContent(TransferJob old, string sha256)
        {
            lock (_sync)
            using (var tx = _db.BeginTransaction())
            {
                var j = Parse<TransferJob>(Json(old)); j.Id = Guid.NewGuid().ToString("N"); j.SourceVersionId = Guid.NewGuid().ToString("N");
                j.SourceSha256 = sha256; j.RecheckContent = false; j.EvidenceLevel = TransferEvidenceLevel.None; j.PublicationIntent = false;
                j.State = "Ready"; j.LeaseToken = null; j.CreatedUtc = DateTime.UtcNow; j.NextAttemptUtc = j.CreatedUtc; j.Attempts = 0;
                j.RemoteTemp = j.Policy.PublishMode == TransferPublishMode.DirectWrite ? j.RemoteFinal :
                    j.Policy.PublishMode == TransferPublishMode.SameDirectoryRename ? j.RemoteFinal + ".em-" + j.Id + ".part" :
                    TransferRules.RemotePath(j.Policy.StagingRoot).TrimEnd('/') + "/em-" + j.Id + ".part";
                old.IsCurrent = false; old.State = "Superseded"; old.LeaseToken = null;
                WriteTransferJob(old, tx); WriteTransferJob(j, tx); TransferEvent(tx, "TransferSourceChanged", "Service", old.Id, j.Id); tx.Commit(); return j;
            }
        }
        public void ScheduleTransferAudits(DateTime now)
        {
            lock (_sync)
            {
                foreach (var j in TransferRead<TransferJob>("SELECT json FROM transfer_jobs WHERE state='Completed' AND current=1 AND lease IS NULL AND julianday(json_extract(json,'$.NextAuditUtc'))<=julianday($0) LIMIT 256", now.ToString("O")))
                    if (j.NextAuditUtc.HasValue && j.NextAuditUtc.Value <= now)
                    { j.State = "Ready"; j.RecheckContent = true; j.NextAttemptUtc = now; WriteTransferJob(j, null); }
            }
        }
        public void RequestTransferRetry(string id, string actor, bool forceResend = false)
        {
            lock (_sync)
            {
                var j = TransferJob(id) ?? throw new ArgumentException("전송 작업이 없습니다.");
                if (j.LeaseToken != null) throw new InvalidOperationException("현재 전송 중입니다.");
                if (!j.IsCurrent) throw new InvalidOperationException("교체된 이전 세대의 파일은 재전송하지 않습니다.");
                if (forceResend) throw new InvalidOperationException("결과 불명 파일의 강제 재게시를 자동 지원하지 않습니다. 원격 상태를 먼저 확인하세요.");
                j.State = j.PublicationIntent && j.State != "Completed" ? "ReconcileRequired" : "Ready";
                j.RecheckContent = j.State == "Ready" && !string.IsNullOrEmpty(j.SourceSha256); j.ManualRequested = true;
                j.NextAttemptUtc = DateTime.UtcNow; j.ErrorCode = null; WriteTransferJob(j, null);
                RecordEvent("TransferRetryRequested", actor, j.Id, j.State);
            }
        }
        public void RequestTransferBackfill(string policyId, string actor)
        {
            lock (_sync)
            {
                if (TransferPolicy(policyId) == null) throw new ArgumentException("전송 정책이 없습니다.");
                TransferExec("INSERT OR REPLACE INTO settings VALUES($0,'true')", null, "transfer-backfill-" + policyId);
                foreach (var j in TransferRead<TransferJob>("SELECT json FROM transfer_jobs WHERE policyid=$0 AND current=1 AND lease IS NULL AND state IN ('WaitingForReady','Ready','NotSelectedForBackfill','RetryWaiting')", policyId))
                { j.State = "Ready"; j.ManualRequested = true; j.NextAttemptUtc = DateTime.UtcNow.AddSeconds(j.Policy.StableSeconds); WriteTransferJob(j, null); }
                RecordEvent("TransferBackfillRequested", actor, policyId, "Observed current sources selected");
            }
        }
        public void RetryTransferCredentials(string connectionId)
        {
            lock (_sync)
                foreach (var j in TransferRead<TransferJob>("SELECT json FROM transfer_jobs WHERE connectionid=$0 AND lease IS NULL AND current=1 AND state='NeedsAttention'", connectionId))
                    if (j.ErrorCode == "AuthenticationFailed" || j.ErrorCode == "CredentialMissing" || j.ErrorCode == "CredentialUnreadable")
                    { j.State = j.PublicationIntent ? "ReconcileRequired" : "RetryWaiting"; j.NextAttemptUtc = DateTime.UtcNow; WriteTransferJob(j, null); }
        }
        public TransferJob TransferJob(string id)
        { lock (_sync) return TransferRead<TransferJob>("SELECT json FROM transfer_jobs WHERE id=$0", id).SingleOrDefault(); }
        public List<TransferJob> TransferJobs(string state, string search, string cursor, int limit, out bool more)
        {
            lock (_sync)
            {
                limit = Math.Max(1, Math.Min(100, limit)); search = (search ?? "").ToUpperInvariant();
                if (search.Length > 200 || (cursor ?? "").Length > 100) throw new ArgumentException("조회 조건이 너무 깁니다.");
                string stamp = null, id = null;
                if (!string.IsNullOrEmpty(cursor)) { var parts = cursor.Split('|'); if (parts.Length != 2) throw new ArgumentException("잘못된 cursor"); stamp = parts[0]; id = parts[1]; }
                var list = TransferRead<TransferJob>(@"SELECT json FROM transfer_jobs WHERE ($0='' OR state=$0) AND ($1='' OR instr(pathkey,$1)>0)
AND ($2 IS NULL OR created<$2 OR (created=$2 AND id<$3)) ORDER BY created DESC,id DESC LIMIT $4", state ?? "", search, stamp, id, limit + 1);
                more = list.Count > limit; return list.Take(limit).ToList();
            }
        }
        public List<TransferAttempt> TransferAttempts(string id)
        { lock (_sync) return TransferRead<TransferAttempt>("SELECT json FROM transfer_attempts WHERE jobid=$0 ORDER BY utc DESC LIMIT 100", id); }
        public List<TransferEvidence> TransferEvidenceForJob(string id)
        { lock (_sync) return TransferRead<TransferEvidence>("SELECT json FROM transfer_evidence WHERE jobid=$0", id); }
        public Dictionary<string, long> TransferCounts()
        {
            lock (_sync) using (var c = TransferCommand("SELECT state,COUNT(*),COALESCE(SUM(json_extract(json,'$.File.Length')),0) FROM transfer_jobs WHERE current=1 GROUP BY state", null))
            using (var r = c.ExecuteReader())
            {
                var d = new Dictionary<string, long>(); while (r.Read()) { d[r.GetString(0)] = r.GetInt64(1); d[r.GetString(0) + "Bytes"] = r.GetInt64(2); } return d;
            }
        }
        public void RecordTransferAttempt(TransferAttempt a)
        { lock (_sync) TransferExec("INSERT OR REPLACE INTO transfer_attempts VALUES($0,$1,$2,$3)", null, a.Id, a.JobId, a.StartedUtc.ToString("O"), Json(a)); }
        public void SaveTransferOperation(TransferOperation<object> operation)
        { lock (_sync) TransferExec("INSERT OR REPLACE INTO transfer_operations VALUES($0,$1,$2)", null, operation.Id, operation.Utc.ToString("O"), Json(operation)); }
        public TransferOperation<object> TransferOperation(string id)
        { lock (_sync) return TransferRead<TransferOperation<object>>("SELECT json FROM transfer_operations WHERE id=$0", id).SingleOrDefault(); }
        public void RecoverTransferOperations()
        {
            lock (_sync)
            {
                foreach (var op in TransferRead<TransferOperation<object>>("SELECT json FROM transfer_operations"))
                    if (op.State == "Running") { op.State = "Interrupted"; op.Error = "서비스 재시작으로 작업이 중단되었습니다. 상태를 확인한 뒤 다시 실행하세요."; SaveTransferOperation(op); }
                TransferExec("DELETE FROM transfer_operations WHERE utc<$0", null, DateTime.UtcNow.AddDays(-7).ToString("O"));
            }
        }
        public void TransferAlert(string connectionId, string code, string message)
        {
            lock (_sync)
            {
                string entity = connectionId + "/" + code;
                if (TransferRead<AlertRecord>("SELECT json FROM alerts WHERE ack=0 ORDER BY utc DESC LIMIT 200").Any(x => x.Category == "Transfer" && x.Entity == entity && x.Utc > DateTime.UtcNow.AddMinutes(-5))) return;
                SaveAlert(new AlertRecord { Category = "Transfer", Entity = entity, Severity = "Warning", Message = message, Utc = DateTime.UtcNow });
            }
        }
        public IDisposable AcquireCommitPermit() { Monitor.Enter(_sync); return new MonitorPermit(_sync); }
        private sealed class MonitorPermit : IDisposable
        {
            private object _value; public MonitorPermit(object value) { _value = value; }
            public void Dispose() { if (_value != null) { var v = _value; _value = null; Monitor.Exit(v); } }
        }
        public DeletionDecision CheckDeletion(FileSnapshot file, string sha256 = null)
        {
            if (file.IsDirectory) return DeletionDecision.Allow();
            lock (_sync)
            {
                if (_transferGuardCache == null) _transferGuardCache = TransferRead<StoredTransferGuard>("SELECT json FROM transfer_guards");
                var guards = _transferGuardCache.Where(x => TransferRules.Matches(x.Policy, file)).ToList();
                var result = DeletionDecision.Allow();
                foreach (var guard in guards.GroupBy(x => x.Fingerprint).Select(x => x.First()))
                {
                    if (!string.Equals(guard.Policy.VolumeId, file.VolumeId, StringComparison.OrdinalIgnoreCase)) return DeletionDecision.Hold("TransferVolumeChanged");
                    var evidence = TransferRead<TransferEvidence>("SELECT json FROM transfer_evidence WHERE pathkey=$0 AND fingerprint=$1", PathKey(file.Path), guard.Fingerprint)
                        .FirstOrDefault(x => x.Level == TransferEvidenceLevel.PublishedVerified && TransferRules.ValidSha256(x.SourceSha256) && TransferRules.SameFile(x.File, file) &&
                            (sha256 == null || string.Equals(x.SourceSha256, sha256, StringComparison.OrdinalIgnoreCase)) && x.SourceSha256 == x.RemoteSha256);
                    if (evidence == null) return DeletionDecision.Hold(sha256 == null ? "AwaitingVerifiedUpload" : "TransferContentChanged");
                    result.EvidenceIds.Add(evidence.Id);
                }
                return result;
            }
        }
    }
}
