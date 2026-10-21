using System;
using ExplorerManager.Domain;
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
