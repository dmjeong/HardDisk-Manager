using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Application;

namespace ExplorerManager.Infrastructure
{
    public sealed class DiskWorkCoordinator : IFileWorkCoordinator
    {
        private readonly IVolumeCatalog _volumes;
        private readonly Func<long> _rate;
        private readonly Dictionary<string, string[]> _topology = new Dictionary<string, string[]>();
        private readonly string[] _fallbackKeys;
        private readonly object _sync = new object();
        private readonly Dictionary<string, SemaphoreSlim> _lanes = new Dictionary<string, SemaphoreSlim>();
        private readonly Dictionary<string, AsyncByteLimiter> _limits = new Dictionary<string, AsyncByteLimiter>();
        public DiskWorkCoordinator(IVolumeCatalog volumes, Func<long> rate)
        {
            _volumes = volumes; _rate = rate;
            foreach (var v in volumes.List())
                if (!string.IsNullOrWhiteSpace(v.PhysicalDisks) && v.PhysicalDisks != "UnknownTopology")
                    _topology[v.Id] = v.PhysicalDisks.Split(',').Select(x => "Disk" + x.Trim()).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
            _fallbackKeys = _topology.Values.SelectMany(x => x).Concat(new[] { "UnknownTopology" }).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }
        private string[] Keys(string volume)
        {
            string[] keys; return _topology.TryGetValue(volume, out keys) ? keys : _fallbackKeys;
        }
        public async Task<IDisposable> AcquireAsync(string volumeId, CancellationToken ct)
        {
            var acquired = new List<SemaphoreSlim>();
            try {
                foreach (string key in Keys(volumeId))
                {
                    SemaphoreSlim lane; lock (_sync) { if (!_lanes.TryGetValue(key, out lane)) _lanes[key] = lane = new SemaphoreSlim(1, 1); }
                    await lane.WaitAsync(ct).ConfigureAwait(false); acquired.Add(lane);
                }
                return new Permit(acquired);
            }
            catch { foreach (var lane in acquired.AsEnumerable().Reverse()) lane.Release(); throw; }
        }
        public async Task LimitReadAsync(string volumeId, int bytes, CancellationToken ct)
        {
            foreach (string key in Keys(volumeId))
            {
                AsyncByteLimiter limiter; lock (_sync) { if (!_limits.TryGetValue(key, out limiter)) _limits[key] = limiter = new AsyncByteLimiter(); }
                await limiter.WaitAsync(bytes, _rate(), ct).ConfigureAwait(false);
            }
        }
        private sealed class Permit : IDisposable
        {
            private List<SemaphoreSlim> _lanes; public Permit(List<SemaphoreSlim> lanes) { _lanes = lanes; }
            public void Dispose() { var lanes = Interlocked.Exchange(ref _lanes, null); if (lanes != null) foreach (var lane in lanes.AsEnumerable().Reverse()) lane.Release(); }
        }
    }
}
