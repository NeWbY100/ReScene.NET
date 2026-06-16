using System.Windows;
using ReScene.NET.Helpers;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Views;

public partial class FileCopyProgressWindow : Window
{
    public FileCopyProgressWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => DarkTitleBar.Enable(this);
        Loaded += (_, _) =>
        {
            if (DataContext is ReconstructorViewModel vm)
            {
                ProgressWindowLifecycle.Attach(this, vm, () => vm.IsCopying,
                    nameof(ReconstructorViewModel.IsCopying), btnCancel);
            }
        };
    }
}
