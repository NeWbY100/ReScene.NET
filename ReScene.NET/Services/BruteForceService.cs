using ReScene.Core;

namespace ReScene.NET.Services;

/// <summary>
/// Wraps Core.Manager to provide brute-force RAR reconstruction as a service.
/// </summary>
public class BruteForceService : IBruteForceService
{
    public event EventHandler<BruteForceProgressEventArgs>? Progress;
    public event EventHandler<BruteForceStatusChangedEventArgs>? StatusChanged;
    public event EventHandler<LogEventArgs>? LogMessage;
    public event EventHandler<FileCopyProgressEventArgs>? FileCopyProgress;
    public event EventHandler<CRCValidationProgressEventArgs>? CRCValidationProgress;
    public event EventHandler<TimestampPreservationFailedEventArgs>? TimestampPreservationFailed;

    private Manager? _manager;

    public async Task<bool> RunAsync(BruteForceOptions options)
    {
        var logger = new ReSceneLogger();
        logger.Logged += (s, e) => LogMessage?.Invoke(s, e);

        _manager = new Manager(logger);
        _manager.BruteForceProgress += (s, e) => Progress?.Invoke(s, e);
        _manager.BruteForceStatusChanged += (s, e) => StatusChanged?.Invoke(s, e);
        _manager.FileCopyProgress += (s, e) => FileCopyProgress?.Invoke(s, e);
        _manager.CRCValidationProgress += (s, e) => CRCValidationProgress?.Invoke(s, e);
        _manager.TimestampPreservationFailed += (s, e) => TimestampPreservationFailed?.Invoke(s, e);

        return await _manager.BruteForceRARVersionAsync(options);
    }

    public void Stop() => _manager?.Stop();
}
