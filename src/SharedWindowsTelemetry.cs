using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ExplorerManager.Domain;

namespace ExplorerManager.Platform
{
    // Shared native measurements require neither a performance counter installation nor a sensor driver.
    internal sealed class WindowsTelemetry
    {
        [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; public ulong Value => ((ulong)High << 32) | Low; }
        [StructLayout(LayoutKind.Sequential)] private struct Memory { public uint Length, Load; public ulong Total, Available, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, AvailableExtended; }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref Memory memory);
        private ulong _idle, _total;
        private bool _primed;
        public IList<Metric> Read()
        {
            var values = new List<Metric>(); FileTime idle, kernel, user;
            if (GetSystemTimes(out idle, out kernel, out user))
            {
                ulong total = kernel.Value + user.Value; double? cpu = null;
                if (_primed && total > _total && idle.Value >= _idle)
                { ulong elapsed = total - _total, resting = idle.Value - _idle; if (resting <= elapsed) cpu = (elapsed - resting) * 100.0 / elapsed; }
                values.Add(new Metric { Entity = "CPU", Name = "CPU", Value = cpu, Unit = "%", Quality = cpu.HasValue ? "Valid" : "WarmingUp", Provider = "GetSystemTimes" });
                _idle = idle.Value; _total = total; _primed = true;
            }
            else values.Add(new Metric { Entity = "CPU", Name = "CPU", Unit = "%", Quality = "Unavailable", Provider = "GetSystemTimes", Detail = "Windows CPU 조회 오류 " + Marshal.GetLastWin32Error() });
            var memory = new Memory { Length = (uint)Marshal.SizeOf(typeof(Memory)) };
            if (GlobalMemoryStatusEx(ref memory))
            {
                values.Add(new Metric { Entity = "RAM", Name = "AvailableRAM", Value = memory.Available / 1073741824.0, Unit = "GiB", Provider = "Windows" });
                values.Add(new Metric { Entity = "RAM", Name = "UsedRAM", Value = (memory.Total - memory.Available) / 1073741824.0, Unit = "GiB", Provider = "Windows" });
            }
            return values;
        }
    }
}
