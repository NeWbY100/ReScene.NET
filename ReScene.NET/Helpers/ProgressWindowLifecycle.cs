using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ReScene.NET.ViewModels;

namespace ReScene.NET.Helpers;

/// <summary>
/// Shares the auto-close / cancel-guard lifecycle that the file-copy and CRC-validation progress
/// windows have in common. Both observe a single "busy" flag on a <see cref="ReconstructorViewModel"/>:
/// when it clears the window auto-closes, and while it is set the window cancels instead of closing.
/// The only per-window differences are which busy flag is watched and which cancel button to drive.
/// </summary>
internal static class ProgressWindowLifecycle
{
    /// <summary>
    /// Wires the window to the view model's busy flag: starts watching <paramref name="busyPropName"/>,
    /// auto-closes when the flag clears, turns the cancel button into a "Cancelling..." action, and
    /// blocks closing (cancelling instead) while the operation is still running. Call once the
    /// window's <see cref="FrameworkElement.DataContext"/> is available (e.g. from its Loaded handler).
    /// </summary>
    public static void Attach(
        Window window,
        ReconstructorViewModel vm,
        Func<bool> isBusy,
        string busyPropName,
        Button cancelButton)
    {
        void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != busyPropName)
            {
                return;
            }

            if (!isBusy())
            {
                vm.PropertyChanged -= OnVmPropertyChanged;
                window.Close();
            }
        }

        void Cancel()
        {
            vm.StopCommand.Execute(null);
            cancelButton.IsEnabled = false;
            cancelButton.Content = "Cancelling...";
        }

        cancelButton.Click += (_, _) => Cancel();

        vm.PropertyChanged += OnVmPropertyChanged;

        window.Closing += (_, e) =>
        {
            if (isBusy())
            {
                // Don't close while the operation is in progress — cancel instead.
                e.Cancel = true;
                Cancel();
                return;
            }

            vm.PropertyChanged -= OnVmPropertyChanged;
        };
    }
}
