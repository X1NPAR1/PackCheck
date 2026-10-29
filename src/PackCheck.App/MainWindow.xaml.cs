using System.ComponentModel;
using System.Windows;
using MessageBox = System.Windows.MessageBox;
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
        StateChanged += (_, _) => RootGrid.Margin = WindowState == WindowState.Maximized && !_fullScreen ? new Thickness(7) : new Thickness(0);
        PreviewMouseDown += OnMouseButtons;
        PreviewKeyDown += OnShortcut;
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public int L, T, R, B;
        public int WL, WT, WR, WB;
        public int Flags;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    /// <summary>The full area of the monitor the window is on, in WPF units (DPI-aware).</summary>
    private Rect MonitorBounds()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var info = new MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref info))
        {
            return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        }

        var m = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var tl = m.Transform(new Point(info.L, info.T));
        var br = m.Transform(new Point(info.R, info.B));
        return new Rect(tl, br);
    }

    /// <summary>Mouse buttons 4/5 go back and forward, the middle button closes the open dialog, panel or server tab.</summary>
    private void OnMouseButtons(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        switch (e.ChangedButton)
        {
            case System.Windows.Input.MouseButton.XButton1:
                e.Handled = vm.GoBack();
                break;
            case System.Windows.Input.MouseButton.XButton2:
                e.Handled = vm.GoForward();
                break;
            case System.Windows.Input.MouseButton.Middle when e.OriginalSource is not System.Windows.Controls.Primitives.TextBoxBase:
                e.Handled = vm.CloseTopmost();
                break;
        }
    }

    private void OnShortcut(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var mods = System.Windows.Input.Keyboard.Modifiers;
        var key = e.Key == System.Windows.Input.Key.System ? e.SystemKey : e.Key;
        const System.Windows.Input.ModifierKeys Ctrl = System.Windows.Input.ModifierKeys.Control;
        const System.Windows.Input.ModifierKeys Alt = System.Windows.Input.ModifierKeys.Alt;
        switch (key)
        {
            case System.Windows.Input.Key.F11 when mods == System.Windows.Input.ModifierKeys.None:
                ToggleFullScreen();
                e.Handled = true;
                break;
            case System.Windows.Input.Key.F1 when mods == System.Windows.Input.ModifierKeys.None:
                vm.ShowShortcuts();
                e.Handled = true;
                break;
            case System.Windows.Input.Key.Left when mods == Alt:
            case System.Windows.Input.Key.BrowserBack:
                e.Handled = vm.GoBack();
                break;
            case System.Windows.Input.Key.Right when mods == Alt:
            case System.Windows.Input.Key.BrowserForward:
                e.Handled = vm.GoForward();
                break;
            case System.Windows.Input.Key.W when mods == Ctrl:
                e.Handled = vm.CloseTopmost();
                break;
            case System.Windows.Input.Key.Tab when (mods & Ctrl) == Ctrl:
                e.Handled = vm.CycleServerTab((mods & System.Windows.Input.ModifierKeys.Shift) != 0 ? -1 : 1);
                break;
            case System.Windows.Input.Key.OemComma when mods == Ctrl:
                vm.Navigate(vm.Settings);
                e.Handled = true;
                break;
            case >= System.Windows.Input.Key.D1 and <= System.Windows.Input.Key.D9 when mods == Ctrl:
                vm.GoToMenu(key - System.Windows.Input.Key.D1);
                e.Handled = true;
                break;
        }
    }

    private bool _fullScreen;
    private WindowState _stateBeforeFullScreen;
    private Rect _boundsBeforeFullScreen;

    /// <summary>F11: a borderless window over the whole monitor the window is on (taskbar included); F11 again returns.</summary>
    public void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        if (_fullScreen)
        {
            _stateBeforeFullScreen = WindowState;
            _boundsBeforeFullScreen = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            var screen = MonitorBounds();
            Left = screen.Left;
            Top = screen.Top;
            Width = screen.Width;
            Height = screen.Height;
            Topmost = true;
            Topmost = false;
            Vm?.Toast(Loc.T("Shell_FullscreenHint"));
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            Left = _boundsBeforeFullScreen.Left;
            Top = _boundsBeforeFullScreen.Top;
            Width = _boundsBeforeFullScreen.Width;
            Height = _boundsBeforeFullScreen.Height;
            WindowState = _stateBeforeFullScreen;
        }

        RootGrid.Margin = WindowState == WindowState.Maximized && !_fullScreen ? new Thickness(7) : new Thickness(0);
        if (Vm is { } vm)
        {
            vm.IsFullScreen = _fullScreen;
        }
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

    /// <summary>Set when the user really quits (tray menu, update), so closing is not turned into hiding.</summary>
    public bool AllowClose { get; set; }

    /// <summary>Set when quitting for an update; the open-ports question would keep the process alive while the updater waits.</summary>
    public bool SkipExitQuestions { get; set; }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        Dispatcher.BeginInvoke(() => _closing = false, System.Windows.Threading.DispatcherPriority.Background);
        base.OnClosing(e);
        if (!AllowClose && App.Tray is not null && App.Settings?.MinimizeToTray == true)
        {
            e.Cancel = true;
            Hide();
            App.Tray.ShowHiddenHint();
            return;
        }

        // Really quitting while servers are open to the internet: offer to close them first.
        if (!_portsChecked && !SkipExitQuestions && App.Settings?.OnlinePorts.Count > 0)
        {
            e.Cancel = true;
            _ = AskAboutOpenPortsAsync();
        }
    }

    private bool _portsChecked;

    private async Task AskAboutOpenPortsAsync()
    {
        Show();
        Activate();
        var ports = string.Join(", ", App.Settings!.OnlinePorts);
        var answer = MessageBox.Show(this, Loc.F("Online_ExitAsk", ports), "PackCheck", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Yes);
        if (answer == MessageBoxResult.Cancel)
        {
            return;
        }

        if (answer == MessageBoxResult.Yes && DataContext is MainViewModel vm && !await vm.Online.CloseAllAsync().ConfigureAwait(true))
        {
            MessageBox.Show(this, Loc.T("Online_ExitCloseFailed"), "PackCheck", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        _portsChecked = true;
        AllowClose = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        Application.Current.Shutdown();
    }

    private bool _closing;

    public void ShowFromTray()
    {
        // Called from the tray while this window may be inside OnClosing (hide-to-tray); showing it then throws.
        if (_closing)
        {
            Dispatcher.BeginInvoke(ShowFromTray, System.Windows.Threading.DispatcherPriority.Background);
            return;
        }

        Show();
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
    }
}
