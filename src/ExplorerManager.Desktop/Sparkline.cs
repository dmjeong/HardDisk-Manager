using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace ExplorerManager.Desktop
{
    // Rendering-only control: consumes ViewModel samples without collecting any data.
    public sealed class Sparkline : FrameworkElement
    {
        public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IEnumerable<double>), typeof(Sparkline),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, Changed));
        public IEnumerable<double> Values { get => (IEnumerable<double>)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
        public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));
        public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
        public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(Sparkline), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
        public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(141, 131, 245)), FrameworkPropertyMetadataOptions.AffectsRender));
        public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
        private readonly Brush _muted = new SolidColorBrush(Color.FromRgb(160, 162, 174));
        private readonly Pen _grid = new Pen(new SolidColorBrush(Color.FromRgb(43, 45, 51)), 1);
        private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var graph = (Sparkline)d;
            if (e.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= graph.CollectionChanged;
            if (e.NewValue is INotifyCollectionChanged next) next.CollectionChanged += graph.CollectionChanged;
        }
        private void CollectionChanged(object sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
        private FormattedText Label(string text) => new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface("Segoe UI, Malgun Gothic"), 10, _muted, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc);
            if (ActualWidth < 80 || ActualHeight < 50) return;
            double left = 38, top = 18, bottom = ActualHeight - 24, right = ActualWidth - 4;
            double width = right - left, height = bottom - top;
            var values = Values?.ToArray() ?? new double[0];
            var valid = values.Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).ToArray();
            double max = Maximum > 0 ? Maximum : NiceMaximum(valid.DefaultIfEmpty(0).Max());
            for (int i = 0; i <= 2; i++)
            {
                double y = top + height * i / 2;
                dc.DrawLine(_grid, new Point(left, y), new Point(right, y));
                var label = Label((max * (2 - i) / 2).ToString("0.#", CultureInfo.CurrentCulture));
                dc.DrawText(label, new Point(Math.Max(0, left - label.Width - 8), y - label.Height / 2));
            }
            var unit = Label(Unit ?? ""); dc.DrawText(unit, new Point(right - unit.Width, 0));
            var previous = Label(values.Length > 1 ? "−" + (values.Length - 1) + " samples" : "Earlier");
            dc.DrawText(previous, new Point(left, bottom + 8));
            var now = Label("Now"); dc.DrawText(now, new Point(right - now.Width, bottom + 8));
            if (valid.Length == 0)
            {
                var waiting = Label("Waiting for samples"); dc.DrawText(waiting, new Point(left + (width - waiting.Width) / 2, top + height / 2 - waiting.Height / 2)); return;
            }
            Point Position(int index) => new Point(left + index * width / Math.Max(1, values.Length - 1), bottom - Math.Max(0, Math.Min(max, values[index])) * height / max);
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                bool drawing = false;
                for (int i = 0; i < values.Length; i++)
                {
                    if (double.IsNaN(values[i]) || double.IsInfinity(values[i])) { drawing = false; continue; }
                    var point = Position(i);
                    if (!drawing) { context.BeginFigure(point, false, false); drawing = true; } else context.LineTo(point, true, false);
                    dc.DrawEllipse(Stroke, null, point, 1.8, 1.8);
                }
            }
            geometry.Freeze(); dc.DrawGeometry(null, new Pen(Stroke, 1.8) { LineJoin = PenLineJoin.Round }, geometry);
        }
        private static double NiceMaximum(double value)
        {
            if (value <= 0) return 1;
            double magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
            return Math.Ceiling(value * 1.15 / magnitude) * magnitude;
        }
    }
}
