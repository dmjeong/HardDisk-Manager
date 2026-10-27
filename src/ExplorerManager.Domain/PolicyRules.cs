using System;
using System.Collections.Generic;
using System.Linq;

namespace ExplorerManager.Domain
{
    public static class PolicyRules
    {
        public static IList<string> Validate(DrivePolicy p) => ValidateFields(p).Values.SelectMany(x => x).Distinct().ToList();
        public static IDictionary<string, List<string>> ValidateFields(DrivePolicy p)
        {
            var errors = new Dictionary<string, List<string>>();
            Action<string, string> add = (field, message) => { List<string> list; if (!errors.TryGetValue(field, out list)) errors[field] = list = new List<string>(); list.Add(message); };
            if (p == null) { add("", "정책이 없습니다."); return errors; }
            if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 100) add(nameof(p.Name), "정책 이름은 1~100자입니다.");
            if (string.IsNullOrWhiteSpace(p.VolumeId)) add(nameof(p.VolumeId), "Target volume을 선택하세요.");
            if (p.Roots == null || p.Roots.Count == 0 || p.Roots.Count > 100) add(nameof(p.Roots), "관리할 폴더를 1~100개 등록하세요.");
            if (!Enum.IsDefined(typeof(ThresholdMode), p.ThresholdMode)) add(nameof(p.ThresholdMode), "용량 기준 종류를 선택하세요.");
            if (!Enum.IsDefined(typeof(AgeBasis), p.AgeBasis)) add(nameof(p.AgeBasis), "파일 시각 기준을 선택하세요.");
            bool finite = !double.IsNaN(p.StartThreshold) && !double.IsInfinity(p.StartThreshold) && !double.IsNaN(p.StopThreshold) && !double.IsInfinity(p.StopThreshold);
            if (!finite || p.StartThreshold < 0 || p.StopThreshold < 0 || (p.ThresholdMode == ThresholdMode.UsedPercent && (p.StartThreshold > 100 || p.StopThreshold > 100)))
            { add(nameof(p.StartThreshold), "용량 기준은 0 이상의 유한한 숫자이며 사용률은 100% 이내입니다."); add(nameof(p.StopThreshold), "용량 기준은 0 이상의 유한한 숫자이며 사용률은 100% 이내입니다."); }
            else if (p.ThresholdMode == ThresholdMode.FreeBytes ? p.StopThreshold <= p.StartThreshold : p.StopThreshold >= p.StartThreshold)
            {
                string message = p.ThresholdMode == ThresholdMode.FreeBytes ? "여유 공간 종료값은 시작값보다 커야 합니다." : "사용량 종료값은 시작값보다 작아야 합니다.";
                add(nameof(p.StartThreshold), message); add(nameof(p.StopThreshold), message);
            }
            if (p.RetentionDays < 1 || p.RetentionDays > 36500) add(nameof(p.RetentionDays), "보관 기간은 1~36500일입니다.");
            if (p.IntervalSeconds < 10) add(nameof(p.IntervalSeconds), "점검 주기는 10초 이상입니다.");
            if (p.CooldownSeconds < 10) add(nameof(p.CooldownSeconds), "재실행 대기는 10초 이상입니다.");
            if (p.MaxScanSeconds < 1) add(nameof(p.MaxScanSeconds), "스캔 제한은 1초 이상입니다.");
            if (p.MaxRunSeconds < 1) add(nameof(p.MaxRunSeconds), "삭제 제한은 1초 이상입니다.");
            if (p.MaxItems < 1 || p.MaxItems > 1000000) add(nameof(p.MaxItems), "처리 항목 상한은 1~1000000입니다.");
            if (p.MaxDeleteBytes < 1) add(nameof(p.MaxDeleteBytes), "1회 삭제 상한은 0보다 커야 합니다.");
            if (p.ReadBytesPerSecond < 65536) add(nameof(p.ReadBytesPerSecond), "읽기 속도는 0.0625 MiB/s 이상입니다.");
            if (!HasValidSchedule(p)) add("Schedule", "실행 요일과 시간대를 확인하세요. 종료 시각 24:00은 허용되며 시작과 종료는 달라야 합니다.");
            if (p.RestoreProtectionHours < 1 || p.RestoreProtectionHours > 8760) add(nameof(p.RestoreProtectionHours), "복구 보호 기간은 1~8760시간입니다.");
            return errors;
        }

        public static bool ShouldStart(DrivePolicy p, VolumeStatus v) => Compare(p, v, true);
        public static bool TargetReached(DrivePolicy p, VolumeStatus v) => Compare(p, v, false);
        private static bool Compare(DrivePolicy p, VolumeStatus v, bool start)
        {
            if (v == null || v.State != "Ready" || v.TotalBytes <= 0) throw new InvalidOperationException("볼륨을 사용할 수 없습니다.");
            var actual = p.ThresholdMode == ThresholdMode.UsedPercent ? v.UsedPercent :
                p.ThresholdMode == ThresholdMode.UsedBytes ? (double)(v.TotalBytes - v.FreeBytes) : v.FreeBytes;
            var threshold = start ? p.StartThreshold : p.StopThreshold;
            return p.ThresholdMode == ThresholdMode.FreeBytes
                ? (start ? actual <= threshold : actual >= threshold)
                : (start ? actual >= threshold : actual <= threshold);
        }
        public static bool IsExpired(FileSnapshot f, DrivePolicy p, DateTime cutoff) =>
            (p.AgeBasis == AgeBasis.CreationTime ? f.CreationUtc : f.LastWriteUtc) < cutoff;

        // A folder directly below a managed root, including its descendants, is one cleanup unit.
        // Loose files remain independent. This does not replace native path/identity verification.
        public static string CleanupUnitPath(FileSnapshot file)
        {
            if (file == null) throw new ArgumentNullException(nameof(file));
            string path = NormalizeCleanupPath(file.Path, nameof(file.Path));
            string root = NormalizeCleanupPath(file.Root, nameof(file.Root));
            string prefix = root + "\\";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("정리 후보는 관리 경로 자체가 아닌 하위 경로여야 합니다.", nameof(file));
            int separator = path.IndexOf('\\', prefix.Length);
            return separator < 0 ? path : path.Substring(0, separator);
        }

        private static string NormalizeCleanupPath(string value, string parameter)
        {
            if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("정리 후보 경로가 없습니다.", parameter);
            string path = value.Replace('/', '\\').TrimEnd('\\');
            if (path.Length == 0) throw new ArgumentException("정리 후보 경로가 없습니다.", parameter);
            int begin = path.StartsWith("\\\\", StringComparison.Ordinal) ? 2 : 0;
            if (path.IndexOf("\\\\", begin, StringComparison.Ordinal) >= 0)
                throw new ArgumentException("정리 후보 경로의 구분자가 잘못되었습니다.", parameter);
            foreach (string part in path.Split('\\'))
                if (part == "." || part == "..") throw new ArgumentException("상대 경로 구간을 정리 단위로 사용할 수 없습니다.", parameter);
            return path;
        }

        public static bool InWindow(DrivePolicy p, DateTime localNow)
        {
            if (p == null || !HasValidSchedule(p)) return false;
            int start = p.StartHour * 60 + p.StartMinute, end = p.EndHour * 60 + p.EndMinute;
            int now = localNow.Hour * 60 + localNow.Minute;
            int day = (int)localNow.DayOfWeek;
            if (start < end) return IsSelectedDay(p, day) && now >= start && now < end;
            // An overnight window belongs to the weekday on which it starts.
            return now >= start ? IsSelectedDay(p, day) : now < end && IsSelectedDay(p, (day + 6) % 7);
        }

        private static bool IsSelectedDay(DrivePolicy p, int day) => ((int)p.ScheduleDays & (1 << day)) != 0;

        private static bool HasValidSchedule(DrivePolicy p)
        {
            int days = (int)p.ScheduleDays;
            return days > 0 && (days & ~(int)ScheduleDays.All) == 0 &&
                p.StartHour >= 0 && p.StartHour <= 23 && p.StartMinute >= 0 && p.StartMinute <= 59 &&
                p.EndHour >= 0 && p.EndHour <= 24 && p.EndMinute >= 0 && p.EndMinute <= 59 &&
                (p.EndHour != 24 || p.EndMinute == 0) &&
                p.StartHour * 60 + p.StartMinute != p.EndHour * 60 + p.EndMinute;
        }
    }

    public sealed class SustainedRule
    {
        private DateTime? _since;
        private bool _active;
        private DateTime _last = DateTime.MinValue;
        public bool Evaluate(bool abnormal, DateTime now, TimeSpan duration)
        {
            if (now < _last) { _since = null; _active = false; }
            _last = now;
            if (!abnormal) { _since = null; _active = false; return false; }
            if (!_since.HasValue) _since = now;
            if (!_active && now - _since.Value >= duration) { _active = true; return true; }
            return false;
        }
    }
}
