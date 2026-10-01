using System.Globalization;
using System.Windows;
using System.Windows.Media;
using ConstantProxy.Core.Analytics;
using ConstantProxy.Core.Traffic;

namespace ConstantProxy.App;

public enum GraphUnit
{
    Rate,
    Milliseconds,
    Percent,
}

/// <summary>One line on a <see cref="SeriesGraph"/>. Dashed lines keep series distinguishable without colour.</summary>
public sealed record GraphSeries(string Name, Brush Stroke, bool Dashed, IReadOnlyList<SeriesPoint> Points);

/// <summary>
/// Time-series line graph for the historical views (SPEC §20). Drawn directly, renders only when its data changes,
/// and breaks the line where values are missing instead of inventing them.
/// </summary>
public sealed class SeriesGraph : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series), typeof(IReadOnlyList<GraphSeries>), typeof(SeriesGraph),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
        nameof(Unit), typeof(GraphUnit), typeof(SeriesGraph),
        new FrameworkPropertyMetadata(GraphUnit.Rate, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText), typeof(string), typeof(SeriesGraph),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<GraphSeries>? Series
    {
        get => (IReadOnlyList<GraphSeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public GraphUnit Unit
    {
        get => (GraphUnit)GetValue(UnitProperty);
        set => SetValue(UnitProperty, value);
    }

    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width < 40 || height < 40)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var text = SystemColors.ControlTextBrush;
        dc.DrawRectangle(SystemColors.WindowBrush, new Pen(SystemColors.ControlDarkBrush, 1), new Rect(0.5, 0.5, width - 1, height - 1));

        var series = Series;
        var values = series?.SelectMany(s => s.Points).Where(p => p.Value is not null).ToList();
        if (series is null || values is null || values.Count == 0)
        {
            DrawText(dc, EmptyText, text, dpi, 12, new Point(0, 0), width, height, centered: true);
            return;
        }

        var times = series.SelectMany(s => s.Points).Select(p => p.Time).ToList();
        var start = times.Min();
        var end = times.Max();
        var span = Math.Max((end - start).TotalSeconds, 1);
        var max = Unit == GraphUnit.Percent ? 100 : Math.Max(values.Max(p => p.Value!.Value) * 1.1, Unit == GraphUnit.Rate ? 1024 : 10);

        var plot = new Rect(6, 18, width - 12, height - 18 - 16);
        var grid = new Pen(SystemColors.ControlDarkBrush, 0.5);
        for (var i = 1; i < 4; i++)
        {
            var y = plot.Top + (plot.Height * i / 4);
            dc.DrawLine(grid, new Point(plot.Left, y), new Point(plot.Right, y));
        }

        foreach (var s in series)
        {
            DrawLine(dc, s, start, span, plot, max);
        }

        var legend = string.Join("   ", series.Select(s => (s.Dashed ? "╌ " : "━ ") + s.Name)) + "   max " + FormatValue(max);
        DrawText(dc, legend, text, dpi, 10, new Point(8, 1), width, 16, centered: false);
        var local = (end - start).TotalHours > 24 ? "MM-dd HH:mm" : "HH:mm";
        DrawText(dc, start.ToLocalTime().ToString(local, CultureInfo.CurrentCulture), text, dpi, 10, new Point(8, height - 15), width, 14, centered: false);
        var endLabel = end.ToLocalTime().ToString(local, CultureInfo.CurrentCulture);
        DrawText(dc, endLabel, text, dpi, 10, new Point(width - 8 - (endLabel.Length * 6), height - 15), width, 14, centered: false);
    }

    private string FormatValue(double value) => Unit switch
    {
        GraphUnit.Rate => TrafficFormatter.FormatRate(value, CultureInfo.CurrentUICulture),
        GraphUnit.Milliseconds => $"{value:0} ms",
        _ => $"{value:0}%",
    };

    private static void DrawLine(DrawingContext dc, GraphSeries series, DateTimeOffset start, double span, Rect plot, double max)
    {
        var pen = new Pen(series.Stroke, 1.5) { DashStyle = series.Dashed ? DashStyles.Dash : DashStyles.Solid };
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            var open = false;
            foreach (var point in series.Points)
            {
                if (point.Value is not { } value)
                {
                    open = false; // gap: no data is drawn as a break, never as zero
                    continue;
                }

                var x = plot.Left + (plot.Width * Math.Clamp((point.Time - start).TotalSeconds / span, 0, 1));
                var y = plot.Bottom - (plot.Height * Math.Clamp(value / max, 0, 1));
                if (!open)
                {
                    ctx.BeginFigure(new Point(x, y), isFilled: false, isClosed: false);
                    open = true;
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

    private static void DrawText(DrawingContext dc, string message, Brush brush, double dpi, double size, Point at, double width, double height, bool centered)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        var formatted = new FormattedText(message, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), size, brush, dpi);
        dc.DrawText(formatted, centered ? new Point((width - formatted.Width) / 2, (height - formatted.Height) / 2) : at);
    }
}
