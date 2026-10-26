using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Contracts;
using ExplorerManager.Domain;
using ExplorerManager.Presentation;
using Xunit;

namespace ExplorerManager.Tests
{
    public sealed class TransferPresentationTests
    {
        private sealed class Ui : IUiServices, ITransferUiServices
        {
            public string Secret = "ui-private-password";
            public bool InputsValid => true;
            public Task<string> RequestTransferPasswordAsync() => Task.FromResult(Secret);
            public Task<bool> ConfirmAsync(string a, string b) => Task.FromResult(true);
            public Task<int> ShowLockAlertAsync(AlertRecord alert) => Task.FromResult(0);
            public Task<string> PickFolderAsync() => Task.FromResult<string>(null);
            public Task SaveTextAsync(string a, string b) => Task.CompletedTask;
            public void Notify(string a, string b) { }
            public void RunElevated() { }
        }
        private sealed class Client : IExplorerClient
        {
            public TransferOverview Overview = new TransferOverview { Available = true, Connections = new List<TransferConnection> {
                new TransferConnection { Id = "11111111111111111111111111111111", Version = 1, Host = "original.example", UserName = "images" } },
                Policies = new List<TransferPolicy> { new TransferPolicy { Id = "22222222222222222222222222222222", Version = 1, ConnectionId = "11111111111111111111111111111111", VolumeId = "volume", LocalRoot = @"D:\Images" } } };
            public TaskCompletionSource<TransferOverview> PendingOverview;
            public List<string> Commands = new List<string>();
            public string SavedPassword;
            public async Task<T> SendAsync<T>(string command, object data = null, CancellationToken ct = default(CancellationToken))
            {
                Commands.Add(command); object result = null;
                switch (command)
                {
                    case "TransferOverview":
                        var pending = PendingOverview; PendingOverview = null; result = pending == null ? Copy(Overview) : await pending.Task; break;
                    case "SaveTransferConnection": var c = Copy((TransferConnection)data); c.Version++; Overview.Connections[0] = c; result = c; break;
                    case "SaveTransferPolicy": var p = Copy((TransferPolicy)data); p.Version++; Overview.Policies[0] = p; result = p; break;
                    case "SaveTransferOptions": Overview.Options = Copy((TransferOptions)data); result = true; break;
                    case "SetTransferSecret":
                        SavedPassword = (string)data.GetType().GetProperty("Password").GetValue(data); Overview.Connections[0].HasSecret = true; result = true; break;
                    case "TestTransferConnection": result = "operation"; break;
                    case "TransferOperation": result = new TransferOperation<TransferCapabilities> { Id = "operation", State = "Completed", Result = new TransferCapabilities { Connected = true, Detail = "Connected" } }; break;
                    case "TransferJobs": result = new TransferJobPage(); break;
                    case "RetryTransfer": result = true; break;
                    default: throw new InvalidOperationException(command);
                }
                return (T)result;
            }
            public Task CloseProcessesAsync(UnlockTicket t, bool f, CancellationToken c) => throw new NotSupportedException();
        }
        private static T Copy<T>(T value) => TransferEditor<T>.Clone(value);
        private static TransferViewModel Model(Client client, Ui ui = null) => new TransferViewModel(client, ui ?? new Ui(), new ObservableCollection<VolumeRow>());
        private static async Task Execute(AsyncCommand command)
        {
            command.Execute(null); var watch = Stopwatch.StartNew(); while (!command.CanExecute(null) && watch.Elapsed.TotalSeconds < 3) await Task.Delay(10);
            Assert.True(command.CanExecute(null));
        }
        [Fact] public async Task PollingPreservesConnectionPolicyAndOptionsDrafts()
        {
            var client = new Client(); var vm = Model(client); await vm.RefreshAsync(true);
            vm.SelectedConnection.Host = "draft.example"; vm.SelectedPolicy.LocalRoot = @"D:\Draft"; vm.Workers = 5;
            client.Overview.Connections[0].Host = "server.example"; client.Overview.Policies[0].LocalRoot = @"D:\Server"; client.Overview.Options.Workers = 1;
            await vm.RefreshAsync(true); Assert.Equal("draft.example", vm.SelectedConnection.Host); Assert.Equal(@"D:\Draft", vm.SelectedPolicy.LocalRoot); Assert.Equal(5, vm.Workers);
        }
        [Fact] public async Task StalePollingResponseCannotUndoSuccessfulConnectionSave()
        {
            var client = new Client(); var vm = Model(client); await vm.RefreshAsync(true);
            var old = Copy(client.Overview); var pending = new TaskCompletionSource<TransferOverview>(); client.PendingOverview = pending; var poll = vm.RefreshAsync(true);
            vm.SelectedConnection.Host = "saved.example"; await Execute(vm.SaveConnectionCommand); Assert.Equal(2, vm.SelectedConnection.Version);
            pending.SetResult(old); await poll; Assert.Equal("saved.example", vm.SelectedConnection.Host); Assert.Equal(2, vm.SelectedConnection.Version); Assert.False(vm.SelectedConnection.IsDirty);
        }
        [Fact] public async Task StalePollingResponseCannotUndoSuccessfulOptionsSave()
        {
            var client = new Client(); var vm = Model(client); await vm.RefreshAsync(true); var old = Copy(client.Overview);
            var pending = new TaskCompletionSource<TransferOverview>(); client.PendingOverview = pending; var poll = vm.RefreshAsync(true);
            vm.Workers = 4; await Execute(vm.SaveOptionsCommand); pending.SetResult(old); await poll; Assert.Equal(4, vm.Workers);
        }
        [Fact] public async Task InvalidTimeDraftRemainsVisibleAndCannotBeSaved()
        {
            var client = new Client(); var vm = Model(client); await vm.RefreshAsync(true);
            Assert.Throws<ArgumentException>(() => vm.SelectedPolicy.StartTime = "25:10"); Assert.Equal("25:10", vm.SelectedPolicy.StartTime); Assert.True(vm.SelectedPolicy.IsDirty);
            await vm.RefreshAsync(true); Assert.Equal("25:10", vm.SelectedPolicy.StartTime);
            await Execute(vm.SavePolicyCommand); Assert.DoesNotContain("SaveTransferPolicy", client.Commands);
            vm.SelectedPolicy.StartTime = "22:30"; vm.SelectedPolicy.EndTime = "06:15"; await Execute(vm.SavePolicyCommand);
            Assert.Equal(22, client.Overview.Policies[0].StartHour); Assert.Equal(30, client.Overview.Policies[0].StartMinute); Assert.False(vm.SelectedPolicy.IsDirty);
        }
        [Fact] public async Task SecretIsSubmittedSeparatelyFromConnectionAndPolicyModels()
        {
            var client = new Client(); var ui = new Ui(); var vm = Model(client, ui); await vm.RefreshAsync(true); await Execute(vm.SaveSecretCommand);
            Assert.Equal(ui.Secret, client.SavedPassword); Assert.True(vm.SelectedConnection.Model.HasSecret);
            Assert.DoesNotContain(ui.Secret, Newtonsoft.Json.JsonConvert.SerializeObject(vm.SelectedConnection.Model));
            Assert.DoesNotContain(ui.Secret, vm.Message); Assert.DoesNotContain(ui.Secret, Newtonsoft.Json.JsonConvert.SerializeObject(vm.SelectedPolicy.Model));
        }
        [Fact] public async Task ConnectionTestUsesBackgroundOperationAndPollingDoesNotDiscardDraft()
        {
            var client = new Client(); var vm = Model(client); await vm.RefreshAsync(true); await Execute(vm.TestConnectionCommand);
            Assert.Contains("TestTransferConnection", client.Commands); Assert.Contains("TransferOperation", client.Commands); Assert.Equal("Connected", vm.Message);
        }
        [Fact] public async Task ReloadExplicitlyDiscardsSelectedDraftAndUnavailableServiceDoesNotQuery()
        {
            var client = new Client(); var vm = Model(client); await vm.RefreshAsync(false); Assert.Empty(client.Commands);
            await vm.RefreshAsync(true); vm.SelectedConnection.Host = "draft.example"; await Execute(vm.ReloadConnectionCommand);
            Assert.Equal("original.example", vm.SelectedConnection.Host); Assert.False(vm.SelectedConnection.IsDirty);
        }
        [Fact] public async Task UnsavedConnectionCannotBeUsedForNetworkTest()
        {
            var client = new Client(); var vm = Model(client); await vm.RefreshAsync(true); vm.SelectedConnection.Host = "unsaved.example";
            await Execute(vm.TestConnectionCommand); Assert.DoesNotContain("TestTransferConnection", client.Commands); Assert.True(vm.SelectedConnection.IsDirty);
        }
    }
}
