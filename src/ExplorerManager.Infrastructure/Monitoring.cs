using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Domain;
using Newtonsoft.Json;
using ExplorerManager.Contracts;
#if NETFRAMEWORK
using System.Diagnostics.Eventing.Reader;
#endif

namespace ExplorerManager.Infrastructure
{
    public sealed class Monitoring : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] private struct Memory { public uint Length, Load; public ulong Total, Available, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, AvailableExtended; }
        [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GlobalMemoryStatusEx(ref Memory memory);
        private readonly SqliteJournal _store;
        private readonly Action<Exception> _error;
        private readonly object _sync = new object();
        private readonly Dictionary<string, SustainedRule> _rules = new Dictionary<string, SustainedRule>();
        private readonly Dictionary<string, double> _previousErrors = new Dictionary<string, double>();
        private readonly Dictionary<string, Tuple<double, int>> _baselines = new Dictionary<string, Tuple<double, int>>();
        private List<Metric> _latest = new List<Metric>(), _sensorMetrics = new List<Metric>();
        private Process _sensor;
        private DateTime _sensorUtc;
        private bool _loadPaused;
        private DateTime? _loadSince, _recoverySince;
#if NETFRAMEWORK
        private EventLogWatcher _events;
#endif
        public Monitoring(SqliteJournal store, Action<Exception> error) { _store = store; _error = error; }
        public IList<Metric> Snapshot() { lock (_sync) return _latest.ToList(); }
        public string LoadStopReason => _loadPaused ? "PausedByLoad" : null;
        public async Task RunAsync(CancellationToken ct)
        {
            var sensorTask = ReadSensorsAsync(ct);
            StartEvents();
            try
            {
                using (var pdh = new PdhReader())
                {
                    var monotonic = Stopwatch.StartNew(); var origin = DateTime.UtcNow;
                    int tick = 0;
                    while (!ct.IsCancellationRequested)
                    {
                        var samples = pdh.Read().ToList();
                        var memory = new Memory { Length = (uint)Marshal.SizeOf(typeof(Memory)) };
                        if (GlobalMemoryStatusEx(ref memory))
                        {
                            samples.Add(new Metric { Entity = "RAM", Name = "AvailableRAM", Value = memory.Available / 1073741824.0, Unit = "GiB", Provider = "Windows" });
                            samples.Add(new Metric { Entity = "RAM", Name = "UsedRAM", Value = (memory.Total - memory.Available) / 1073741824.0, Unit = "GiB", Provider = "Windows" });
                        }
                        lock (_sync)
                        {
                            if ((DateTime.UtcNow - _sensorUtc).TotalSeconds < 20) samples.AddRange(_sensorMetrics);
                            else samples.Add(new Metric { Entity = "CPU", Name = "Temperature", Quality = "Stale", Unit = "°C", Provider = "SensorHost", Detail = "센서 호스트 응답 대기/권한/장치 미지원" });
                        }
                        Evaluate(samples, origin + monotonic.Elapsed, _store.Settings());
                        lock (_sync) _latest = samples;
                        if (++tick % 5 == 0) _store.SaveMetrics(samples);
                        await Task.Delay(2000, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally { StopSensor(); try { await sensorTask.ConfigureAwait(false); } catch (OperationCanceledException) { } }
        }
        private void Evaluate(List<Metric> samples, DateTime now, AppSettings settings)
        {
            foreach (var queue in samples.Where(m => m.Name == "Queue" && m.Quality == "Valid").ToList())
            {
                var io = samples.Where(m => m.Entity == queue.Entity && (m.Name == "ReadBytes" || m.Name == "WriteBytes") && m.Quality == "Valid").ToList();
                if (io.Count == 2) samples.Add(new Metric { Entity = queue.Entity, Name = "StalledIO",
                    Value = queue.Value > 0 && io.All(m => m.Value == 0) ? 1 : 0, Unit = "flag", Provider = "DerivedPDH", Detail = "미완료 큐가 있으나 읽기/쓰기 완료 처리량이 0" });
            }
            var cpu = samples.FirstOrDefault(m => m.Name == "CPU" && m.Quality == "Valid")?.Value;
            var ram = samples.FirstOrDefault(m => m.Name == "AvailableRAM" && m.Quality == "Valid")?.Value;
            var cpuTemp = samples.Where(m => m.Entity == "CPU" && m.Name == "Temperature" && m.Quality == "Valid" && m.Value.HasValue).Select(m => m.Value.Value).DefaultIfEmpty(0).Max();
            bool overloaded = cpu > settings.CpuWarningPercent || (ram.HasValue && ram * 1073741824 < settings.MinimumAvailableRam) || cpuTemp > settings.CpuTemperatureWarning;
            if (overloaded) { _recoverySince = null; if (!_loadSince.HasValue) _loadSince = now; if (now - _loadSince.Value > TimeSpan.FromSeconds(settings.AnomalyDurationSeconds)) _loadPaused = true; }
            else { _loadSince = null; if (!_recoverySince.HasValue) _recoverySince = now; if (now - _recoverySince.Value > TimeSpan.FromSeconds(60)) _loadPaused = false; }
            foreach (var metric in samples.Where(m => m.Quality == "Valid" && m.Value.HasValue))
            {
                bool bad = false; string severity = "Warning";
                if (metric.Name == "StalledIO") { bad = metric.Value > 0; severity = "Critical"; }
                else if (metric.Name == "ReadLatency" || metric.Name == "WriteLatency")
                {
                    string activity = metric.Name == "ReadLatency" ? "Reads" : "Writes";
                    bool io = samples.Any(m => m.Entity == metric.Entity && m.Name == activity && m.Value > 0 && m.Quality == "Valid");
                    bad = io && metric.Value > settings.DiskLatencyWarningMs;
                    if (metric.Value > 500) severity = "Critical";
                }
                else if (metric.Name == "Temperature")
                { bad = metric.Value > (metric.Entity == "CPU" ? settings.CpuTemperatureWarning : settings.DiskTemperatureWarning); if (metric.Value > (metric.Entity == "CPU" ? settings.CpuTemperatureWarning + 10 : settings.DiskTemperatureWarning + 5)) severity = "Critical"; }
                else if (metric.Name == "ReadErrorsUncorrected" || metric.Name == "WriteErrorsUncorrected")
                {
                    string key = metric.Entity + metric.Name; double previous;
                    if (_previousErrors.TryGetValue(key, out previous) && metric.Value.Value > previous)
                        _store.SaveAlert(new AlertRecord { Utc = DateTime.UtcNow, Category = "DiskHealth", Severity = "Critical", Entity = metric.Entity, Message = metric.Name + " 증가: " + previous + " → " + metric.Value });
                    _previousErrors[key] = metric.Value.Value; continue;
                }
                else if (metric.Name == "ReadBytes" || metric.Name == "WriteBytes")
                {
                    string key = metric.Entity + metric.Name; Tuple<double, int> baseline;
                    bool busy = samples.Any(m => m.Entity == metric.Entity && m.Name == (metric.Name == "ReadBytes" ? "Reads" : "Writes") && m.Value > 0);
                    if (busy && metric.Value > 0)
                    {
                        if (!_baselines.TryGetValue(key, out baseline)) baseline = Tuple.Create(metric.Value.Value, 0);
                        bool delayed = samples.Any(m => m.Entity == metric.Entity && (m.Name == "ReadLatency" || m.Name == "WriteLatency") && m.Value > settings.DiskLatencyWarningMs);
                        bad = baseline.Item2 > 60 && delayed && metric.Value < baseline.Item1 * 0.2;
                        if (!delayed) _baselines[key] = Tuple.Create(baseline.Item1 * 0.98 + metric.Value.Value * 0.02, baseline.Item2 + 1);
                    }
                }
                else continue;
                string ruleKey = metric.Entity + "/" + metric.Name;
                SustainedRule rule;
                if (!_rules.TryGetValue(ruleKey, out rule)) _rules[ruleKey] = rule = new SustainedRule();
                if (rule.Evaluate(bad, now, TimeSpan.FromSeconds(settings.AnomalyDurationSeconds)))
                    _store.SaveAlert(new AlertRecord { Utc = DateTime.UtcNow, Category = metric.Name == "Temperature" ? "Temperature" : "DiskPerformance",
                        Severity = severity, Entity = metric.Entity, Message = metric.Name + " 지속 이상: " + metric.Display + " (증거: " + metric.Detail + ")" });
            }
        }
        private async Task ReadSensorsAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    try
                    {
                        var privileged = await PrivilegedSensors.ReadAsync(ct).ConfigureAwait(false);
                        lock (_sync) { _sensorMetrics = privileged; _sensorUtc = DateTime.UtcNow; }
                        await Task.Delay(5000, ct).ConfigureAwait(false); continue;
                    }
                    catch (Exception ex) when (ex is IOException || ex is TimeoutException || ex is OperationCanceledException || ex is UnauthorizedAccessException) { if (ct.IsCancellationRequested) return; }
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ExplorerManager.SensorHost.exe");
                    if (!File.Exists(path)) throw new FileNotFoundException("센서 호스트가 없습니다.", path);
                    _sensor = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true });
                    _sensor.ErrorDataReceived += (s, e) => { if (!string.IsNullOrEmpty(e.Data)) _error(new IOException(e.Data)); };
                    _sensor.BeginErrorReadLine();
                    int frames = 0;
                    using (ct.Register(StopSensor))
                        while (!ct.IsCancellationRequested && !_sensor.HasExited)
                        {
                            var line = _sensor.StandardOutput.ReadLineAsync();
                            if (await Task.WhenAny(line, Task.Delay(20000, ct)).ConfigureAwait(false) != line) throw new TimeoutException("센서 호출 응답이 없어 호스트를 재시작합니다.");
                            string json = await line.ConfigureAwait(false); if (json == null) break;
                            if (json.Length > 1024 * 1024) throw new IOException("센서 응답 크기 제한");
                            var data = JsonConvert.DeserializeObject<List<Metric>>(json, Protocol.Json);
                            if (data != null) lock (_sync) { _sensorMetrics = data; _sensorUtc = DateTime.UtcNow; }
                            if (++frames >= 3) break;
                        }
                }
                catch (Exception ex) { if (!ct.IsCancellationRequested) _error(ex); }
                finally { StopSensor(); }
                try { await Task.Delay(10000, ct).ConfigureAwait(false); } catch (OperationCanceledException) { break; }
            }
        }
        private void StopSensor()
        {
            var process = _sensor; _sensor = null;
            if (process == null) return;
            try { if (!process.HasExited) process.Kill(); } catch { }
            process.Dispose();
        }
        private void StartEvents()
        {
#if NETFRAMEWORK
            try
            {
                var query = new EventLogQuery("System", PathType.LogName, "*[System[(Level=1 or Level=2 or Level=3) and (Provider[@Name='disk'] or Provider[@Name='Ntfs'] or Provider[@Name='storahci'] or Provider[@Name='stornvme'] or Provider[@Name='storport'])]]");
                _events = new EventLogWatcher(query);
                _events.EventRecordWritten += (s, e) =>
                {
                    if (e.EventException != null) { _error(e.EventException); return; }
                    using (var record = e.EventRecord)
                    {
                        if (record == null) return;
                        try { _store.SaveAlert(new AlertRecord { Utc = record.TimeCreated?.ToUniversalTime() ?? DateTime.UtcNow, Category = "WindowsStorageEvent",
                            Severity = record.Level <= 2 ? "Critical" : "Warning", Entity = "Storage", Message = record.ProviderName + " event " + record.Id + ": " + record.FormatDescription() }); }
                        catch (Exception ex) { _error(ex); }
                    }
                };
                _events.Enabled = true;
            }
            catch (Exception ex) { _error(ex); }
#endif
        }
        public void Dispose()
        {
            StopSensor();
#if NETFRAMEWORK
            _events?.Dispose();
#endif
        }
    }
}
