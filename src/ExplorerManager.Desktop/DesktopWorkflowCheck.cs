using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using ExplorerManager.Client;
using ExplorerManager.Domain;
using ExplorerManager.Contracts;
using ExplorerManager.Presentation;

namespace ExplorerManager.Desktop
{
    // Explicit CI entry point: exercises real WPF controls and the authenticated service.
    internal static class DesktopWorkflowCheck
    {
        public static async Task RunAsync(MainWindow window, MainViewModel vm, bool offline, string root, string log)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(log));
                if (offline)
                {
                    await UntilAsync(() => vm.IsLocalReadOnly && vm.Metrics.Any(x => x.Entity == "CPU" && x.Name == "CPU" && DisplayText.HasValue(x)), 25);
                    Require(!vm.IsServiceConnected && vm.ShowConnectionHelp, "Missing service is explicitly reported");
                    Require(vm.Volumes.Any(v => v.HasCapacity), "Offline desktop enumerates real local volumes");
                    Require(vm.CpuText != "측정 대기", "Offline desktop measures real CPU");
                    File.WriteAllText(log, "PASS: service absent; real local volumes and CPU; setup action visible; X closes desktop.");
                    window.Close(); return;
                }
                Require(Path.GetFileName(root).StartsWith("ExplorerManager-UI-", StringComparison.Ordinal) && Directory.Exists(root), "Isolated workflow fixture required");
                await UntilAsync(() => vm.IsLocalReadOnly || vm.IsServiceConnected, 25);
                if (!vm.IsServiceConnected)
                {
                    await LayoutAsync(window); Invoke(Find<Button>(window, "Set up background service"));
                    await UntilAsync(() => vm.IsServiceConnected, 100);
                }
                await UntilAsync(() => File.Exists(Path.Combine(root, ".ready")), 45);
                await UntilAsync(() => vm.Metrics.Any(x => x.Entity == "CPU" && x.Name == "CPU" && DisplayText.HasValue(x)), 25);
                Require(vm.Volumes.Any(x => x.HasCapacity), "Connected service returns volumes");
                Require(vm.Metrics.Any(x => x.Entity.StartsWith("Disk", StringComparison.Ordinal) && x.Name == "ReadBytes" && DisplayText.HasValue(x)), "Connected service returns physical disk throughput");
                window.Sections.SelectedIndex = 1; await LayoutAsync(window);
                Invoke(FindButton(window, "+ New policy")); await LayoutAsync(window);
                var name = Find<TextBox>(window, "Policy name"); var folders = Find<TextBox>(window, "관리할 폴더 경로"); var volume = Find<ComboBox>(window, "Target volume");
                Require(!Validation.GetHasError(name), "A missing root does not invalidate policy name");
                Require(Validation.GetHasError(folders), "Missing root has its own visible error");
                Require(volume.Items.Count > 0, "Target volume dropdown has real entries");
                volume.IsDropDownOpen = true; await LayoutAsync(window);
                var target = vm.Volumes.First(x => x.HasCapacity && root.StartsWith(x.Mount, StringComparison.OrdinalIgnoreCase));
                var item = (ComboBoxItem)volume.ItemContainerGenerator.ContainerFromItem(target);
                Require(item != null && Descendants(item).OfType<TextBlock>().Any(t => !string.IsNullOrWhiteSpace(t.Text)), "Dropdown entries render visible text");
                item.IsSelected = true;
                volume.IsDropDownOpen = false;
                name.Text = "UI workflow " + Guid.NewGuid().ToString("N"); folders.Text = root;
                var retention = Find<TextBox>(window, "Retention (days)"); retention.Text = "0"; await LayoutAsync(window);
                Require(Validation.GetHasError(retention) && !Validation.GetHasError(name), "Field-specific errors appear without contaminating other fields");
                retention.Text = "30";
                var start = Find<TextBox>(window, "Start threshold"); start.Text = "not-a-number"; await LayoutAsync(window);
                Require(Validation.GetHasError(start), "Invalid numeric text is rejected");
                start.Text = "90"; await LayoutAsync(window);
                Require(!Validation.GetHasError(start) && !Validation.GetHasError(retention) && !Validation.GetHasError(folders) && !vm.SelectedPolicy.HasErrors, "Correcting inputs clears all policy errors");
                string id = vm.SelectedPolicy.Id; Invoke(FindButton(window, "Save policy"));
                await UntilAsync(() => vm.SelectedPolicy?.Id == id && vm.SelectedPolicy.Version > 0 && !vm.SelectedPolicy.IsDirty, 25);
                var client = new PipeClient(); var saved = (await client.SendAsync<Dashboard>("Dashboard")).Policies.Single(x => x.Id == id);
                Require(saved.VolumeId == target.Id && saved.Roots.Single() == root && !saved.Enabled, "Edited policy is persisted by the real service");
                // Conversion errors on a saved policy must survive the 3-second refresh.
                var savedEditor = vm.SelectedPolicy; start.Text = "unfinished-number"; await LayoutAsync(window);
                await Task.Delay(4000); await vm.RefreshAsync(); await LayoutAsync(window);
                Require(ReferenceEquals(savedEditor, vm.SelectedPolicy) && start.Text == "unfinished-number" && Validation.GetHasError(start), "Polling preserves invalid numeric input on saved policies");
                start.Text = "90"; await LayoutAsync(window);
                Invoke(FindButton(window, "Save policy")); await UntilAsync(() => !vm.SelectedPolicy.IsDirty, 25);
                Invoke(FindButton(window, "Preview"));
                await UntilAsync(async () => (await client.SendAsync<Dashboard>("Dashboard")).Runs.Any(x => x.PolicyId == id && x.FinishedUtc.HasValue), 35);
                var history = await client.SendAsync<HistoryPage>("History");
                Require(history.Items.Any(x => x.PolicyId == id && x.State == "WouldDelete" && x.Path == Path.Combine(root, "expired.txt")), "Preview inspects the real fixture file");
                Require(File.Exists(Path.Combine(root, "expired.txt")), "Preview preserves source file");
                // Exercise actual deletion, RAM cache and restore with only this isolated fixture.
                saved = (await client.SendAsync<Dashboard>("Dashboard")).Policies.Single(x => x.Id == id);
                saved.ThresholdMode = ThresholdMode.FreeBytes; saved.StartThreshold = (double)target.TotalBytes + 1048576; saved.StopThreshold = saved.StartThreshold + 1;
                saved = await client.SendAsync<DrivePolicy>("SavePolicy", saved);
                await vm.RefreshAsync(); await UntilAsync(() => vm.SelectedPolicy.Version == saved.Version, 15);
                Invoke(FindButton(window, "지금 정리"));
                await UntilAsync(() => !File.Exists(Path.Combine(root, "expired.txt")), 35);
                await UntilAsync(async () => (await client.SendAsync<RecoveryInfo[]>("Recovery")).Any(x => x.Path == Path.Combine(root, "expired.txt")), 15);
                await UntilAsync(() => vm.History.Any(x => x.PolicyId == id && x.State == "DeletedConfirmed"), 20);
                await UntilAsync(async () => (await client.SendAsync<Dashboard>("Dashboard")).Runs.Any(x => x.PolicyId == id && !x.DryRun && x.FinishedUtc.HasValue), 15);
                var copy = (await client.SendAsync<RecoveryInfo[]>("Recovery")).Single(x => x.Path == Path.Combine(root, "expired.txt"));
                await client.SendAsync<string>("Restore", new { copy.Id });
                await UntilAsync(() => File.Exists(Path.Combine(root, "expired.txt")), 20);
                Require(File.ReadAllText(Path.Combine(root, "expired.txt")).Trim() == "Real UI workflow fixture", "RAM restore preserves original bytes");
                window.Sections.SelectedIndex = 6; await LayoutAsync(window);
                vm.CacheCapacityMiB = 128; Invoke(FindButton(window, "Save changes"));
                await UntilAsync(async () => (await client.SendAsync<AppSettings>("Settings")).CacheBytes == 128L * 1048576, 20);
                await client.SendAsync<object>("DeletePolicy", new { Id = id });
                await client.SendAsync<object>("SaveSettings", new AppSettings());
                File.WriteAllText(log, "PASS: UI setup/start; authenticated service; real volumes/CPU/disk; dropdown selection/text; invalid input correction; policy save; real preview; saved invalid numeric input retained; real delete/history/RAM restore; settings persisted; X closes desktop.");
                window.Close();
            }
            catch (Exception ex) { File.WriteAllText(log, ex + Environment.NewLine + vm.Status + Environment.NewLine + vm.Message); System.Windows.Application.Current.Shutdown(10); }
        }
        public static async Task UpgradeAsync(MainWindow window, MainViewModel vm, string root, string log)
        {
            try {
                Require(Path.GetFileName(root).StartsWith("ExplorerManager-UI-", StringComparison.Ordinal) && Directory.Exists(root), "Isolated upgrade fixture required");
                await UntilAsync(() => vm.IsServiceConnected, 30);
                Require(vm.RequiresServiceUpdate && vm.ShowConnectionHelp && !vm.IsServiceNewer, "Old service must display update action while connected");
                await LayoutAsync(window); Require(Find<Button>(window, "Set up background service").Content.ToString() == "서비스 업데이트", "Visible update button");
                var client = new PipeClient(); var original = await client.SendAsync<AppSettings>("Settings");
                var changed = await client.SendAsync<AppSettings>("Settings"); changed.HistoryDays = 123;
                await client.SendAsync<object>("SaveSettings", changed);
                var volume = vm.Volumes.First(x => x.HasCapacity && root.StartsWith(x.Mount, StringComparison.OrdinalIgnoreCase));
                var policy = await client.SendAsync<DrivePolicy>("SavePolicy", new DrivePolicy { Name = "Upgrade sentinel", VolumeId = volume.Id, Roots = new System.Collections.Generic.List<string> { root } });
                await WindowsDesktopIntegration.UpdateServiceAsync(AppDomain.CurrentDomain.BaseDirectory);
                await UntilAsync(() => vm.IsServiceConnected && !vm.RequiresServiceUpdate, 40);
                var state = await client.SendAsync<Dashboard>("Dashboard");
                Require(state.Version == Protocol.ProductVersion && state.Policies.Any(x => x.Id == policy.Id && x.Roots.Single() == root), "Updated service preserves policy and reports actual version");
                Require((await client.SendAsync<AppSettings>("Settings")).HistoryDays == 123, "Upgrade preserves settings");
                await client.SendAsync<object>("DeletePolicy", new { policy.Id }); await client.SendAsync<object>("SaveSettings", original);
                File.WriteAllText(log, "PASS: real 1.0.1 installed service; live old Desktop; visible version mismatch/update action; update installer; matching versions; policy/settings preserved; new Desktop exits.");
                window.Close();
            } catch (Exception ex) { File.WriteAllText(log, ex + Environment.NewLine + vm.Status + Environment.NewLine + vm.Message); System.Windows.Application.Current.Shutdown(12); }
        }
        private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
        private static async Task LayoutAsync(MainWindow window) { await window.Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle); }
        private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        { yield return parent; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child; }
        private static T Find<T>(DependencyObject parent, string name) where T : FrameworkElement => Descendants(parent).OfType<T>().First(x => AutomationProperties.GetName(x) == name);
        private static Button FindButton(DependencyObject parent, string text) => Descendants(parent).OfType<Button>().First(x => Equals(x.Content, text));
        private static void Invoke(Button button) { Require(button.IsEnabled, "Button is enabled: " + button.Content); ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke(); }
        private static Task UntilAsync(Func<bool> condition, int seconds) => UntilAsync(() => Task.FromResult(condition()), seconds);
        private static async Task UntilAsync(Func<Task<bool>> condition, int seconds)
        { var watch = Stopwatch.StartNew(); while (watch.Elapsed.TotalSeconds < seconds) { if (await condition()) return; await Task.Delay(100); } throw new TimeoutException("Desktop workflow did not reach expected state."); }
    }
}
