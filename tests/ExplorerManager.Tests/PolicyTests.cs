using System;
using System.Collections.Generic;
using System.Linq;
using ExplorerManager.Domain;
using ExplorerManager.Presentation;
using Newtonsoft.Json;
using Xunit;

namespace ExplorerManager.Tests
{
    public sealed class PolicyTests
    {
        [Theory]
        [InlineData(100, true, false)]
        [InlineData(101, false, false)]
        [InlineData(150, false, true)]
        [InlineData(151, false, true)]
        public void PercentHysteresisUsesCorrectBoundary(long free, bool starts, bool stops)
        {
            var p = new DrivePolicy(); var v = new VolumeStatus { TotalBytes = 1000, FreeBytes = free };
            Assert.Equal(starts, PolicyRules.ShouldStart(p, v));
            Assert.Equal(stops, PolicyRules.TargetReached(p, v));
        }
        [Theory]
        [InlineData(ThresholdMode.UsedBytes, 900, 800, 100, true, false)]
        [InlineData(ThresholdMode.UsedBytes, 900, 800, 200, false, true)]
        [InlineData(ThresholdMode.FreeBytes, 100, 200, 100, true, false)]
        [InlineData(ThresholdMode.FreeBytes, 100, 200, 200, false, true)]
        public void ByteModesUseCorrectDirection(ThresholdMode mode, double start, double stop, long free, bool begins, bool ends)
        {
            var p = new DrivePolicy { ThresholdMode = mode, StartThreshold = start, StopThreshold = stop };
            var v = new VolumeStatus { TotalBytes = 1000, FreeBytes = free };
            Assert.Equal(begins, PolicyRules.ShouldStart(p, v)); Assert.Equal(ends, PolicyRules.TargetReached(p, v));
        }
        [Fact] public void ExactRetentionBoundaryIsKept()
        {
            var cutoff = DateTime.UtcNow;
            Assert.False(PolicyRules.IsExpired(new FileSnapshot { LastWriteUtc = cutoff }, new DrivePolicy(), cutoff));
            Assert.True(PolicyRules.IsExpired(new FileSnapshot { LastWriteUtc = cutoff.AddTicks(-1) }, new DrivePolicy(), cutoff));
        }
        [Fact] public void NanAndInvalidRangesAreRejected()
        {
            var p = new DrivePolicy { Roots = new System.Collections.Generic.List<string> { "D:\\archive" }, VolumeId = "v" };
            Assert.Empty(PolicyRules.Validate(p)); p.StartThreshold = double.NaN; Assert.NotEmpty(PolicyRules.Validate(p));
            p.StartThreshold = 80; Assert.NotEmpty(PolicyRules.Validate(p));
        }
        [Fact] public void OvernightScheduleIncludesBothSides()
        {
            var p = new DrivePolicy { StartHour = 22, EndHour = 6 };
            Assert.True(PolicyRules.InWindow(p, DateTime.Today.AddHours(23)));
            Assert.True(PolicyRules.InWindow(p, DateTime.Today.AddHours(5)));
            Assert.False(PolicyRules.InWindow(p, DateTime.Today.AddHours(6)));
            Assert.False(PolicyRules.InWindow(p, DateTime.Today.AddHours(12)));
        }
        [Fact] public void MinuteScheduleIncludesStartAndExcludesEndOnSelectedDay()
        {
            var p = ValidPolicy(); p.ScheduleDays = ScheduleDays.Monday;
            p.StartHour = 9; p.StartMinute = 15; p.EndHour = 9; p.EndMinute = 45;
            var monday = new DateTime(2026, 10, 26);
            Assert.Empty(PolicyRules.Validate(p));
            Assert.False(PolicyRules.InWindow(p, monday.AddHours(9).AddMinutes(14).AddSeconds(59)));
            Assert.True(PolicyRules.InWindow(p, monday.AddHours(9).AddMinutes(15)));
            Assert.True(PolicyRules.InWindow(p, monday.AddHours(9).AddMinutes(44).AddSeconds(59)));
            Assert.False(PolicyRules.InWindow(p, monday.AddHours(9).AddMinutes(45)));
            Assert.False(PolicyRules.InWindow(p, monday.AddDays(1).AddHours(9).AddMinutes(30)));
        }
        [Fact] public void FridayOvernightWindowBelongsToFridayIncludingSaturdayMorning()
        {
            var p = ValidPolicy(); p.ScheduleDays = ScheduleDays.Friday;
            p.StartHour = 22; p.StartMinute = 30; p.EndHour = 6; p.EndMinute = 15;
            var friday = new DateTime(2026, 10, 23);
            Assert.False(PolicyRules.InWindow(p, friday.AddHours(6)));
            Assert.False(PolicyRules.InWindow(p, friday.AddHours(22).AddMinutes(29)));
            Assert.True(PolicyRules.InWindow(p, friday.AddHours(22).AddMinutes(30)));
            Assert.True(PolicyRules.InWindow(p, friday.AddDays(1).AddHours(6).AddMinutes(14).AddSeconds(59)));
            Assert.False(PolicyRules.InWindow(p, friday.AddDays(1).AddHours(6).AddMinutes(15)));
            Assert.False(PolicyRules.InWindow(p, friday.AddDays(1).AddHours(23)));
            Assert.False(PolicyRules.InWindow(p, friday.AddDays(2).AddHours(5)));
        }
        [Fact] public void OvernightSelectionWrapsFromSaturdayIntoSunday()
        {
            var p = ValidPolicy(); p.ScheduleDays = ScheduleDays.Saturday;
            p.StartHour = 23; p.StartMinute = 55; p.EndHour = 0; p.EndMinute = 10;
            var sunday = new DateTime(2026, 10, 25);
            Assert.True(PolicyRules.InWindow(p, sunday.AddMinutes(9)));
            Assert.False(PolicyRules.InWindow(p, sunday.AddMinutes(10)));
            Assert.False(PolicyRules.InWindow(p, sunday.AddDays(1).AddMinutes(9)));
        }
        [Fact] public void MidnightEndAndFullDayWindowsUseExclusiveEnd()
        {
            var p = ValidPolicy(); p.ScheduleDays = ScheduleDays.Friday;
            var friday = new DateTime(2026, 10, 23);
            Assert.True(PolicyRules.InWindow(p, friday));
            Assert.True(PolicyRules.InWindow(p, friday.AddDays(1).AddTicks(-1)));
            Assert.False(PolicyRules.InWindow(p, friday.AddDays(1)));
            p.StartHour = 22; p.EndHour = 0;
            Assert.Empty(PolicyRules.Validate(p));
            Assert.True(PolicyRules.InWindow(p, friday.AddHours(23)));
            Assert.False(PolicyRules.InWindow(p, friday.AddDays(1)));
        }
        [Theory]
        [InlineData(-1, 0, 24, 0, 127)]
        [InlineData(24, 0, 6, 0, 127)]
        [InlineData(0, -1, 24, 0, 127)]
        [InlineData(0, 60, 24, 0, 127)]
        [InlineData(0, 0, -1, 0, 127)]
        [InlineData(0, 0, 25, 0, 127)]
        [InlineData(0, 0, 24, 1, 127)]
        [InlineData(0, 0, 23, -1, 127)]
        [InlineData(0, 0, 23, 60, 127)]
        [InlineData(9, 15, 9, 15, 127)]
        [InlineData(0, 0, 0, 0, 127)]
        [InlineData(0, 0, 24, 0, 0)]
        [InlineData(0, 0, 24, 0, 128)]
        [InlineData(0, 0, 24, 0, -1)]
        public void InvalidScheduleIsRejectedAndCannotOpenWindow(int startHour, int startMinute, int endHour, int endMinute, int days)
        {
            var p = ValidPolicy(); p.StartHour = startHour; p.StartMinute = startMinute;
            p.EndHour = endHour; p.EndMinute = endMinute; p.ScheduleDays = (ScheduleDays)days;
            Assert.NotEmpty(PolicyRules.Validate(p));
            Assert.False(PolicyRules.InWindow(p, new DateTime(2026, 10, 23, 12, 0, 0)));
        }
        [Fact] public void LegacyJsonRetainsAllDaysAndWholeHourSchedule()
        {
            var p = JsonConvert.DeserializeObject<DrivePolicy>("{\"StartHour\":22,\"EndHour\":6}");
            Assert.Equal(ScheduleDays.All, p.ScheduleDays); Assert.Equal(0, p.StartMinute); Assert.Equal(0, p.EndMinute);
            Assert.True(PolicyRules.InWindow(p, new DateTime(2026, 10, 24, 5, 59, 59)));
            Assert.False(PolicyRules.InWindow(p, new DateTime(2026, 10, 24, 6, 0, 0)));
            var defaults = JsonConvert.DeserializeObject<DrivePolicy>("{}");
            Assert.Equal(24, defaults.EndHour); Assert.Equal(ScheduleDays.All, defaults.ScheduleDays);
            Assert.True(PolicyRules.InWindow(defaults, new DateTime(2026, 10, 25, 23, 59, 59)));
        }
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("9:00")]
        [InlineData("09:0")]
        [InlineData(" 09:00")]
        [InlineData("09:00 ")]
        [InlineData("09.00")]
        [InlineData("24:00")]
        [InlineData("12:60")]
        [InlineData("-1:00")]
        [InlineData("ab:cd")]
        public void EditorKeepsInvalidTimeDraftWithoutSavingPriorValidSchedule(string draft)
        {
            var p = ValidPolicy(); p.Version = 1;
            var editor = new PolicyEditor(p); Assert.False(editor.IsDirty); Assert.False(editor.HasErrors);
            editor.StartTime = draft;
            Assert.Equal(draft, editor.StartTime); Assert.True(editor.IsDirty); Assert.True(editor.HasErrors);
            Assert.NotEmpty(editor.GetErrors(nameof(editor.StartTime)).Cast<string>());
            Assert.Equal(-1, editor.ToPolicy().StartHour);
            Assert.False(PolicyRules.InWindow(editor.ToPolicy(), DateTime.Now));
            editor.StartTime = "09:15";
            Assert.Equal(9, editor.StartHour); Assert.False(editor.HasErrors); Assert.Equal(9, editor.ToPolicy().StartHour); Assert.Equal(15, p.StartMinute);
        }
        [Fact] public void EditorValidatesEndTimeAndCrossFieldEquality()
        {
            var editor = new PolicyEditor(ValidPolicy());
            Assert.Equal("00:00", editor.StartTime); Assert.Equal("24:00", editor.EndTime);
            editor.EndTime = "24:01"; Assert.True(editor.HasErrors); Assert.Equal(-1, editor.ToPolicy().EndHour);
            editor.EndTime = "24:00"; Assert.False(editor.HasErrors);
            editor.StartTime = "09:15"; editor.EndTime = "09:15"; Assert.True(editor.HasErrors);
            editor.EndTime = "09:16"; Assert.False(editor.HasErrors);
            editor.EndTime = "00:00"; Assert.False(editor.HasErrors);
        }
        [Fact] public void EditorRoundTripsWeekdaysAndMinutesAndNotifiesDraftState()
        {
            var p = ValidPolicy(); p.Version = 1;
            var editor = new PolicyEditor(p); var changed = new List<string>(); int errorEvents = 0;
            editor.PropertyChanged += (sender, args) => changed.Add(args.PropertyName);
            editor.ErrorsChanged += (sender, args) => errorEvents++;
            editor.RunMonday = false; editor.RunTuesday = false; editor.RunWednesday = false; editor.RunThursday = false;
            editor.RunFriday = false; editor.RunSaturday = false; editor.RunSunday = false;
            Assert.True(editor.HasErrors); Assert.Equal(ScheduleDays.None, editor.ToPolicy().ScheduleDays);
            editor.RunFriday = true; editor.StartTime = "22:35"; editor.EndTime = "06:10";
            Assert.False(editor.HasErrors); Assert.True(editor.IsDirty);
            Assert.Contains(nameof(editor.IsDirty), changed); Assert.Contains(nameof(editor.HasErrors), changed);
            Assert.Contains(nameof(editor.RunFriday), changed); Assert.True(errorEvents > 0);
            var copy = JsonConvert.DeserializeObject<DrivePolicy>(JsonConvert.SerializeObject(editor.ToPolicy()));
            Assert.Equal(ScheduleDays.Friday, copy.ScheduleDays);
            Assert.Equal(22, copy.StartHour); Assert.Equal(35, copy.StartMinute); Assert.Equal(6, copy.EndHour); Assert.Equal(10, copy.EndMinute);
            var reloaded = new PolicyEditor(copy);
            Assert.Equal("22:35", reloaded.StartTime); Assert.Equal("06:10", reloaded.EndTime);
            Assert.True(reloaded.RunFriday); Assert.False(reloaded.RunSaturday); Assert.False(reloaded.RunSunday);
        }
        [Fact] public void LegacyEditorHourPropertiesUpdateNewTimeFields()
        {
            var p = ValidPolicy(); p.StartMinute = 20; p.EndMinute = 30; p.EndHour = 6;
            var editor = new PolicyEditor(p); editor.StartHour = 22; editor.EndHour = 7;
            Assert.Equal("22:20", editor.StartTime); Assert.Equal("07:30", editor.EndTime);
            Assert.Equal(22, editor.ToPolicy().StartHour); Assert.Equal(7, editor.ToPolicy().EndHour);
            Assert.False(editor.HasErrors);
        }
        private static DrivePolicy ValidPolicy() => new DrivePolicy { Roots = new List<string> { "D:\\archive" }, VolumeId = "v" };
        [Fact] public void SustainedRuleDoesNotFireForAnInstantAndResetsOnRecovery()
        {
            var rule = new SustainedRule(); var now = DateTime.UtcNow; var duration = TimeSpan.FromSeconds(60);
            Assert.False(rule.Evaluate(true, now, duration)); Assert.False(rule.Evaluate(true, now.AddSeconds(59), duration));
            Assert.True(rule.Evaluate(true, now.AddSeconds(60), duration)); Assert.False(rule.Evaluate(true, now.AddSeconds(61), duration));
            Assert.False(rule.Evaluate(false, now.AddSeconds(62), duration)); Assert.False(rule.Evaluate(true, now.AddSeconds(63), duration));
        }
        [Fact] public void OfflineVolumeNeverLooksLikeFreeSpace()
        { Assert.Throws<InvalidOperationException>(() => PolicyRules.ShouldStart(new DrivePolicy(), new VolumeStatus { State = "Offline", TotalBytes = 100 })); }
    }
}
