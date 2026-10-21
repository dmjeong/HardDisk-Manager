using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Threading;
using System.ServiceProcess;
using System.Text;
using ExplorerManager.Domain;
using LibreHardwareMonitor.Hardware;
using Newtonsoft.Json;
using ExplorerManager.Contracts;

namespace ExplorerManager.SensorHost
{
    internal static class Program
    {
        private static int Main(string[] args)
        {
            if (args.Contains("--service")) { ServiceBase.Run(new HardwareSensorService()); return 0; }
            Console.OutputEncoding = new UTF8Encoding(false);
            var computer = new Computer { IsCpuEnabled = true, IsStorageEnabled = true };
            try
            {
                computer.Open();
                while (true)
                {
                    var metrics = new List<Metric>();
                    foreach (var hardware in computer.Hardware) Read(hardware, metrics);
                    if (!metrics.Any(m => m.Entity == "CPU" && m.Name == "Temperature"))
                        metrics.Add(new Metric { Entity = "CPU", Name = "Temperature", Unit = "°C", Quality = "Unsupported", Provider = "LibreHardwareMonitor", Detail = "센서 미지원, 드라이버 또는 권한 확인 필요" });
                    try
                    {
                        using (var search = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_StorageReliabilityCounter"))
                        using (var data = search.Get())
                            foreach (ManagementObject device in data)
                            {
                                using (device)
                                    foreach (string field in new[] { "Temperature", "ReadErrorsUncorrected", "WriteErrorsUncorrected", "ReadErrorsTotal", "WriteErrorsTotal", "Wear" })
                                    {
                                        object value = device[field];
                                        if (value != null) metrics.Add(new Metric { Entity = "Disk" + device["DeviceId"], Name = field, Value = Convert.ToDouble(value),
                                            Unit = field == "Temperature" ? "°C" : field == "Wear" ? "%" : "count", Provider = "StorageReliabilityCounter" });
                                    }
                            }
                    }
                    catch (Exception ex) { metrics.Add(new Metric { Entity = "Storage", Name = "Health", Quality = "Unsupported", Provider = "WMI", Detail = ex.Message }); }
                    Console.WriteLine(JsonConvert.SerializeObject(metrics, Protocol.Json)); Console.Out.Flush();
                    Thread.Sleep(5000);
                }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
            finally { computer.Close(); }
        }
        internal static void Read(IHardware hardware, List<Metric> metrics)
        {
            hardware.Update();
            foreach (var sensor in hardware.Sensors.Where(s => s.SensorType == SensorType.Temperature && s.Value.HasValue))
                metrics.Add(new Metric { Entity = hardware.HardwareType == HardwareType.Cpu ? "CPU" : hardware.Name, Name = "Temperature",
                    Value = sensor.Value, Unit = "°C", Provider = "LibreHardwareMonitor", Detail = sensor.Name });
            foreach (var child in hardware.SubHardware) Read(child, metrics);
        }
    }
}
