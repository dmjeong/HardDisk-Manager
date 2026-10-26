using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Domain;

namespace ExplorerManager.Application
{
    public interface ITransferReadLease : IDisposable { FileSnapshot Snapshot { get; } Stream Content { get; } }
    public interface ITransferSource
    {
        IEnumerable<FileSnapshot> Enumerate(TransferPolicy policy, CancellationToken cancellation);
        ITransferReadLease OpenVerified(FileSnapshot expected);
    }
    public interface IFileDeletionGate
    {
        IDisposable AcquireCommitPermit();
        DeletionDecision CheckDeletion(FileSnapshot file, string sha256 = null);
    }
    public interface IFileWorkCoordinator
    {
        Task<IDisposable> AcquireAsync(string volumeId, CancellationToken cancellation);
        Task LimitReadAsync(string volumeId, int bytes, CancellationToken cancellation);
    }
    public interface ITransferStore
    {
        TransferOptions TransferOptions();
        TransferPolicy TransferPolicy(string id);
        IList<TransferPolicy> TransferPolicies();
        TransferJob ClaimTransfer(string epoch, IList<string> excludedConnections, DateTime now);
        bool SaveTransferJob(TransferJob job);
        void CompleteTransfer(TransferJob job, TransferEvidence evidence);
        TransferJob ReplaceTransferContent(TransferJob job, string sha256);
        void RecordTransferAttempt(TransferAttempt attempt);
        void TransferAlert(string connectionId, string code, string message);
    }
    public interface ITransferTransport : IDisposable
    {
        Task<TransferCapabilities> ProbeAsync(CancellationToken cancellation);
        Task PrepareAsync(TransferJob job, CancellationToken cancellation);
        Task<TransferRemoteState> InspectAsync(string path, CancellationToken cancellation);
        Task<TransferUploadResult> UploadAsync(TransferJob job, Stream source, long offset, Action<TransferProgress> progress, CancellationToken cancellation);
        Task<TransferVerificationResult> VerifyAsync(string path, long length, TransferVerification mode, Action<long> progress, CancellationToken cancellation);
        Task<string> PublishAsync(TransferJob job, CancellationToken cancellation);
        Task RemoveOwnedTempAsync(TransferJob job, CancellationToken cancellation);
    }
    public sealed class TransferRemoteState
    {
        public bool Known { get; set; }
        public bool Exists { get; set; }
        public long Length { get; set; }
    }
    public sealed class TransferUploadResult { public long Sent { get; set; } public string Reply { get; set; } }
    public sealed class TransferVerificationResult
    {
        public string Sha256 { get; set; }
        public string Method { get; set; }
        public long BytesRead { get; set; }
        public TransferEvidenceLevel Level { get; set; }
    }
    public sealed class TransferFailure : IOException
    {
        public string Code { get; }
        public bool Retryable { get; }
        public TransferFailure(string code, string message, bool retryable = false) : base(message) { Code = code; Retryable = retryable; }
    }
}
