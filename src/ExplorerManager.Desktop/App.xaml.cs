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
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            if (e.Args.Contains("--smoke"))
            {
                int logAt = Array.IndexOf(e.Args, "--screenshot");
                string logDirectory = logAt >= 0 && logAt + 1 < e.Args.Length
                    ? Path.GetDirectoryName(Path.GetFullPath(e.Args[logAt + 1])) : Environment.CurrentDirectory;
                DispatcherUnhandledException += (sender, args) =>
                    File.WriteAllText(Path.Combine(logDirectory, "wpf-startup-error.log"), args.Exception.ToString());
            }
            var window = new MainWindow(); MainWindow = window;
            var ui = new UiAdapters(window, (title, message) => _tray?.ShowBalloonTip(4000, title, message, Forms.ToolTipIcon.Warning));
            var vm = new MainViewModel(new PipeClient(), ui); window.DataContext = vm;
            _tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Explorer Manager", Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("열기", null, (s, a) => Dispatcher.Invoke(() => { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); }));
            menu.Items.Add("Exit · 화면 종료", null, (s, a) => Dispatcher.Invoke(() => Shutdown()));
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += (s, a) => Dispatcher.Invoke(() => { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); });
            window.Show();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
            _timer.Tick += async (s, a) => await vm.RefreshAsync();
            _timer.Start();
            _ = vm.RefreshAsync();
            if (e.Args.Contains("--offline-smoke") || e.Args.Contains("--workflow-smoke"))
            {
                int output = Array.IndexOf(e.Args, "--test-output"), workflow = Array.IndexOf(e.Args, "--workflow-smoke");
                if (output < 0 || output + 1 >= e.Args.Length) { Shutdown(11); return; }
                _ = DesktopWorkflowCheck.RunAsync(window, vm, e.Args.Contains("--offline-smoke"), workflow >= 0 && workflow + 1 < e.Args.Length ? e.Args[workflow + 1] : null, Path.GetFullPath(e.Args[output + 1]));
                return;
            }
            if (e.Args.Contains("--setup-service")) vm.SetupServiceCommand.Execute(null);
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
                            await CaptureSectionsAsync(window, screenshot, 1440, 920);
                            window.Width = window.MinWidth; window.Height = window.MinHeight;
                            await CaptureSectionsAsync(window, Path.Combine(outputDirectory, "compact.png"), 1120, 720);
                            window.Width = 1440; window.Height = 920;
                            if (e.Args.Contains("--ui-preview"))
                            {
                                _timer.Stop();
                                window.DataContext = await UiPreviewClient.CreateAsync(ui);
                                await CaptureSectionsAsync(window, Path.Combine(outputDirectory, "preview.png"), 1440, 920);
                                window.Width = window.MinWidth; window.Height = window.MinHeight;
                                await CaptureSectionsAsync(window, Path.Combine(outputDirectory, "preview-compact.png"), 1120, 720);
                                var alert = new ExplorerManager.Domain.AlertRecord { Message = "예시: 다른 앱에서 파일을 사용하고 있어 건너뛰었습니다.", Path = @"D:\Media\Archive\capture-2026-08.mp4" };
                                var dialog = new LockAlertWindow { Owner = window, DataContext = new LockAlertViewModel(alert) };
                                dialog.Show();
                                await Dispatcher.InvokeAsync(() => dialog.UpdateLayout(), DispatcherPriority.ContextIdle);
                                Capture(dialog, Path.Combine(outputDirectory, "locked-file.png")); dialog.Close();
                            }
                        }
                        listener.Flush(); string errors = bindingOutput.ToString();
                        if (!string.IsNullOrWhiteSpace(errors)) { File.WriteAllText(Path.Combine(outputDirectory, "wpf-binding-errors.log"), errors); Shutdown(2); return; }
                        Shutdown(0);
                    }
                    catch (Exception ex) { File.WriteAllText(Path.Combine(outputDirectory, "wpf-smoke-error.log"), ex.ToString()); Shutdown(3); }
                };
                finish.Start();
            }
            else if (e.Args.Contains("--tray")) window.Hide();
        }
        private async System.Threading.Tasks.Task CaptureSectionsAsync(MainWindow window, string screenshot, int width, int height)
        {
            // Detach the real view from the runner's constrained HWND while rendering.
            // A measured off-screen visual also paints the application's canvas background.
            var root = window.Sections;
            root.DataContext = window.DataContext;
            root.FontFamily = window.FontFamily; root.FontSize = window.FontSize;
            root.UseLayoutRounding = true;
            window.Content = null;
            root.Width = width; root.Height = height;
            var host = new System.Windows.Controls.Border
            {
                Width = width, Height = height, Background = (Brush)FindResource("CanvasBrush"), Child = root
            };
            try
            {
                for (int index = 0; index < root.Items.Count; index++)
                {
                    root.SelectedIndex = index;
                    var model = window.DataContext as MainViewModel;
                    if (index == 7 && model != null) { model.Transfers.IsActive = true; await model.Transfers.RefreshAsync(true); }
                    await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                    host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout();
                    if (Math.Abs(root.ActualWidth - width) > 1 || Math.Abs(root.ActualHeight - height) > 1)
                        throw new InvalidOperationException("WPF capture did not use the requested layout size.");
                    string path = index == 0 ? screenshot : Path.Combine(Path.GetDirectoryName(screenshot),
                        Path.GetFileNameWithoutExtension(screenshot) + "-" + index + ".png");
                    Capture(host, path);
                    if (index == 7 && model != null)
                    {
                        foreach (int panel in new[] { 1, 2, 3 })
                        {
                            model.Transfers.SelectedPanel = panel;
                            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
                            host.Measure(new Size(width, height)); host.Arrange(new Rect(0, 0, width, height)); host.UpdateLayout();
                            string suffix = panel == 1 ? "policies" : panel == 2 ? "connections" : "options";
                            Capture(host, Path.Combine(Path.GetDirectoryName(screenshot), Path.GetFileNameWithoutExtension(screenshot) + "-transfers-" + suffix + ".png"));
                        }
                        model.Transfers.SelectedPanel = 0;
                    }
                }
            }
            finally
            {
                host.Child = null;
                root.ClearValue(FrameworkElement.WidthProperty); root.ClearValue(FrameworkElement.HeightProperty);
                root.ClearValue(FrameworkElement.DataContextProperty);
                root.ClearValue(System.Windows.Controls.Control.FontFamilyProperty);
                root.ClearValue(System.Windows.Controls.Control.FontSizeProperty);
                root.ClearValue(FrameworkElement.UseLayoutRoundingProperty);
                window.Content = root;
            }
        }

        private static void Capture(FrameworkElement window, string path)
        {
            var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(window);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var file = File.Create(path)) encoder.Save(file);
        }
        protected override void OnExit(ExitEventArgs e)
        {
            _timer?.Stop();
            if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
            base.OnExit(e);
        }
    }
}
