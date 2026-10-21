using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Domain;

namespace ExplorerManager.Application
{
    public interface IClock { DateTime UtcNow { get; } }
    public interface IVolumeCatalog
    {
        IList<VolumeStatus> List();
        VolumeStatus Get(string volumeId);
    }
    public interface IJournal
    {
        void SaveRun(RunRecord run);
        void RecordItem(ItemRecord item);
        void AddCandidate(string runId, FileSnapshot file);
        FileSnapshot TakeNextCandidate(string runId);
        void ClearCandidates(string runId);
        void SetCandidateTotals(RunRecord run);
        void SaveAlert(AlertRecord alert);
        void SetRecoveryState(string cacheId, string state);
        bool IsProtected(FileSnapshot file, DateTime now);
        void Protect(FileSnapshot file, DateTime untilUtc);
        void RecordEvent(string action, string actor, string entity, string detail);
    }
    public interface IFileLease : IDisposable
    {
        FileSnapshot Snapshot { get; }
        Stream Content { get; }
        void MarkForDeletion();
    }
    public interface IFileSystem
    {
        Task ScanAsync(DrivePolicy policy, DateTime cutoff, string runId, IJournal journal, CancellationToken cancellation);
        IFileLease OpenVerified(FileSnapshot expected, DrivePolicy policy);
        bool IsAbsent(FileSnapshot file);
        FileSnapshot FindEmptyParent(FileSnapshot deleted, DrivePolicy policy, DateTime cutoff);
        Task<FileSnapshot> RestoreAsync(FileSnapshot original, Stream content, string expectedHash, string targetDirectory, CancellationToken cancellation);
    }
    public interface IRecoveryLease : IDisposable
    {
        RecoveryInfo Info { get; }
        FileSnapshot Original { get; }
        Stream OpenContent();
    }
    public interface IRecoveryCache
    {
        long Capacity { get; }
        long Used { get; }
        string Epoch { get; }
        bool CanFit(FileSnapshot file);
        Task<RecoveryInfo> CaptureAsync(FileSnapshot file, Stream content, string itemId, string runId, string policyId, long bytesPerSecond, CancellationToken cancellation);
        void Commit(string id, bool confirmed);
        void Remove(string id, string reason);
        IList<RecoveryInfo> List();
        IRecoveryLease Pin(string id);
    }
    public interface ILockInspector { IList<ProcessIdentity> Inspect(string path); }
    public interface IExecutionGate
    {
        string StopReason(DrivePolicy policy);
    }
    public sealed class CacheCapacityException : Exception
    {
        public CacheCapacityException(string message) : base(message) { }
    }
    public sealed class FileChangedException : IOException
    {
        public FileChangedException(string message) : base(message) { }
    }
}
