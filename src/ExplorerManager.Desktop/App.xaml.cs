using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ExplorerManager.Client;
using ExplorerManager.Presentation;
using Forms = System.Windows.Forms;

namespace ExplorerManager.Desktop
{
    public partial class App : System.Windows.Application
    {
        private Forms.NotifyIcon _tray;
        private DispatcherTimer _timer;
        private bool _exit;
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var window = new MainWindow(); MainWindow = window;
            var ui = new UiAdapters(window, (title, message) => _tray?.ShowBalloonTip(4000, title, message, Forms.ToolTipIcon.Warning));
            var vm = new MainViewModel(new PipeClient(), ui); window.DataContext = vm;
            _tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Explorer Manager", Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("열기", null, (s, a) => Dispatcher.Invoke(() => { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); }));
            menu.Items.Add("화면 종료 (서비스 계속 실행)", null, (s, a) => Dispatcher.Invoke(() => { _exit = true; Shutdown(); }));
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, a) => Dispatcher.Invoke(() => { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); });
            window.Closing += (s, a) => { if (!_exit) { a.Cancel = true; window.Hide(); } };
            window.Show();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _timer.Tick += async (s, a) => await vm.RefreshAsync();
            _timer.Start();
            _ = vm.RefreshAsync();
            bool smoke = e.Args.Contains("--smoke");
            if (smoke)
            {
                int screenshotAt = Array.IndexOf(e.Args, "--screenshot");
                string outputDirectory = screenshotAt >= 0 && screenshotAt + 1 < e.Args.Length
                    ? Path.GetDirectoryName(Path.GetFullPath(e.Args[screenshotAt + 1])) : Environment.CurrentDirectory;
                var bindingOutput = new StringWriter();
                var listener = new TextWriterTraceListener(bindingOutput);
                PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
                PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
                var finish = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
                finish.Tick += async (s, a) =>
                {
                    finish.Stop();
                    try
                    {
                        if (!vm.Status.StartsWith("서비스 연결됨", StringComparison.Ordinal) && !vm.Status.StartsWith("신규 삭제", StringComparison.Ordinal))
                            throw new InvalidOperationException("WPF 서비스 연결 실패: " + vm.Status + " / " + vm.Message);
                        int at = Array.IndexOf(e.Args, "--screenshot");
                        if (at >= 0 && at + 1 < e.Args.Length)
                        {
                            string screenshot = Path.GetFullPath(e.Args[at + 1]);
                            for (int index = 0; index < window.Sections.Items.Count; index++)
                            {
                                window.Sections.SelectedIndex = index;
                                await Dispatcher.InvokeAsync(() => window.UpdateLayout(), DispatcherPriority.ContextIdle);
                                string path = index == 0 ? screenshot : Path.Combine(outputDirectory,
                                    Path.GetFileNameWithoutExtension(screenshot) + "-" + index + ".png");
                                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                                bitmap.Render(window);
                                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                                using (var file = File.Create(path)) encoder.Save(file);
                            }
                        }
                        listener.Flush(); string errors = bindingOutput.ToString();
                        if (!string.IsNullOrWhiteSpace(errors)) { File.WriteAllText(Path.Combine(outputDirectory, "wpf-binding-errors.log"), errors); _exit = true; Shutdown(2); return; }
                        _exit = true; Shutdown(0);
                    }
                    catch (Exception ex) { File.WriteAllText(Path.Combine(outputDirectory, "wpf-smoke-error.log"), ex.ToString()); _exit = true; Shutdown(3); }
                };
                finish.Start();
            }
            else if (e.Args.Contains("--tray")) window.Hide();
        }
        protected override void OnExit(ExitEventArgs e)
        {
            _timer?.Stop();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            base.OnExit(e);
        }
    }
}
