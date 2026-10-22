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
                        case "UsedPercent": return "사용률 (%)";
                        case "UsedBytes": return "사용 용량 (bytes)";
                        case "FreeBytes": return "여유 용량 (bytes)";
                        case "LastWriteTime": return "마지막 수정 시각";
                        case "CreationTime": return "생성 시각";
                        default: return value?.ToString() ?? "";
                    }
                case "MetricName":
                    switch (value as string)
                    {
                        case "CPU": return "CPU 사용률";
                        case "Temperature": return "온도";
                        case "AvailableRAM": return "사용 가능 메모리";
                        case "UsedRAM": return "사용 메모리";
                        case "ReadBytes": return "읽기 속도";
                        case "WriteBytes": return "쓰기 속도";
                        case "ReadLatency": return "읽기 지연";
                        case "WriteLatency": return "쓰기 지연";
                        case "Queue": return "디스크 대기열";
                        case "Reads": return "초당 읽기 작업";
                        case "Writes": return "초당 쓰기 작업";
                        case "StalledIO": return "I/O 정체";
                        default: return value;
                    }
                default: return value;
            }
        }
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
