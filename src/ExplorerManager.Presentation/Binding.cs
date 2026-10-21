using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using ExplorerManager.Domain;

namespace ExplorerManager.Presentation
{
    public abstract class ObservableObject : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Changed([CallerMemberName] string name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Changed(name); return true; }
    }
    public sealed class AsyncCommand : ICommand
    {
        private readonly Func<Task> _execute;
        private readonly Action<Exception> _error;
        private bool _running;
        public AsyncCommand(Func<Task> execute, Action<Exception> error) { _execute = execute; _error = error; }
        public bool CanExecute(object parameter) => !_running;
        public event EventHandler CanExecuteChanged;
        public async void Execute(object parameter)
        {
            if (_running) return; _running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
            try { await _execute(); } catch (Exception ex) { _error(ex); }
            finally { _running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
        }
    }
    public interface IUiServices
    {
        bool InputsValid { get; }
        Task<bool> ConfirmAsync(string title, string text);
        Task<int> ShowLockAlertAsync(AlertRecord alert);
        Task<string> PickFolderAsync();
        Task SaveTextAsync(string name, string content);
        void Notify(string title, string message);
        void RunElevated();
    }
    public sealed class ActionCommand : ICommand
    {
        private readonly Action _action;
        public ActionCommand(Action action) { _action = action; }
        public bool CanExecute(object parameter) => true;
        public void Execute(object parameter) => _action();
        public event EventHandler CanExecuteChanged { add { } remove { } }
    }
    public sealed class LockAlertViewModel
    {
        public LockAlertViewModel(AlertRecord alert)
        {
            Message = alert.Message + "\n\n" + alert.Path + "\n\n" +
                string.Join("\n", alert.Processes.ConvertAll(p => p.Name + " · PID " + p.Pid));
            Skip = new ActionCommand(() => Choose(0)); Retry = new ActionCommand(() => Choose(1));
            NormalClose = new ActionCommand(() => Choose(2)); ForceClose = new ActionCommand(() => Choose(3));
        }
        public string Message { get; }
        public int Choice { get; private set; }
        public ActionCommand Skip { get; }
        public ActionCommand Retry { get; }
        public ActionCommand NormalClose { get; }
        public ActionCommand ForceClose { get; }
        public event Action CloseRequested;
        private void Choose(int value) { Choice = value; CloseRequested?.Invoke(); }
    }
}
