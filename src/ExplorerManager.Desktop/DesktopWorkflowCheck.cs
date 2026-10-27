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
                Invoke(FindButton(window, "Preview"));
                await UntilAsync(async () => (await client.SendAsync<Dashboard>("Dashboard")).Runs.Any(x => x.PolicyId == id && x.FinishedUtc.HasValue), 35);
                var history = await client.SendAsync<HistoryPage>("History");
                Require(history.Items.Any(x => x.PolicyId == id && x.State == "WouldDelete" && x.Path == Path.Combine(root, "expired.txt")), "Preview inspects the real fixture file");
                Require(File.Exists(Path.Combine(root, "expired.txt")), "Preview preserves source file");
                await client.SendAsync<object>("DeletePolicy", new { Id = id });
                File.WriteAllText(log, "PASS: UI setup/start; authenticated service; real volumes/CPU/disk; dropdown selection/text; invalid input correction; policy save; real preview; source preserved; X closes desktop.");
                window.Close();
            }
            catch (Exception ex) { File.WriteAllText(log, ex + Environment.NewLine + vm.Status + Environment.NewLine + vm.Message); System.Windows.Application.Current.Shutdown(10); }
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
