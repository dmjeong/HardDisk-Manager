using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Domain;

namespace ExplorerManager.Application
{
    public sealed class CleanupEngine
    {
        private readonly IVolumeCatalog _volumes;
        private readonly IFileSystem _files;
        private readonly IJournal _journal;
        private readonly IRecoveryCache _cache;
        private readonly IClock _clock;
        private readonly IExecutionGate _gate;
        private readonly ILockInspector _locks;
        private readonly IFileDeletionGate _deletionGate;
        private readonly IFileWorkCoordinator _work;
        public CleanupEngine(IVolumeCatalog volumes, IFileSystem files, IJournal journal, IRecoveryCache cache, IClock clock, IExecutionGate gate, ILockInspector locks,
            IFileDeletionGate deletionGate = null, IFileWorkCoordinator work = null)
        { _volumes = volumes; _files = files; _journal = journal; _cache = cache; _clock = clock; _gate = gate; _locks = locks; _deletionGate = deletionGate ?? journal as IFileDeletionGate; _work = work; }

        public async Task<RunRecord> RunAsync(DrivePolicy policy, bool preview, string actor, CancellationToken cancellation, string onlyPath = null)
        {
            var errors = PolicyRules.Validate(policy);
            if (errors.Count > 0) throw new ArgumentException(string.Join("\n", errors));
            var before = _volumes.Get(policy.VolumeId);
            var run = new RunRecord { PolicyId = policy.Id, PolicyVersion = policy.Version, PolicyName = policy.Name,
                StartedUtc = _clock.UtcNow, CutoffUtc = _clock.UtcNow.AddDays(-policy.RetentionDays), Actor = actor, DryRun = preview,
                FreeBefore = before.FreeBytes, FreeAfter = before.FreeBytes };
            _journal.SaveRun(run);
            var elapsed = Stopwatch.StartNew();
            try
            {
                if (!preview && !PolicyRules.ShouldStart(policy, before)) { run.State = "BelowThreshold"; return run; }
                if (!preview && !PolicyRules.InWindow(policy, _clock.UtcNow.ToLocalTime())) { run.State = "OutsideWindow"; return run; }
                if (!preview) { string reason = _gate.StopReason(policy); if (reason != null) { run.State = reason; return run; } }
                await _files.ScanAsync(policy, run.CutoffUtc, run.Id, _journal, cancellation).ConfigureAwait(false);
                _journal.SetCandidateTotals(run);
                run.State = preview ? "Preview" : "Deleting"; _journal.SaveRun(run);
                var deleteElapsed = Stopwatch.StartNew();
                int attempted = 0;
                long requestedDeleteBytes = 0;
                // Capacity stops wait for the current unit; safety stops remain per item.
                string activeUnit = null;
                bool activeUnitStarted = false;
                bool stopAfterUnit = false; // Latched even if an external writer consumes the recovered space.
                while (true)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (Math.Abs((_clock.UtcNow - run.StartedUtc).TotalSeconds - elapsed.Elapsed.TotalSeconds) > 30)
                    { run.State = "ClockChanged"; break; }
                    if (deleteElapsed.Elapsed.TotalSeconds >= policy.MaxRunSeconds || attempted >= policy.MaxItems)
                    { run.State = "LimitReached"; break; }
                    if (!preview)
                    {
                        string reason = _gate.StopReason(policy);
                        if (reason != null) { run.State = reason; break; }
                        if (!PolicyRules.InWindow(policy, _clock.UtcNow.ToLocalTime())) { run.State = "OutsideWindow"; break; }
                        bool targetReached = PolicyRules.TargetReached(policy, _volumes.Get(policy.VolumeId));
                        if (activeUnitStarted) stopAfterUnit |= targetReached;
                        else if (targetReached) { run.State = "TargetReached"; break; }
                    }
                    var expected = _journal.TakeNextCandidate(run.Id, activeUnit);
                    if (expected == null)
                    {
                        if (activeUnit != null)
                        {
                            activeUnit = null; activeUnitStarted = false;
                            if (stopAfterUnit) { run.State = "TargetReached"; break; }
                            continue;
                        }
                        run.State = preview ? "PreviewComplete" : "NoEligibleCandidates"; break;
                    }
                    if (onlyPath != null && !string.Equals(onlyPath, expected.Path, StringComparison.OrdinalIgnoreCase)) continue;
                    if (onlyPath == null && activeUnit == null) activeUnit = PolicyRules.CleanupUnitPath(expected);
                    attempted++;
                    var item = new ItemRecord { RunId = run.Id, PolicyId = policy.Id, Utc = _clock.UtcNow, File = expected, Actor = actor, State = "Candidate" };
                    if (!PolicyRules.IsExpired(expected, policy, run.CutoffUtc) || _journal.IsProtected(expected, _clock.UtcNow))
                    { Skip(item, run, "보관 기간 또는 복구 보호 대상"); continue; }
                    var preflight = _deletionGate?.CheckDeletion(expected);
                    if (preflight != null && !preflight.Allowed) { Skip(item, run, preflight.Reason); continue; }
                    if (!_cache.CanFit(expected)) { Skip(item, run, "CacheTooSmall"); continue; }
                    if (expected.Length > policy.MaxDeleteBytes - requestedDeleteBytes) { Skip(item, run, "BatchByteLimit"); continue; }
                    RecoveryInfo cached = null;
                    bool requested = false;
                    try
                    {
                        using (var lane = _work == null ? null : await _work.AcquireAsync(policy.VolumeId, cancellation).ConfigureAwait(false))
                        using (var file = _files.OpenVerified(expected, policy))
                        {
                            item.File = file.Snapshot; run.CurrentPath = item.Path;
                            if (preview) { item.State = preflight?.EvidenceIds.Count > 0 ? "EligiblePendingFinalVerification" : "WouldDelete"; _journal.RecordItem(item); continue; }
                            Stream captureInput = _work == null ? file.Content : new TransferReadStream(file.Content, (n, ct) => _work.LimitReadAsync(policy.VolumeId, n, ct), cancellation);
                            cached = await _cache.CaptureAsync(file.Snapshot, captureInput, item.Id, run.Id, policy.Id, policy.ReadBytesPerSecond, cancellation).ConfigureAwait(false);
                            item.CacheId = cached.Id; item.RecoveryState = "CacheReady"; item.State = "DeleteIntent";
                            cancellation.ThrowIfCancellationRequested();
                            var immediatelyBefore = _volumes.Get(policy.VolumeId);
                            string stopReason = Math.Abs((_clock.UtcNow - run.StartedUtc).TotalSeconds - elapsed.Elapsed.TotalSeconds) > 30 ? "ClockChanged" :
                                deleteElapsed.Elapsed.TotalSeconds >= policy.MaxRunSeconds ? "LimitReached" :
                                _gate.StopReason(policy) ?? (!PolicyRules.InWindow(policy, _clock.UtcNow.ToLocalTime()) ? "OutsideWindow" : null);
                            bool targetReached = PolicyRules.TargetReached(policy, immediatelyBefore);
                            if (activeUnitStarted) stopAfterUnit |= targetReached;
                            else if (targetReached && stopReason == null) stopReason = "TargetReached";
                            if (stopReason != null)
                            {
                                _cache.Remove(cached.Id, "Cancelled"); cached = null;
                                Skip(item, run, "조건 변경: " + stopReason); run.State = stopReason; break;
                            }
                            using (var permit = _deletionGate?.AcquireCommitPermit())
                            {
                                var decision = _deletionGate?.CheckDeletion(file.Snapshot, cached.Hash);
                                if (decision != null && !decision.Allowed)
                                { _cache.Remove(cached.Id, decision.Reason); cached = null; Skip(item, run, decision.Reason); continue; }
                                item.TransferEvidenceIds = decision?.EvidenceIds ?? new System.Collections.Generic.List<string>();
                                item.VerifiedSourceHash = cached.Hash;
                                item.FreeBefore = immediatelyBefore.FreeBytes;
                                _journal.RecordItem(item);
                                requested = true;
                                requestedDeleteBytes += item.Bytes;
                                file.MarkForDeletion();
                                file.Dispose(); // Policy/guard publication cannot race deletion handle close.
                            }
                        }
                        bool absent = _files.IsAbsent(item.File);
                        _cache.Commit(cached.Id, absent);
                        item.State = absent ? "DeletedConfirmed" : "OutcomeUnknown";
                        item.RecoveryState = absent ? "Recoverable" : "OutcomeUnknown";
                        var afterDeletion = absent ? _volumes.Get(policy.VolumeId) : null;
                        if (absent) item.FreeAfter = afterDeletion.FreeBytes;
                        _journal.RecordItem(item);
                        if (absent)
                        {
                            if (item.File.IsDirectory) run.DeletedDirectories++; else { run.DeletedFiles++; run.DeletedBytes += item.Bytes; }
                            run.FreeAfter = afterDeletion.FreeBytes;
                            if (onlyPath == null)
                            {
                                activeUnitStarted = true;
                                stopAfterUnit |= PolicyRules.TargetReached(policy, afterDeletion);
                                var parent = _files.FindEmptyParent(item.File, policy, run.CutoffUtc);
                                if (parent != null && string.Equals(PolicyRules.CleanupUnitPath(parent), activeUnit, StringComparison.OrdinalIgnoreCase))
                                    _journal.AddCandidate(run.Id, parent);
                            }
                        }
                        else run.Failed++;
                    }
                    catch (OperationCanceledException)
                    {
                        if (cached != null) { if (requested) _cache.Commit(cached.Id, false); else _cache.Remove(cached.Id, "Cancelled"); }
                        item.State = requested ? "OutcomeUnknown" : "Cancelled"; item.RecoveryState = requested ? "OutcomeUnknown" : "Unavailable";
                        _journal.RecordItem(item); throw;
                    }
                    catch (Exception ex) when (ex is IOException || ex is Win32Exception || ex is UnauthorizedAccessException || ex is CacheCapacityException)
                    {
                        if (cached != null) { if (requested) _cache.Commit(cached.Id, false); else _cache.Remove(cached.Id, "PreparationFailed"); }
                        int code = ex is Win32Exception ? ((Win32Exception)ex).NativeErrorCode : ex.HResult & 0xffff;
                        item.ErrorCode = code; item.Reason = ex.Message;
                        item.State = requested ? "OutcomeUnknown" : "Skipped";
                        item.RecoveryState = requested ? "OutcomeUnknown" : "Unavailable";
                        if (requested) run.Failed++; else run.Skipped++;
                        _journal.RecordItem(item);
                        if (code == 32 || code == 33)
                            _journal.SaveAlert(new AlertRecord { Utc = _clock.UtcNow, Category = "LockedFile", Severity = "Warning",
                                Message = "사용 중인 항목을 건너뛰었습니다. 강제 종료 후 삭제하시겠습니까?", Path = item.Path, PolicyId = policy.Id,
                                Processes = _locks.Inspect(item.Path).ToList() });
                        if (code == 23 || code == 1117 || code == 21)
                        {
                            _journal.SaveAlert(new AlertRecord { Utc = _clock.UtcNow, Category = "DiskIO", Severity = "Critical", Entity = policy.VolumeId, Path = item.Path, Message = ex.Message });
                            run.State = "PausedByHealth"; break;
                        }
                    }
                    catch
                    {
                        if (cached != null)
                        {
                            if (requested) _cache.Commit(cached.Id, false);
                            else _cache.Remove(cached.Id, "AuditUnavailable");
                        }
                        throw;
                    }
                    _journal.SaveRun(run);
                }
            }
            catch (OperationCanceledException)
            {
                run.State = "UserCancelled";
                try { run.State = _gate.StopReason(policy) ?? run.State; } catch { run.State = "AuditUnavailable"; }
            }
            catch (TimeoutException ex) { run.State = "ScanIncomplete"; run.Detail = ex.Message; }
            catch (Exception ex) { run.State = "Failed"; run.Detail = ex.ToString(); throw; }
            finally
            {
                run.FinishedUtc = _clock.UtcNow; run.CurrentPath = null;
                try { run.FreeAfter = _volumes.Get(policy.VolumeId).FreeBytes; } catch { }
                _journal.SaveRun(run);
                _journal.ClearCandidates(run.Id);
            }
            return run;
        }
        private void Skip(ItemRecord i, RunRecord r, string reason)
        { i.State = "Skipped"; i.Reason = reason; r.Skipped++; _journal.RecordItem(i); }

        public async Task<string> RestoreAsync(string cacheId, string targetDirectory, DrivePolicy policy, string actor, CancellationToken cancellation)
        {
            FileSnapshot restored;
            using (var lease = _cache.Pin(cacheId))
            using (var content = lease.OpenContent())
            {
                _journal.RecordEvent("RestoreIntent", actor, cacheId, targetDirectory ?? lease.Info.Path);
                restored = await _files.RestoreAsync(lease.Original, content, lease.Info.Hash, targetDirectory, cancellation).ConfigureAwait(false);
                _journal.Protect(restored, _clock.UtcNow.AddHours(policy?.RestoreProtectionHours ?? 24));
                _journal.RecordEvent("Restored", actor, cacheId, restored.Path);
            }
            _cache.Remove(cacheId, "Restored");
            return restored.Path;
        }
    }
}
