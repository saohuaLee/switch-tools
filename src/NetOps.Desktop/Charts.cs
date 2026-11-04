using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NetOps.Domain;

namespace NetOps.Desktop;

public sealed class TrendChart : FrameworkElement
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(nameof(Samples), typeof(MonitorSample[]), typeof(TrendChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PrefixProperty = DependencyProperty.Register(nameof(Prefix), typeof(string), typeof(TrendChart), new FrameworkPropertyMetadata("cpu", FrameworkPropertyMetadataOptions.AffectsRender));
    public MonitorSample[]? Samples { get => (MonitorSample[]?)GetValue(SamplesProperty); set => SetValue(SamplesProperty, value); }
    public string Prefix { get => (string)GetValue(PrefixProperty); set => SetValue(PrefixProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); var width = Math.Max(1, ActualWidth - 48); var height = Math.Max(1, ActualHeight - 37);
        void Text(string value, double x, double y, string color = "#94A3B8", double size = 10) => dc.DrawText(new FormattedText(value, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size, (Brush)new BrushConverter().ConvertFromString(color)!, VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
        var samples = Samples ?? []; var values = samples.Select(s => s.Metrics.Where(m => m.Key.StartsWith(Prefix) && m.Quality == CollectionQuality.Valid && m.Value is not null).Select(m => m.Value!.Value).DefaultIfEmpty(double.NaN).Max()).ToArray();
        var finite = values.Where(double.IsFinite).ToArray();
        var min = Prefix == "temperature" && finite.Length > 0 ? Math.Min(0, Math.Floor(finite.Min() / 20) * 20) : 0;
        var max = Prefix == "temperature" && finite.Length > 0 ? Math.Max(60, Math.Ceiling(finite.Max() / 20) * 20) : 100;
        for (var i = 0; i <= 4; i++) { var y = 7 + height * i / 4; dc.DrawLine(new(new SolidColorBrush(Color.FromRgb(234, 239, 246)), 1), new(30, y), new(30 + width, y)); Text((max - (max - min) * i / 4).ToString("0.#"), 0, y - 5); }
        if (samples.Length < 2 || values.All(double.IsNaN)) { Text("尚无有效采样", Math.Max(34, ActualWidth / 2 - 45), ActualHeight / 2 - 5, "#64748B", 12); return; }
        var start = samples[0].CollectedAt; var duration = Math.Max(1, (samples[^1].CollectedAt - start).TotalSeconds); Point? previous = null;
        var blue = new SolidColorBrush(Prefix == "temperature" ? Color.FromRgb(217, 133, 49) : Prefix == "memory" ? Color.FromRgb(130, 91, 213) : Color.FromRgb(58, 115, 235));
        for (var i = 0; i < samples.Length; i++)
        {
            if (!double.IsFinite(values[i])) { previous = null; continue; }
            var point = new Point(30 + width * (samples[i].CollectedAt - start).TotalSeconds / duration, 7 + height * (1 - Math.Clamp((values[i] - min) / (max - min), 0, 1)));
            if (previous is Point p && (samples[i].CollectedAt - samples[i - 1].CollectedAt).TotalMinutes < 10) { dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(20, blue.Color.R, blue.Color.G, blue.Color.B)), null, new StreamGeometryBuilder().Area(p, point, 7 + height)); dc.DrawLine(new(blue, 2), p, point); }
            previous = point;
        }
        Text(start.ToLocalTime().ToString("HH:mm"), 30, height + 15); Text(samples[^1].CollectedAt.ToLocalTime().ToString("HH:mm"), Math.Max(35, ActualWidth - 40), height + 15);
    }
    private sealed class StreamGeometryBuilder
    {
        public StreamGeometry Area(Point a, Point b, double bottom) { var g = new StreamGeometry(); using var c = g.Open(); c.BeginFigure(new(a.X, bottom), true, true); c.LineTo(a, true, false); c.LineTo(b, true, false); c.LineTo(new(b.X, bottom), true, false); return g; }
    }
}
