using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ExplorerManager.Application;
using ExplorerManager.Domain;

namespace ExplorerManager.Infrastructure
{
    public sealed class WindowsVolumeCatalog : IVolumeCatalog
    {
        public IList<VolumeStatus> List()
        {
            var result = new List<VolumeStatus>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive.DriveType != DriveType.Fixed && drive.DriveType != DriveType.Removable) continue;
                try { result.Add(Read(drive.RootDirectory.FullName)); }
                catch (Exception ex) { result.Add(new VolumeStatus { Id = "unavailable:" + drive.Name, Mount = drive.Name, State = "Unavailable: " + ex.Message }); }
            }
            return result;
        }
        public VolumeStatus Get(string id)
        {
            foreach (var v in List()) if (string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase) && v.State == "Ready") return v;
            throw new IOException("등록된 볼륨이 없거나 미디어 식별자가 변경되었습니다: " + id);
        }
        public static VolumeStatus Read(string root)
        {
            var guid = new StringBuilder(128); var fs = new StringBuilder(64); var label = new StringBuilder(256);
            uint serial, max, flags; ulong available, total, free;
            NativeMethods.Check(NativeMethods.GetVolumeNameForVolumeMountPointW(root, guid, guid.Capacity));
            NativeMethods.Check(NativeMethods.GetVolumeInformationW(root, label, (uint)label.Capacity, out serial, out max, out flags, fs, (uint)fs.Capacity));
            NativeMethods.Check(NativeMethods.GetDiskFreeSpaceExW(root, out available, out total, out free));
            return new VolumeStatus { Id = guid + "|" + serial.ToString("x8"), Mount = root, Label = label.ToString(),
                FileSystem = fs.ToString(), TotalBytes = checked((long)total), FreeBytes = checked((long)free),
                AvailableBytes = checked((long)available), PhysicalDisks = DiskNumbers(root) };
        }
        private static string DiskNumbers(string root)
        {
            using (var h = NativeMethods.CreateFileW(@"\\.\" + root.TrimEnd('\\'), 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero))
            {
                if (h.IsInvalid) return "UnknownTopology";
                IntPtr buffer = Marshal.AllocHGlobal(4096);
                try
                {
                    int returned;
                    if (!NativeMethods.DeviceIoControl(h, 0x00560000, IntPtr.Zero, 0, buffer, 4096, out returned, IntPtr.Zero)) return "UnknownTopology";
                    int count = Marshal.ReadInt32(buffer);
                    if (count < 1 || count > 100 || returned < 8 + count * 24) return "UnknownTopology";
                    var numbers = new List<int>();
                    for (int i = 0; i < count; i++) numbers.Add(Marshal.ReadInt32(buffer, 8 + i * 24));
                    return string.Join(",", numbers);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
        }
    }
    public sealed class SystemClock : IClock { public DateTime UtcNow => DateTime.UtcNow; }
}
