using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ExplorerManager.Application;
using ExplorerManager.Contracts;
using ExplorerManager.Domain;
using Microsoft.Data.Sqlite;
using Newtonsoft.Json;

namespace ExplorerManager.Infrastructure
{
    public sealed class SqliteJournal : IJournal, IDisposable
    {
        private readonly object _sync = new object();
        private readonly SqliteConnection _db;
        public SqliteJournal(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
            _db.Open();
            using (var cmd = _db.CreateCommand())
            {
                cmd.CommandText = @"PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;
CREATE TABLE IF NOT EXISTS settings (id TEXT PRIMARY KEY, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS policies (id TEXT PRIMARY KEY, version INTEGER NOT NULL, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS runs (id TEXT PRIMARY KEY, utc TEXT NOT NULL, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS items (id TEXT PRIMARY KEY, runid TEXT NOT NULL, utc TEXT NOT NULL, path TEXT NOT NULL, cacheid TEXT, state TEXT, recovery TEXT, json TEXT NOT NULL);
CREATE INDEX IF NOT EXISTS items_history ON items(utc DESC);
CREATE INDEX IF NOT EXISTS items_cache ON items(cacheid);
CREATE TABLE IF NOT EXISTS candidates (runid TEXT NOT NULL, path TEXT NOT NULL, age TEXT NOT NULL, bytes INTEGER NOT NULL, processed INTEGER NOT NULL DEFAULT 0, json TEXT NOT NULL, PRIMARY KEY(runid,path));
CREATE INDEX IF NOT EXISTS candidates_next ON candidates(runid,processed,age,path);
CREATE TABLE IF NOT EXISTS alerts (id TEXT PRIMARY KEY, utc TEXT NOT NULL, ack INTEGER NOT NULL DEFAULT 0, json TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS protection (path TEXT PRIMARY KEY, fileid TEXT NOT NULL, untilutc TEXT NOT NULL);
CREATE TABLE IF NOT EXISTS events (id INTEGER PRIMARY KEY AUTOINCREMENT, utc TEXT NOT NULL, action TEXT NOT NULL, actor TEXT, entity TEXT, detail TEXT);
CREATE TABLE IF NOT EXISTS requests (sid TEXT NOT NULL, id TEXT NOT NULL, fingerprint TEXT NOT NULL, json TEXT, utc TEXT NOT NULL, PRIMARY KEY(sid,id));
CREATE TABLE IF NOT EXISTS metrics (bucket TEXT NOT NULL, entity TEXT NOT NULL, name TEXT NOT NULL, unit TEXT, sum REAL, min REAL, max REAL, count INTEGER NOT NULL, PRIMARY KEY(bucket,entity,name));
PRAGMA user_version=1;";
                cmd.ExecuteNonQuery();
            }
        }
        private static string Json(object o) => JsonConvert.SerializeObject(o, Protocol.Json);
        private static T Parse<T>(string s) => JsonConvert.DeserializeObject<T>(s, Protocol.Json);
        private SqliteCommand Command(string sql, params object[] values)
        {
            var c = _db.CreateCommand(); c.CommandText = sql;
            for (int i = 0; i < values.Length; i++) c.Parameters.AddWithValue("$" + i, values[i] ?? DBNull.Value);
            return c;
        }
        private void Exec(string sql, params object[] values) { using (var c = Command(sql, values)) c.ExecuteNonQuery(); }
        private List<T> Read<T>(string sql, params object[] values)
        {
            using (var c = Command(sql, values))
            using (var r = c.ExecuteReader())
            { var list = new List<T>(); while (r.Read()) list.Add(Parse<T>(r.GetString(0))); return list; }
        }
        public List<DrivePolicy> Policies() { lock (_sync) return Read<DrivePolicy>("SELECT json FROM policies ORDER BY id"); }
        public DrivePolicy Policy(string id) { lock (_sync) return Read<DrivePolicy>("SELECT json FROM policies WHERE id=$0", id).SingleOrDefault(); }
        public DrivePolicy SavePolicy(DrivePolicy p)
        {
            lock (_sync)
            {
                var current = Policy(p.Id);
                if (current != null && current.Version != p.Version) throw new InvalidOperationException("다른 창에서 정책이 변경되었습니다. 새로 고침하세요.");
                if (current == null && p.Version != 0) throw new InvalidOperationException("정책이 삭제되었습니다.");
                p.Version++;
                Exec("INSERT OR REPLACE INTO policies VALUES($0,$1,$2)", p.Id, p.Version, Json(p));
                return p;
            }
        }
        public void DeletePolicy(string id) { lock (_sync) Exec("DELETE FROM policies WHERE id=$0", id); }
        public Response RequestReceipt(string sid, string id, string fingerprint)
        {
            lock (_sync)
            using (var c = Command("SELECT fingerprint,json FROM requests WHERE sid=$0 AND id=$1", sid, id))
            using (var r = c.ExecuteReader())
            {
                if (!r.Read()) return null;
                if (r.GetString(0) != fingerprint) throw new InvalidOperationException("같은 요청 ID에 다른 내용이 전달되었습니다.");
                if (r.IsDBNull(1)) throw new InvalidOperationException("이전 요청의 결과가 불확정 상태입니다. 상태를 새로 조회하세요.");
                return Parse<Response>(r.GetString(1));
            }
        }
        public void BeginRequest(string sid, string id, string fingerprint)
        { lock (_sync) Exec("INSERT INTO requests VALUES($0,$1,$2,NULL,$3)", sid, id, fingerprint, DateTime.UtcNow.ToString("O")); }
        public void EndRequest(string sid, string id, Response response)
        { lock (_sync) Exec("UPDATE requests SET json=$0 WHERE sid=$1 AND id=$2", Json(response), sid, id); }
        public ItemRecord LatestItem(string path, string policy)
        { lock (_sync) return Read<ItemRecord>("SELECT json FROM items WHERE path=$0 ORDER BY utc DESC LIMIT 50", path).FirstOrDefault(x => x.PolicyId == policy); }
        public AppSettings Settings()
        {
            lock (_sync) return Read<AppSettings>("SELECT json FROM settings WHERE id='app'").SingleOrDefault() ?? new AppSettings();
        }
        public void SaveSettings(AppSettings settings) { lock (_sync) Exec("INSERT OR REPLACE INTO settings VALUES('app',$0)", Json(settings)); }
        public void SaveRun(RunRecord r) { lock (_sync) Exec("INSERT OR REPLACE INTO runs VALUES($0,$1,$2)", r.Id, r.StartedUtc.ToString("O"), Json(r)); }
        public List<RunRecord> Runs(int limit = 50) { lock (_sync) return Read<RunRecord>("SELECT json FROM runs ORDER BY utc DESC LIMIT $0", Math.Min(200, limit)); }
        public void RecordItem(ItemRecord i)
        {
            lock (_sync)
            {
                using (var tx = _db.BeginTransaction())
                {
                    using (var c = Command("INSERT OR REPLACE INTO items VALUES($0,$1,$2,$3,$4,$5,$6,$7)", i.Id, i.RunId, i.Utc.ToString("O"), i.Path, i.CacheId, i.State, i.RecoveryState, Json(i)))
                    { c.Transaction = tx; c.ExecuteNonQuery(); }
                    using (var c = Command("INSERT INTO events(utc,action,actor,entity,detail) VALUES($0,$1,$2,$3,$4)", DateTime.UtcNow.ToString("O"), i.State, i.Actor, i.Id, i.Reason))
                    { c.Transaction = tx; c.ExecuteNonQuery(); }
                    tx.Commit();
                }
            }
        }
        public void AddCandidate(string runId, FileSnapshot f)
        {
            lock (_sync) Exec("INSERT OR IGNORE INTO candidates(runid,path,age,bytes,json) VALUES($0,$1,$2,$3,$4)",
                runId, f.Path, f.AgeUtc.ToString("O"), f.Length, Json(f));
        }
        public FileSnapshot TakeNextCandidate(string runId)
        {
            lock (_sync)
            {
                var f = Read<FileSnapshot>("SELECT json FROM candidates WHERE runid=$0 AND processed=0 ORDER BY age,path LIMIT 1", runId).FirstOrDefault();
                if (f != null) Exec("UPDATE candidates SET processed=1 WHERE runid=$0 AND path=$1", runId, f.Path);
                return f;
            }
        }
        public void SetCandidateTotals(RunRecord run)
        {
            lock (_sync)
            using (var c = Command("SELECT count(*),coalesce(sum(bytes),0) FROM candidates WHERE runid=$0", run.Id))
            using (var r = c.ExecuteReader()) { r.Read(); run.Candidates = r.GetInt32(0); run.CandidateBytes = r.GetInt64(1); }
        }
        public void ClearCandidates(string runId) { lock (_sync) Exec("DELETE FROM candidates WHERE runid=$0", runId); }
        public void SaveAlert(AlertRecord a) { lock (_sync) Exec("INSERT OR REPLACE INTO alerts VALUES($0,$1,$2,$3)", a.Id, a.Utc.ToString("O"), a.Acknowledged ? 1 : 0, Json(a)); }
        public List<AlertRecord> Alerts() { lock (_sync) return Read<AlertRecord>("SELECT json FROM alerts ORDER BY ack,utc DESC LIMIT 100"); }
        public AlertRecord Alert(string id) { lock (_sync) return Read<AlertRecord>("SELECT json FROM alerts WHERE id=$0", id).FirstOrDefault(); }
        public void SetRecoveryState(string id, string state)
        {
            lock (_sync)
                foreach (var i in Read<ItemRecord>("SELECT json FROM items WHERE cacheid=$0", id))
                { i.RecoveryState = state; Exec("UPDATE items SET recovery=$0,json=$1 WHERE id=$2", state, Json(i), i.Id); }
        }
        public bool IsProtected(FileSnapshot f, DateTime now)
        {
            lock (_sync)
            using (var c = Command("SELECT count(*) FROM protection WHERE path=$0 AND fileid=$1 AND untilutc>$2", f.Path, f.FileId, now.ToString("O")))
                return Convert.ToInt64(c.ExecuteScalar()) != 0;
        }
        public void Protect(FileSnapshot f, DateTime untilUtc) { lock (_sync) Exec("INSERT OR REPLACE INTO protection VALUES($0,$1,$2)", f.Path, f.FileId, untilUtc.ToString("O")); }
        public void RecordEvent(string action, string actor, string entity, string detail)
        { lock (_sync) Exec("INSERT INTO events(utc,action,actor,entity,detail) VALUES($0,$1,$2,$3,$4)", DateTime.UtcNow.ToString("O"), action, actor, entity, detail); }
        public HistoryPage History(string filter, int offset, string runId = null)
        {
            lock (_sync)
            {
                string escaped = (filter ?? "").Replace(@"\", @"\\").Replace("%", @"\%").Replace("_", @"\_");
                var list = Read<ItemRecord>(@"SELECT json FROM items WHERE path LIKE $0 ESCAPE '\' AND ($1 IS NULL OR runid=$1) ORDER BY utc DESC,id LIMIT 101 OFFSET $2",
                    "%" + escaped + "%", runId, Math.Max(0, offset));
                return new HistoryPage { Items = list.Take(100).ToList(), HasMore = list.Count > 100, Offset = offset };
            }
        }
        public void Reconcile()
        {
            lock (_sync)
            {
                foreach (var i in Read<ItemRecord>("SELECT json FROM items WHERE recovery IN ('Recoverable','OutcomeUnknown','CacheReady') OR state IN ('DeleteIntent','DeleteRequested')"))
                {
                    i.RecoveryState = "UnavailableAfterRestart";
                    if (i.State == "DeleteIntent" || i.State == "DeleteRequested") { i.State = "OutcomeUnknown"; i.Reason = "서비스 재시작으로 결과를 확정할 수 없습니다. 자동 재삭제하지 않습니다."; }
                    RecordItem(i);
                }
                foreach (var r in Runs(200).Where(x => !x.FinishedUtc.HasValue))
                { r.State = "Interrupted"; r.FinishedUtc = DateTime.UtcNow; SaveRun(r); }
                Exec("DELETE FROM candidates");
            }
        }
        public void SaveMetrics(IEnumerable<Metric> samples)
        {
            lock (_sync)
                foreach (var m in samples.Where(x => x.Value.HasValue && x.Quality == "Valid"))
                    Exec(@"INSERT INTO metrics VALUES($0,$1,$2,$3,$4,$4,$4,1)
ON CONFLICT(bucket,entity,name) DO UPDATE SET sum=sum+excluded.sum,min=min(min,excluded.min),max=max(max,excluded.max),count=count+1",
                        m.Utc.ToString("yyyy-MM-ddTHH:mm:00Z"), m.Entity, m.Name, m.Unit, m.Value.Value);
        }
        public void Maintain(AppSettings settings)
        {
            lock (_sync)
            {
                string cutoff = DateTime.UtcNow.AddDays(-settings.HistoryDays).ToString("O");
                Exec("DELETE FROM items WHERE utc<$0 AND recovery NOT IN ('Recoverable','OutcomeUnknown','CacheReady') AND state NOT IN ('DeleteIntent','DeleteRequested')", cutoff);
                Exec("DELETE FROM runs WHERE utc<$0 AND id NOT IN (SELECT runid FROM items)", cutoff);
                Exec("DELETE FROM alerts WHERE utc<$0 AND ack=1", cutoff);
                Exec("DELETE FROM events WHERE utc<$0", cutoff);
                Exec("DELETE FROM protection WHERE untilutc<$0", DateTime.UtcNow.ToString("O"));
                Exec("DELETE FROM metrics WHERE bucket<$0", DateTime.UtcNow.AddDays(-7).ToString("yyyy-MM-ddTHH:mm:00Z"));
                Exec("PRAGMA wal_checkpoint(TRUNCATE)");
            }
        }
        public void Dispose() { lock (_sync) _db.Dispose(); }
    }
}
