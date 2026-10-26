using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace ExplorerManager.Domain
{
    public enum TransferEncryption { ExplicitTls, ImplicitTls, Plain }
    public enum TransferPublishMode { StagingThenRename, SameDirectoryRename, DirectWrite }
    public enum TransferVerification { AutoStrong, ReadBackSha256, SizeOnly }
    public enum TransferTrigger { OnReady, Schedule, Manual }
    public enum TransferEvidenceLevel { None, UploadAcknowledged, SizeChecked, ContentVerified, PublishedVerified }

    public sealed class TransferConnection
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public int Version { get; set; }
        public string Name { get; set; } = "FTP server";
        public string Host { get; set; } = "";
        public int Port { get; set; } = 21;
        public string UserName { get; set; } = "";
        public TransferEncryption Encryption { get; set; } = TransferEncryption.ExplicitTls;
        public string EncodingName { get; set; } = "utf-8";
        public bool UseEpsv { get; set; } = true;
        public int ConnectTimeoutSeconds { get; set; } = 15;
        public int CommandTimeoutSeconds { get; set; } = 30;
        public int NoProgressTimeoutSeconds { get; set; } = 60;
        public int MaxConnections { get; set; } = 2;
        public bool HasSecret { get; set; }
        public TransferCapabilities Capabilities { get; set; }
    }
    public sealed class TransferCapabilities
    {
        public DateTime CheckedUtc { get; set; }
        public string TargetFingerprint { get; set; }
        public bool Connected { get; set; }
        public bool ServerSha256 { get; set; }
        public bool ResumeAdvertised { get; set; }
        public bool UploadResumeVerified { get; set; }
        public bool ReadBackVerified { get; set; }
        public bool RenameVerified { get; set; }
        public string Detail { get; set; }
    }
    public sealed class TransferPolicy
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public int Version { get; set; }
        public string Name { get; set; } = "Image transfer";
        public string ConnectionId { get; set; } = "";
        public string VolumeId { get; set; } = "";
        public string LocalRoot { get; set; } = "";
        public string RemoteRoot { get; set; } = "/incoming";
        public string StagingRoot { get; set; } = "/staging";
        public bool PreserveSubdirectories { get; set; } = true;
        public bool IncludeSubdirectories { get; set; } = true;
        public List<string> Extensions { get; set; } = new List<string> { ".jpg", ".jpeg", ".png", ".bmp", ".tif", ".tiff" };
        public List<string> Exclusions { get; set; } = new List<string>();
        public bool Enabled { get; set; }
        public bool Archived { get; set; }
        public bool ProtectionRegistered { get; set; }
        public bool RequireVerifiedUpload { get; set; } = true;
        public bool BackfillExisting { get; set; } = true;
        public bool ValidateImageHeader { get; set; } = true;
        public bool AllowCreateDirectories { get; set; } = true;
        public bool PublishCompatibilityConfirmed { get; set; }
        public bool ExclusiveDestinationConfirmed { get; set; }
        public TransferPublishMode PublishMode { get; set; } = TransferPublishMode.StagingThenRename;
        public TransferVerification Verification { get; set; } = TransferVerification.AutoStrong;
        public TransferTrigger Trigger { get; set; }
        public int StableSeconds { get; set; } = 10;
        public int ReconcileSeconds { get; set; } = 300;
        public int MaxScanSeconds { get; set; } = 1800;
        public int ContentAuditHours { get; set; } = 24;
        public int Priority { get; set; } = 3;
        public long MaxFileBytes { get; set; } = 2147483648L;
        public int StartHour { get; set; }
        public int EndHour { get; set; } = 24;
        public int StartMinute { get; set; }
        public int EndMinute { get; set; }
        public ScheduleDays ScheduleDays { get; set; } = ScheduleDays.All;
        public DateTime? LastCompleteScanUtc { get; set; }
        public DateTime? ActivatedUtc { get; set; }
    }
    public sealed class TransferOptions
    {
        public int Workers { get; set; } = 2;
        public long UploadBytesPerSecond { get; set; } = 10 * 1048576L;
        public long DownloadBytesPerSecond { get; set; } = 10 * 1048576L;
        public long DiskReadBytesPerSecond { get; set; } = 10 * 1048576L;
        public int MaxPendingJobs { get; set; } = 1000000;
        public int MaxAttemptSeconds { get; set; } = 3600;
        public bool Paused { get; set; }
    }
    public sealed class TransferJob
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string SourceVersionId { get; set; } = Guid.NewGuid().ToString("N");
        public TransferPolicy Policy { get; set; }
        public TransferConnection Connection { get; set; }
        public FileSnapshot File { get; set; }
        public string DestinationFingerprint { get; set; }
        public string SourceSha256 { get; set; }
        public string RemoteTemp { get; set; }
        public string RemoteFinal { get; set; }
        public string State { get; set; } = "WaitingForReady";
        public TransferEvidenceLevel EvidenceLevel { get; set; }
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
        public DateTime StableSinceUtc { get; set; } = DateTime.UtcNow;
        public DateTime NextAttemptUtc { get; set; } = DateTime.UtcNow;
        public DateTime? NextAuditUtc { get; set; }
        public int Attempts { get; set; }
        public int StateVersion { get; set; }
        public string LeaseToken { get; set; }
        public string BootEpoch { get; set; }
        public bool IsCurrent { get; set; } = true;
        public bool PublicationIntent { get; set; }
        public bool RecheckContent { get; set; }
        public bool ManualRequested { get; set; }
        public string ErrorCode { get; set; }
        public string Detail { get; set; }
        public long BytesSent { get; set; }
        public long BytesVerified { get; set; }
        public string Path => File?.Path;
        public long Bytes => File?.Length ?? 0;
    }
    public sealed class TransferEvidence
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string JobId { get; set; }
        public FileSnapshot File { get; set; }
        public string DestinationFingerprint { get; set; }
        public string SourceSha256 { get; set; }
        public string RemoteSha256 { get; set; }
        public string RemoteFinal { get; set; }
        public string VerificationMethod { get; set; }
        public string UploadReply { get; set; }
        public string PublishReply { get; set; }
        public TransferEvidenceLevel Level { get; set; }
        public DateTime Utc { get; set; } = DateTime.UtcNow;
    }
    public sealed class TransferAttempt
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string JobId { get; set; }
        public DateTime StartedUtc { get; set; }
        public DateTime FinishedUtc { get; set; }
        public string State { get; set; }
        public string ErrorCode { get; set; }
        public string Detail { get; set; }
        public long BytesSent { get; set; }
        public long BytesVerified { get; set; }
    }
    public sealed class TransferProgress
    {
        public string JobId { get; set; }
        public string Stage { get; set; }
        public long Sent { get; set; }
        public long Verified { get; set; }
        public long Total { get; set; }
        public double BytesPerSecond { get; set; }
        public double Percent => Total <= 0 ? 0 : Math.Min(100, 100.0 * (Stage == "VerifyingRemote" ? Verified : Sent) / Total);
    }
    public sealed class DeletionDecision
    {
        public bool Allowed { get; set; }
        public string Reason { get; set; }
        public List<string> EvidenceIds { get; set; } = new List<string>();
        public static DeletionDecision Allow() => new DeletionDecision { Allowed = true };
        public static DeletionDecision Hold(string reason) => new DeletionDecision { Reason = reason };
    }
    public static class TransferRules
    {
        public static bool Within(string path, string root) => !string.IsNullOrEmpty(path) && !string.IsNullOrEmpty(root) &&
            (path.Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));
        public static bool Matches(TransferPolicy p, FileSnapshot f)
        {
            if (p == null || f == null || f.IsDirectory || !Within(f.Path, p.LocalRoot)) return false;
            string relative = f.Path.Substring(p.LocalRoot.TrimEnd('\\').Length + 1);
            return (p.IncludeSubdirectories || relative.IndexOf('\\') < 0) &&
                (p.Extensions ?? new List<string>()).Any(x => string.Equals(x, Path.GetExtension(f.Path), StringComparison.OrdinalIgnoreCase)) &&
                !(p.Exclusions ?? new List<string>()).Any(x => Within(f.Path, x));
        }
        public static bool SameFile(FileSnapshot a, FileSnapshot b) => a != null && b != null &&
            string.Equals(a.VolumeId, b.VolumeId, StringComparison.OrdinalIgnoreCase) && a.FileId == b.FileId &&
            string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase) && a.Length == b.Length &&
            a.CreationUtc == b.CreationUtc && a.LastWriteUtc == b.LastWriteUtc;
        public static string Digest(string value)
        { using (var sha = SHA256.Create()) return Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(value))); }
        public static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
        public static bool ValidSha256(string hash) => hash != null && hash.Length == 64 && hash.All(x => (x >= '0' && x <= '9') || (x >= 'a' && x <= 'f'));
        public static string ConnectionFingerprint(TransferConnection c) => Digest(string.Join("\n", c.Id, c.Host.ToLowerInvariant(), c.Port, c.UserName, c.EncodingName, c.Encryption));
        public static string DestinationFingerprint(TransferPolicy p, TransferConnection c) => Digest(string.Join("\n", ConnectionFingerprint(c),
            RemotePath(p.RemoteRoot), p.PreserveSubdirectories, p.LocalRoot.ToUpperInvariant()));
        public static string RemotePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || !path.StartsWith("/", StringComparison.Ordinal) ||
                path.IndexOfAny(new[] { '\r', '\n', '\0', '\\' }) >= 0 || path.Split('/').Any(x => x == "." || x == ".."))
                throw new ArgumentException("원격 경로는 /로 시작하며 .., 역슬래시, 제어 문자를 포함할 수 없습니다.");
            return path == "/" ? "/" : path.TrimEnd('/');
        }
        public static string RemoteFinal(TransferPolicy p, FileSnapshot f)
        {
            string relative = f.Path.Substring(p.LocalRoot.TrimEnd('\\').Length + 1).Replace('\\', '/');
            if (!p.PreserveSubdirectories) relative = relative.Substring(relative.LastIndexOf('/') + 1);
            if (relative.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0 || relative.Split('/').Any(x => x.Length == 0 || x == "." || x == "..")) throw new ArgumentException("파일 이름을 원격 경로로 표현할 수 없습니다.");
            return RemotePath(RemotePath(p.RemoteRoot).TrimEnd('/') + "/" + relative);
        }
        public static bool InWindow(TransferPolicy p, DateTime local) => PolicyRules.InWindow(new DrivePolicy {
            StartHour = p.StartHour, StartMinute = p.StartMinute, EndHour = p.EndHour, EndMinute = p.EndMinute, ScheduleDays = p.ScheduleDays }, local);
        public static void ValidateConnection(TransferConnection c)
        {
            Guid id;
            if (c == null || !Guid.TryParseExact(c.Id, "N", out id) || string.IsNullOrWhiteSpace(c.Name) || c.Name.Length > 100 ||
                string.IsNullOrWhiteSpace(c.Host) || c.Host.Length > 253 || Uri.CheckHostName(c.Host) == UriHostNameType.Unknown ||
                c.Port < 1 || c.Port > 65535 || string.IsNullOrWhiteSpace(c.UserName) || c.UserName.Length > 256 ||
                c.UserName.IndexOfAny(new[] { '\r', '\n', '\0' }) >= 0 || !Enum.IsDefined(typeof(TransferEncryption), c.Encryption) ||
                c.ConnectTimeoutSeconds < 1 || c.ConnectTimeoutSeconds > 300 || c.CommandTimeoutSeconds < 1 || c.CommandTimeoutSeconds > 600 ||
                c.NoProgressTimeoutSeconds < 5 || c.NoProgressTimeoutSeconds > 3600 || c.MaxConnections < 1 || c.MaxConnections > 8 ||
                string.IsNullOrWhiteSpace(c.EncodingName) || c.EncodingName.Length > 64)
                throw new ArgumentException("FTP 연결 이름·주소·포트·계정·timeout 범위를 확인하세요.");
            Encoding.GetEncoding(c.EncodingName, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        public static void ValidatePolicy(TransferPolicy p)
        {
            Guid id;
            if (p == null || !Guid.TryParseExact(p.Id, "N", out id) || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 100 ||
                string.IsNullOrWhiteSpace(p.VolumeId) || string.IsNullOrWhiteSpace(p.ConnectionId) || string.IsNullOrWhiteSpace(p.LocalRoot) ||
                p.Extensions == null || p.Extensions.Count == 0 || p.Extensions.Count > 50 || p.Extensions.Any(x => x == null || x.Length < 2 || x.Length > 16 || !x.StartsWith(".") || x.Skip(1).Any(ch => !char.IsLetterOrDigit(ch))) ||
                p.Exclusions == null || p.Exclusions.Count > 100 || p.Exclusions.Any(x => !Within(x, p.LocalRoot)) ||
                p.StableSeconds < 2 || p.StableSeconds > 3600 || p.ReconcileSeconds < 30 || p.ReconcileSeconds > 86400 ||
                p.MaxScanSeconds < 30 || p.MaxScanSeconds > 86400 || p.ContentAuditHours < 0 || p.ContentAuditHours > 8760 || p.Priority < 1 || p.Priority > 5 || p.MaxFileBytes < 1 || p.MaxFileBytes > 16L * 1073741824 ||
                !Enum.IsDefined(typeof(TransferPublishMode), p.PublishMode) || !Enum.IsDefined(typeof(TransferVerification), p.Verification) ||
                !Enum.IsDefined(typeof(TransferTrigger), p.Trigger) || !InWindowValid(p)) throw new ArgumentException("전송 경로·형식·안정화·스케줄·용량 범위를 확인하세요.");
            RemotePath(p.RemoteRoot); RemotePath(p.StagingRoot);
            if (p.Enabled && (!p.PublishCompatibilityConfirmed || !p.ExclusiveDestinationConfirmed))
                throw new ArgumentException("상위 수신기의 게시 방식과 장비별 원격 경로/고유 파일명 호환성을 확인하세요.");
            if (p.RequireVerifiedUpload && p.Verification == TransferVerification.SizeOnly)
                throw new ArgumentException("삭제 보호 연동은 SHA-256 검증이 필요합니다. 크기 검사는 Independent 전송에서 사용하세요.");
        }
        private static bool InWindowValid(TransferPolicy p) => p.StartHour >= 0 && p.StartHour < 24 && p.EndHour >= 0 && p.EndHour <= 24 &&
            p.StartMinute >= 0 && p.StartMinute < 60 && p.EndMinute >= 0 && p.EndMinute < 60 && (p.EndHour != 24 || p.EndMinute == 0) &&
            p.StartHour * 60 + p.StartMinute != p.EndHour * 60 + p.EndMinute && (int)p.ScheduleDays > 0 && ((int)p.ScheduleDays & ~127) == 0;
        public static void ValidateOptions(TransferOptions o)
        {
            if (o == null || o.Workers < 1 || o.Workers > 8 || o.UploadBytesPerSecond < 0 || o.UploadBytesPerSecond > 1073741824 ||
                o.DownloadBytesPerSecond < 0 || o.DownloadBytesPerSecond > 1073741824 || o.DiskReadBytesPerSecond < 0 || o.DiskReadBytesPerSecond > 1073741824 ||
                o.MaxPendingJobs < 1 || o.MaxPendingJobs > 2000000 || o.MaxAttemptSeconds < 30 || o.MaxAttemptSeconds > 86400)
                throw new ArgumentException("전송 동시성·속도·대기열·attempt 한도 범위를 확인하세요.");
        }
        public static bool ImageHeader(string extension, byte[] data, int count)
        {
            switch (extension.ToLowerInvariant())
            {
                case ".jpg": case ".jpeg": return count >= 3 && data[0] == 255 && data[1] == 216 && data[2] == 255;
                case ".png": return count >= 8 && data.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
                case ".bmp": return count >= 2 && data[0] == 66 && data[1] == 77;
                case ".tif": case ".tiff": return count >= 4 && ((data[0] == 73 && data[1] == 73 && (data[2] == 42 || data[2] == 43) && data[3] == 0) ||
                    (data[0] == 77 && data[1] == 77 && data[2] == 0 && (data[3] == 42 || data[3] == 43)));
                case ".webp": return count >= 12 && Encoding.ASCII.GetString(data, 0, 4) == "RIFF" && Encoding.ASCII.GetString(data, 8, 4) == "WEBP";
                default: return false;
            }
        }
    }
}
