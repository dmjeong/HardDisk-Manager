using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ExplorerManager.Domain;
using ExplorerManager.Client;
using ExplorerManager.Presentation;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ExplorerManager.Desktop
{
    internal sealed class UiAdapters : IUiServices, ITransferUiServices, IDesktopHost
    {
        private readonly Window _owner;
        private readonly Action<string, string> _notify;
        public UiAdapters(Window owner, Action<string, string> notify) { _owner = owner; _notify = notify; }
        public bool InputsValid => !HasErrors(_owner);
        private static bool HasErrors(DependencyObject view)
        {
            if (Validation.GetHasError(view)) return true;
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(view); i++)
                if (HasErrors(VisualTreeHelper.GetChild(view, i))) return true;
            return false;
        }
        public Task<bool> ConfirmAsync(string title, string text) => Task.FromResult(MessageBox.Show(_owner, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
        public Task<string> RequestTransferPasswordAsync()
        {
            var password = new PasswordBox { Margin = new Thickness(0, 16, 0, 20), Padding = new Thickness(10),
                Background = (Brush)_owner.FindResource("InputBrush"), Foreground = (Brush)_owner.FindResource("TextBrush"),
                BorderBrush = (Brush)_owner.FindResource("BorderBrush"), MaxLength = 4096 };
            var dialog = new Window { Title = "FTP credential", Owner = _owner, Width = 440, SizeToContent = SizeToContent.Height,
                ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var body = new StackPanel { Margin = new Thickness(24) };
            body.Children.Add(new TextBlock { Text = "FTP password", FontSize = 17, FontWeight = FontWeights.SemiBold });
            body.Children.Add(new TextBlock { Text = "서비스의 보호된 저장소에 등록합니다.", Margin = new Thickness(0, 8, 0, 0) }); body.Children.Add(password);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
            var save = new Button { Content = "Set password", IsDefault = true, Style = (Style)_owner.FindResource("PrimaryButton") };
            save.Click += (sender, args) => dialog.DialogResult = true; buttons.Children.Add(cancel); buttons.Children.Add(save); body.Children.Add(buttons); dialog.Content = body;
            dialog.Loaded += (sender, args) => password.Focus(); bool accepted = dialog.ShowDialog() == true;
            string value = accepted ? password.Password : null; password.Clear(); return Task.FromResult(value);
        }
        public Task<int> ShowLockAlertAsync(AlertRecord alert)
        {
            var model = new LockAlertViewModel(alert);
            var dialog = new LockAlertWindow { DataContext = model, Owner = _owner.IsVisible ? _owner : null };
            Action close = () => dialog.Close(); model.CloseRequested += close;
            try { dialog.ShowDialog(); return Task.FromResult(model.Choice); } finally { model.CloseRequested -= close; }
        }
        public Task<string> PickFolderAsync()
        {
            using (var dialog = new Forms.FolderBrowserDialog { Description = "로컬 NTFS 폴더를 선택하세요.", ShowNewFolderButton = true })
                return Task.FromResult(dialog.ShowDialog() == Forms.DialogResult.OK ? dialog.SelectedPath : null);
        }
        public Task SaveTextAsync(string name, string content)
        {
            var dialog = new SaveFileDialog { FileName = name, Filter = "CSV 파일 (*.csv)|*.csv", AddExtension = true };
            if (dialog.ShowDialog(_owner) == true) File.WriteAllText(dialog.FileName, content, new UTF8Encoding(true));
            return Task.CompletedTask;
        }
        public void Notify(string title, string message) => _notify(title, message.Length > 250 ? message.Substring(0, 250) : message);
        public void RunElevated()
        {
            Relaunch("");
        }
        private void Relaunch(string arguments)
        {
            using (var process = Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName, arguments) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppDomain.CurrentDomain.BaseDirectory }))
                if (process != null) ExitDesktop();
        }
        public async Task SetupServiceAsync()
        {
            if (!WindowsDesktopIntegration.IsAdministrator) { Relaunch("--setup-service"); return; }
            await WindowsDesktopIntegration.EnsureServiceAsync(AppDomain.CurrentDomain.BaseDirectory);
        }
        public Task UpdateServiceAsync() => WindowsDesktopIntegration.UpdateServiceAsync(AppDomain.CurrentDomain.BaseDirectory);
        public void OpenDiagnostics() { Directory.CreateDirectory(DesktopDiagnosticsLog.DirectoryPath); Process.Start(new ProcessStartInfo(DesktopDiagnosticsLog.DirectoryPath) { UseShellExecute = true }); }
        public void HideToTray() => _owner.Hide();
        public Task GrantFolderAccessAsync(string path) => WindowsDesktopIntegration.GrantFolderAccessAsync(AppDomain.CurrentDomain.BaseDirectory, path);
        public void ExitDesktop() => System.Windows.Application.Current.Shutdown();
    }
}
