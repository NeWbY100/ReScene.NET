namespace ReScene.App.Core.Services;

/// <summary>
/// Creates <see cref="IUiTimer"/> instances. Injected into view-models so they can create a
/// UI-thread timer without depending on a concrete UI framework.
/// </summary>
public interface IUiTimerFactory
{
    /// <summary>
    /// Creates a stopped timer that raises <paramref name="onTick"/> on the UI thread every
    /// <paramref name="interval"/> once started.
    /// </summary>
    public IUiTimer Create(TimeSpan interval, Action onTick);
}
