using System;
using System.Collections.Generic;
using System.Linq;
using ExplorerManager.Domain;
using Xunit;

namespace ExplorerManager.Tests
{
    public sealed class InterlockTests
    {
        private static readonly DateTime Start = new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc);
        private static AppSettings Settings() => new AppSettings { InterlockTripSeconds = 10, InterlockRecoverySeconds = 10 };
        private static IList<Metric> Samples(DateTime now, double cpu = 20, double ram = 2, double cpuTemp = 40,
            double diskTemp = 35, double latency = 3, double reads = 1)
        {
            return new List<Metric>
            {
                Sample("CPU", "CPU", cpu, now), Sample("RAM", "AvailableRAM", ram, now),
                Sample("CPU", "Temperature", cpuTemp, now), Sample("Disk0", "Temperature", diskTemp, now),
                Sample("Disk0", "ReadLatency", latency, now), Sample("Disk0", "Reads", reads, now),
                Sample("Disk0", "WriteLatency", 2, now), Sample("Disk0", "Writes", 1, now)
            };
        }
        private static Metric Sample(string entity, string name, double value, DateTime utc) =>
            new Metric { Entity = entity, Name = name, Value = value, Quality = "Valid", Utc = utc };
        private static InterlockStatus Channel(ResourceInterlocks interlocks, string code) => interlocks.Snapshot().Single(s => s.Code == code);
        private static void Update(ResourceInterlocks interlocks, AppSettings settings, int seconds, double cpu = 20,
            double ram = 2, double cpuTemp = 40, double diskTemp = 35, double latency = 3, double reads = 1)
        {
            var now = Start.AddSeconds(seconds);
            interlocks.Update(Samples(now, cpu, ram, cpuTemp, diskTemp, latency, reads), now, settings);
        }

        [Fact] public void CpuDebouncesAndRequiresACompleteStableRecoveryWindow()
        {
            var core = new ResourceInterlocks(); var settings = Settings();
            Update(core, settings, 0, cpu: 95); Assert.Equal("Pending", Channel(core, "CpuLoad").State);
            Update(core, settings, 9, cpu: 95); Assert.False(Channel(core, "CpuLoad").Blocking);
            Update(core, settings, 10, cpu: 95); Assert.True(Channel(core, "CpuLoad").Blocking);
            Update(core, settings, 11); Assert.Equal("Recovering", Channel(core, "CpuLoad").State);
            Update(core, settings, 20); Assert.True(Channel(core, "CpuLoad").Blocking);
            Update(core, settings, 21); Assert.False(Channel(core, "CpuLoad").Blocking);
            Assert.False(Channel(core, "CpuLoad").Latched);
        }
        [Fact] public void ResourceTimersRemainIndependentAndLowRamTripsImmediately()
        {
            var core = new ResourceInterlocks(); var settings = Settings();
            Update(core, settings, 0, cpu: 95, ram: .5);
            Assert.True(Channel(core, "LowMemory").Blocking); Assert.False(Channel(core, "CpuLoad").Blocking);
            Update(core, settings, 5, cpu: 95, ram: .5, diskTemp: 70);
            Update(core, settings, 10, cpu: 95, ram: .5, diskTemp: 70);
            Assert.True(Channel(core, "CpuLoad").Blocking); Assert.False(Channel(core, "DiskTemperature").Blocking);
            Update(core, settings, 15, cpu: 95, ram: .5, diskTemp: 70);
            Assert.True(Channel(core, "DiskTemperature").Blocking);
            Assert.Equal(3, core.Snapshot().Count(s => s.Blocking));
        }
        [Fact] public void MissingTrippedSensorCannotBeClearedByResetAndFreshRecoveryRestartsItsTimer()
        {
            var core = new ResourceInterlocks(); var settings = Settings();
            Update(core, settings, 0, cpuTemp: 95); Update(core, settings, 10, cpuTemp: 95);
            var missing = Samples(Start.AddSeconds(15)).Where(m => !(m.Entity == "CPU" && m.Name == "Temperature"));
            core.Update(missing, Start.AddSeconds(15), settings);
            Assert.True(Channel(core, "CpuTemperature").Blocking); Assert.Equal("Unknown", Channel(core, "CpuTemperature").State);
            Assert.False(core.Reset(Start.AddSeconds(15), settings));
            Update(core, settings, 16); Update(core, settings, 25);
            Assert.True(Channel(core, "CpuTemperature").Blocking);
            Update(core, settings, 26); Assert.False(Channel(core, "CpuTemperature").Blocking);
        }
        [Theory]
        [InlineData("Valid", double.NaN, 0)]
        [InlineData("Valid", double.PositiveInfinity, 0)]
        [InlineData("Stale", 20, 0)]
        [InlineData("Unsupported", 20, 0)]
        [InlineData("Error", 20, 0)]
        [InlineData("Valid", 20, 21)]
        [InlineData("Valid", 20, -6)]
        public void InvalidStaleOrFutureEvidenceNeverClearsAnActiveLatch(string quality, double value, int ageSeconds)
        {
            var core = new ResourceInterlocks(); var settings = Settings();
            Update(core, settings, 0, cpu: 95); Update(core, settings, 10, cpu: 95);
            var now = Start.AddSeconds(11); var samples = Samples(now);
            var cpu = samples.Single(m => m.Name == "CPU"); cpu.Value = value; cpu.Quality = quality; cpu.Utc = now.AddSeconds(-ageSeconds);
            core.Update(samples, now, settings);
            Assert.True(Channel(core, "CpuLoad").Blocking); Assert.True(Channel(core, "CpuLoad").Latched);
            Assert.False(core.Reset(now, settings));
        }
        [Fact] public void UnsupportedNeverTrippedSensorsDoNotBlockStartup()
        {
            var core = new ResourceInterlocks(); var settings = Settings();
            var samples = Samples(Start); samples.Single(m => m.Entity == "CPU" && m.Name == "Temperature").Quality = "Unsupported";
            core.Update(samples, Start, settings);
            Assert.Equal("Unsupported", Channel(core, "CpuTemperature").State);
            Assert.DoesNotContain(core.Snapshot(), s => s.Blocking);
            Assert.True(core.Reset(Start, settings));
        }
        [Fact] public void OnlySustainedLatencyDuringActiveIoTripsAndIdleFreshEvidenceCanRecover()
        {
            var core = new ResourceInterlocks(); var settings = Settings();
            Update(core, settings, 0, latency: 200, reads: 0); Update(core, settings, 10, latency: 200, reads: 0);
            Assert.False(Channel(core, "DiskReadLatency").Blocking);
            Update(core, settings, 11, latency: 200); Update(core, settings, 20, latency: 200);
            Assert.False(Channel(core, "DiskReadLatency").Blocking);
            Update(core, settings, 21, latency: 200); Assert.True(Channel(core, "DiskReadLatency").Blocking);
            Update(core, settings, 22, latency: 200, reads: 0); Update(core, settings, 32, latency: 200, reads: 0);
            Assert.False(Channel(core, "DiskReadLatency").Blocking);
        }
        [Fact] public void ManualResetIsAtomicAcrossSimultaneousReasonsAndRefusesUnsafeOrRecoveringResources()
        {
            var core = new ResourceInterlocks(); var settings = Settings(); settings.InterlockManualReset = true;
            Update(core, settings, 0, cpu: 95, ram: .5, diskTemp: 70, latency: 200);
            Update(core, settings, 10, cpu: 95, ram: .5, diskTemp: 70, latency: 200);
            Assert.Equal(4, core.Snapshot().Count(s => s.Blocking)); Assert.False(core.Reset(Start.AddSeconds(10), settings));
            Update(core, settings, 11); Assert.False(core.Reset(Start.AddSeconds(11), settings));
            Update(core, settings, 21); Assert.Equal(4, core.Snapshot().Count(s => s.State == "AwaitingReset"));
            Update(core, settings, 22, ram: .5);
            Assert.False(core.Reset(Start.AddSeconds(22), settings)); Assert.Equal(4, core.Snapshot().Count(s => s.Blocking));
            Update(core, settings, 23); Update(core, settings, 33);
            Assert.True(core.Reset(Start.AddSeconds(33), settings)); Assert.DoesNotContain(core.Snapshot(), s => s.Blocking || s.Latched);
        }
        [Fact] public void ResetUsesCurrentSettingsAndOriginalSampleTimestamp()
        {
            var core = new ResourceInterlocks(); var settings = Settings(); settings.InterlockManualReset = true;
            Update(core, settings, 0, cpu: 95); Update(core, settings, 10, cpu: 95);
            settings.CpuWarningPercent = 99;
            Update(core, settings, 11, cpu: 95); Update(core, settings, 21, cpu: 95);
            Assert.Equal("AwaitingReset", Channel(core, "CpuLoad").State);
            settings.CpuWarningPercent = 90;
            Assert.False(core.Reset(Start.AddSeconds(21), settings)); Assert.Equal("Tripped", Channel(core, "CpuLoad").State);
            Update(core, settings, 22); Update(core, settings, 32);
            Assert.Equal("AwaitingReset", Channel(core, "CpuLoad").State);
            Assert.False(core.Reset(Start.AddSeconds(53), settings));
            Assert.True(Channel(core, "CpuLoad").Blocking);
        }
        [Fact] public void PendingUnsafeResourcePreventsResettingAnotherRecoveredLatchWithoutRestartingItsTripTimer()
        {
            var core = new ResourceInterlocks(); var settings = Settings(); settings.InterlockManualReset = true;
            Update(core, settings, 0, ram: .5);
            Update(core, settings, 1); Update(core, settings, 11);
            Assert.Equal("AwaitingReset", Channel(core, "LowMemory").State);
            Update(core, settings, 12, cpu: 95);
            Assert.Equal("Pending", Channel(core, "CpuLoad").State);
            Assert.False(core.Reset(Start.AddSeconds(15), settings));
            Assert.Equal("AwaitingReset", Channel(core, "LowMemory").State); Assert.True(Channel(core, "LowMemory").Blocking);
            Update(core, settings, 22, cpu: 95);
            Assert.Equal("Tripped", Channel(core, "CpuLoad").State);
        }
        [Fact] public void DisablingManualResetDoesNotClearAnUnsafeCondition()
        {
            var core = new ResourceInterlocks(); var settings = Settings(); settings.InterlockManualReset = true;
            Update(core, settings, 0, cpu: 95); Update(core, settings, 10, cpu: 95);
            settings.InterlockManualReset = false;
            Update(core, settings, 11, cpu: 95); Assert.True(Channel(core, "CpuLoad").Blocking);
            Update(core, settings, 12); Update(core, settings, 22); Assert.False(Channel(core, "CpuLoad").Blocking);
        }
        [Fact] public void BackwardClockAndObservationGapsCannotCompleteRecovery()
        {
            var core = new ResourceInterlocks(); var settings = Settings();
            Update(core, settings, 100, cpu: 95); Update(core, settings, 110, cpu: 95); Update(core, settings, 111);
            Update(core, settings, 50); Assert.True(Channel(core, "CpuLoad").Blocking);
            Update(core, settings, 59); Assert.True(Channel(core, "CpuLoad").Blocking);
            Update(core, settings, 60); Assert.False(Channel(core, "CpuLoad").Blocking);
            Update(core, settings, 61, cpu: 95); Update(core, settings, 71, cpu: 95); Update(core, settings, 72);
            Update(core, settings, 100); Assert.True(Channel(core, "CpuLoad").Blocking);
            Update(core, settings, 110); Assert.False(Channel(core, "CpuLoad").Blocking);
        }
        [Fact] public void MissingActivityCannotClearAPreviouslyTrippedLatencyChannel()
        {
            var core = new ResourceInterlocks(); var settings = Settings();
            Update(core, settings, 0, latency: 200); Update(core, settings, 10, latency: 200);
            var now = Start.AddSeconds(11);
            core.Update(Samples(now).Where(m => m.Name != "Reads"), now, settings);
            Assert.True(Channel(core, "DiskReadLatency").Blocking); Assert.False(core.Reset(now, settings));
        }
        [Fact] public void SnapshotsAndRememberedEvidenceAreIsolatedFromCallerMutation()
        {
            var core = new ResourceInterlocks(); var settings = Settings(); settings.InterlockManualReset = true;
            var samples = Samples(Start, ram: .5); var states = core.Update(samples, Start, settings);
            states.Single(s => s.Code == "LowMemory").Blocking = false;
            samples.Single(m => m.Name == "AvailableRAM").Value = 10;
            Assert.True(Channel(core, "LowMemory").Blocking); Assert.False(core.Reset(Start.AddSeconds(1), settings));
        }
        [Fact] public void RestartRestoresOnlyLatchedResourcesAndRequiresFreshRecoveryAndManualReset()
        {
            var before = new ResourceInterlocks(); var settings = Settings(); settings.InterlockManualReset = true;
            Update(before, settings, 0, ram: .5, cpu: 95);
            Assert.Equal("Pending", Channel(before, "CpuLoad").State);
            var stored = before.Snapshot();
            var restarted = new ResourceInterlocks(); restarted.Restore(stored);
            Assert.Single(restarted.Snapshot());
            var restored = Channel(restarted, "LowMemory");
            Assert.True(restored.Blocking); Assert.True(restored.Latched); Assert.Equal("Unknown", restored.State);
            Assert.Equal(Channel(before, "LowMemory").SinceUtc, restored.SinceUtc);
            Assert.False(restarted.Reset(Start.AddSeconds(1), settings));
            Update(restarted, settings, 2); Assert.False(restarted.Reset(Start.AddSeconds(2), settings));
            Update(restarted, settings, 11); Assert.True(Channel(restarted, "LowMemory").Blocking);
            Update(restarted, settings, 12); Assert.Equal("AwaitingReset", Channel(restarted, "LowMemory").State);
            Assert.True(restarted.Reset(Start.AddSeconds(12), settings));
            var afterClear = new ResourceInterlocks(); afterClear.Restore(restarted.Snapshot());
            Assert.Empty(afterClear.Snapshot());
        }
        [Fact] public void RestartDoesNotReusePreRestartRecoveryTimeOrAllowMissingEvidenceToClear()
        {
            var before = new ResourceInterlocks(); var settings = Settings();
            Update(before, settings, 0, ram: .5); Update(before, settings, 1); Update(before, settings, 9);
            var restarted = new ResourceInterlocks(); restarted.Restore(before.Snapshot());
            restarted.Update(new Metric[0], Start.AddSeconds(10), settings);
            Assert.True(Channel(restarted, "LowMemory").Blocking);
            Update(restarted, settings, 11); Update(restarted, settings, 20);
            Assert.True(Channel(restarted, "LowMemory").Blocking);
            Update(restarted, settings, 21); Assert.False(Channel(restarted, "LowMemory").Blocking);
        }
    }
}
