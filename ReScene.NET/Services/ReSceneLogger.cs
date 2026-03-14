using ReScene.Core;

namespace ReScene.NET.Services;

/// <summary>
/// An IReSceneLogger implementation that fires events for UI consumption.
/// </summary>
public class ReSceneLogger : IReSceneLogger
{
    public event EventHandler<LogEventArgs>? Logged;

    public void Debug(object? sender, string message, LogTarget target = LogTarget.System)
    {
        Logged?.Invoke(sender, new LogEventArgs($"[DEBUG] {message}", target));
    }

    public void Information(object? sender, string message, LogTarget target = LogTarget.System)
    {
        Logged?.Invoke(sender, new LogEventArgs($"[INFO] {message}", target));
    }

    public void Warning(object? sender, string message, LogTarget target = LogTarget.System)
    {
        Logged?.Invoke(sender, new LogEventArgs($"[WARNING] {message}", target));
    }

    public void Error(object? sender, string message, LogTarget target = LogTarget.System)
    {
        Logged?.Invoke(sender, new LogEventArgs($"[ERROR] {message}", target));
    }

    public void Error(object? sender, Exception exception, string message, LogTarget target = LogTarget.System)
    {
        Logged?.Invoke(sender, new LogEventArgs($"[ERROR] {message}: {exception.Message}", target));
    }

    public void Verbose(object? sender, string message)
    {
        // Don't fire events for verbose as it may be too noisy
    }
}
