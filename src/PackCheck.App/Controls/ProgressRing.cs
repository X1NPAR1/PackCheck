using System.Windows;
using System.Windows.Media;

namespace PackCheck.App.Controls;

/// <summary>Determinate ring drawn with native geometry. Value is 0..1.</summary>
public sealed class ProgressRing : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(6d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(ProgressRing), new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(ProgressRing), new FrameworkPropertyMetadata(Brushes.Green, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SpinProperty = DependencyProperty.Register(
        nameof(Spin), typeof(double), typeof(ProgressRing), new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public Brush Track
    {
        get => (Brush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    /// <summary>Rotation offset in degrees, animated for the "spinning while filling" look.</summary>
    public double Spin
    {
        get => (double)GetValue(SpinProperty);
        set => SetValue(SpinProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        var r = (size - Thickness) / 2;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        drawingContext.DrawEllipse(null, new Pen(Track, Thickness), c, r, r);

        var v = Math.Clamp(Value, 0, 1);
        if (v <= 0)
        {
            return;
        }

        var pen = new Pen(Fill, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (v >= 0.9999)
        {
            drawingContext.DrawEllipse(null, pen, c, r, r);
            return;
        }

        var start = -90 + Spin;
        var end = start + (v * 360);
        Point P(double deg) => new(c.X + (r * Math.Cos(deg * Math.PI / 180)), c.Y + (r * Math.Sin(deg * Math.PI / 180)));
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(P(start), false, false);
            ctx.ArcTo(P(end), new Size(r, r), 0, v > 0.5, SweepDirection.Clockwise, true, false);
        }

        geo.Freeze();
        drawingContext.DrawGeometry(null, pen, geo);
    }
}
