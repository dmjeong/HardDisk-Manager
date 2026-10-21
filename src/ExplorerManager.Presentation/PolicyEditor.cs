using System;
using System.Collections;
using System.ComponentModel;
using System.Linq;
using ExplorerManager.Domain;

namespace ExplorerManager.Presentation
{
    public sealed class PolicyEditor : ObservableObject, INotifyDataErrorInfo
    {
        private readonly DrivePolicy _policy;
        public bool IsDirty { get; private set; }
        public PolicyEditor(DrivePolicy p) { _policy = p; IsDirty = p.Version == 0; _roots = string.Join(Environment.NewLine, p.Roots); _excluded = string.Join(Environment.NewLine, p.Exclusions); _extensions = string.Join(", ", p.ExcludedExtensions); }
        public string Id => _policy.Id;
        public int Version => _policy.Version;
        public string Name { get => _policy.Name; set { _policy.Name = value; Edit(); } }
        public string VolumeId { get => _policy.VolumeId; set { _policy.VolumeId = value; Edit(); } }
        public bool Enabled { get => _policy.Enabled; set { _policy.Enabled = value; Edit(); } }
        public ThresholdMode Mode { get => _policy.ThresholdMode; set { _policy.ThresholdMode = value; Edit(); } }
        public AgeBasis AgeBasis { get => _policy.AgeBasis; set { _policy.AgeBasis = value; Edit(); } }
        public double Start { get => _policy.StartThreshold; set { _policy.StartThreshold = value; Edit(); } }
        public double Stop { get => _policy.StopThreshold; set { _policy.StopThreshold = value; Edit(); } }
        public int RetentionDays { get => _policy.RetentionDays; set { _policy.RetentionDays = value; Edit(); } }
        public int Interval { get => _policy.IntervalSeconds; set { _policy.IntervalSeconds = value; Edit(); } }
        public int Cooldown { get => _policy.CooldownSeconds; set { _policy.CooldownSeconds = value; Edit(); } }
        public int ScanLimit { get => _policy.MaxScanSeconds; set { _policy.MaxScanSeconds = value; Edit(); } }
        public int RunLimit { get => _policy.MaxRunSeconds; set { _policy.MaxRunSeconds = value; Edit(); } }
        public int ItemLimit { get => _policy.MaxItems; set { _policy.MaxItems = value; Edit(); } }
        public double DeleteLimitGiB { get => _policy.MaxDeleteBytes / 1073741824.0; set { _policy.MaxDeleteBytes = checked((long)(value * 1073741824)); Edit(); } }
        public double ReadMiB { get => _policy.ReadBytesPerSecond / 1048576.0; set { _policy.ReadBytesPerSecond = checked((long)(value * 1048576)); Edit(); } }
        public int StartHour { get => _policy.StartHour; set { _policy.StartHour = value; Edit(); } }
        public int EndHour { get => _policy.EndHour; set { _policy.EndHour = value; Edit(); } }
        public int ProtectionHours { get => _policy.RestoreProtectionHours; set { _policy.RestoreProtectionHours = value; Edit(); } }
        private string _roots, _excluded, _extensions;
        public string Roots { get => _roots; set { _roots = value; Edit(); } }
        public string Excluded { get => _excluded; set { _excluded = value; Edit(); } }
        public string Extensions { get => _extensions; set { _extensions = value; Edit(); } }
        private void Edit([System.Runtime.CompilerServices.CallerMemberName] string name = null)
        { IsDirty = true; Changed(name); Changed(nameof(HasErrors)); ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs("")); }
        public DrivePolicy ToPolicy()
        {
            _policy.Roots = Lines(Roots).ToList(); _policy.Exclusions = Lines(Excluded).ToList();
            _policy.ExcludedExtensions = (_extensions ?? "").Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            return _policy;
        }
        private static string[] Lines(string text) => (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        public bool HasErrors => PolicyRules.Validate(ToPolicy()).Count > 0;
        public IEnumerable GetErrors(string propertyName) => PolicyRules.Validate(ToPolicy());
        public event EventHandler<DataErrorsChangedEventArgs> ErrorsChanged;
        public override string ToString() => Name;
    }
}
