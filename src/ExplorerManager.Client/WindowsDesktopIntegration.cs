using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Contracts;
using ExplorerManager.Domain;
using ExplorerManager.Platform;

namespace ExplorerManager.Client
{
    public sealed class WindowsDesktopIntegration : IDesktopDiagnostics
    {
        private readonly WindowsTelemetry _telemetry = new WindowsTelemetry();
        private readonly object _sync = new object();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetVolumeNameForVolumeMountPointW(string root, StringBuilder name, int length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetVolumeInformationW(string root, StringBuilder label, uint labelLength, out uint serial, out uint maximum, out uint flags, StringBuilder fileSystem, uint fileSystemLength);
        public static bool IsAdministrator { get { using (var identity = WindowsIdentity.GetCurrent()) return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); } }
        public Task<DesktopStatus> ReadDesktopStatusAsync(CancellationToken cancellation) => Task.Run(() =>
        {
            cancellation.ThrowIfCancellationRequested(); var state = new DesktopStatus { IsAdministrator = IsAdministrator };
            try { using (var service = new ServiceController("ExplorerManager")) { state.ServiceState = service.Status.ToString(); state.Detail = "Windows Service: " + state.ServiceState; } }
            catch (InvalidOperationException ex) { var win32 = ex.InnerException as Win32Exception; state.ServiceState = win32?.NativeErrorCode == 1060 ? "NotInstalled" : "Unavailable"; state.Detail = ex.InnerException?.Message ?? ex.Message; }
            foreach (var drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable))
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    if (!drive.IsReady) { state.Volumes.Add(new VolumeStatus { Id = "unavailable:" + drive.Name, Mount = drive.Name, State = "미디어 없음 / 잠김" }); continue; }
                    var name = new StringBuilder(128); var label = new StringBuilder(256); var fs = new StringBuilder(64); uint serial, maximum, flags;
                    if (!GetVolumeNameForVolumeMountPointW(drive.Name, name, name.Capacity) || !GetVolumeInformationW(drive.Name, label, (uint)label.Capacity, out serial, out maximum, out flags, fs, (uint)fs.Capacity)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    state.Volumes.Add(new VolumeStatus { Id = name + "|" + serial.ToString("x8"), Mount = drive.Name, Label = label.ToString(), FileSystem = fs.ToString(), TotalBytes = drive.TotalSize, FreeBytes = drive.TotalFreeSpace, AvailableBytes = drive.AvailableFreeSpace });
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is Win32Exception)
                { state.Volumes.Add(new VolumeStatus { Id = "unavailable:" + drive.Name, Mount = drive.Name, State = "Unavailable: " + ex.Message }); }
            }
            lock (_sync) state.Metrics.AddRange(_telemetry.Read());
            return state;
        }, cancellation);
        public static string FindInstaller(string startDirectory)
        {
            var folder = new DirectoryInfo(Path.GetFullPath(startDirectory));
            for (int depth = 0; folder != null && depth < 8; depth++, folder = folder.Parent)
                foreach (string root in new[] { Path.Combine(folder.FullName, "Setup"), folder.FullName })
                    if (File.Exists(Path.Combine(root, "Install.ps1")) && File.Exists(Path.Combine(root, "app", "Service", "ExplorerManager.Service.exe"))) return Path.Combine(root, "Install.ps1");
            throw new FileNotFoundException("설치 파일을 찾지 못했습니다. 전체 ZIP을 압축 해제하거나 솔루션을 빌드한 뒤 다시 실행하세요.");
        }
        public static async Task EnsureServiceAsync(string applicationDirectory)
        {
            if (!IsAdministrator) throw new UnauthorizedAccessException("서비스 설치·시작에는 관리자 권한이 필요합니다.");
            bool missing = false;
            using (var service = new ServiceController("ExplorerManager"))
            {
                try { var status = service.Status; }
                catch (InvalidOperationException ex) when ((ex.InnerException as Win32Exception)?.NativeErrorCode == 1060) { missing = true; }
                if (!missing)
                {
                    await Task.Run(() => {
                        if (service.Status == ServiceControllerStatus.StopPending) service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                        if (service.Status == ServiceControllerStatus.Stopped) service.Start();
                        service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    }).ConfigureAwait(false);
                    return; // Never reinstall a running service or discard its RAM recovery cache.
                }
            }
            await RunInstallerAsync(applicationDirectory, false).ConfigureAwait(false);
        }
        public static Task UpdateServiceAsync(string applicationDirectory) => RunInstallerAsync(applicationDirectory, true);
        private static async Task RunInstallerAsync(string applicationDirectory, bool update)
        {
            if (!IsAdministrator) throw new UnauthorizedAccessException("관리자 권한으로 Desktop을 실행하세요.");
            string installer = FindInstaller(applicationDirectory);
            string installedDirectory = null;
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\ExplorerManager"))
            {
                string path = key?.GetValue("ImagePath") as string;
                if (!string.IsNullOrWhiteSpace(path)) {
                    path = Environment.ExpandEnvironmentVariables(path).Trim();
                    if (path.StartsWith("\"", StringComparison.Ordinal)) { int end = path.IndexOf('"', 1); if (end > 1) path = path.Substring(1, end - 1); }
                    if (string.Equals(Path.GetFileName(path), "ExplorerManager.Service.exe", StringComparison.OrdinalIgnoreCase)) installedDirectory = Path.GetDirectoryName(Path.GetDirectoryName(path));
                }
            }
            bool replacingThisDesktop = update && installedDirectory != null && string.Equals(Path.GetFullPath(applicationDirectory).TrimEnd('\\'), Path.Combine(installedDirectory, "Desktop"), StringComparison.OrdinalIgnoreCase);
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoProfile -ExecutionPolicy Bypass -File \"" + installer + "\"" + (replacingThisDesktop ? "" : " -NoStartUi"))
                { UseShellExecute = replacingThisDesktop, WorkingDirectory = Path.GetDirectoryName(installer), CreateNoWindow = !replacingThisDesktop, RedirectStandardOutput = !replacingThisDesktop, RedirectStandardError = !replacingThisDesktop };
            using (var process = Process.Start(info))
            {
                var output = replacingThisDesktop ? Task.FromResult("") : process.StandardOutput.ReadToEndAsync(); var error = replacingThisDesktop ? Task.FromResult("") : process.StandardError.ReadToEndAsync();
                await Task.Run(() => process.WaitForExit()).ConfigureAwait(false);
                string detail = (await error.ConfigureAwait(false)).Trim(); await output.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidOperationException("서비스 설치/업데이트 실패: " + detail + "\n로그: " + Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"ExplorerManager\Logs"));
            }
        }
        public static async Task GrantFolderAccessAsync(string applicationDirectory, string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(new[] { '"', '\r', '\n', '\0' }) >= 0) throw new ArgumentException("관리할 폴더 경로를 확인하세요.");
            string script = null; var folder = new DirectoryInfo(Path.GetFullPath(applicationDirectory));
            for (int depth = 0; folder != null && depth < 8 && script == null; depth++, folder = folder.Parent)
                foreach (string candidate in new[] { Path.Combine(folder.FullName, "Setup", "Grant-ManagedRoot.ps1"), Path.Combine(folder.FullName, "Grant-ManagedRoot.ps1") })
                    if (File.Exists(candidate)) { script = candidate; break; }
            if (script == null) throw new FileNotFoundException("권한 설정 스크립트를 찾을 수 없습니다. 최신 전체 패키지로 설치하세요.");
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"), "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -Path \"" + path.TrimEnd('\\') + "\"") { UseShellExecute = true, Verb = "runas" };
            using (var process = Process.Start(info)) { await Task.Run(() => process.WaitForExit()).ConfigureAwait(false); if (process.ExitCode != 0) throw new InvalidOperationException("폴더 권한 설정에 실패했습니다. 보호 경로·junction·관리자 권한을 확인하세요."); }
        }
    }
    public static class DesktopDiagnosticsLog
    {
        private static readonly object Sync = new object();
        public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"ExplorerManager\Logs");
        public static void Write(string category, string detail)
        {
            lock (Sync) try {
                Directory.CreateDirectory(DirectoryPath); string path = Path.Combine(DirectoryPath, "desktop.log");
                if (File.Exists(path) && new FileInfo(path).Length > 2 * 1024 * 1024) { string old = path + ".previous"; if (File.Exists(old)) File.Delete(old); File.Move(path, old); }
                File.AppendAllText(path, DateTime.UtcNow.ToString("O") + " [" + category + "] " + detail + Environment.NewLine, Encoding.UTF8);
            } catch (Exception) { /* Diagnostics must never prevent the UI from starting or closing. */ }
        }
    }
}
