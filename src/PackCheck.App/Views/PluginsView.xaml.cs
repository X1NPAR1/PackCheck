using System.Windows;
using System.Windows.Controls;
using PackCheck.App.ViewModels;

namespace PackCheck.App.Views;

public partial class PluginsView : UserControl
{
    public PluginsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ContentBrowserViewModel vm)
            {
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ContentBrowserViewModel.Page))
                    {
                        ResultsScroll.ScrollToTop();
                    }
                };
            }
        };
    }

    private void OnCreateServer(object sender, RoutedEventArgs e)
    {
        if (MainViewModel.Shell is { } shell)
        {
            shell.Navigate(shell.Servers);
            shell.Servers.NewServerCommand.Execute(null);
        }
    }
}
