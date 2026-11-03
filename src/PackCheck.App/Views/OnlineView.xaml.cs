using System.Windows;
using System.Windows.Controls;
using PackCheck.App.ViewModels;

namespace PackCheck.App.Views;

public partial class OnlineView : UserControl
{
    public OnlineView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is OnlineViewModel old)
            {
                old.TunnelCardRequested -= ShowTunnelCard;
            }

            if (e.NewValue is OnlineViewModel fresh)
            {
                fresh.TunnelCardRequested += ShowTunnelCard;
            }

            if (DataContext is OnlineViewModel vm && Token.Password != vm.DuckToken)
            {
                Token.Password = vm.DuckToken;
            }
        };
    }

    private void ShowTunnelCard()
    {
        TunnelCard.BringIntoView();
        TunnelCard.Focus();
    }

    private void OnTokenChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is OnlineViewModel vm)
        {
            vm.DuckToken = Token.Password;
        }
    }
}
