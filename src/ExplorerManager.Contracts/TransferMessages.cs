using System;
using System.Collections.Generic;
using ExplorerManager.Domain;

namespace ExplorerManager.Contracts
{
    public sealed class TransferOverview
    {
        public bool Available { get; set; }
        public TransferOptions Options { get; set; } = new TransferOptions();
        public List<TransferConnection> Connections { get; set; } = new List<TransferConnection>();
        public List<TransferPolicy> Policies { get; set; } = new List<TransferPolicy>();
        public Dictionary<string, long> Counts { get; set; } = new Dictionary<string, long>();
        public List<TransferProgress> Active { get; set; } = new List<TransferProgress>();
        public string InterlockReason { get; set; }
    }
    public sealed class TransferJobRow
    {
        public string Id { get; set; }
        public string PolicyName { get; set; }
        public string ConnectionName { get; set; }
        public string Path { get; set; }
        public string RemoteFinal { get; set; }
        public long Bytes { get; set; }
        public string State { get; set; }
        public TransferEvidenceLevel EvidenceLevel { get; set; }
        public DateTime CreatedUtc { get; set; }
        public DateTime UpdatedUtc { get; set; }
        public DateTime? NextAttemptUtc { get; set; }
        public int Attempts { get; set; }
        public string ErrorCode { get; set; }
        public string Detail { get; set; }
        public double Progress { get; set; }
        public static TransferJobRow From(TransferJob j) => new TransferJobRow { Id = j.Id, PolicyName = j.Policy.Name, ConnectionName = j.Connection.Name,
            Path = Clip(j.Path, 1024), RemoteFinal = Clip(j.RemoteFinal, 1024), Bytes = j.Bytes, State = j.State, EvidenceLevel = j.EvidenceLevel,
            CreatedUtc = j.CreatedUtc, UpdatedUtc = j.UpdatedUtc, NextAttemptUtc = j.NextAttemptUtc == DateTime.MaxValue ? null : (DateTime?)j.NextAttemptUtc,
            Attempts = j.Attempts, ErrorCode = j.ErrorCode, Detail = Clip(j.Detail, 400), Progress = j.State == "Completed" ? 100 : 0 };
        private static string Clip(string value, int length) => value?.Length > length ? value.Substring(0, length) + "…" : value;
    }
    public sealed class TransferJobPage
    {
        public List<TransferJobRow> Items { get; set; } = new List<TransferJobRow>();
        public string NextCursor { get; set; }
        public bool HasMore { get; set; }
    }
    public sealed class TransferDetail
    {
        public TransferJob Job { get; set; }
        public List<TransferAttempt> Attempts { get; set; } = new List<TransferAttempt>();
        public List<TransferEvidence> Evidence { get; set; } = new List<TransferEvidence>();
    }
    public sealed class TransferPreview
    {
        public long Files { get; set; }
        public long Bytes { get; set; }
        public long Unsupported { get; set; }
        public bool Complete { get; set; }
        public string Detail { get; set; }
        public List<string> Samples { get; set; } = new List<string>();
    }
    public sealed class TransferOperation<T>
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string State { get; set; }
        public DateTime Utc { get; set; }
        public string Error { get; set; }
        public T Result { get; set; }
    }
}
