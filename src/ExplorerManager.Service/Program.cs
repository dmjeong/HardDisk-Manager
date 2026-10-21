using System;
using System.IO;
using System.Linq;
using System.ServiceProcess;
using System.Threading;
using ExplorerManager.Infrastructure;

namespace ExplorerManager.Service
{
    internal static class Program
    {
        internal static string DataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ExplorerManager", "Data");
        private static void Main(string[] args)
        {
            int at = Array.IndexOf(args, "--data-dir"); if (at >= 0 && at + 1 < args.Length) DataPath = Path.GetFullPath(args[at + 1]);
            if (args.Contains("--console"))
            {
                using (var stopped = new ManualResetEventSlim())
                using (var runtime = new ServiceRuntime(DataPath))
                {
                    Console.CancelKeyPress += (s, e) => { e.Cancel = true; stopped.Set(); };
                    runtime.Start(); Console.WriteLine("Explorer Manager service console. Ctrl+C to stop."); stopped.Wait();
                    runtime.StopAsync().GetAwaiter().GetResult();
                }
            }
            else ServiceBase.Run(new ManagerService());
        }
    }
    internal sealed class ManagerService : ServiceBase
    {
        private ServiceRuntime _runtime;
        public ManagerService() { ServiceName = "ExplorerManager"; CanStop = true; CanShutdown = true; AutoLog = true; }
        protected override void OnStart(string[] args) { _runtime = new ServiceRuntime(Program.DataPath); _runtime.Start(); }
        protected override void OnStop()
        {
            if (_runtime == null) return;
            RequestAdditionalTime(30000);
            if (_runtime.StopAsync().Wait(TimeSpan.FromSeconds(25))) { _runtime.Dispose(); _runtime = null; }
            else throw new System.TimeoutException("파일 작업 종료를 확인하지 못했습니다. 서비스 프로세스의 감사 저널을 점검하세요.");
        }
        protected override void OnShutdown() { OnStop(); base.OnShutdown(); }
    }
}
