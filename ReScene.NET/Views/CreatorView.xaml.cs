using System.Windows;
using System.Windows.Controls;
using ReScene.NET.Helpers;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views;

public partial class CreatorView : UserControl
{
    // The "Stored As" value before an inline edit, so a duplicate edit can be reverted.
    private string? _storedNameBeforeEdit;

    public CreatorView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object _, RoutedEventArgs e)
    {
        if (DataContext is not CreatorViewModel vm)
        {
            return;
        }

        TextBoxDropHelper.SetupFileDrop(InputTextBox, path => vm.InputPath = path);
        TextBoxDropHelper.SetupFileDrop(OutputTextBox, path => vm.OutputPath = path);
    }

    private void OnStoredFilesDragOver(object _, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void OnStoredFilesDrop(object _, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return;
        }

        if (DataContext is CreatorViewModel vm)
        {
            vm.AddStoredFiles(files);
        }

        e.Handled = true;
    }

    private void OnStoredNameBeginningEdit(object _, DataGridBeginningEditEventArgs e)
    {
        _storedNameBeforeEdit = (e.Row.Item as CreatorViewModel.StoredFileItem)?.StoredName;
    }

    private void OnStoredNameCellEditEnding(object _, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit
            || DataContext is not CreatorViewModel vm
            || e.Row.Item is not CreatorViewModel.StoredFileItem item
            || e.EditingElement is not TextBox editor)
        {
            return;
        }

        string newName = editor.Text.Replace('\\', '/').Trim();

        // Reject a rename onto a name another stored file already uses; otherwise normalize the
        // committed value to the SRR's key space (forward slashes). The binding is live
        // (PropertyChanged), so the model already holds the typed text — set it back either way.
        if (!newName.Equals(_storedNameBeforeEdit, StringComparison.OrdinalIgnoreCase)
            && vm.IsStoredNameTaken(newName, item))
        {
            editor.Text = _storedNameBeforeEdit;
            item.StoredName = _storedNameBeforeEdit ?? item.StoredName;
            MessageBox.Show(
                $"A stored file is already named \"{newName}\". The name was not changed.",
                "Duplicate stored name", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            editor.Text = newName;
            item.StoredName = newName;
        }
    }
}
