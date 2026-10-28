using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PackCheck.App.ViewModels;

namespace PackCheck.App.Views;

public partial class FilesView : UserControl
{
    public FilesView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => (DataContext as FilesViewModel)?.Refresh();
    }

    private FilesViewModel? Vm => DataContext as FilesViewModel;

    private void OnOpen(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: FileEntry entry })
        {
            Vm?.OpenCommand.Execute(entry);
            e.Handled = true;
        }
    }

    private void OnItemKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is ListBoxItem { DataContext: FileEntry entry })
        {
            Vm?.OpenCommand.Execute(entry);
            e.Handled = true;
        }
        else if (e.Key == Key.Back)
        {
            Vm?.UpCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Files dragged from Explorer are copied into the current folder.</summary>
    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            Vm?.Import(paths);
        }
    }
}
