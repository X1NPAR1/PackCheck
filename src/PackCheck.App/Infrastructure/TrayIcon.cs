using System.Windows;
using Forms = System.Windows.Forms;

namespace PackCheck.App.Infrastructure;

/// <summary>System tray icon: closing the window hides it while servers keep running.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private bool _hintShown;

    public TrayIcon(Action open, Action exit)
    {
        var stream = Application.GetResourceStream(new Uri("pack://application:,,,/PackCheck;component/Assets/app.ico"))!.Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream),
            Text = "PackCheck",
            Visible = true,
            ContextMenuStrip = new Forms.ContextMenuStrip(),
        };
        _icon.DoubleClick += (_, _) => open();
        _icon.ContextMenuStrip.Opening += (_, _) =>
        {
            var menu = _icon.ContextMenuStrip!;
            menu.Items.Clear();
            menu.Items.Add(Loc.T("Tray_Open"), null, (_, _) => open());
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add(Loc.T("Tray_Exit"), null, (_, _) => exit());
        };
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

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
