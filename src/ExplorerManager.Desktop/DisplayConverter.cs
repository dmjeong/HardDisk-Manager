using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using ExplorerManager.Domain;
using ExplorerManager.Presentation;

namespace ExplorerManager.Desktop
{
    // Pure display adaptation; policy and service decisions remain in the ViewModel.
    public sealed class DisplayConverter : IValueConverter
    {
        public string Kind { get; set; }
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == DependencyProperty.UnsetValue) return "—";
            switch (Kind)
            {
                case "Bytes": return value == null ? "—" : DisplayText.Bytes(System.Convert.ToDouble(value, CultureInfo.InvariantCulture));
                case "Time": return value is DateTime date ? date.ToLocalTime().ToString("MM.dd HH:mm:ss") : "—";
                case "Empty": return value is int count && count == 0 ? Visibility.Visible : Visibility.Collapsed;
                case "State": return DisplayText.State(value as string);
                case "TransferState":
                    switch (value as string) {
                        case "All": return "All states";
                        case "WaitingForReady": return "Waiting for file";
                        case "Ready": return "Queued";
                        case "Hashing": return "Reading source";
                        case "UploadingTemp": return "Uploading";
                        case "VerifyingRemote": return "Verifying";
                        case "Publishing": return "Publishing";
                        case "Completed": return "Uploaded";
                        case "RetryWaiting": return "Retry scheduled";
                        case "PublicationUnknown": case "ReconcileRequired": return "Needs review";
                        case "NeedsAttention": return "Action required";
                        case "NotSelectedForBackfill": return "Not selected";
                        case "SourceMissing": return "Source missing";
                        case "Superseded": return "Source replaced";
                        default: return value;
                    }
                case "TransferEvidence":
                    switch (value?.ToString()) {
                        case "None": return "Pending";
                        case "UploadAcknowledged": return "Acknowledged";
                        case "SizeChecked": return "Size checked";
                        case "ContentVerified": return "Content verified";
                        case "PublishedVerified": return "SHA-256 verified";
                        default: return value;
                    }
                case "MetricValue": return DisplayText.MetricValue(value as Metric);
                case "Enum":
                    switch (value?.ToString())
                    {
                        case "UsedPercent": return "Used (%)";
                        case "UsedBytes": return "Used (bytes)";
                        case "FreeBytes": return "Free (bytes)";
                        case "LastWriteTime": return "Last modified";
                        case "CreationTime": return "Created";
                        case "ExplicitTls": return "FTPS · explicit TLS";
                        case "ImplicitTls": return "FTPS · implicit TLS";
                        case "Plain": return "FTP · plain";
                        case "StagingThenRename": return "Staging → final";
                        case "SameDirectoryRename": return "Temporary name → final";
                        case "DirectWrite": return "Direct to final path";
                        case "AutoStrong": return "SHA-256 · auto";
                        case "ReadBackSha256": return "Read-back SHA-256";
                        case "SizeOnly": return "Size only";
                        case "OnReady": return "When file is ready";
                        case "Schedule": return "Scheduled window";
                        case "Manual": return "Manual";
                        default: return value?.ToString() ?? "";
                    }
                case "MetricName":
                    switch (value as string)
                    {
                        case "CPU": return "CPU usage";
                        case "Temperature": return "Temperature";
                        case "AvailableRAM": return "Available RAM";
                        case "UsedRAM": return "Used RAM";
                        case "ReadBytes": return "Read throughput";
                        case "WriteBytes": return "Write throughput";
                        case "ReadLatency": return "Read latency";
                        case "WriteLatency": return "Write latency";
                        case "Queue": return "Queue length";
                        case "Reads": return "Read IOPS";
                        case "Writes": return "Write IOPS";
                        case "StalledIO": return "Stalled I/O";
                        default: return value;
                    }
                default: return value;
            }
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
