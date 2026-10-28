using System.Windows.Controls;
using System.Windows.Input;
using PackCheck.App.ViewModels;

namespace PackCheck.App.Views;

public partial class ServersView : UserControl
{
    public ServersView() => InitializeComponent();

    private void OnCommandKey(object sender, KeyEventArgs e)
    {
        if (((System.Windows.FrameworkElement)sender).DataContext is not ServerItemViewModel vm)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter:
                vm.SendCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Up:
                vm.HistoryStep(-1);
                e.Handled = true;
                break;
            case Key.Down:
                vm.HistoryStep(1);
                e.Handled = true;
                break;
        }
    }
}
