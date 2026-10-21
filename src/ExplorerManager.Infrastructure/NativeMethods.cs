using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ExplorerManager.Infrastructure
{
    internal static class NativeMethods
    {
        internal const uint ReadAttributes = 0x80, ReadControl = 0x20000, Delete = 0x10000;
        internal const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, WriteDac = 0x40000;
        internal const uint BackupSemantics = 0x02000000, OpenReparsePoint = 0x00200000;
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileInfo
        {
            public uint Attributes;
            public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileIdInfo
        {
            public ulong Serial;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)] public byte[] Id;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct StreamData
        {
            public long Size;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
        [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileId(SafeFileHandle handle, int type, out FileIdInfo info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int type, IntPtr info, uint size);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileInformationByHandle(SafeFileHandle handle, int type, IntPtr info, uint size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetVolumeNameForVolumeMountPointW(string root, StringBuilder name, int length);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetVolumeInformationW(string root, StringBuilder label, uint labelLength, out uint serial, out uint maxComponent, out uint flags, StringBuilder fs, uint fsLength);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetDiskFreeSpaceExW(string root, out ulong available, out ulong total, out ulong free);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern IntPtr FindFirstStreamW(string path, int level, out StreamData data, uint flags);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindNextStreamW(IntPtr find, out StreamData data);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool FindClose(IntPtr handle);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetKernelObjectSecurity(SafeFileHandle handle, uint requested, byte[] descriptor, uint length, out uint needed);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ConvertSecurityDescriptorToStringSecurityDescriptorW(byte[] descriptor, uint revision, uint info, out IntPtr text, out uint length);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text, uint revision, out IntPtr descriptor, out uint length);
        [DllImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetKernelObjectSecurity(SafeFileHandle handle, uint info, IntPtr descriptor);
        [DllImport("kernel32.dll")]
        internal static extern IntPtr LocalFree(IntPtr ptr);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetFileTime(SafeFileHandle handle, ref long creation, IntPtr access, ref long write);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateDirectoryW(string path, IntPtr security);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr input, int inputSize, IntPtr output, int outputSize, out int returned, IntPtr overlapped);
        internal static void Check(bool ok) { if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error()); }
        internal static string LongPath(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path : @"\\?\" + path;
        internal static string Plain(string path) => path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path.Substring(4) : path;
        internal static string FinalPath(SafeFileHandle h)
        {
            var b = new StringBuilder(32768); uint n = GetFinalPathNameByHandleW(h, b, (uint)b.Capacity, 0);
            if (n == 0 || n >= b.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error());
            return Plain(b.ToString()).TrimEnd('\\');
        }
        internal static string Id(SafeFileHandle h)
        {
            FileIdInfo f;
            Check(GetFileId(h, 18, out f, 24));
            return f.Serial.ToString("x16") + ":" + BitConverter.ToString(f.Id).Replace("-", "");
        }
        internal static string ReadDacl(SafeFileHandle h)
        {
            uint needed;
            GetKernelObjectSecurity(h, 4, null, 0, out needed);
            if (needed == 0 || needed > 1024 * 1024) throw new Win32Exception(Marshal.GetLastWin32Error());
            var bytes = new byte[needed];
            Check(GetKernelObjectSecurity(h, 4, bytes, needed, out needed));
            IntPtr text; uint length;
            Check(ConvertSecurityDescriptorToStringSecurityDescriptorW(bytes, 1, 4, out text, out length));
            try { return Marshal.PtrToStringUni(text); } finally { LocalFree(text); }
        }
        internal static void ApplyDacl(SafeFileHandle h, string dacl)
        {
            if (string.IsNullOrWhiteSpace(dacl)) throw new InvalidOperationException("복구 DACL이 없습니다.");
            IntPtr sd; uint length;
            Check(ConvertStringSecurityDescriptorToSecurityDescriptorW(dacl, 1, out sd, out length));
            try { Check(SetKernelObjectSecurity(h, 4, sd)); } finally { LocalFree(sd); }
        }
        internal static DateTime Time(System.Runtime.InteropServices.ComTypes.FILETIME f) =>
            DateTime.FromFileTimeUtc(((long)f.dwHighDateTime << 32) | (uint)f.dwLowDateTime);
    }
}
