using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace PackCheck.App.Controls;

/// <summary>One point of a <see cref="LineChart"/>; a null value leaves a gap (server was offline / no data).</summary>
public sealed record ChartPoint(DateTimeOffset Time, double? Value);

/// <summary>
/// Lightweight time-series chart: y-axis labels, grid, filled line with gaps for missing data,
/// an optional threshold line (e.g. 20 TPS, 50 ms) and a hover read-out.
/// </summary>
public sealed class LineChart : FrameworkElement
{
    public static readonly DependencyProperty PointsProperty = Reg(nameof(Points), typeof(IReadOnlyList<ChartPoint>), null);
    public static readonly DependencyProperty StrokeProperty = Reg(nameof(Stroke), typeof(Brush), Brushes.SeaGreen);
    public static readonly DependencyProperty GridBrushProperty = Reg(nameof(GridBrush), typeof(Brush), Brushes.LightGray);
    public static readonly DependencyProperty LabelBrushProperty = Reg(nameof(LabelBrush), typeof(Brush), Brushes.Gray);
    public static readonly DependencyProperty MinimumProperty = Reg(nameof(Minimum), typeof(double), double.NaN);
    public static readonly DependencyProperty MaximumProperty = Reg(nameof(Maximum), typeof(double), double.NaN);
    public static readonly DependencyProperty ThresholdProperty = Reg(nameof(Threshold), typeof(double), double.NaN);
    public static readonly DependencyProperty UnitProperty = Reg(nameof(Unit), typeof(string), string.Empty);
    public static readonly DependencyProperty FormatProperty = Reg(nameof(Format), typeof(string), "0.#");

    private const double AxisWidth = 44;
    private const double BottomHeight = 18;
    private Point? _hover;

    public IReadOnlyList<ChartPoint>? Points { get => (IReadOnlyList<ChartPoint>?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public Brush GridBrush { get => (Brush)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush LabelBrush { get => (Brush)GetValue(LabelBrushProperty); set => SetValue(LabelBrushProperty, value); }
    public double Minimum { get => (double)GetValue(MinimumProperty); set => SetValue(MinimumProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public double Threshold { get => (double)GetValue(ThresholdProperty); set => SetValue(ThresholdProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string Format { get => (string)GetValue(FormatProperty); set => SetValue(FormatProperty, value); }

    private static DependencyProperty Reg(string name, Type type, object? def) =>
        DependencyProperty.Register(name, type, typeof(LineChart), new FrameworkPropertyMetadata(def, FrameworkPropertyMetadataOptions.AffectsRender));

    protected override void OnMouseMove(System.Windows.Input.MouseEventArgs e)
    {
        _hover = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(System.Windows.Input.MouseEventArgs e)
    {
        _hover = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        // Transparent fill so the whole area receives mouse events.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var points = Points ?? [];
        var values = points.Select(p => p.Value).OfType<double>().ToList();
        var plot = new Rect(AxisWidth, 6, Math.Max(1, ActualWidth - AxisWidth - 6), Math.Max(1, ActualHeight - BottomHeight - 10));
        var culture = CultureInfo.CurrentUICulture;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface("Segoe UI");

        FormattedText Text(string s, Brush b, double size = 10.5) => new(s, culture, System.Windows.FlowDirection.LeftToRight, typeface, size, b, dpi);

        if (values.Count == 0)
        {
            var empty = Text(PackCheck.App.Infrastructure.Loc.T("Perf_NoData"), LabelBrush, 12);
            dc.DrawText(empty, new Point((ActualWidth - empty.Width) / 2, (ActualHeight - empty.Height) / 2));
            return;
        }

        var min = double.IsNaN(Minimum) ? values.Min() : Math.Min(Minimum, values.Min());
        var max = double.IsNaN(Maximum) ? values.Max() : Math.Max(Maximum, values.Max());
        if (!double.IsNaN(Threshold))
        {
            max = Math.Max(max, Threshold);
        }

        if (max - min < 1e-9)
        {
            max = min + 1;
        }

        var pad = double.IsNaN(Maximum) ? (max - min) * 0.1 : 0;
        max += pad;
        double Y(double v) => plot.Bottom - ((v - min) / (max - min) * plot.Height);

        var gridPen = new Pen(GridBrush, 1) { DashStyle = DashStyles.Dot };
        gridPen.Freeze();
        for (var i = 0; i <= 3; i++)
        {
            var v = min + ((max - min) * i / 3);
            var y = Y(v);
            dc.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var label = Text(v.ToString(Format, culture), LabelBrush);
            dc.DrawText(label, new Point(plot.Left - label.Width - 6, y - (label.Height / 2)));
        }

        if (!double.IsNaN(Threshold))
        {
            var tp = new Pen(LabelBrush, 1) { DashStyle = DashStyles.Dash };
            tp.Freeze();
            dc.DrawLine(tp, new Point(plot.Left, Y(Threshold)), new Point(plot.Right, Y(Threshold)));
        }

        var t0 = points[0].Time;
        var span = Math.Max(1, (points[^1].Time - t0).TotalSeconds);
        double X(DateTimeOffset t) => plot.Left + ((t - t0).TotalSeconds / span * plot.Width);

        // Line and area, broken at gaps (null values or holes longer than 5 minutes).
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var l = line.Open())
        using (var a = area.Open())
        {
            var run = new List<Point>();
            void Flush()
            {
                if (run.Count == 0)
                {
                    return;
                }

                l.BeginFigure(run[0], false, false);
                l.PolyLineTo(run.Skip(1).DefaultIfEmpty(run[0]).ToList(), true, true);
                a.BeginFigure(new Point(run[0].X, plot.Bottom), true, true);
                a.PolyLineTo([.. run, new Point(run[^1].X, plot.Bottom)], false, false);
                run.Clear();
            }

            DateTimeOffset? last = null;
            foreach (var p in points)
            {
                if (p.Value is not { } v || (last is { } lt && (p.Time - lt).TotalMinutes > Math.Max(5, span / 60 / 40)))
                {
                    Flush();
                }

                if (p.Value is { } value)
                {
                    run.Add(new Point(X(p.Time), Y(value)));
                    last = p.Time;
                }
            }

            Flush();
        }

        line.Freeze();
        area.Freeze();
        var fill = Stroke.Clone();
        fill.Opacity = 0.15;
        fill.Freeze();
        dc.DrawGeometry(fill, null, area);
        var pen = new Pen(Stroke, 1.8) { LineJoin = PenLineJoin.Round };
        pen.Freeze();
        dc.DrawGeometry(null, pen, line);

        // Time labels at both ends.
        var longRange = span > 36 * 3600;
        string TimeText(DateTimeOffset t) => t.LocalDateTime.ToString(longRange ? "dd.MM HH:mm" : "HH:mm", culture);
        var start = Text(TimeText(t0), LabelBrush);
        var end = Text(TimeText(points[^1].Time), LabelBrush);
        dc.DrawText(start, new Point(plot.Left, plot.Bottom + 4));
        dc.DrawText(end, new Point(plot.Right - end.Width, plot.Bottom + 4));

        if (_hover is { } h && h.X >= plot.Left && h.X <= plot.Right)
        {
            var nearest = points.Where(p => p.Value is not null).MinBy(p => Math.Abs(X(p.Time) - h.X));
            if (nearest?.Value is { } nv)
            {
                var x = X(nearest.Time);
                var hp = new Pen(LabelBrush, 1);
                hp.Freeze();
                dc.DrawLine(hp, new Point(x, plot.Top), new Point(x, plot.Bottom));
                dc.DrawEllipse(Stroke, null, new Point(x, Y(nv)), 3.5, 3.5);
                var tip = Text($"{nv.ToString(Format, culture)} {Unit}  ·  {TimeText(nearest.Time)}", Brushes.White, 11.5);
                var tx = Math.Clamp(x - (tip.Width / 2) - 6, plot.Left, Math.Max(plot.Left, plot.Right - tip.Width - 12));
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(225, 30, 30, 30)), null, new Rect(tx, plot.Top, tip.Width + 12, tip.Height + 6), 4, 4);
                dc.DrawText(tip, new Point(tx + 6, plot.Top + 3));
            }
        }
    }
}
