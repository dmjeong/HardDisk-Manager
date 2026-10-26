using System;
using System.Linq;
using System.Runtime.CompilerServices;
using ExplorerManager.Domain;
using Newtonsoft.Json;

namespace ExplorerManager.Presentation
{
    public abstract class TransferEditor<T> : ObservableObject
    {
        public T Model { get; protected set; }
        public bool IsDirty { get; private set; }
        public long EditVersion { get; private set; }
        protected TransferEditor(T model) { Model = Clone(model); }
        protected void Edit(Action change, [CallerMemberName] string property = null)
        { change(); IsDirty = true; EditVersion++; Changed(property); Changed(nameof(IsDirty)); Changed("DisplayName"); }
        public static V Clone<V>(V value) => JsonConvert.DeserializeObject<V>(JsonConvert.SerializeObject(value));
        public T Snapshot() => Clone(Model);
        protected void Clean(T model) { Model = Clone(model); IsDirty = false; Changed(""); }
        protected int Range(int n, int min, int max) { if (n < min || n > max) { IsDirty = true; EditVersion++; Changed(nameof(IsDirty)); Changed("DisplayName"); throw new ArgumentOutOfRangeException(nameof(n), min + "–" + max + " 범위를 입력하세요."); } return n; }
    }
    public sealed class TransferConnectionEditor : TransferEditor<TransferConnection>
    {
        public TransferConnectionEditor(TransferConnection c) : base(c) { }
        public string Id => Model.Id;
        public int Version => Model.Version;
        public string DisplayName => Name + (IsDirty ? " •" : "");
        public string Name { get => Model.Name; set { if (Name != value) Edit(() => Model.Name = value); } }
        public string Host { get => Model.Host; set { if (Host != value) Edit(() => Model.Host = value); } }
        public int Port { get => Model.Port; set { Range(value, 1, 65535); if (Port != value) Edit(() => Model.Port = value); } }
        public string UserName { get => Model.UserName; set { if (UserName != value) Edit(() => Model.UserName = value); } }
        public TransferEncryption Encryption { get => Model.Encryption; set { if (Encryption != value) Edit(() => Model.Encryption = value); } }
        public string EncodingName { get => Model.EncodingName; set { if (EncodingName != value) Edit(() => Model.EncodingName = value); } }
        public bool UseEpsv { get => Model.UseEpsv; set { if (UseEpsv != value) Edit(() => Model.UseEpsv = value); } }
        public int MaxConnections { get => Model.MaxConnections; set { Range(value, 1, 8); if (MaxConnections != value) Edit(() => Model.MaxConnections = value); } }
        public int ConnectTimeoutSeconds { get => Model.ConnectTimeoutSeconds; set { Range(value, 1, 300); if (ConnectTimeoutSeconds != value) Edit(() => Model.ConnectTimeoutSeconds = value); } }
        public int CommandTimeoutSeconds { get => Model.CommandTimeoutSeconds; set { Range(value, 1, 600); if (CommandTimeoutSeconds != value) Edit(() => Model.CommandTimeoutSeconds = value); } }
        public int NoProgressTimeoutSeconds { get => Model.NoProgressTimeoutSeconds; set { Range(value, 5, 3600); if (NoProgressTimeoutSeconds != value) Edit(() => Model.NoProgressTimeoutSeconds = value); } }
        public string SecretStatus => Model.HasSecret ? "Password registered in service" : "Password not set";
        public string Capabilities => Model.Capabilities == null ? "연결 시험을 실행하면 서버 지원 기능을 확인할 수 있습니다." :
            (Model.Capabilities.Connected ? "Connected" : "Disconnected") + " · SHA-256 " + (Model.Capabilities.ServerSha256 ? "supported" : "read-back required") +
            " · Resume " + (Model.Capabilities.UploadResumeVerified ? "tested" : "not tested") + " · Rename " + (Model.Capabilities.RenameVerified ? "tested" : "not tested") +
            Environment.NewLine + Model.Capabilities.Detail;
        public void Refresh(TransferConnection c) {
            if (c.Version < Model.Version) return;
            if (!IsDirty) Clean(c);
            else { Model.HasSecret |= c.HasSecret; if (c.Capabilities != null && (Model.Capabilities == null || c.Capabilities.CheckedUtc >= Model.Capabilities.CheckedUtc)) Model.Capabilities = c.Capabilities; Changed(nameof(SecretStatus)); Changed(nameof(Capabilities)); }
        }
        public void Accept(TransferConnection saved, long editVersion)
        { if (EditVersion == editVersion) Clean(saved); else { Model.Version = saved.Version; Model.HasSecret = saved.HasSecret; Model.Capabilities = saved.Capabilities; Changed(nameof(Version)); Changed(nameof(SecretStatus)); } }
        public void Reload(TransferConnection saved) => Clean(saved);
    }
    public sealed class TransferPolicyEditor : TransferEditor<TransferPolicy>
    {
        private string _startTime, _endTime;
        public TransferPolicyEditor(TransferPolicy p) : base(p) { }
        public string Id => Model.Id;
        public int Version => Model.Version;
        public string DisplayName => Name + (IsDirty ? " •" : "");
        public string Name { get => Model.Name; set { if (Name != value) Edit(() => Model.Name = value); } }
        public string ConnectionId { get => Model.ConnectionId; set { if (value != null && ConnectionId != value) Edit(() => Model.ConnectionId = value); } }
        public string VolumeId { get => Model.VolumeId; set { if (value != null && VolumeId != value) Edit(() => Model.VolumeId = value); } }
        public string LocalRoot { get => Model.LocalRoot; set { if (LocalRoot != value) Edit(() => Model.LocalRoot = value); } }
        public string RemoteRoot { get => Model.RemoteRoot; set { if (RemoteRoot != value) Edit(() => Model.RemoteRoot = value); } }
        public string StagingRoot { get => Model.StagingRoot; set { if (StagingRoot != value) Edit(() => Model.StagingRoot = value); } }
        public string Extensions { get => string.Join(", ", Model.Extensions); set { if (Extensions != value) Edit(() => Model.Extensions = (value ?? "").Split(new[] { ',', ';', ' ', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.StartsWith(".") ? x.ToLowerInvariant() : "." + x.ToLowerInvariant()).Distinct().ToList()); } }
        public string Exclusions { get => string.Join(Environment.NewLine, Model.Exclusions); set { if (Exclusions != value) Edit(() => Model.Exclusions = (value ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList()); } }
        public bool Enabled { get => Model.Enabled; set { if (Enabled != value) Edit(() => Model.Enabled = value); } }
        public bool IncludeSubdirectories { get => Model.IncludeSubdirectories; set { if (IncludeSubdirectories != value) Edit(() => Model.IncludeSubdirectories = value); } }
        public bool PreserveSubdirectories { get => Model.PreserveSubdirectories; set { if (PreserveSubdirectories != value) Edit(() => Model.PreserveSubdirectories = value); } }
        public bool RequireVerifiedUpload { get => Model.RequireVerifiedUpload; set { if (RequireVerifiedUpload != value) Edit(() => Model.RequireVerifiedUpload = value); } }
        public bool BackfillExisting { get => Model.BackfillExisting; set { if (BackfillExisting != value) Edit(() => Model.BackfillExisting = value); } }
        public bool ValidateImageHeader { get => Model.ValidateImageHeader; set { if (ValidateImageHeader != value) Edit(() => Model.ValidateImageHeader = value); } }
        public bool AllowCreateDirectories { get => Model.AllowCreateDirectories; set { if (AllowCreateDirectories != value) Edit(() => Model.AllowCreateDirectories = value); } }
        public bool PublishCompatibilityConfirmed { get => Model.PublishCompatibilityConfirmed; set { if (PublishCompatibilityConfirmed != value) Edit(() => Model.PublishCompatibilityConfirmed = value); } }
        public bool ExclusiveDestinationConfirmed { get => Model.ExclusiveDestinationConfirmed; set { if (ExclusiveDestinationConfirmed != value) Edit(() => Model.ExclusiveDestinationConfirmed = value); } }
        public TransferPublishMode PublishMode { get => Model.PublishMode; set { if (PublishMode != value) Edit(() => Model.PublishMode = value); } }
        public TransferVerification Verification { get => Model.Verification; set { if (Verification != value) Edit(() => Model.Verification = value); } }
        public TransferTrigger Trigger { get => Model.Trigger; set { if (Trigger != value) Edit(() => Model.Trigger = value); } }
        public int StableSeconds { get => Model.StableSeconds; set { Range(value, 2, 3600); if (StableSeconds != value) Edit(() => Model.StableSeconds = value); } }
        public int ReconcileSeconds { get => Model.ReconcileSeconds; set { Range(value, 30, 86400); if (ReconcileSeconds != value) Edit(() => Model.ReconcileSeconds = value); } }
        public int MaxScanSeconds { get => Model.MaxScanSeconds; set { Range(value, 30, 86400); if (MaxScanSeconds != value) Edit(() => Model.MaxScanSeconds = value); } }
        public int ContentAuditHours { get => Model.ContentAuditHours; set { Range(value, 0, 8760); if (ContentAuditHours != value) Edit(() => Model.ContentAuditHours = value); } }
        public int Priority { get => Model.Priority; set { Range(value, 1, 5); if (Priority != value) Edit(() => Model.Priority = value); } }
        public int MaxFileMiB { get => (int)(Model.MaxFileBytes / 1048576); set { Range(value, 1, 16384); if (MaxFileMiB != value) Edit(() => Model.MaxFileBytes = value * 1048576L); } }
        public string StartTime { get => _startTime ?? Model.StartHour.ToString("00") + ":" + Model.StartMinute.ToString("00"); set { if (StartTime == value) return; Edit(() => _startTime = value); int hour, minute; ParseTime(value, false, out hour, out minute); Model.StartHour = hour; Model.StartMinute = minute; } }
        public string EndTime { get => _endTime ?? Model.EndHour.ToString("00") + ":" + Model.EndMinute.ToString("00"); set { if (EndTime == value) return; Edit(() => _endTime = value); int hour, minute; ParseTime(value, true, out hour, out minute); Model.EndHour = hour; Model.EndMinute = minute; } }
        private static void ParseTime(string text, bool end, out int hour, out int minute)
        {
            hour = minute = 0; var parts = (text ?? "").Split(':');
            if (parts.Length != 2 || parts[0].Length != 2 || parts[1].Length != 2 || !int.TryParse(parts[0], out hour) || !int.TryParse(parts[1], out minute) ||
                minute < 0 || minute > 59 || hour < 0 || hour > (end ? 24 : 23) || (hour == 24 && minute != 0)) throw new ArgumentException("HH:mm 형식으로 입력하세요. 종료는 24:00까지 가능합니다.");
        }
        private bool Day(ScheduleDays day) => (Model.ScheduleDays & day) != 0;
        private void Day(ScheduleDays day, bool value, [CallerMemberName] string property = null) { if (Day(day) != value) Edit(() => Model.ScheduleDays = value ? Model.ScheduleDays | day : Model.ScheduleDays & ~day, property); }
        public bool Monday { get => Day(ScheduleDays.Monday); set => Day(ScheduleDays.Monday, value); }
        public bool Tuesday { get => Day(ScheduleDays.Tuesday); set => Day(ScheduleDays.Tuesday, value); }
        public bool Wednesday { get => Day(ScheduleDays.Wednesday); set => Day(ScheduleDays.Wednesday, value); }
        public bool Thursday { get => Day(ScheduleDays.Thursday); set => Day(ScheduleDays.Thursday, value); }
        public bool Friday { get => Day(ScheduleDays.Friday); set => Day(ScheduleDays.Friday, value); }
        public bool Saturday { get => Day(ScheduleDays.Saturday); set => Day(ScheduleDays.Saturday, value); }
        public bool Sunday { get => Day(ScheduleDays.Sunday); set => Day(ScheduleDays.Sunday, value); }
        public string ProtectionText => Model.ProtectionRegistered ? "삭제 보호 등록됨 · Pause/Archive 후에도 보호 유지" : "활성화 시 선택한 삭제 연동 규칙 적용";
        public new TransferPolicy Snapshot() { int h, m; ParseTime(StartTime, false, out h, out m); ParseTime(EndTime, true, out h, out m); return base.Snapshot(); }
        public void Refresh(TransferPolicy p) { if (!IsDirty && p.Version >= Model.Version) { _startTime = _endTime = null; Clean(p); } }
        public void Accept(TransferPolicy saved, long editVersion)
        { if (EditVersion == editVersion) { _startTime = _endTime = null; Clean(saved); } else { Model.Version = saved.Version; Model.ProtectionRegistered = saved.ProtectionRegistered; Changed(nameof(Version)); Changed(nameof(ProtectionText)); } }
        public void Reload(TransferPolicy saved) { _startTime = _endTime = null; Clean(saved); }
    }
}
