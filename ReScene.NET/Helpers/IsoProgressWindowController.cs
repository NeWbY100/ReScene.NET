using System.Windows;
using System.Windows.Threading;
using ReScene.NET.Views;

namespace ReScene.NET.Helpers;

/// <summary>
/// Manages the modal <see cref="ISOProgressWindow"/> shared by the SRS Creator and SRS Reconstructor
/// views. Both open the dialog when ISO processing starts, cancel the underlying operation if the user
/// closes the dialog while still processing, and close the dialog when processing finishes — only the
/// owning control, the "is processing" check, and the cancel action differ between the two views.
/// </summary>
internal sealed class IsoProgressWindowController(FrameworkElement owner, Func<bool> isProcessing, Action cancel)
{
    private readonly FrameworkElement _owner = owner;
    private readonly Func<bool> _isProcessing = isProcessing;
    private readonly Action _cancel = cancel;

    private ISOProgressWindow? _isoWindow;

    /// <summary>
    /// Opens (when <paramref name="processing"/> is <see langword="true"/>) or closes the ISO progress
    /// dialog. Call from the view model's <c>ISOProcessing</c> property-changed notification.
    /// </summary>
    public void OnProcessingChanged(bool processing)
    {
        Dispatcher dispatcher = _owner.Dispatcher;

        if (processing)
        {
            dispatcher.BeginInvoke(DispatcherPriority.Normal, () =>
            {
                _isoWindow = new ISOProgressWindow
                {
                    Owner = Window.GetWindow(_owner),
                    DataContext = _owner.DataContext
                };

                _isoWindow.Closed += (_, _) =>
                {
                    // If the window was cancelled (not closed by code), cancel the operation.
                    if (_isProcessing())
                    {
                        _cancel();
                    }

                    _isoWindow = null;
                };

                _isoWindow.ShowDialog();
            });
        }
        else
        {
            dispatcher.BeginInvoke(() =>
            {
                _isoWindow?.Close();
                _isoWindow = null;
            });
        }
    }
}
