using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace PackCheck.App.Infrastructure;

public sealed class NullToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null || (value is string s && s.Length == 0) || (value is int i && i == 0) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class IndexToBool : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i && int.TryParse(parameter?.ToString(), out var p) && i == p;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && int.TryParse(parameter?.ToString(), out var p) ? p : Binding.DoNothing;
}

public sealed class Inverse : IValueConverter
{
    public static Inverse Bool { get; } = new();

    public static IValueConverter Visibility { get; } = new InverseVisibility();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    private sealed class InverseVisibility : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is true || (value is int i && i > 0) ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

/// <summary>Maps a status keyword (Pass/Warn/Fail/Skip/Error, info/warn/error/cmd, Running…) to a theme brush.</summary>
public sealed class StatusBrush : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value?.ToString() switch
        {
            "Pass" or "Running" or "cmd" => "PassBrush",
            "PassWithWarnings" or "Warn" or "warn" or "Starting" or "Stopping" => "WarnBrush",
            "Fail" or "Error" or "error" or "Crashed" or "Incomplete" => "FailBrush",
            "info" => "TextBrush",
            _ => "SkipBrush",
        };
        return Application.Current.TryFindResource(key);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Attached animation helpers that honor the animation level.</summary>
public static class Anim
{
    public static readonly DependencyProperty PopInProperty = DependencyProperty.RegisterAttached(
        "PopIn", typeof(bool), typeof(Anim), new PropertyMetadata(false, OnPopIn));

    public static readonly DependencyProperty StaggerProperty = DependencyProperty.RegisterAttached(
        "Stagger", typeof(bool), typeof(Anim), new PropertyMetadata(false, OnStagger));

    public static readonly DependencyProperty AutoScrollProperty = DependencyProperty.RegisterAttached(
        "AutoScroll", typeof(bool), typeof(Anim), new PropertyMetadata(false, OnAutoScroll));

    public static bool GetPopIn(DependencyObject o) => (bool)o.GetValue(PopInProperty);

    public static void SetPopIn(DependencyObject o, bool v) => o.SetValue(PopInProperty, v);

    public static bool GetStagger(DependencyObject o) => (bool)o.GetValue(StaggerProperty);

    public static void SetStagger(DependencyObject o, bool v) => o.SetValue(StaggerProperty, v);

    public static bool GetAutoScroll(DependencyObject o) => (bool)o.GetValue(AutoScrollProperty);

    public static void SetAutoScroll(DependencyObject o, bool v) => o.SetValue(AutoScrollProperty, v);

    private static void OnPopIn(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || e.NewValue is not true)
        {
            return;
        }

        fe.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is not true || !Motion.Enabled)
            {
                return;
            }

            var scale = new ScaleTransform(0.94, 0.94);
            fe.RenderTransformOrigin = new Point(0.5, 0.5);
            fe.RenderTransform = scale;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.94, 1, Motion.D(220)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.94, 1, Motion.D(220)) { EasingFunction = ease });
            fe.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Motion.D(200)));
        };
    }

    /// <summary>Items fade and slide in one after another when they are generated.</summary>
    private static void OnStagger(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ItemsControl ic || e.NewValue is not true)
        {
            return;
        }

        ic.ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (ic.ItemContainerGenerator.Status != System.Windows.Controls.Primitives.GeneratorStatus.ContainersGenerated || !Motion.Enabled)
            {
                return;
            }

            var i = 0;
            foreach (var item in ic.Items)
            {
                if (ic.ItemContainerGenerator.ContainerFromItem(item) is FrameworkElement c && c.Tag is not "shown")
                {
                    c.Tag = "shown";
                    var t = new TranslateTransform(0, 12);
                    c.RenderTransform = t;
                    var begin = TimeSpan.FromMilliseconds(Math.Min(i++, 10) * 35);
                    t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(12, 0, Motion.D(260)) { BeginTime = begin, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                    c.Opacity = 0;
                    c.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Motion.D(240)) { BeginTime = begin });
                }
            }
        };
    }

    private static void OnAutoScroll(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ListBox lb || e.NewValue is not true)
        {
            return;
        }

        void Hook()
        {
            if (lb.ItemsSource is INotifyCollectionChanged ncc)
            {
                ncc.CollectionChanged += (_, args) =>
                {
                    if (args.Action == NotifyCollectionChangedAction.Add && lb.Items.Count > 0)
                    {
                        lb.ScrollIntoView(lb.Items[^1]);
                    }
                };
            }
        }

        lb.Loaded += (_, _) => Hook();
        DependencyPropertyDescriptorHelper.OnItemsSourceChanged(lb, Hook);
    }
}

internal static class DependencyPropertyDescriptorHelper
{
    public static void OnItemsSourceChanged(ItemsControl ic, Action callback) =>
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(ItemsControl.ItemsSourceProperty, typeof(ItemsControl))
            ?.AddValueChanged(ic, (_, _) => callback());
}

/// <summary>Draws a small sparkline from a list of numbers (used for the memory chart).</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IEnumerable), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnValues));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.Green, FrameworkPropertyMetadataOptions.AffectsRender));

    public IEnumerable? Values
    {
        get => (IEnumerable?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public Brush Stroke
    {
        get => (Brush)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    private static void OnValues(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is INotifyCollectionChanged ncc)
        {
            ncc.CollectionChanged += (_, _) => ((Sparkline)d).InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var values = Values?.Cast<double>().ToList() ?? [];
        if (values.Count < 2)
        {
            return;
        }

        var max = Math.Max(values.Max(), 1);
        var step = ActualWidth / 59;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            var x0 = ActualWidth - ((values.Count - 1) * step);
            ctx.BeginFigure(new Point(x0, ActualHeight - (values[0] / max * ActualHeight)), false, false);
            for (var i = 1; i < values.Count; i++)
            {
                ctx.LineTo(new Point(x0 + (i * step), ActualHeight - (values[i] / max * ActualHeight * 0.9)), true, true);
            }
        }

        geo.Freeze();
        drawingContext.DrawGeometry(null, new Pen(Stroke, 2) { LineJoin = PenLineJoin.Round }, geo);
    }
}

public sealed class IndexToVisibility : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i && int.TryParse(parameter?.ToString(), out var p) && i == p ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class Bytes : IValueConverter
{
    public static Bytes Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var b = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
        string[] units = ["B", "KB", "MB", "GB"];
        var u = 0;
        while (b >= 1024 && u < units.Length - 1)
        {
            b /= 1024;
            u++;
        }

        return string.Format(Loc.Instance.Culture, "{0:0.#} {1}", b, units[u]);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class AllTrue : IMultiValueConverter
{
    public static AllTrue Visibility { get; } = new();

    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.All(v => v is true) ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
