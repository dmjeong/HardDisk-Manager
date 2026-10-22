using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ExplorerManager.Domain
{
    public sealed class ResourceInterlocks
    {
        public const int MaximumSampleAgeSeconds = 20;
        private readonly object _sync = new object();
        private readonly Dictionary<string, Channel> _channels = new Dictionary<string, Channel>(StringComparer.Ordinal);
        private List<Metric> _lastSamples = new List<Metric>();
        private DateTime _lastUpdate;
        private sealed class Channel
        {
            public InterlockStatus Status;
            public DateTime? BadSince, SafeSince;
        }
        private sealed class Observation
        {
            public bool? Unsafe;
            public bool Unsupported;
            public string Detail;
        }

        public IList<InterlockStatus> Update(IEnumerable<Metric> samples, DateTime utcNow, AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            lock (_sync)
            {
                _lastSamples = (samples ?? Enumerable.Empty<Metric>()).Where(m => m != null).Select(CopyMetric).ToList();
                Evaluate(utcNow, settings);
                return Copies();
            }
        }
        public IList<InterlockStatus> Snapshot() { lock (_sync) return Copies(); }
        public void Restore(IEnumerable<InterlockStatus> statuses)
        {
            lock (_sync)
            {
                foreach (var saved in (statuses ?? Enumerable.Empty<InterlockStatus>()).Where(s => s != null && (s.Blocking || s.Latched)))
                {
                    if (string.IsNullOrWhiteSpace(saved.Code) || string.IsNullOrWhiteSpace(saved.Entity))
                        throw new ArgumentException("저장된 인터록 식별자가 잘못되었습니다.", nameof(statuses));
                    var channel = Get(saved.Code, saved.Entity, saved.SinceUtc);
                    channel.Status = new InterlockStatus { Code = saved.Code, Entity = saved.Entity, State = "Unknown",
                        Blocking = true, Latched = true, SinceUtc = saved.SinceUtc,
                        Detail = "서비스 재시작 후 최신 안전 측정과 안정 회복을 기다립니다. " + saved.Detail };
                    channel.BadSince = null; channel.SafeSince = null;
                }
                _lastSamples.Clear(); _lastUpdate = default(DateTime);
            }
        }
        public bool Reset(DateTime utcNow, AppSettings settings)
        {
            if (settings == null) throw new ArgumentNullException(nameof(settings));
            lock (_sync)
            {
                Evaluate(utcNow, settings, true);
                if (_channels.Values.Any(c => c.Status.State == "Pending" || c.Status.State == "Tripped" || c.Status.State == "Recovering" ||
                    (c.Status.Blocking && c.Status.State != "AwaitingReset"))) return false;
                foreach (var channel in _channels.Values.Where(c => c.Status.Blocking)) Clear(channel, utcNow, "안전 상태 유지 후 운영자 리셋 완료");
                return true;
            }
        }

        private void Evaluate(DateTime now, AppSettings settings, bool holdRecovered = false)
        {
            // A backward clock or a gap in observations cannot count toward trip or recovery time.
            if (_lastUpdate != default(DateTime) && (now < _lastUpdate || now - _lastUpdate > TimeSpan.FromSeconds(MaximumSampleAgeSeconds)))
                foreach (var channel in _channels.Values)
                {
                    channel.BadSince = null; channel.SafeSince = null;
                    if (channel.Status.SinceUtc > now) channel.Status.SinceUtc = now;
                }
            _lastUpdate = now;
            Get("LowMemory", "RAM", now); Get("CpuLoad", "CPU", now); Get("CpuTemperature", "CPU", now);
            foreach (string entity in _lastSamples.Where(m => m.Entity != null && m.Entity.StartsWith("Disk", StringComparison.Ordinal)).Select(m => m.Entity).Distinct())
            { Get("DiskTemperature", entity, now); Get("DiskReadLatency", entity, now); Get("DiskWriteLatency", entity, now); }
            foreach (var channel in _channels.Values)
            {
                var status = channel.Status;
                Observation observation;
                if (status.Code == "LowMemory")
                    observation = Observe("RAM", "AvailableRAM", now, value => value * 1073741824.0 < settings.MinimumAvailableRam,
                        "가용 RAM 기준 " + settings.MinimumAvailableRam.ToString(CultureInfo.InvariantCulture) + " bytes");
                else if (status.Code == "CpuLoad")
                    observation = Threshold("CPU", "CPU", now, settings.CpuWarningPercent, "%");
                else if (status.Code == "CpuTemperature")
                    observation = Threshold("CPU", "Temperature", now, settings.CpuTemperatureWarning, "°C");
                else if (status.Code == "DiskTemperature")
                    observation = Threshold(status.Entity, "Temperature", now, settings.DiskTemperatureWarning, "°C");
                else if (status.Code == "DiskReadLatency" || status.Code == "DiskWriteLatency")
                    observation = Latency(status.Entity, status.Code == "DiskReadLatency" ? "Read" : "Write", now, settings.DiskLatencyWarningMs);
                else observation = new Observation { Detail = "저장된 인터록 종류를 확인할 수 없습니다. 안전 상태 확인이 필요합니다." };
                Apply(channel, observation, now, settings, status.Code == "LowMemory", holdRecovered);
            }
        }

        private Channel Get(string code, string entity, DateTime now)
        {
            string key = code + "/" + entity; Channel result;
            if (!_channels.TryGetValue(key, out result))
            {
                result = new Channel { Status = new InterlockStatus { Code = code, Entity = entity, State = "Unknown", SinceUtc = now } };
                _channels.Add(key, result);
            }
            return result;
        }
        private Observation Threshold(string entity, string name, DateTime now, double threshold, string unit)
        {
            if (!Finite(threshold)) return new Observation { Detail = "유효하지 않은 인터록 기준값" };
            return Observe(entity, name, now, value => value > threshold,
                name + " 기준 " + threshold.ToString("0.##", CultureInfo.InvariantCulture) + " " + unit);
        }
        private Observation Observe(string entity, string name, DateTime now, Func<double, bool> unsafeValue, string detail)
        {
            var all = _lastSamples.Where(m => m.Entity == entity && m.Name == name).ToList();
            var valid = all.Where(m => Fresh(m, now)).ToList();
            var unsafeSample = valid.FirstOrDefault(m => unsafeValue(m.Value.Value));
            if (unsafeSample != null) return new Observation { Unsafe = true, Detail = unsafeSample.Display + " · " + detail };
            if (valid.Count > 0 && valid.Count == all.Count)
                return new Observation { Unsafe = false, Detail = valid[0].Display + " · " + detail };
            return new Observation { Unsupported = all.Count > 0 && all.All(m => m.Quality == "Unsupported"),
                Detail = "최신 정상 측정값을 확인할 수 없습니다. " + detail };
        }
        private Observation Latency(string entity, string direction, DateTime now, double threshold)
        {
            if (!Finite(threshold)) return new Observation { Detail = "유효하지 않은 지연 기준값" };
            var latency = _lastSamples.Where(m => m.Entity == entity && m.Name == direction + "Latency").ToList();
            var activity = _lastSamples.Where(m => m.Entity == entity && m.Name == direction + "s").ToList();
            if (latency.Count == 0 || activity.Count == 0 || !latency.All(m => Fresh(m, now)) || !activity.All(m => Fresh(m, now)))
                return new Observation { Detail = "디스크 지연 또는 I/O 활성 측정값이 없거나 오래되었습니다." };
            double highest = latency.Max(m => m.Value.Value);
            bool busy = activity.Any(m => m.Value > 0);
            return new Observation { Unsafe = busy && highest > threshold,
                Detail = direction + " 지연 " + highest.ToString("0.##", CultureInfo.InvariantCulture) + " ms / 기준 " +
                    threshold.ToString("0.##", CultureInfo.InvariantCulture) + " ms · " + (busy ? "I/O 활성" : "I/O 유휴") };
        }
        private static bool Fresh(Metric metric, DateTime now) => metric.Quality == "Valid" && metric.Value.HasValue &&
            Finite(metric.Value.Value) && metric.Value.Value >= 0 && metric.Utc != default(DateTime) &&
            metric.Utc <= now.AddSeconds(5) && now - metric.Utc <= TimeSpan.FromSeconds(MaximumSampleAgeSeconds);
        private static bool Finite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private static void Apply(Channel channel, Observation observation, DateTime now, AppSettings settings, bool immediate, bool holdRecovered)
        {
            channel.Status.Detail = observation.Detail;
            if (!observation.Unsafe.HasValue)
            {
                channel.BadSince = null; channel.SafeSince = null;
                State(channel, observation.Unsupported ? "Unsupported" : "Unknown", now);
                return;
            }
            if (observation.Unsafe.Value)
            {
                channel.SafeSince = null;
                if (!channel.BadSince.HasValue) channel.BadSince = now;
                if (channel.Status.Latched || immediate || now - channel.BadSince.Value >= TimeSpan.FromSeconds(Math.Max(0, settings.InterlockTripSeconds)))
                { channel.Status.Blocking = true; channel.Status.Latched = true; State(channel, "Tripped", now); }
                else State(channel, "Pending", now);
                return;
            }
            channel.BadSince = null;
            if (!channel.Status.Latched) { Clear(channel, now, observation.Detail); return; }
            if (!channel.SafeSince.HasValue) channel.SafeSince = now;
            if (now - channel.SafeSince.Value < TimeSpan.FromSeconds(Math.Max(0, settings.InterlockRecoverySeconds)))
            { State(channel, "Recovering", now); return; }
            if (settings.InterlockManualReset || holdRecovered) State(channel, "AwaitingReset", now);
            else Clear(channel, now, observation.Detail);
        }
        private static void State(Channel channel, string state, DateTime now)
        {
            if (channel.Status.State == state) return;
            channel.Status.State = state; channel.Status.SinceUtc = now;
        }
        private static void Clear(Channel channel, DateTime now, string detail)
        {
            channel.Status.Blocking = false; channel.Status.Latched = false;
            channel.Status.Detail = detail; channel.SafeSince = null;
            State(channel, "Clear", now);
        }
        private IList<InterlockStatus> Copies() => _channels.Values.Select(c => Copy(c.Status))
            .OrderByDescending(s => s.Blocking).ThenBy(s => s.Code, StringComparer.Ordinal).ThenBy(s => s.Entity, StringComparer.Ordinal).ToList();
        private static InterlockStatus Copy(InterlockStatus s) => new InterlockStatus { Code = s.Code, Entity = s.Entity,
            State = s.State, Blocking = s.Blocking, Latched = s.Latched, SinceUtc = s.SinceUtc, Detail = s.Detail };
        private static Metric CopyMetric(Metric m) => new Metric { Utc = m.Utc, Entity = m.Entity, Name = m.Name,
            Value = m.Value, Unit = m.Unit, Quality = m.Quality, Provider = m.Provider, Detail = m.Detail };
    }
}
