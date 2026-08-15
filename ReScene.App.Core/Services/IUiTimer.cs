namespace ReScene.App.Core.Services;

/// <summary>
/// A recurring timer whose tick is raised on the UI thread. Abstracted so view-models can drive
/// periodic UI updates (e.g. an elapsed-time display) without referencing a UI framework's timer.
/// </summary>
public interface IUiTimer
{
    /// <summary>Starts (or restarts) the timer.</summary>
    public void Start();

    /// <summary>Stops the timer. Safe to call when already stopped.</summary>
    public void Stop();
}
