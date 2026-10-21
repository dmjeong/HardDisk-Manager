using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ExplorerManager.Domain;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExplorerManager.Contracts
{
    public static class Protocol
    {
        public const string PipeName = "ExplorerManager.v1";
        public const int Version = 1;
        public const int MaxFrameBytes = 2 * 1024 * 1024;
        public static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.None, MaxDepth = 40,
            DateTimeZoneHandling = DateTimeZoneHandling.Utc, FloatParseHandling = FloatParseHandling.Double
        };
    }
    public sealed class Request
    {
        public int Version { get; set; } = Protocol.Version;
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Command { get; set; }
        public JObject Data { get; set; } = new JObject();
    }
    public sealed class Response
    {
        public string Id { get; set; }
        public bool Success { get; set; }
        public string Error { get; set; }
        public JToken Data { get; set; }
        public static Response Ok(Request r, object data) => new Response { Id = r.Id, Success = true, Data = data == null ? JValue.CreateNull() : JToken.FromObject(data) };
    }
    public sealed class Dashboard
    {
        public string Version { get; set; } = "1.0.0";
        public DateTime Utc { get; set; }
        public bool IsOperator { get; set; }
        public bool Paused { get; set; }
        public string Health { get; set; }
        public long CacheUsed { get; set; }
        public long CacheCapacity { get; set; }
        public string CacheEpoch { get; set; }
        public List<VolumeStatus> Volumes { get; set; } = new List<VolumeStatus>();
        public List<DrivePolicy> Policies { get; set; } = new List<DrivePolicy>();
        public List<Metric> Metrics { get; set; } = new List<Metric>();
        public List<RunRecord> Runs { get; set; } = new List<RunRecord>();
        public List<AlertRecord> Alerts { get; set; } = new List<AlertRecord>();
    }
    public sealed class UnlockTicket
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string AlertId { get; set; }
        public string PolicyId { get; set; }
        public int PolicyVersion { get; set; }
        public FileSnapshot File { get; set; }
        public List<ProcessIdentity> Processes { get; set; } = new List<ProcessIdentity>();
        public string OperatorSid { get; set; }
        public int SessionId { get; set; }
        public DateTime ExpiresUtc { get; set; }
    }
    public sealed class HistoryPage
    {
        public List<ItemRecord> Items { get; set; } = new List<ItemRecord>();
        public int Offset { get; set; }
        public bool HasMore { get; set; }
    }
    public interface IExplorerClient
    {
        Task<T> SendAsync<T>(string command, object data = null, CancellationToken cancellation = default(CancellationToken));
        Task CloseProcessesAsync(UnlockTicket ticket, bool force, CancellationToken cancellation);
    }
}
