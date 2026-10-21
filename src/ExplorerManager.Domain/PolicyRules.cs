using System;
using System.Collections.Generic;

namespace ExplorerManager.Domain
{
    public static class PolicyRules
    {
        public static IList<string> Validate(DrivePolicy p)
        {
            var errors = new List<string>();
            if (p == null) { errors.Add("정책이 없습니다."); return errors; }
            if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 100) errors.Add("정책 이름은 1~100자입니다.");
            if (string.IsNullOrWhiteSpace(p.VolumeId)) errors.Add("볼륨을 선택하세요.");
            if (p.Roots == null || p.Roots.Count == 0 || p.Roots.Count > 100) errors.Add("관리 경로를 1~100개 등록하세요.");
            if (!Enum.IsDefined(typeof(ThresholdMode), p.ThresholdMode) || !Enum.IsDefined(typeof(AgeBasis), p.AgeBasis)) errors.Add("정책 모드가 잘못되었습니다.");
            if (double.IsNaN(p.StartThreshold) || double.IsInfinity(p.StartThreshold) ||
                double.IsNaN(p.StopThreshold) || double.IsInfinity(p.StopThreshold)) errors.Add("용량 기준은 유한한 숫자여야 합니다.");
            if (p.ThresholdMode == ThresholdMode.FreeBytes)
            { if (p.StartThreshold < 0 || p.StopThreshold <= p.StartThreshold) errors.Add("여유 공간 종료값은 시작값보다 커야 합니다."); }
            else if (p.StopThreshold < 0 || p.StartThreshold <= p.StopThreshold ||
                     (p.ThresholdMode == ThresholdMode.UsedPercent && p.StartThreshold > 100))
                errors.Add("사용량 종료값은 시작값보다 작아야 합니다.");
            if (p.RetentionDays < 1 || p.RetentionDays > 36500) errors.Add("보관 기간은 1~36500일입니다.");
            if (p.IntervalSeconds < 10 || p.CooldownSeconds < 10 || p.MaxScanSeconds < 1 || p.MaxRunSeconds < 1) errors.Add("점검/대기 간격은 10초 이상이어야 합니다.");
            if (p.MaxItems < 1 || p.MaxItems > 1000000 || p.MaxDeleteBytes < 1 || p.ReadBytesPerSecond < 65536) errors.Add("작업 상한 또는 읽기 속도를 확인하세요.");
            if (p.StartHour < 0 || p.StartHour > 23 || p.EndHour < 1 || p.EndHour > 24 || p.StartHour == p.EndHour) errors.Add("실행 시간대를 확인하세요.");
            if (p.RestoreProtectionHours < 1 || p.RestoreProtectionHours > 8760) errors.Add("복구 보호 기간은 1~8760시간입니다.");
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

        public static bool InWindow(DrivePolicy p, DateTime localNow)
        {
            int h = localNow.Hour;
            return p.StartHour < p.EndHour ? h >= p.StartHour && h < p.EndHour : h >= p.StartHour || h < p.EndHour;
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
