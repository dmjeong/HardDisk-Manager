using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Domain;

namespace ExplorerManager.Contracts
{
    public sealed class DesktopStatus
    {
        public DateTime Utc { get; set; } = DateTime.UtcNow;
        public string ServiceState { get; set; }
        public string Detail { get; set; }
        public bool IsAdministrator { get; set; }
        public List<VolumeStatus> Volumes { get; set; } = new List<VolumeStatus>();
        public List<Metric> Metrics { get; set; } = new List<Metric>();
    }
    public interface IDesktopDiagnostics
    {
        Task<DesktopStatus> ReadDesktopStatusAsync(CancellationToken cancellation);
    }
}
