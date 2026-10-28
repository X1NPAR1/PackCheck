using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using PackCheck.App.Infrastructure;
using PackCheck.App.ViewModels;

namespace PackCheck.App;

public partial class MainWindow : Window
{
    private int _lastIndex;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel vm)
            {
                vm.PropertyChanged += OnVmChanged;
            }
        };
        StateChanged += (_, _) => RootGrid.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    }

    public void FadeIn() => BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.D(260)));

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.NavIndex) || sender is not MainViewModel vm || !Motion.Enabled)
        {
            return;
        }

        var dir = vm.NavIndex >= _lastIndex ? 1 : -1;
        _lastIndex = vm.NavIndex;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        PageShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24 * dir, 0, Motion.D(280)) { EasingFunction = ease });
        PageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, Motion.D(240)));
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximize(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Application.Current.Shutdown();
    }
}
