using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using PackCheck.App.ViewModels;
using PackCheck.Servers;
using Forms = System.Windows.Forms;
using Image = System.Windows.Controls.Image;
using Orientation = System.Windows.Controls.Orientation;
using Cursors = System.Windows.Input.Cursors;
using TextElement = System.Windows.Documents.TextElement;

namespace PackCheck.App.Infrastructure;

/// <summary>What the tray menu can do in the app.</summary>
public sealed record TrayActions(
    Action Open,
    Action Exit,
    Func<IReadOnlyList<ServerItemViewModel>> Servers,
    Action<ServerItemViewModel> ShowServer,
    Action<int> ShowPage);

/// <summary>
/// System tray icon: closing the window hides it while servers keep running.
/// Right click opens a themed menu with every server's live state, start/stop, and shortcuts.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly TrayActions _actions;
    private Window? _menu;
    private bool _hintShown;

    public TrayIcon(TrayActions actions)
    {
        _actions = actions;
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/PackCheck;component/Assets/app.ico"))!.Stream;
        _icon = new Forms.NotifyIcon { Icon = new System.Drawing.Icon(stream), Text = "PackCheck", Visible = true };
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Right)
            {
                ShowMenu();
            }
        };
        _icon.DoubleClick += (_, _) => actions.Open();
    }

    /// <summary>Keeps the hover text useful: how many servers run.</summary>
    public void UpdateTooltip()
    {
        var servers = _actions.Servers();
        var running = servers.Count(s => s.IsRunning);
        var text = running == 0 ? "PackCheck" : $"PackCheck · {Loc.F("Tray_RunningCount", running, servers.Count)}";
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    public void ShowHiddenHint()
    {
        if (_hintShown)
        {
            return;
        }

        _hintShown = true;
        _icon.ShowBalloonTip(4000, "PackCheck", Loc.T("Tray_Running"), Forms.ToolTipIcon.None);
    }

    /// <summary>Draws the menu into a PNG (for screenshots and UI checks; no click needed).</summary>
    public void RenderMenu(string path)
    {
        var menu = BuildMenu();
        menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        menu.Arrange(new Rect(menu.DesiredSize));
        menu.UpdateLayout();
        var bmp = new RenderTargetBitmap((int)Math.Ceiling(menu.ActualWidth * 2 + 48), (int)Math.Ceiling(menu.ActualHeight * 2 + 48), 192, 192, PixelFormats.Pbgra32);
        bmp.Render(menu);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var file = System.IO.File.Create(path);
        encoder.Save(file);
    }

    public void ShowMenu()
    {
        _menu?.Close();
        var content = BuildMenu();
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = true,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            Content = content,
            Left = -10000,
            Top = -10000,
        };
        window.Deactivated += (_, _) => window.Close();
        window.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                window.Close();
            }
        };
        window.Closed += (_, _) =>
        {
            if (_menu == window)
            {
                _menu = null;
            }
        };
        _menu = window;
        window.Show();

        // Place the menu's bottom-right corner at the cursor, kept inside the work area (pixels -> DIPs).
        var cursor = Forms.Cursor.Position;
        var area = Forms.Screen.FromPoint(cursor).WorkingArea;
        var m = PresentationSource.FromVisual(window)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var at = m.Transform(new Point(cursor.X, cursor.Y));
        var min = m.Transform(new Point(area.Left, area.Top));
        var max = m.Transform(new Point(area.Right, area.Bottom));
        window.Left = Math.Clamp(at.X - window.ActualWidth, min.X, max.X - window.ActualWidth);
        window.Top = Math.Clamp(at.Y - window.ActualHeight, min.Y, max.Y - window.ActualHeight);
        window.Activate();
    }

    private Border BuildMenu()
    {
        var servers = _actions.Servers();
        var running = servers.Count(s => s.IsRunning);
        var panel = new StackPanel { Width = 330 };

        // header
        var header = new DockPanel { Margin = new Thickness(14, 12, 14, 10) };
        var logo = new Image { Source = new BitmapImage(new Uri("pack://application:,,,/PackCheck;component/Assets/cube-256.png")), Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0) };
        DockPanel.SetDock(logo, Dock.Left);
        header.Children.Add(logo);
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(Text("PackCheck", 14, FontWeights.Bold, "TextBrush", "DisplayFont"));
        titles.Children.Add(Text($"v{Updater.UpdateService.CurrentVersion.ToString(3)} · {Loc.F("Tray_RunningCount", running, servers.Count)}", 11.5, FontWeights.Normal, "TextMutedBrush"));
        header.Children.Add(titles);
        panel.Children.Add(header);
        panel.Children.Add(Line());

        // servers
        panel.Children.Add(SectionLabel(Loc.T("Nav_Servers")));
        if (servers.Count == 0)
        {
            panel.Children.Add(Text(Loc.T("Tray_NoServers"), 12, FontWeights.Normal, "TextMutedBrush", margin: new Thickness(14, 2, 14, 8)));
        }

        foreach (var s in servers.Take(8))
        {
            panel.Children.Add(ServerRow(s));
        }

        panel.Children.Add(Line());
        panel.Children.Add(Item(Loc.T("Tray_Open"), "IconServers", () => _actions.Open()));
        panel.Children.Add(Item(Loc.T("Nav_Validate"), "IconValidate", () => _actions.ShowPage(1)));
        panel.Children.Add(Item(Loc.T("Nav_Settings"), "IconSettings", () => _actions.ShowPage(3)));
        if (running > 0)
        {
            panel.Children.Add(Item(Loc.T("Tray_StopAll"), null, () =>
            {
                foreach (var s in servers.Where(x => x.IsRunning))
                {
                    s.StopCommand.Execute(null);
                }
            }));
        }

        panel.Children.Add(Line());
        panel.Children.Add(Item(Loc.T("Tray_Exit"), null, () => _actions.Exit(), "FailBrush"));

        var card = new Border
        {
            Child = panel,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0, 0, 0, 6),
            Margin = new Thickness(12),
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.35 },
        };
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        card.SetResourceReference(TextElement.FontFamilyProperty, "UiFont");
        return card;
    }

    private Border ServerRow(ServerItemViewModel s)
    {
        var row = new DockPanel { Margin = new Thickness(0) };
        var dot = new Ellipse { Width = 9, Height = 9, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        dot.SetResourceReference(Shape.FillProperty, s.State switch
        {
            ServerState.Running => "PassBrush",
            ServerState.Starting or ServerState.Stopping => "WarnBrush",
            ServerState.Crashed => "FailBrush",
            _ => "SkipBrush",
        });
        DockPanel.SetDock(dot, Dock.Left);

        var action = new Button
        {
            Content = Loc.T(s.IsRunning ? "Servers_Stop" : "Servers_Start"),
            Padding = new Thickness(10, 3, 10, 3),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Command = s.IsRunning ? s.StopCommand : s.StartCommand,
        };
        action.SetResourceReference(FrameworkElement.StyleProperty, s.IsRunning ? "BaseButton" : "PrimaryButton");
        action.Click += (_, _) => _menu?.Close();
        DockPanel.SetDock(action, Dock.Right);

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(Text(s.Name, 13, FontWeights.SemiBold, "TextBrush"));
        var detail = $"{s.Definition.Platform} {s.Definition.MinecraftVersion} · :{s.Definition.Port} · {s.StateText}";
        if (s.IsRunning)
        {
            detail += $" · {s.Uptime} · {s.PlayersText}";
        }

        info.Children.Add(Text(detail, 11.5, FontWeights.Normal, "TextMutedBrush"));

        row.Children.Add(dot);
        row.Children.Add(action);
        row.Children.Add(info);
        return Clickable(row, () => _actions.ShowServer(s), new Thickness(14, 6, 10, 6));
    }

    private Border Item(string text, string? icon, Action click, string brush = "TextBrush")
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var path = new Path { Width = 15, Height = 15, Stretch = Stretch.Uniform, StrokeThickness = 1.6, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        if (icon is not null && Application.Current.TryFindResource(icon) is Geometry g)
        {
            path.Data = g;
        }

        path.SetResourceReference(Shape.StrokeProperty, brush);
        row.Children.Add(path);
        row.Children.Add(Text(text, 13, FontWeights.Normal, brush));
        return Clickable(row, click, new Thickness(14, 8, 14, 8));
    }

    private Border Clickable(FrameworkElement child, Action click, Thickness padding)
    {
        var b = new Border { Child = child, Padding = padding, Background = Brushes.Transparent, Cursor = Cursors.Hand, Margin = new Thickness(6, 0, 6, 0), CornerRadius = new CornerRadius(7) };
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "Surface2Brush");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindParent<Button>(d) is not null)
            {
                return; // the row's own button handled it
            }

            _menu?.Close();
            click();
        };
        return b;
    }

    private static T? FindParent<T>(DependencyObject d)
        where T : DependencyObject
    {
        for (var p = d; p is not null; p = VisualTreeHelper.GetParent(p))
        {
            if (p is T t)
            {
                return t;
            }
        }

        return null;
    }

    private static TextBlock Text(string text, double size, FontWeight weight, string brush, string? font = null, Thickness? margin = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextTrimming = TextTrimming.CharacterEllipsis, Margin = margin ?? new Thickness(0) };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        if (font is not null)
        {
            t.SetResourceReference(TextBlock.FontFamilyProperty, font);
        }

        return t;
    }

    private static TextBlock SectionLabel(string text) =>
        Text(text.ToUpper(Loc.Instance.Culture), 10.5, FontWeights.SemiBold, "TextFaintBrush", margin: new Thickness(14, 8, 14, 4));

    private static Border Line()
    {
        var b = new Border { Height = 1, Margin = new Thickness(0, 4, 0, 4) };
        b.SetResourceReference(Border.BackgroundProperty, "LineBrush");
        return b;
    }

    public void Dispose()
    {
        _menu?.Close();
        _icon.Visible = false;
        _icon.Dispose();
    }
}
