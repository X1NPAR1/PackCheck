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

/// <summary>Health level (0 excellent … 4 critical, -1 none) to its theme brush.</summary>
public sealed class HealthBrush : IValueConverter
{
    public static HealthBrush Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        Application.Current.TryFindResource(value is int i and >= 0 and <= 4 ? $"Health{i}Brush" : "HealthNoneBrush");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Health level to its localized word ("Excellent" …), empty when there is no data.</summary>
public sealed class HealthText : IValueConverter
{
    public static HealthText Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i and >= 0 and <= 4 ? Loc.T("Health_" + i) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Console line level to the fixed console palette.</summary>
public sealed class ConsoleBrush : IValueConverter
{
    public static ConsoleBrush Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Application.Current.TryFindResource((value as string) switch
    {
        "Warn" => "ConsoleWarnBrush",
        "Error" => "ConsoleErrorBrush",
        "Command" => "ConsoleCmdBrush",
        _ => "ConsoleTextBrush",
    });

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class CountToBool : IValueConverter
{
    public static CountToBool Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int i && i > 0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Localizes a resource key held by the bound value.</summary>
public sealed class LocKey : IValueConverter
{
    public static LocKey Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string k ? Loc.T(k) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class PageTitle : IValueConverter
{
    public static PageTitle Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Loc.T(value is true ? "Mods_Title" : "Plugins_Title");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NoServerText : IValueConverter
{
    public static NoServerText Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Loc.T(value is true ? "Browse_NoServerMods" : "Browse_NoServerPlugins");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Remote icon to an image; cached, decoded small, failures (e.g. unsupported formats) yield no image.</summary>
public sealed class UriToImage : IValueConverter
{
    private static readonly Dictionary<Uri, System.Windows.Media.ImageSource?> Cache = [];

    public static UriToImage Instance { get; } = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not Uri uri || uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        if (Cache.TryGetValue(uri, out var cached))
        {
            return cached;
        }

        try
        {
            var img = new System.Windows.Media.Imaging.BitmapImage();
            img.BeginInit();
            img.UriSource = uri;
            img.DecodePixelWidth = 96;
            img.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
            img.EndInit();
            img.DownloadFailed += (_, _) => Cache[uri] = null;
            Cache[uri] = img;
            return img;
        }
        catch (Exception ex) when (ex is NotSupportedException or System.IO.IOException or InvalidOperationException)
        {
            Cache[uri] = null;
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Formats the bound value with the localized string whose key is the converter parameter.</summary>
public sealed class FormatKey : IValueConverter
{
    public static FormatKey Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Loc.F(parameter as string ?? string.Empty, value ?? string.Empty);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Type icons for the file list, drawn as vector geometry (no image assets).</summary>
public sealed class FileIcon : IValueConverter
{
    private const string Page = "M5,2 H14 L19,7 V22 H5 Z M14,2 V7 H19";
    private static readonly Dictionary<string, (string Geometry, string Color)> Icons = new(StringComparer.OrdinalIgnoreCase)
    {
        ["folder"] = ("M2,6 H9 L11,8 H22 V20 H2 Z", "#D9B44A"),
        ["json"] = (Page + " M9,11 C8,11 8,12 8,13 C8,14 7,14 7,14 C7,14 8,14 8,15 C8,16 8,17 9,17 M15,11 C16,11 16,12 16,13 C16,14 17,14 17,14 C17,14 16,14 16,15 C16,16 16,17 15,17", "#E1C27A"),
        ["yml"] = (Page + " M8,11 H16 M8,14 H14 M8,17 H16", "#C77DDB"),
        ["yaml"] = (Page + " M8,11 H16 M8,14 H14 M8,17 H16", "#C77DDB"),
        ["toml"] = (Page + " M8,11 H16 M8,14 H14 M8,17 H16", "#7FB3AD"),
        ["properties"] = (Page + " M8,12 H10 M12,12 H16 M8,16 H10 M12,16 H16", "#8FCB6B"),
        ["cfg"] = (Page + " M8,12 H10 M12,12 H16 M8,16 H10 M12,16 H16", "#8FCB6B"),
        ["conf"] = (Page + " M8,12 H10 M12,12 H16 M8,16 H10 M12,16 H16", "#8FCB6B"),
        ["ini"] = (Page + " M8,12 H10 M12,12 H16 M8,16 H10 M12,16 H16", "#8FCB6B"),
        ["jar"] = ("M6,7 H18 L17,21 H7 Z M6,7 C6,4 18,4 18,7 M9,11 V17 M12,11 V17 M15,11 V17", "#E0894F"),
        ["zip"] = (Page + " M11,3 V5 M12,5 V7 M11,7 V9 M12,9 V11 M10,12 H13 V15 H10 Z", "#A9A8A0"),
        ["gz"] = (Page + " M11,3 V5 M12,5 V7 M11,7 V9 M12,9 V11 M10,12 H13 V15 H10 Z", "#A9A8A0"),
        ["log"] = (Page + " M8,11 H16 M8,14 H16 M8,17 H12", "#9C9D96"),
        ["txt"] = (Page + " M8,11 H16 M8,14 H16 M8,17 H12", "#C9C9C2"),
        ["md"] = (Page + " M8,11 H16 M8,14 H16 M8,17 H12", "#C9C9C2"),
        ["png"] = (Page + " M8,17 L11,13 L13,15 L15,12 L17,17 Z M9.5,10.5 A1,1 0 1 1 9.49,10.5", "#6FAE8C"),
        ["jpg"] = (Page + " M8,17 L11,13 L13,15 L15,12 L17,17 Z M9.5,10.5 A1,1 0 1 1 9.49,10.5", "#6FAE8C"),
        ["dat"] = (Page + " M8,11 A4,1.5 0 1 0 16,11 A4,1.5 0 1 0 8,11 M8,11 V16 A4,1.5 0 0 0 16,16 V11", "#7F9FC8"),
        ["mca"] = (Page + " M8,11 A4,1.5 0 1 0 16,11 A4,1.5 0 1 0 8,11 M8,11 V16 A4,1.5 0 0 0 16,16 V11", "#7F9FC8"),
        ["bat"] = (Page + " M8,12 L10,14 L8,16 M12,16 H16", "#8FD46A"),
        ["sh"] = (Page + " M8,12 L10,14 L8,16 M12,16 H16", "#8FD46A"),
        ["js"] = (Page + " M8,12 L10,14 L8,16 M12,16 H16", "#E6C24D"),
        ["xml"] = (Page + " M10,11 L8,14 L10,17 M14,11 L16,14 L14,17", "#E08A5A"),
    };

    private readonly int _part;

    private FileIcon(int part) => _part = part;

    public static FileIcon Geometry { get; } = new(0);

    public static FileIcon Fill { get; } = new(1);

    public static FileIcon Stroke { get; } = new(2);

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var (geometry, color) = Icons.TryGetValue(value as string ?? string.Empty, out var icon) ? icon : (Page, "#9C9D96");
        var brush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(color)!;
        return _part switch
        {
            0 => System.Windows.Media.Geometry.Parse(geometry),
            1 => value as string == "folder" ? brush : null,
            _ => brush,
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when a count is above one (for example a filter list that has more than its "All" entry).</summary>
public sealed class CountToVisibility : IValueConverter
{
    public static CountToVisibility MoreThanOne { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is int i && i > 1 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>"a, b, c" for a string list; "–" when empty.</summary>
public sealed class JoinList : IValueConverter
{
    public static JoinList Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is IEnumerable<string> list && list.Any() ? string.Join(", ", list) : "–";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class AiProviderName : IValueConverter
{
    public static AiProviderName Instance { get; } = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Ai.AiProviderKind k ? Ai.AiProviders.DisplayName(k) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
