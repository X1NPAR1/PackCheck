using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Input;
using PackCheck.App.ViewModels;

namespace PackCheck.App.Views;

public partial class AssistantView : UserControl
{
    public AssistantView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is AssistantViewModel vm)
            {
                ((INotifyCollectionChanged)vm.Messages).CollectionChanged += (_, _) => Scroll.ScrollToEnd();
            }
        };
    }

    /// <summary>Enter sends, Shift+Enter adds a line.</summary>
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift && DataContext is AssistantViewModel vm)
        {
            vm.SendCommand.Execute(null);
            e.Handled = true;
        }
    }
}
