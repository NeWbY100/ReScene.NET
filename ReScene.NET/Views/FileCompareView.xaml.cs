using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views;

public partial class FileCompareView : UserControl
{
    private static readonly string[] _supportedExtensions = [".srr", ".srs", ".rar"];

    private static readonly Brush _activeDropBrush = new SolidColorBrush(Color.FromArgb(0x60, 0x00, 0x78, 0xD4));
    private static readonly Brush _inactiveDropBrush = new SolidColorBrush(Color.FromArgb(0x30, 0x00, 0x78, 0xD4));

    public FileCompareView()
    {
        InitializeComponent();
        AllowDrop = true;
        PreviewDragOver += OnPreviewDragOver;
        PreviewDrop += OnPreviewDrop;
        PreviewDragLeave += OnPreviewDragLeave;
    }

    private void LeftTree_SelectedItemChanged(object _, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is FileCompareViewModel vm)
        {
            vm.SelectedLeftTreeNode = e.NewValue as TreeNodeViewModel;
        }
    }

    private void RightTree_SelectedItemChanged(object _, RoutedPropertyChangedEventArgs<object> e)
    {
        if (DataContext is FileCompareViewModel vm)
        {
            vm.SelectedRightTreeNode = e.NewValue as TreeNodeViewModel;
        }
    }

    private void OnPreviewDragOver(object _, DragEventArgs e)
    {
        if (!HasSupportedFile(e))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;

        // Show overlays and highlight the active side
        bool isLeft = IsOnLeftSide(e);
        LeftDropOverlay.Visibility = Visibility.Visible;
        RightDropOverlay.Visibility = Visibility.Visible;
        LeftDropOverlay.Background = isLeft ? _activeDropBrush : _inactiveDropBrush;
        RightDropOverlay.Background = isLeft ? _inactiveDropBrush : _activeDropBrush;
    }

    private void OnPreviewDragLeave(object _, DragEventArgs e)
    {
        LeftDropOverlay.Visibility = Visibility.Collapsed;
        RightDropOverlay.Visibility = Visibility.Collapsed;
    }

    private void OnPreviewDrop(object _, DragEventArgs e)
    {
        LeftDropOverlay.Visibility = Visibility.Collapsed;
        RightDropOverlay.Visibility = Visibility.Collapsed;

        string? file = GetDroppedFile(e);
        if (file is null || DataContext is not FileCompareViewModel vm)
        {
            return;
        }

        if (IsOnLeftSide(e))
        {
            vm.LoadLeftFile(file);
        }
        else
        {
            vm.LoadRightFile(file);
        }

        e.Handled = true;
    }

    private bool IsOnLeftSide(DragEventArgs e)
    {
        Point pos = e.GetPosition(this);
        return pos.X < ActualWidth / 2;
    }

    private static bool HasSupportedFile(DragEventArgs e)
    {
        return e.Data.GetDataPresent(DataFormats.FileDrop)
            && e.Data.GetData(DataFormats.FileDrop) is string[] files
            && files.Length > 0
            && IsSupportedFile(files[0]);
    }

    private static string? GetDroppedFile(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return null;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return null;
        }

        return IsSupportedFile(files[0]) ? files[0] : null;
    }

    private static DataGrid? GetSourceDataGrid(object sender)
    {
        if (sender is MenuItem { Parent: ContextMenu { PlacementTarget: DataGrid grid } })
        {
            return grid;
        }

        return null;
    }

    private void OnCopyPropertyNameClick(object sender, RoutedEventArgs e)
    {
        if (GetSourceDataGrid(sender)?.SelectedItem is PropertyItem item)
        {
            Clipboard.SetText(item.Name);
        }
    }

    private void OnCopyPropertyValueClick(object sender, RoutedEventArgs e)
    {
        if (GetSourceDataGrid(sender)?.SelectedItem is PropertyItem item)
        {
            Clipboard.SetText(item.Value);
        }
    }

    private void OnCopyPropertyClick(object sender, RoutedEventArgs e)
    {
        if (GetSourceDataGrid(sender)?.SelectedItem is PropertyItem item)
        {
            Clipboard.SetText($"{item.Name}: {item.Value}");
        }
    }

    private static bool IsSupportedFile(string path)
    {
        string ext = Path.GetExtension(path);
        foreach (string supported in _supportedExtensions)
        {
            if (ext.Equals(supported, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
