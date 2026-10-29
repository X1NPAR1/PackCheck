using System.Collections.Specialized;
using System.Windows;
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
        PreviewKeyDown += OnPageKey;
    }

    /// <summary>Enter sends, Shift+Enter adds a line, Ctrl+V pastes screenshots and copied files as attachments.</summary>
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (DataContext is not AssistantViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers != ModifierKeys.Shift)
        {
            vm.SendCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && PasteAttachments(vm))
        {
            e.Handled = true;
        }
    }

    /// <summary>Ctrl+H history, Ctrl+N new chat, Ctrl+O attach a file.</summary>
    private void OnPageKey(object sender, KeyEventArgs e)
    {
        if (DataContext is not AssistantViewModel vm || Keyboard.Modifiers != ModifierKeys.Control)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.H:
                vm.IsHistoryOpen = !vm.IsHistoryOpen;
                e.Handled = true;
                break;
            case Key.N:
                vm.NewChatCommand.Execute(null);
                InputBox.Focus();
                e.Handled = true;
                break;
            case Key.O:
                vm.AttachFilesCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private static bool PasteAttachments(AssistantViewModel vm)
    {
        try
        {
            if (Clipboard.ContainsFileDropList())
            {
                vm.AddFiles(Clipboard.GetFileDropList().Cast<string>());
                return true;
            }

            if (Clipboard.ContainsImage() && Clipboard.GetImage() is { } image)
            {
                vm.AddImage(image);
                return true;
            }
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Clipboard busy (another program holds it); let the normal paste run.
        }

        return false;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is AssistantViewModel vm && e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            vm.AddFiles(files);
            e.Handled = true;
        }
    }
}
