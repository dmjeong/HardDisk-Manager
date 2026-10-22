using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using ExplorerManager.Domain;

namespace ExplorerManager.Presentation
{
    public sealed class PolicyEditor : ObservableObject, INotifyDataErrorInfo
    {
        private readonly DrivePolicy _policy;
        public bool IsDirty { get; private set; }
        public PolicyEditor(DrivePolicy p)
        {
            _policy = p; IsDirty = p.Version == 0;
            _roots = string.Join(Environment.NewLine, p.Roots); _excluded = string.Join(Environment.NewLine, p.Exclusions);
            _extensions = string.Join(", ", p.ExcludedExtensions);
            _startTime = TimeText(p.StartHour, p.StartMinute); _endTime = TimeText(p.EndHour, p.EndMinute);
        }
        public string Id => _policy.Id;
        public int Version => _policy.Version;
        public string Name { get => _policy.Name; set { _policy.Name = value; Edit(); } }
        public string VolumeId { get => _policy.VolumeId; set { if (_policy.VolumeId != value) { _policy.VolumeId = value; Edit(); } } }
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
        public int StartHour { get => _policy.StartHour; set { _policy.StartHour = value; _startTime = TimeText(value, _policy.StartMinute); Edit(); Changed(nameof(StartTime)); } }
        public int EndHour { get => _policy.EndHour; set { _policy.EndHour = value; _endTime = TimeText(value, _policy.EndMinute); Edit(); Changed(nameof(EndTime)); } }
        private string _startTime, _endTime;
        public string StartTime { get => _startTime; set { if (_startTime != value) { _startTime = value; ApplyTimeDrafts(); Edit(); Changed(nameof(StartHour)); } } }
        public string EndTime { get => _endTime; set { if (_endTime != value) { _endTime = value; ApplyTimeDrafts(); Edit(); Changed(nameof(EndHour)); } } }
        public bool RunMonday { get => RunsOn(ScheduleDays.Monday); set => SetDay(ScheduleDays.Monday, value); }
        public bool RunTuesday { get => RunsOn(ScheduleDays.Tuesday); set => SetDay(ScheduleDays.Tuesday, value); }
        public bool RunWednesday { get => RunsOn(ScheduleDays.Wednesday); set => SetDay(ScheduleDays.Wednesday, value); }
        public bool RunThursday { get => RunsOn(ScheduleDays.Thursday); set => SetDay(ScheduleDays.Thursday, value); }
        public bool RunFriday { get => RunsOn(ScheduleDays.Friday); set => SetDay(ScheduleDays.Friday, value); }
        public bool RunSaturday { get => RunsOn(ScheduleDays.Saturday); set => SetDay(ScheduleDays.Saturday, value); }
        public bool RunSunday { get => RunsOn(ScheduleDays.Sunday); set => SetDay(ScheduleDays.Sunday, value); }
        private bool RunsOn(ScheduleDays day) => (_policy.ScheduleDays & day) != 0;
        private void SetDay(ScheduleDays day, bool value, [System.Runtime.CompilerServices.CallerMemberName] string name = null)
        {
            if (RunsOn(day) == value) return;
            _policy.ScheduleDays = value ? _policy.ScheduleDays | day : _policy.ScheduleDays & ~day;
            Edit(name);
        }
        public int ProtectionHours { get => _policy.RestoreProtectionHours; set { _policy.RestoreProtectionHours = value; Edit(); } }
        private string _roots, _excluded, _extensions;
        public string Roots { get => _roots; set { _roots = value; Edit(); } }
        public string Excluded { get => _excluded; set { _excluded = value; Edit(); } }
        public string Extensions { get => _extensions; set { _extensions = value; Edit(); } }
        private void Edit([System.Runtime.CompilerServices.CallerMemberName] string name = null)
        { IsDirty = true; Changed(name); Changed(nameof(IsDirty)); Changed(nameof(HasErrors)); ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs("")); }
        public DrivePolicy ToPolicy()
        {
            ApplyTimeDrafts();
            _policy.Roots = Lines(Roots).ToList(); _policy.Exclusions = Lines(Excluded).ToList();
            _policy.ExcludedExtensions = (_extensions ?? "").Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            return _policy;
        }
        private void ApplyTimeDrafts()
        {
            int hour, minute;
            bool valid = TryTime(_startTime, false, out hour, out minute);
            _policy.StartHour = valid ? hour : -1; _policy.StartMinute = valid ? minute : -1;
            valid = TryTime(_endTime, true, out hour, out minute);
            _policy.EndHour = valid ? hour : -1; _policy.EndMinute = valid ? minute : -1;
        }
        private static string TimeText(int hour, int minute) => hour.ToString("00", CultureInfo.InvariantCulture) + ":" + minute.ToString("00", CultureInfo.InvariantCulture);
        private static bool TryTime(string value, bool end, out int hour, out int minute)
        {
            hour = minute = 0;
            if (value == null || value.Length != 5 || value[2] != ':' ||
                value[0] < '0' || value[0] > '9' || value[1] < '0' || value[1] > '9' ||
                value[3] < '0' || value[3] > '9' || value[4] < '0' || value[4] > '9') return false;
            hour = (value[0] - '0') * 10 + value[1] - '0'; minute = (value[3] - '0') * 10 + value[4] - '0';
            return hour <= (end ? 24 : 23) && minute <= 59 && (hour != 24 || minute == 0);
        }
        private static string[] Lines(string text) => (text ?? "").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        private IList<string> ValidationErrors()
        {
            var errors = PolicyRules.Validate(ToPolicy());
            int hour, minute;
            if (!TryTime(StartTime, false, out hour, out minute)) errors.Add("시작 시각을 HH:mm 형식(00:00~23:59)으로 입력하세요.");
            if (!TryTime(EndTime, true, out hour, out minute)) errors.Add("종료 시각을 HH:mm 형식(00:00~24:00)으로 입력하세요.");
            return errors;
        }
        public bool HasErrors => ValidationErrors().Count > 0;
        public IEnumerable GetErrors(string propertyName) => ValidationErrors();
        public event EventHandler<DataErrorsChangedEventArgs> ErrorsChanged;
        public override string ToString() => Name;
    }
}
