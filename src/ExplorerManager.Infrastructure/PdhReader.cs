using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ExplorerManager.Domain;

namespace ExplorerManager.Infrastructure
{
    public sealed class PdhReader : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] private struct Value { public uint Status; public double Number; }
        [StructLayout(LayoutKind.Sequential)] private struct ArrayItem { public IntPtr Name; public Value Value; }
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string source, IntPtr user, out IntPtr query);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
        [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
        [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out Value value);
        [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr items);
        [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
        private IntPtr _query;
        private bool _warmed;
        private readonly Dictionary<string, Tuple<IntPtr, string, double>> _counters = new Dictionary<string, Tuple<IntPtr, string, double>>();
        public PdhReader()
        {
            uint rc = PdhOpenQueryW(null, IntPtr.Zero, out _query);
            if (rc != 0) throw new InvalidOperationException("PDH 조회 실패: " + rc);
            Add("CPU", @"\Processor(_Total)\% Processor Time", "%", 1);
            Add("ReadBytes", @"\PhysicalDisk(*)\Disk Read Bytes/sec", "MiB/s", 1.0 / 1048576);
            Add("WriteBytes", @"\PhysicalDisk(*)\Disk Write Bytes/sec", "MiB/s", 1.0 / 1048576);
            Add("Reads", @"\PhysicalDisk(*)\Disk Reads/sec", "IOPS", 1);
            Add("Writes", @"\PhysicalDisk(*)\Disk Writes/sec", "IOPS", 1);
            Add("ReadLatency", @"\PhysicalDisk(*)\Avg. Disk sec/Read", "ms", 1000);
            Add("WriteLatency", @"\PhysicalDisk(*)\Avg. Disk sec/Write", "ms", 1000);
            Add("Queue", @"\PhysicalDisk(*)\Current Disk Queue Length", "requests", 1);
            Add("Active", @"\PhysicalDisk(*)\% Idle Time", "%", -1);
        }
        private void Add(string name, string path, string unit, double scale)
        {
            IntPtr counter;
            if (PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out counter) == 0) _counters.Add(name, Tuple.Create(counter, unit, scale));
        }
        public IList<Metric> Read()
        {
            var result = new List<Metric>();
            if (PdhCollectQueryData(_query) != 0) { _warmed = false; return result; }
            foreach (var counter in _counters)
            {
                if (counter.Key == "CPU")
                {
                    Value value; uint type;
                    uint code = PdhGetFormattedCounterValue(counter.Value.Item1, 0x200, out type, out value);
                    bool valid = _warmed && code == 0 && (value.Status == 0 || value.Status == 1) && !double.IsNaN(value.Number) && !double.IsInfinity(value.Number);
                    result.Add(new Metric { Entity = "CPU", Name = "CPU", Unit = "%", Value = valid ? (double?)value.Number : null,
                        Quality = valid ? "Valid" : "WarmingUp", Provider = "PDH" });
                    continue;
                }
                uint bytes = 0, count;
                PdhGetFormattedCounterArrayW(counter.Value.Item1, 0x200, ref bytes, out count, IntPtr.Zero);
                if (bytes == 0 || bytes > 4 * 1024 * 1024) continue;
                IntPtr buffer = Marshal.AllocHGlobal((int)bytes);
                try
                {
                    if (PdhGetFormattedCounterArrayW(counter.Value.Item1, 0x200, ref bytes, out count, buffer) != 0) continue;
                    int stride = Marshal.SizeOf(typeof(ArrayItem));
                    if ((long)stride * count > bytes) continue;
                    for (int i = 0; i < count; i++)
                    {
                        var item = (ArrayItem)Marshal.PtrToStructure(IntPtr.Add(buffer, i * stride), typeof(ArrayItem));
                        string instance = Marshal.PtrToStringUni(item.Name);
                        if (counter.Key != "CPU" && instance == "_Total") continue;
                        bool valid = _warmed && (item.Value.Status == 0 || item.Value.Status == 1) && !double.IsNaN(item.Value.Number) && !double.IsInfinity(item.Value.Number);
                        double number = counter.Key == "Active" ? Math.Max(0, Math.Min(100, 100 - item.Value.Number)) : item.Value.Number * counter.Value.Item3;
                        result.Add(new Metric { Entity = counter.Key == "CPU" ? "CPU" : "Disk" + instance.Split(' ')[0], Name = counter.Key, Value = valid ? (double?)number : null,
                            Unit = counter.Value.Item2, Quality = valid ? "Valid" : "WarmingUp", Provider = "PDH", Detail = instance });
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            _warmed = true; return result;
        }
        public void Dispose() { if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; } }
    }
}
