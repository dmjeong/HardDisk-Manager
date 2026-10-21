using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using ExplorerManager.Application;
using ExplorerManager.Domain;

namespace ExplorerManager.Infrastructure
{
    public sealed class RestartManagerInspector : ILockInspector
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct UniqueProcess { public int Pid; public System.Runtime.InteropServices.ComTypes.FILETIME Started; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessInfo
        {
            public UniqueProcess Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceName;
            public int Type; public uint Status, Session;
            [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
        }
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint session, int flags, StringBuilder key);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint session, uint files, string[] names, uint apps, IntPtr processes, uint services, IntPtr serviceNames);
        [DllImport("rstrtmgr.dll")] private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] ProcessInfo[] info, ref uint reboot);
        [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint session);
        public IList<ProcessIdentity> Inspect(string path)
        {
            var list = new List<ProcessIdentity>(); uint session;
            int result = RmStartSession(out session, 0, new StringBuilder(33));
            if (result != 0) return list;
            try
            {
                if (RmRegisterResources(session, 1, new[] { path }, 0, IntPtr.Zero, 0, IntPtr.Zero) != 0) return list;
                uint needed, count = 0, reboot = 0;
                result = RmGetList(session, out needed, ref count, null, ref reboot);
                for (int attempt = 0; result == 234 && attempt < 3 && needed <= 1024; attempt++)
                {
                    var info = new ProcessInfo[needed]; count = needed;
                    result = RmGetList(session, out needed, ref count, info, ref reboot);
                    if (result != 0) continue;
                    for (int i = 0; i < count; i++)
                    {
                        try
                        {
                            var identity = ProcessIdentityReader.Read(info[i].Process.Pid);
                            if (identity.StartTimeUtcTicks != NativeMethods.Time(info[i].Process.Started).Ticks) continue;
                            if (info[i].Type == 3 || info[i].Type == 1000 || !string.IsNullOrEmpty(info[i].ServiceName))
                            { identity.CanTerminate = false; identity.Reason = "서비스 또는 필수 프로세스"; }
                            list.Add(identity);
                        }
                        catch (Exception ex) when (ex is Win32Exception || ex is InvalidOperationException || ex is ArgumentException) { }
                    }
                }
            }
            finally { RmEndSession(session); }
            return list;
        }
    }
    public static class ProcessIdentityReader
    {
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int id);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr token);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr h, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageNameW(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ProcessIdToSessionId(int pid, out int session);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool IsProcessCritical(IntPtr process, out bool critical);
        public static ProcessIdentity Read(int pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                long started, exited, kernel, user; int session, length = 32768; bool critical;
                NativeMethods.Check(GetProcessTimes(h, out started, out exited, out kernel, out user));
                var path = new StringBuilder(length);
                NativeMethods.Check(QueryFullProcessImageNameW(h, 0, path, ref length));
                NativeMethods.Check(ProcessIdToSessionId(pid, out session));
                NativeMethods.Check(IsProcessCritical(h, out critical));
                IntPtr token;
                NativeMethods.Check(OpenProcessToken(h, 8, out token));
                string sid;
                try { using (var identity = new WindowsIdentity(token)) sid = identity.User.Value; }
                finally { CloseHandle(token); }
                string name = Path.GetFileName(path.ToString());
                bool protectedProcess = critical || session == 0 || pid == Process.GetCurrentProcess().Id ||
                    name.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) || name.StartsWith("ExplorerManager.", StringComparison.OrdinalIgnoreCase);
                return new ProcessIdentity { Pid = pid, StartTimeUtcTicks = DateTime.FromFileTimeUtc(started).Ticks, SessionId = session, OwnerSid = sid,
                    Executable = path.ToString(), Name = name, CanTerminate = !protectedProcess, Reason = protectedProcess ? "보호 프로세스" : "" };
            }
            finally { CloseHandle(h); }
        }
    }
}
