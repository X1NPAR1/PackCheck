using System.Windows.Controls;
using System.Windows.Media.Imaging;
using PackCheck.App.Infrastructure;

namespace PackCheck.App.Views;

public partial class AboutView : UserControl
{
    public AboutView()
    {
        InitializeComponent();
        void Update() => Wordmark.Source = new BitmapImage(new Uri($"pack://application:,,,/PackCheck;component/Assets/{(ThemeManager.IsDark ? "logo-wordmark.png" : "logo-wordmark-dark.png")}"));
        Update();
        ThemeManager.ThemeChanged += (_, _) => Update();
    }
}
