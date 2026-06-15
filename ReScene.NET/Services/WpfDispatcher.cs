using System.Windows;
using System.Windows.Threading;

namespace ReScene.NET.Services;

/// <summary>
/// <see cref="IUiDispatcher"/> backed by <see cref="Application.Current"/>'s dispatcher.
/// When there is no running <see cref="Application"/> (e.g. unit tests or non-UI contexts) the
/// action is run inline so callers stay safe.
/// </summary>
public sealed class WpfDispatcher : IUiDispatcher
{
    public void Invoke(Action action)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    public void Post(Action action)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }

    public void Post(Action action, DispatcherPriority priority)
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(priority, action);
    }

    public bool CheckAccess()
    {
        Dispatcher? dispatcher = Application.Current?.Dispatcher;
        return dispatcher?.CheckAccess() ?? true;
    }
}
