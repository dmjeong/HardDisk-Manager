using System;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ExplorerManager.Client
{
    internal static class EndpointVerifier
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct Status { public uint Type, State, Accepted, Win32Exit, ServiceExit, CheckPoint, WaitHint, Pid, Flags; }
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManagerW(string machine, string database, uint access);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenServiceW(IntPtr manager, string name, uint access);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(IntPtr service, int info, out Status status, int length, out int needed);
        [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
        internal static uint ServicePid()
        {
            IntPtr manager = OpenSCManagerW(null, null, 1);
            if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                IntPtr service = OpenServiceW(manager, "ExplorerManager", 4);
                if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                try
                {
                    Status status; int needed;
                    if (!QueryServiceStatusEx(service, 0, out status, Marshal.SizeOf(typeof(Status)), out needed)) throw new Win32Exception(Marshal.GetLastWin32Error());
                    if (status.State != 4 || status.Pid == 0) throw new InvalidOperationException("ExplorerManager 서비스가 실행 중이 아닙니다.");
                    return status.Pid;
                }
                finally { CloseServiceHandle(service); }
            }
            finally { CloseServiceHandle(manager); }
        }
        internal static void Verify(NamedPipeClientStream pipe, uint expected)
        {
            uint actual;
            if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out actual)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (actual != expected || ServicePid() != expected) throw new UnauthorizedAccessException("서비스와 Pipe 서버의 프로세스가 일치하지 않습니다.");
        }
    }
}
