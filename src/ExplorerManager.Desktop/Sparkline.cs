using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace ExplorerManager.Desktop
{
    public sealed class Sparkline : FrameworkElement
    {
        public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(IEnumerable<double>), typeof(Sparkline),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, Changed));
        public IEnumerable<double> Values { get => (IEnumerable<double>)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
        private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var graph = (Sparkline)d;
            if (e.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= graph.CollectionChanged;
            if (e.NewValue is INotifyCollectionChanged next) next.CollectionChanged += graph.CollectionChanged;
        }
        private void CollectionChanged(object sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
        protected override void OnRender(DrawingContext dc)
        {
            base.OnRender(dc); double width = ActualWidth, height = ActualHeight;
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(16, 29, 44)), null, new Rect(0, 0, width, height));
            for (int i = 1; i <= 3; i++) dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(42, 61, 80)), 1), new Point(0, height * i / 4), new Point(width, height * i / 4));
            var values = Values?.Where(x => !double.IsNaN(x) && !double.IsInfinity(x)).ToArray();
            if (values == null || values.Length < 2) return;
            var geometry = new StreamGeometry();
            using (var context = geometry.Open())
            {
                for (int i = 0; i < values.Length; i++)
                {
                    var point = new Point(i * width / (values.Length - 1), height - Math.Max(0, Math.Min(100, values[i])) * height / 100);
                    if (i == 0) context.BeginFigure(point, false, false); else context.LineTo(point, true, false);
                }
            }
            geometry.Freeze(); dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromRgb(73, 166, 255)), 2), geometry);
        }
    }
}
