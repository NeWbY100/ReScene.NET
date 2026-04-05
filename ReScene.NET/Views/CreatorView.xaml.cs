using System.Windows.Controls;
using ReScene.NET.Helpers;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views;

public partial class CreatorView : UserControl
{
    public CreatorView()
    {
        InitializeComponent();

        Loaded += OnLoaded;
    }

    private void OnLoaded(object _, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not CreatorViewModel vm)
        {
            return;
        }

        TextBoxDropHelper.SetupFileDrop(InputTextBox, path => vm.InputPath = path);
        TextBoxDropHelper.SetupFileDrop(OutputTextBox, path => vm.OutputPath = path);
    }
}
