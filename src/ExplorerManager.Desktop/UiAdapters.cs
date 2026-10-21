using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using ExplorerManager.Domain;
using ExplorerManager.Presentation;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ExplorerManager.Desktop
{
    internal sealed class UiAdapters : IUiServices
    {
        private readonly Window _owner;
        private readonly Action<string, string> _notify;
        public UiAdapters(Window owner, Action<string, string> notify) { _owner = owner; _notify = notify; }
        public Task<bool> ConfirmAsync(string title, string text) => Task.FromResult(MessageBox.Show(_owner, text, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);
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
            Process.Start(new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName) { UseShellExecute = true, Verb = "runas" });
        }
    }
}
