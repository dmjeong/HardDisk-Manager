using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Management;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Contracts;
using ExplorerManager.Domain;
using LibreHardwareMonitor.Hardware;
using Newtonsoft.Json;

namespace ExplorerManager.SensorHost
{
    // Read-only local hardware boundary. No path, PID, or command is accepted from a client.
    internal sealed class HardwareSensorService : ServiceBase
    {
        private readonly object _sync = new object();
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private List<Metric> _latest = new List<Metric>();
        private DateTime _lastSample = DateTime.UtcNow;
        private Timer _watchdog;
        private Task _collector, _server;
        public HardwareSensorService() { ServiceName = "ExplorerManagerSensors"; CanShutdown = true; }
        protected override void OnStart(string[] args)
        {
            _lastSample = DateTime.UtcNow;
            _collector = Task.Run(Collect);
            _server = ServeAsync(_stop.Token);
            _watchdog = new Timer(s =>
            {
                if (DateTime.UtcNow - _lastSample > TimeSpan.FromSeconds(45))
                    Environment.FailFast("Hardware sensor provider stopped responding; SCM will restart this isolated host.");
            }, null, 10000, 10000);
        }
        private void Collect()
        {
            var computer = new Computer { IsCpuEnabled = true, IsStorageEnabled = true };
            try
            {
                computer.Open();
                while (!_stop.IsCancellationRequested)
                {
                    var result = new List<Metric>();
                    try
                    {
                        foreach (var h in computer.Hardware) Program.Read(h, result);
                        if (!result.Any(x => x.Entity == "CPU"))
                            result.Add(new Metric { Entity = "CPU", Name = "Temperature", Unit = "°C", Quality = "Unsupported", Provider = "LibreHardwareMonitor", Detail = "장치 또는 드라이버 미지원" });
                        using (var query = new ManagementObjectSearcher(@"root\Microsoft\Windows\Storage", "SELECT * FROM MSFT_StorageReliabilityCounter"))
                        using (var devices = query.Get())
                            foreach (ManagementObject device in devices)
                                using (device)
                                    foreach (string field in new[] { "Temperature", "ReadErrorsUncorrected", "WriteErrorsUncorrected", "Wear" })
                                    {
                                        object value = device[field]; if (value == null) continue;
                                        result.Add(new Metric { Entity = "Disk" + device["DeviceId"], Name = field, Value = Convert.ToDouble(value),
                                            Unit = field == "Temperature" ? "°C" : field == "Wear" ? "%" : "count", Provider = "StorageReliabilityCounter" });
                                    }
                    }
                    catch (Exception ex) { result.Add(new Metric { Entity = "Storage", Name = "Health", Quality = "Error", Provider = "HardwareSensorService", Detail = ex.Message }); }
                    lock (_sync) _latest = result.Take(512).ToList();
                    _lastSample = DateTime.UtcNow;
                    _stop.Token.WaitHandle.WaitOne(5000);
                }
            }
            catch (Exception ex)
            {
                lock (_sync) _latest = new List<Metric> { new Metric { Entity = "CPU", Name = "Temperature", Quality = "Unsupported", Provider = "HardwareSensorService", Detail = ex.Message } };
                // The watchdog restarts only this read-only process, never the cleanup cache owner.
            }
            finally { computer.Close(); }
        }
        private async Task ServeAsync(CancellationToken ct)
        {
            var serviceSid = (SecurityIdentifier)new NTAccount("NT SERVICE", "ExplorerManager").Translate(typeof(SecurityIdentifier));
            while (!ct.IsCancellationRequested)
            {
                var acl = new PipeSecurity(); acl.SetAccessRuleProtection(true, false);
                acl.AddAccessRule(new PipeAccessRule(serviceSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
                using (var pipe = new NamedPipeServerStream("ExplorerManager.Sensors.v1", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536, acl))
                {
                    try
                    {
                        await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                        bool authorized = false;
                        pipe.RunAsClient(() =>
                        {
                            using (var identity = WindowsIdentity.GetCurrent(true))
                                authorized = identity != null && (identity.User == serviceSid || identity.User.IsWellKnown(WellKnownSidType.LocalSystemSid)) &&
                                    !new WindowsPrincipal(identity).IsInRole(new SecurityIdentifier(WellKnownSidType.NetworkSid, null));
                        });
                        if (!authorized) continue;
                        byte[] bytes;
                        lock (_sync) bytes = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(_latest, Protocol.Json));
                        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                        {
                            timeout.CancelAfter(3000);
                            using (timeout.Token.Register(() => pipe.Dispose()))
                            {
                                await pipe.WriteAsync(BitConverter.GetBytes(bytes.Length), 0, 4, timeout.Token).ConfigureAwait(false);
                                await pipe.WriteAsync(bytes, 0, bytes.Length, timeout.Token).ConfigureAwait(false);
                                await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
                            }
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
                    catch (Exception ex) { try { EventLog.WriteEntry(ex.Message); } catch { } }
                }
            }
        }
        protected override void OnStop()
        {
            _watchdog?.Dispose(); _stop.Cancel();
            if (!Task.WhenAll(_collector ?? Task.CompletedTask, _server ?? Task.CompletedTask).Wait(10000))
                Environment.Exit(0);
        }
        protected override void OnShutdown() { OnStop(); base.OnShutdown(); }
    }
}
