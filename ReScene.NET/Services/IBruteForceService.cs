using ReScene.Core;

namespace ReScene.NET.Services;

public interface IBruteForceService
{
    Task<bool> RunAsync(BruteForceOptions options);
    void Stop();
    event EventHandler<BruteForceProgressEventArgs>? Progress;
    event EventHandler<BruteForceStatusChangedEventArgs>? StatusChanged;
    event EventHandler<LogEventArgs>? LogMessage;
    event EventHandler<FileCopyProgressEventArgs>? FileCopyProgress;
    event EventHandler<CrcValidationProgressEventArgs>? CrcValidationProgress;
}
