using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ConstantProxy.Core.Traffic;

namespace ConstantProxy.App;

/// <summary>
/// Lightweight download/upload rate graph drawn directly (no charting dependency, SPEC §20). It re-renders only when
/// its data changes (about once per second), so it costs nothing while idle. Upload is dashed so the two series
/// are distinguishable without relying on colour.
/// </summary>
public sealed class TrafficGraph : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<TrafficPoint>), typeof(TrafficGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty WindowSecondsProperty = DependencyProperty.Register(
        nameof(WindowSeconds), typeof(int), typeof(TrafficGraph),
        new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(TrafficGraph),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<TrafficPoint>? Points
    {
        get => (IReadOnlyList<TrafficPoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public int WindowSeconds
    {
        get => (int)GetValue(WindowSecondsProperty);
        set => SetValue(WindowSecondsProperty, value);
    }

    /// <summary>Shown instead of a plot when there is nothing to draw (for example "Unavailable").</summary>
    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 20 || height < 20)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = SystemColors.ControlTextBrush;
        var grid = new Pen(SystemColors.ControlDarkBrush, 0.5);
        dc.DrawRectangle(SystemColors.WindowBrush, new Pen(SystemColors.ControlDarkBrush, 1), new Rect(0.5, 0.5, width - 1, height - 1));

        var points = Points;
        if (points is null || points.Count < 2)
        {
            DrawCentered(dc, EmptyText, text, dpi, width, height);
            return;
        }

        var end = points[^1].TimeUtc;
        var start = end - TimeSpan.FromSeconds(Math.Max(WindowSeconds, 2));
        var visible = points.Where(p => p.TimeUtc >= start).ToList();
        if (visible.Count < 2)
        {
            DrawCentered(dc, EmptyText, text, dpi, width, height);
            return;
        }

        var max = Math.Max(visible.Max(p => Math.Max(p.UploadRate, p.DownloadRate)), 1024);
        const double pad = 4;
        var plot = new Rect(pad, 16, width - (2 * pad), height - 16 - pad);

        for (var i = 1; i < 4; i++)
        {
            var y = plot.Top + (plot.Height * i / 4);
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        DrawSeries(dc, visible, start, plot, max, p => p.DownloadRate, new Pen(Brushes.SeaGreen, 1.5));
        DrawSeries(dc, visible, start, plot, max, p => p.UploadRate, new Pen(Brushes.DarkOrange, 1.5) { DashStyle = DashStyles.Dash });

        var label = new FormattedText(
            "↓ solid   ↑ dashed   max " + TrafficFormatter.FormatRate(max, CultureInfo.CurrentUICulture),
            CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 10, text, dpi);
        dc.DrawText(label, new Point(pad + 2, 1));
    }

    private static void DrawSeries(DrawingContext dc, List<TrafficPoint> points, DateTimeOffset start, Rect plot, double max, Func<TrafficPoint, double> value, Pen pen)
    {
        var span = (points[^1].TimeUtc - start).TotalSeconds;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var i = 0; i < points.Count; i++)
            {
                var x = plot.Left + (plot.Width * Math.Clamp((points[i].TimeUtc - start).TotalSeconds / span, 0, 1));
                var y = plot.Bottom - (plot.Height * Math.Clamp(value(points[i]) / max, 0, 1));
                if (i == 0)
                {
                    ctx.BeginFigure(new Point(x, y), isFilled: false, isClosed: false);
                }
                else
                {
                    ctx.LineTo(new Point(x, y), isStroked: true, isSmoothJoin: false);
                }
            }
        }

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
    }

    private static void DrawCentered(DrawingContext dc, string message, Brush brush, double dpi, double width, double height)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        var formatted = new FormattedText(message, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, brush, dpi);
        dc.DrawText(formatted, new Point((width - formatted.Width) / 2, (height - formatted.Height) / 2));
    }
}
