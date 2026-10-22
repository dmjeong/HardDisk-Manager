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
                case "MetricValue": return DisplayText.MetricValue(value as Metric);
                case "Enum":
                    switch (value?.ToString())
                    {
                        case "UsedPercent": return "Used (%)";
                        case "UsedBytes": return "Used (bytes)";
                        case "FreeBytes": return "Free (bytes)";
                        case "LastWriteTime": return "Last modified";
                        case "CreationTime": return "Created";
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
