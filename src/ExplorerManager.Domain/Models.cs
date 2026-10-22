using System;
using System.Collections.Generic;

namespace ExplorerManager.Domain
{
    public enum ThresholdMode { UsedPercent, UsedBytes, FreeBytes }
    public enum AgeBasis { LastWriteTime, CreationTime }

    public sealed class DrivePolicy
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public int Version { get; set; }
        public string Name { get; set; } = "새 정책";
        public string VolumeId { get; set; } = "";
        public List<string> Roots { get; set; } = new List<string>();
        public List<string> Exclusions { get; set; } = new List<string>();
        public List<string> ExcludedExtensions { get; set; } = new List<string>();
        public ThresholdMode ThresholdMode { get; set; } = ThresholdMode.UsedPercent;
        public double StartThreshold { get; set; } = 90;
        public double StopThreshold { get; set; } = 85;
        public AgeBasis AgeBasis { get; set; } = AgeBasis.LastWriteTime;
        public int RetentionDays { get; set; } = 30;
        public bool Enabled { get; set; }
        public int IntervalSeconds { get; set; } = 60;
        public int CooldownSeconds { get; set; } = 300;
        public int MaxScanSeconds { get; set; } = 1800;
        public int MaxRunSeconds { get; set; } = 300;
        public int MaxItems { get; set; } = 10000;
        public long MaxDeleteBytes { get; set; } = 10L * 1024 * 1024 * 1024;
        public long ReadBytesPerSecond { get; set; } = 10L * 1024 * 1024;
        public int StartHour { get; set; }
        public int EndHour { get; set; } = 24;
        public int RestoreProtectionHours { get; set; } = 24;
    }

    public sealed class VolumeStatus
    {
        public string Id { get; set; }
        public string Mount { get; set; }
        public string Label { get; set; }
        public string FileSystem { get; set; }
        public long TotalBytes { get; set; }
        public long FreeBytes { get; set; }
        public long AvailableBytes { get; set; }
        public string State { get; set; } = "Ready";
        public string PhysicalDisks { get; set; } = "UnknownTopology";
        public double UsedPercent => TotalBytes <= 0 ? 0 : 100.0 * (TotalBytes - FreeBytes) / TotalBytes;
        public string Summary => Mount + "  " + UsedPercent.ToString("F1") + "%  / 여유 " + (FreeBytes / 1073741824.0).ToString("F2") + " GiB";
    }

    public sealed class FileSnapshot
    {
        public string Path { get; set; }
        public string Root { get; set; }
        public string VolumeId { get; set; }
        public string FileId { get; set; }
        public string ParentPath { get; set; }
        public bool IsDirectory { get; set; }
        public long Length { get; set; }
        public DateTime CreationUtc { get; set; }
        public DateTime LastWriteUtc { get; set; }
        public DateTime AgeUtc { get; set; }
        public int Attributes { get; set; }
        public string Dacl { get; set; }
        public int LinkCount { get; set; }
        public string UnsupportedReason { get; set; }
    }

    public sealed class RunRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string PolicyId { get; set; }
        public int PolicyVersion { get; set; }
        public string PolicyName { get; set; }
        public string Actor { get; set; }
        public bool DryRun { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime? FinishedUtc { get; set; }
        public DateTime CutoffUtc { get; set; }
        public string State { get; set; } = "Scanning";
        public long FreeBefore { get; set; }
        public long FreeAfter { get; set; }
        public long DeletedBytes { get; set; }
        public long ObservedFreeDelta => FreeAfter - FreeBefore;
        public int DeletedFiles { get; set; }
        public int DeletedDirectories { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }
        public int Candidates { get; set; }
        public long CandidateBytes { get; set; }
        public string CurrentPath { get; set; }
        public string Detail { get; set; }
    }

    public sealed class ItemRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string RunId { get; set; }
        public string PolicyId { get; set; }
        public FileSnapshot File { get; set; }
        public DateTime Utc { get; set; }
        public string State { get; set; }
        public string Reason { get; set; }
        public int ErrorCode { get; set; }
        public string RecoveryState { get; set; } = "Unavailable";
        public string CacheId { get; set; }
        public string Actor { get; set; }
        public long? FreeBefore { get; set; }
        public long? FreeAfter { get; set; }
        public long? ObservedFreeDelta => FreeAfter - FreeBefore;
        public string Path => File?.Path;
        public long Bytes => File?.Length ?? 0;
        public string Kind => File != null && File.IsDirectory ? "폴더" : "파일";
    }
    public sealed class FolderSummary
    {
        public string Path { get; set; }
        public string RunId { get; set; }
        public int DeletedFiles { get; set; }
        public int DeletedDirectories { get; set; }
        public long LogicalBytes { get; set; }
        public long ObservedFreeDelta { get; set; }
    }

    public sealed class RecoveryInfo
    {
        public string Id { get; set; }
        public string ItemId { get; set; }
        public string RunId { get; set; }
        public string PolicyId { get; set; }
        public string Epoch { get; set; }
        public string Path { get; set; }
        public string Hash { get; set; }
        public bool IsDirectory { get; set; }
        public long Length { get; set; }
        public long ChargedBytes { get; set; }
        public DateTime DeletedUtc { get; set; }
        public string State { get; set; }
    }

    public sealed class ProcessIdentity
    {
        public int Pid { get; set; }
        public long StartTimeUtcTicks { get; set; }
        public string Executable { get; set; }
        public string OwnerSid { get; set; }
        public int SessionId { get; set; }
        public string Name { get; set; }
        public bool CanTerminate { get; set; }
        public string Reason { get; set; }
    }

    public sealed class AlertRecord
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public DateTime Utc { get; set; }
        public string Category { get; set; }
        public string Severity { get; set; }
        public string Message { get; set; }
        public string Path { get; set; }
        public string PolicyId { get; set; }
        public string Entity { get; set; }
        public bool Acknowledged { get; set; }
        public List<ProcessIdentity> Processes { get; set; } = new List<ProcessIdentity>();
    }

    public sealed class Metric
    {
        public DateTime Utc { get; set; } = DateTime.UtcNow;
        public string Entity { get; set; }
        public string Name { get; set; }
        public double? Value { get; set; }
        public string Unit { get; set; }
        public string Quality { get; set; } = "Valid";
        public string Provider { get; set; }
        public string Detail { get; set; }
        public string Display => Value.HasValue ? Value.Value.ToString("F2") + " " + Unit : Quality;
    }

    public sealed class AppSettings
    {
        public const long MinimumCacheBytes = 1048576L;
        public const long MaximumCacheBytes = 16L * 1073741824;
        public const long DefaultCacheBytes = 1073741824L;
        public long CacheBytes { get; set; } = DefaultCacheBytes;
        public List<string> AllowedProcessNames { get; set; } = new List<string>();
        public int HistoryDays { get; set; } = 90;
        public long HistoryMaxBytes { get; set; } = 2147483648;
        public double CpuWarningPercent { get; set; } = 90;
        public double CpuTemperatureWarning { get; set; } = 85;
        public double DiskTemperatureWarning { get; set; } = 50;
        public double DiskLatencyWarningMs { get; set; } = 100;
        public int AnomalyDurationSeconds { get; set; } = 60;
        public bool PauseOnHealthCritical { get; set; } = true;
        public long MinimumAvailableRam { get; set; } = 1073741824;
    }
}
