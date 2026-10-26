using System;
using System.IO;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Domain;

namespace ExplorerManager.Application
{
    public sealed class TransferCoordinator
    {
        private readonly ITransferStore _store;
        private readonly ITransferSource _source;
        private readonly Func<TransferConnection, ITransferTransport> _transport;
        private readonly IFileWorkCoordinator _work;
        private readonly Action<TransferProgress> _progress;
        private readonly Random _random = new Random();
        public TransferCoordinator(ITransferStore store, ITransferSource source, Func<TransferConnection, ITransferTransport> transport,
            IFileWorkCoordinator work = null, Action<TransferProgress> progress = null)
        { _store = store; _source = source; _transport = transport; _work = work; _progress = progress; }

        public async Task RunAsync(TransferJob job, CancellationToken ct)
        {
            var attempt = new TransferAttempt { JobId = job.Id, StartedUtc = DateTime.UtcNow };
            try
            {
                using (var remote = _transport(job.Connection))
                {
                    if (job.State == "ReconcileRequired") { await ReconcileAsync(job, remote, ct).ConfigureAwait(false); return; }
                    using (var lane = _work == null ? null : await _work.AcquireAsync(job.File.VolumeId, ct).ConfigureAwait(false))
                    using (var file = _source.OpenVerified(job.File))
                    {
                        if (!TransferRules.SameFile(file.Snapshot, job.File)) throw new TransferFailure("SourceChanged", "원본 파일이 변경되었습니다. 재검색 후 새 세대로 전송합니다.", true);
                        if (file.Snapshot.Length <= 0 || file.Snapshot.Length > job.Policy.MaxFileBytes) throw new TransferFailure("FileSizeLimit", "이미지 크기가 전송 정책의 범위를 벗어납니다.");
                        if (DateTime.UtcNow < job.StableSinceUtc.AddSeconds(job.Policy.StableSeconds))
                        { job.State = "WaitingForReady"; job.NextAttemptUtc = job.StableSinceUtc.AddSeconds(job.Policy.StableSeconds); Save(job); return; }
                        if (job.Policy.ValidateImageHeader)
                        {
                            byte[] header = new byte[16]; int read = 0;
                            while (read < header.Length) { int n = await file.Content.ReadAsync(header, read, header.Length - read, ct).ConfigureAwait(false); if (n == 0) break; read += n; }
                            file.Content.Position = 0;
                            if (!TransferRules.ImageHeader(Path.GetExtension(job.Path), header, read)) throw new TransferFailure("ImageSignature", "확장자와 이미지 signature가 일치하지 않습니다.");
                        }
                        Stage(job, "Hashing");
                        Func<int, CancellationToken, Task> limit = (n, token) => _work?.LimitReadAsync(job.File.VolumeId, n, token) ?? Task.CompletedTask;
                        string hash = await TransferIO.HashAsync(file.Content, job.Bytes, limit, ct).ConfigureAwait(false);
                        if (!string.IsNullOrEmpty(job.SourceSha256) && hash != job.SourceSha256)
                        { _store.ReplaceTransferContent(job, hash); return; }
                        if (job.RecheckContent && job.EvidenceLevel >= TransferEvidenceLevel.UploadAcknowledged && hash == job.SourceSha256)
                        {
                            job.RecheckContent = false; job.State = "Completed"; job.ErrorCode = null; job.Detail = "Source hash unchanged";
                            job.NextAuditUtc = job.Policy.ContentAuditHours == 0 ? null : (DateTime?)DateTime.UtcNow.AddHours(job.Policy.ContentAuditHours); Save(job); return;
                        }
                        job.SourceSha256 = hash; Save(job); file.Content.Position = 0;
                        await remote.PrepareAsync(job, ct).ConfigureAwait(false);
                        var final = await remote.InspectAsync(job.RemoteFinal, ct).ConfigureAwait(false);
                        if (!final.Known) throw new TransferFailure("RemoteStateUnknown", "최종 경로의 존재/권한을 확정할 수 없습니다.");
                        if (final.Exists)
                        {
                            if (final.Length != job.Bytes) throw new TransferFailure("Conflict", "원격 최종 경로에 다른 파일이 있습니다. 덮어쓰지 않습니다.");
                            var existing = await remote.VerifyAsync(job.RemoteFinal, job.Bytes, TransferVerification.AutoStrong, n => VerifyProgress(job, n), ct).ConfigureAwait(false);
                            if (existing.Sha256 != hash) throw new TransferFailure("Conflict", "원격 최종 파일의 내용이 다릅니다. 덮어쓰지 않습니다.");
                            Complete(job, existing, "Existing remote content verified", "Already present; no mutation"); return;
                        }
                        long offset = 0;
                        if (job.Policy.PublishMode != TransferPublishMode.DirectWrite)
                        {
                            var temp = await remote.InspectAsync(job.RemoteTemp, ct).ConfigureAwait(false);
                            if (!temp.Known) throw new TransferFailure("RemoteStateUnknown", "소유한 임시 파일의 상태를 확인할 수 없습니다.");
                            if (temp.Exists && temp.Length > 0 && temp.Length <= job.Bytes)
                            {
                                Stage(job, "VerifyingRemote");
                                try
                                {
                                    var prefix = await remote.VerifyAsync(job.RemoteTemp, temp.Length, TransferVerification.AutoStrong, n => VerifyProgress(job, n), ct).ConfigureAwait(false);
                                    file.Content.Position = 0;
                                    string prefixHash = await TransferIO.HashAsync(file.Content, temp.Length, limit, ct).ConfigureAwait(false);
                                    if (prefix.Sha256 == prefixHash)
                                    {
                                        if (temp.Length == job.Bytes) offset = job.Bytes;
                                        else
                                        {
                                            var caps = await remote.ProbeAsync(ct).ConfigureAwait(false);
                                            if (caps.UploadResumeVerified) offset = temp.Length;
                                        }
                                    }
                                }
                                catch (TransferFailure ex) when (ex.Code == "VerificationUnavailable") { offset = 0; }
                            }
                            if (temp.Exists && offset == 0) await remote.RemoveOwnedTempAsync(job, ct).ConfigureAwait(false);
                        }
                        string reply = "Owned temp already verified";
                        if (offset < job.Bytes)
                        {
                            if (job.Policy.PublishMode == TransferPublishMode.DirectWrite) { job.PublicationIntent = true; Stage(job, "Publishing"); }
                            else Stage(job, "UploadingTemp");
                            file.Content.Position = offset;
                            using (var input = new TransferReadStream(file.Content, limit, ct))
                            {
                                var result = await remote.UploadAsync(job, input, offset, p => {
                                    job.BytesSent = p.Sent; _progress?.Invoke(p);
                                }, ct).ConfigureAwait(false);
                                reply = result.Reply;
                                if (result.Sent != job.Bytes) throw new TransferFailure("UploadIncomplete", "송신 길이가 원본 길이와 다릅니다.", true);
                            }
                        }
                        Stage(job, "VerifyingRemote");
                        var verified = await remote.VerifyAsync(job.RemoteTemp, job.Bytes, job.Policy.Verification, n => VerifyProgress(job, n), ct).ConfigureAwait(false);
                        if (verified.Level >= TransferEvidenceLevel.ContentVerified && verified.Sha256 != hash) throw new TransferFailure("HashMismatch", "원격 이미지 SHA-256이 원본과 다릅니다.", job.Attempts < 3);
                        if (job.Policy.RequireVerifiedUpload && verified.Level < TransferEvidenceLevel.ContentVerified) throw new TransferFailure("VerificationUnavailable", "삭제 보호 연동에 필요한 원격 SHA-256 검증을 완료하지 못했습니다.");
                        job.PublicationIntent = true; Stage(job, "Publishing");
                        ct.ThrowIfCancellationRequested();
                        string published = job.Policy.PublishMode == TransferPublishMode.DirectWrite ? "Direct write acknowledged" : await remote.PublishAsync(job, ct).ConfigureAwait(false);
                        Complete(job, verified, reply, published);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                job.State = job.PublicationIntent && job.State != "Completed" ? "ReconcileRequired" : "RetryWaiting";
                job.ErrorCode = "Interrupted"; job.Detail = "전송이 중단되었습니다. 원격 상태를 확인한 뒤 재개합니다.";
                job.NextAttemptUtc = DateTime.UtcNow.AddSeconds(5); Save(job);
            }
            catch (TransferFailure ex)
            {
                job.ErrorCode = ex.Code; job.Detail = ex.Message;
                if (ex.Code == "PublicationUnknown" || (job.PublicationIntent && ex.Code != "Conflict" && ex.Code != "HashMismatch" && ex.Code != "VerificationUnavailable"))
                    job.State = "PublicationUnknown";
                else if (ex.Code == "SourceMissing") job.State = "SourceMissing";
                else job.State = ex.Retryable ? "RetryWaiting" : "NeedsAttention";
                job.NextAttemptUtc = ex.Retryable && job.State == "RetryWaiting" ? DateTime.UtcNow.AddSeconds(RetryDelay(job.Attempts)) : DateTime.MaxValue;
                Save(job);
                if (!ex.Retryable || job.Attempts >= 5) _store.TransferAlert(job.Connection.Id, ex.Code, ex.Message);
            }
            catch (IOException)
            {
                job.ErrorCode = "ConnectionInterrupted"; job.Detail = "연결 또는 파일 I/O가 중단되었습니다.";
                job.State = job.PublicationIntent ? "PublicationUnknown" : "RetryWaiting";
                job.NextAttemptUtc = job.PublicationIntent ? DateTime.MaxValue : DateTime.UtcNow.AddSeconds(RetryDelay(job.Attempts)); Save(job);
            }
            catch (Win32Exception ex)
            {
                bool locked = ex.NativeErrorCode == 32 || ex.NativeErrorCode == 33;
                job.State = locked ? "RetryWaiting" : "NeedsAttention"; job.ErrorCode = locked ? "SourceLocked" : "SourceAccess";
                job.Detail = "원본 파일/폴더의 읽기 잠금과 권한을 확인하세요."; job.NextAttemptUtc = locked ? DateTime.UtcNow.AddSeconds(10) : DateTime.MaxValue; Save(job);
            }
            catch (UnauthorizedAccessException)
            { job.State = "NeedsAttention"; job.ErrorCode = "SourceAccess"; job.Detail = "서비스의 원본 읽기 권한을 확인하세요."; job.NextAttemptUtc = DateTime.MaxValue; Save(job); }
            finally
            {
                attempt.FinishedUtc = DateTime.UtcNow; attempt.State = job.State; attempt.ErrorCode = job.ErrorCode;
                attempt.Detail = job.Detail; attempt.BytesSent = job.BytesSent; attempt.BytesVerified = job.BytesVerified;
                _store.RecordTransferAttempt(attempt);
            }
        }
        private async Task ReconcileAsync(TransferJob job, ITransferTransport remote, CancellationToken ct)
        {
            if (!TransferRules.ValidSha256(job.SourceSha256)) throw new TransferFailure("PublicationUnknown", "게시 intent의 원본 hash가 없습니다. 자동 재전송하지 않습니다.");
            var final = await remote.InspectAsync(job.RemoteFinal, ct).ConfigureAwait(false);
            if (!final.Known) throw new TransferFailure("PublicationUnknown", "최종 경로를 조회할 수 없어 게시 결과가 불명확합니다.");
            if (final.Exists)
            {
                if (final.Length != job.Bytes) throw new TransferFailure("Conflict", "최종 경로에 다른 길이의 파일이 있습니다.");
                var verified = await remote.VerifyAsync(job.RemoteFinal, job.Bytes, TransferVerification.AutoStrong, n => VerifyProgress(job, n), ct).ConfigureAwait(false);
                if (verified.Sha256 != job.SourceSha256) throw new TransferFailure("Conflict", "최종 경로의 hash가 원본과 다릅니다.");
                Complete(job, verified, "Recovered by remote SHA-256", "Publication reconciled"); return;
            }
            if (job.Policy.PublishMode != TransferPublishMode.DirectWrite)
            {
                var temp = await remote.InspectAsync(job.RemoteTemp, ct).ConfigureAwait(false);
                if (temp.Known && temp.Exists && temp.Length == job.Bytes)
                {
                    var verified = await remote.VerifyAsync(job.RemoteTemp, job.Bytes, TransferVerification.AutoStrong, n => VerifyProgress(job, n), ct).ConfigureAwait(false);
                    if (verified.Sha256 != job.SourceSha256) throw new TransferFailure("HashMismatch", "소유 임시 파일의 hash가 다릅니다.");
                    Stage(job, "Publishing");
                    string reply = await remote.PublishAsync(job, ct).ConfigureAwait(false); Complete(job, verified, "Owned temp verified on recovery", reply); return;
                }
            }
            throw new TransferFailure("PublicationUnknown", "최종 파일과 임시 파일이 없거나 상태가 불명확합니다. 상위 프로그램이 이미 소비했을 수 있어 자동 재업로드하지 않습니다.");
        }
        private void Save(TransferJob job)
        { if (!_store.SaveTransferJob(job)) throw new OperationCanceledException("Transfer lease changed"); }
        private void Stage(TransferJob job, string state)
        {
            job.State = state; Save(job); _progress?.Invoke(new TransferProgress { JobId = job.Id, Stage = state, Total = job.Bytes, Sent = job.BytesSent, Verified = job.BytesVerified });
        }
        private void VerifyProgress(TransferJob job, long bytes)
        { job.BytesVerified = bytes; _progress?.Invoke(new TransferProgress { JobId = job.Id, Stage = "VerifyingRemote", Verified = bytes, Sent = job.BytesSent, Total = job.Bytes }); }
        private void Complete(TransferJob job, TransferVerificationResult verification, string uploadReply, string publishReply)
        {
            var level = verification.Level >= TransferEvidenceLevel.ContentVerified ? TransferEvidenceLevel.PublishedVerified : verification.Level;
            _store.CompleteTransfer(job, new TransferEvidence { JobId = job.Id, File = job.File, DestinationFingerprint = job.DestinationFingerprint,
                SourceSha256 = job.SourceSha256, RemoteSha256 = verification.Sha256, RemoteFinal = job.RemoteFinal,
                VerificationMethod = verification.Method, UploadReply = uploadReply, PublishReply = publishReply, Level = level });
        }
        private double RetryDelay(int attempts)
        { lock (_random) return Math.Max(1, _random.NextDouble() * Math.Min(300, 5 * Math.Pow(2, Math.Min(10, Math.Max(0, attempts - 1))))); }
    }
}
