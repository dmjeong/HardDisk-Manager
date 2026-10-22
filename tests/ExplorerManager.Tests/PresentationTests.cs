using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Contracts;
using ExplorerManager.Domain;
using ExplorerManager.Presentation;
using Xunit;

namespace ExplorerManager.Tests
{
    public sealed class PresentationTests
    {
        [Fact] public async Task RefreshPreservesDraftAndVolumeObjectWhileUpdatingMeasurements()
        {
            var client = new FakeClient(); var vm = new MainViewModel(client, new FakeUi());
            await vm.RefreshAsync(); var volume = vm.Volumes[0];
            vm.SelectedPolicy.Name = "아직 저장하지 않은 편집";
            client.State.Volumes[0].FreeBytes = 500;
            await vm.RefreshAsync();
            Assert.Same(volume, vm.Volumes[0]); Assert.Equal(500, vm.Volumes[0].FreeBytes);
            Assert.Equal("아직 저장하지 않은 편집", vm.SelectedPolicy.Name);
            Assert.Equal("v", vm.SelectedPolicy.VolumeId);
        }
        [Fact] public async Task DisconnectedRegisteredVolumeKeepsIdentityForPolicyEditor()
        {
            var client = new FakeClient(); var vm = new MainViewModel(client, new FakeUi());
            await vm.RefreshAsync(); client.State.Volumes.Clear(); await vm.RefreshAsync();
            Assert.Equal("v", vm.SelectedPolicy.VolumeId); Assert.Single(vm.Volumes);
            Assert.Contains("Offline", vm.Volumes[0].State);
        }
        [Theory]
        [InlineData(0L, "0 B")]
        [InlineData(999L, "999 B")]
        [InlineData(1500L, "1.5 KB")]
        [InlineData(1000000000L, "1 GB")]
        [InlineData(1500000000000L, "1.5 TB")]
        [InlineData(-1000000L, "-1 MB")]
        public void ByteDisplayUsesDecimalUnitsAndPreservesSignedMeasurements(long bytes, string expected)
        {
            Assert.Equal(expected, DisplayText.Bytes(bytes));
        }
        [Fact] public void MetricDisplayDistinguishesUnavailableReadingsFromMeasuredZero()
        {
            Assert.Equal("측정 대기", DisplayText.MetricValue(null));
            Assert.Equal("미지원", DisplayText.MetricValue(new Metric { Quality = "Unsupported", Value = 42, Unit = "°C" }));
            Assert.Equal("측정 대기", DisplayText.MetricValue(new Metric { Quality = "Stale", Value = 42, Unit = "°C" }));
            Assert.Equal("측정 대기", DisplayText.MetricValue(new Metric { Value = double.NaN, Unit = "%" }));
            Assert.Equal("0 MiB/s", DisplayText.MetricValue(new Metric { Value = 0, Unit = "MiB/s" }));
            Assert.Equal("알 수 없는 상태", DisplayText.State("알 수 없는 상태"));
        }
        [Fact] public void VolumeCardUpdatesLabelsAndHidesUnavailableCapacity()
        {
            var row = new VolumeRow(new VolumeStatus { Id = "v", Mount = "D:\\", Label = "영상", TotalBytes = 1000000000, FreeBytes = 250000000 });
            var changed = new List<string>(); row.PropertyChanged += (sender, args) => changed.Add(args.PropertyName);
            Assert.Equal("D:  영상", row.Title); Assert.Equal("1 GB", row.CapacityText);
            Assert.Equal("250 MB", row.FreeText); Assert.Equal("750 MB", row.UsedText); Assert.Equal("75% used", row.UsageText);
            row.Apply(new VolumeStatus { Id = "v", Mount = "D:\\", State = "Offline / IdentityMismatch", TotalBytes = 1000000000, FreeBytes = 250000000 });
            Assert.False(row.HasCapacity); Assert.Equal("확인 불가", row.FreeText); Assert.Equal("—", row.UsedPercentText);
            Assert.Contains("연결 끊김", row.StateText); Assert.Contains(nameof(VolumeRow.FreeText), changed); Assert.Contains(nameof(VolumeRow.HasCapacity), changed);
        }
        [Fact] public async Task DashboardSummariesUseAvailableVolumesSavedPoliciesAndUnacknowledgedAlerts()
        {
            var client = new FakeClient();
            client.State.Volumes[0].TotalBytes = 1000000000; client.State.Volumes[0].FreeBytes = 250000000;
            client.State.Volumes.Add(new VolumeStatus { Id = "offline", State = "Offline", TotalBytes = 2000000000, FreeBytes = 1000000000 });
            client.State.Policies[0].Enabled = true;
            client.State.Alerts.Add(new AlertRecord { Acknowledged = false, Category = "DiskHealth" });
            client.State.Alerts.Add(new AlertRecord { Acknowledged = true, Category = "DiskHealth" });
            client.State.CacheUsed = client.State.CacheCapacity / 4; client.State.Health = "Ready"; client.State.Paused = true;
            var vm = new MainViewModel(client, new FakeUi()); await vm.RefreshAsync();
            Assert.Equal("2개", vm.VolumeCountText); Assert.Equal("250 MB", vm.TotalFreeText);
            Assert.Equal("1개", vm.EnabledPolicyCountText); Assert.Equal("1건", vm.AlertCountText); Assert.Equal(25, vm.CachePercent);
            Assert.True(vm.IsServiceConnected); Assert.True(vm.IsPaused); Assert.Equal("정상", vm.HealthText);
            Assert.Equal("자동 정리 일시정지", vm.ServiceText); Assert.Contains("업데이트", vm.LastUpdatedText);
        }
        [Fact] public async Task SelectedDiskChangesMetricCardsAndUnavailableSamplesRemainExplicit()
        {
            var client = new FakeClient();
            client.State.Metrics.AddRange(new[]
            {
                new Metric { Entity = "CPU", Name = "CPU", Value = 12.5, Unit = "%" },
                new Metric { Entity = "CPU", Name = "Temperature", Quality = "Unsupported", Unit = "°C" },
                new Metric { Entity = "Disk0", Name = "ReadBytes", Value = 12, Unit = "MiB/s" },
                new Metric { Entity = "Disk0", Name = "WriteBytes", Value = 0, Unit = "MiB/s" },
                new Metric { Entity = "Disk0", Name = "ReadLatency", Value = 4, Unit = "ms" },
                new Metric { Entity = "Disk0", Name = "WriteLatency", Value = 8, Unit = "ms" },
                new Metric { Entity = "Disk1", Name = "ReadBytes", Value = 2.5, Unit = "MiB/s" }
            });
            var vm = new MainViewModel(client, new FakeUi()); await vm.RefreshAsync();
            Assert.Equal("12.5 %", vm.CpuText); Assert.Equal("미지원", vm.CpuTemperatureText);
            Assert.Equal("12 MiB/s", vm.ReadRateText); Assert.Equal("0 MiB/s", vm.WriteRateText); Assert.Equal("8 ms", vm.LatencyText);
            var changed = new List<string>(); vm.PropertyChanged += (sender, args) => changed.Add(args.PropertyName);
            vm.SelectedDisk = "Disk1";
            Assert.Equal("2.5 MiB/s", vm.ReadRateText); Assert.Equal("측정 대기", vm.WriteRateText); Assert.Equal("측정 대기", vm.LatencyText);
            Assert.Contains(nameof(MainViewModel.ReadRateText), changed); Assert.Contains(nameof(MainViewModel.LatencyText), changed);
            client.State.Metrics.Clear(); client.State.CacheCapacity = 0; await vm.RefreshAsync();
            Assert.Equal("측정 대기", vm.CpuText); Assert.Equal("측정 대기", vm.CacheSummary); Assert.Equal(0, vm.CachePercent);
        }
        [Fact] public async Task FailedConnectionDoesNotPresentPreviousMetricsAsCurrentReadings()
        {
            var client = new FakeClient(); client.State.Metrics.Add(new Metric { Entity = "CPU", Name = "CPU", Value = 25, Unit = "%" });
            var vm = new MainViewModel(client, new FakeUi()); await vm.RefreshAsync(); Assert.Equal("25 %", vm.CpuText);
            client.FailDashboard = true; await vm.RefreshAsync();
            Assert.False(vm.IsServiceConnected); Assert.Equal("서비스 연결 끊김", vm.ServiceText); Assert.Equal("측정 대기", vm.CpuText);
            Assert.Empty(vm.Metrics);
        }
        [Fact] public async Task ChartsRecordMissingSamplesAndConnectionFailuresAsGaps()
        {
            var client = new FakeClient();
            var cpu = new Metric { Entity = "CPU", Name = "CPU", Value = 10, Unit = "%" };
            var read = new Metric { Entity = "Disk0", Name = "ReadBytes", Value = 1, Unit = "MiB/s" };
            client.State.Metrics.AddRange(new[] { cpu, read,
                new Metric { Entity = "Disk0", Name = "WriteBytes", Value = 2, Unit = "MiB/s" },
                new Metric { Entity = "Disk0", Name = "ReadLatency", Value = 3, Unit = "ms" } });
            var vm = new MainViewModel(client, new FakeUi()); await vm.RefreshAsync();
            Assert.Equal(10, vm.CpuSeries[0]); Assert.Equal(1, vm.ReadSeries[0]);
            cpu.Quality = "Unsupported"; read.Value = double.PositiveInfinity; await vm.RefreshAsync();
            Assert.Equal(2, vm.CpuSeries.Count); Assert.True(double.IsNaN(vm.CpuSeries[1])); Assert.True(double.IsNaN(vm.ReadSeries[1]));
            client.FailDashboard = true; await vm.RefreshAsync();
            Assert.Equal(3, vm.CpuSeries.Count); Assert.True(double.IsNaN(vm.CpuSeries[2])); Assert.True(double.IsNaN(vm.ReadSeries[2]));
            Assert.True(double.IsNaN(vm.WriteSeries[2])); Assert.True(double.IsNaN(vm.LatencySeries[2]));
        }
        private sealed class FakeClient : IExplorerClient
        {
            public bool FailDashboard { get; set; }
            public Dashboard State { get; } = new Dashboard
            {
                Utc = DateTime.UtcNow, IsOperator = true, CacheCapacity = 1073741824,
                Policies = new List<DrivePolicy> { new DrivePolicy { Id = "p", Version = 1, Name = "Saved", VolumeId = "v", Roots = new List<string> { "D:\\archive" } } },
                Volumes = new List<VolumeStatus> { new VolumeStatus { Id = "v", Mount = "D:\\", TotalBytes = 1000, FreeBytes = 100 } }
            };
            public Task<T> SendAsync<T>(string command, object data = null, CancellationToken ct = default(CancellationToken))
            {
                object value;
                switch (command)
                {
                    case "Dashboard": if (FailDashboard) throw new InvalidOperationException("연결 실패"); value = State; break;
                    case "Recovery": value = Array.Empty<RecoveryInfo>(); break;
                    case "Folders": value = Array.Empty<FolderSummary>(); break;
                    case "History": value = new HistoryPage(); break;
                    case "Settings": value = new AppSettings(); break;
                    default: throw new InvalidOperationException(command);
                }
                return Task.FromResult((T)value);
            }
            public Task CloseProcessesAsync(UnlockTicket t, bool f, CancellationToken c) => Task.CompletedTask;
        }
        private sealed class FakeUi : IUiServices
        {
            public bool InputsValid => true;
            public Task<bool> ConfirmAsync(string title, string text) => Task.FromResult(false);
            public Task<int> ShowLockAlertAsync(AlertRecord alert) => Task.FromResult(0);
            public Task<string> PickFolderAsync() => Task.FromResult<string>(null);
            public Task SaveTextAsync(string name, string text) => Task.CompletedTask;
            public void Notify(string title, string text) { }
            public void RunElevated() { }
        }
    }
}
