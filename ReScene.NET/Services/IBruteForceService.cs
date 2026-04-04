using ReScene.Core;

namespace ReScene.NET.Services;

public interface IBruteForceService
{
    public Task<bool> RunAsync(BruteForceOptions options);
    public void Stop();
    public event EventHandler<BruteForceProgressEventArgs>? Progress;
    public event EventHandler<BruteForceStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<LogEventArgs>? LogMessage;
    public event EventHandler<FileCopyProgressEventArgs>? FileCopyProgress;
    public event EventHandler<CrcValidationProgressEventArgs>? CrcValidationProgress;
}
