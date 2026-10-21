using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Contracts;
using ExplorerManager.Domain;

namespace ExplorerManager.Client
{
    internal static class ProcessTermination
    {
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr h, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessCritical(IntPtr h, out bool critical);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(int pid, out int session);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(IntPtr h, uint code);
        [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr h, uint timeout);
        public static Task<string> ExecuteAsync(UnlockTicket ticket, bool force, CancellationToken ct) => Task.Run(() =>
        {
            string sid = WindowsIdentity.GetCurrent().User.Value;
            int currentSession = Process.GetCurrentProcess().SessionId;
            if (ticket.ExpiresUtc <= DateTime.UtcNow || ticket.OperatorSid != sid || ticket.SessionId != currentSession)
                throw new InvalidOperationException("승인 티켓이 만료되었거나 세션이 변경되었습니다.");
            foreach (var expected in ticket.Processes)
            {
                ct.ThrowIfCancellationRequested();
                if (!expected.CanTerminate || expected.OwnerSid != sid || expected.SessionId != currentSession) throw new InvalidOperationException("종료할 수 없는 대상입니다.");
                IntPtr h = OpenProcess(0x1000 | 0x100000 | (force ? 1u : 0u), false, expected.Pid);
                if (h == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    long created, exit, kernel, user; bool critical; int session;
                    var path = new StringBuilder(32768); int len = path.Capacity; IntPtr token;
                    if (!GetProcessTimes(h, out created, out exit, out kernel, out user) || !QueryFullProcessImageNameW(h, 0, path, ref len) ||
                        !IsProcessCritical(h, out critical) || !ProcessIdToSessionId(expected.Pid, out session) || !OpenProcessToken(h, 8, out token))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    string owner;
                    try { using (var identity = new WindowsIdentity(token)) owner = identity.User.Value; } finally { CloseHandle(token); }
                    if (DateTime.FromFileTimeUtc(created).Ticks != expected.StartTimeUtcTicks || owner != sid || session != currentSession || critical ||
                        !string.Equals(path.ToString(), expected.Executable, StringComparison.OrdinalIgnoreCase) || expected.Pid == Process.GetCurrentProcess().Id)
                        throw new InvalidOperationException("프로세스 식별자가 변경되었습니다.");
                    if (force) { if (!TerminateProcess(h, 1)) throw new Win32Exception(Marshal.GetLastWin32Error()); }
                    else
                    {
                        using (var p = Process.GetProcessById(expected.Pid))
                        {
                            if (p.StartTime.ToUniversalTime().Ticks != expected.StartTimeUtcTicks || !p.CloseMainWindow())
                                throw new InvalidOperationException("정상 종료 요청을 보낼 수 없습니다. 자동 강제 종료는 수행하지 않습니다.");
                        }
                    }
                    if (WaitForSingleObject(h, 5000) != 0) throw new InvalidOperationException("종료를 확인하지 못했습니다. 파일을 유지합니다.");
                }
                finally { CloseHandle(h); }
            }
            return force ? "사용자 승인 강제 종료 완료" : "사용자 승인 정상 종료 완료";
        }, ct);
    }
}
