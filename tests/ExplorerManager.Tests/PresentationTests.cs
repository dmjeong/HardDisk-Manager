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
        private sealed class FakeClient : IExplorerClient
        {
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
                    case "Dashboard": value = State; break;
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
