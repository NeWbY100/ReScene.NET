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

    public async Task<bool> RunAsync(BruteForceOptions options, CancellationToken cancellationToken = default)
    {
        var logger = new ReSceneLogger();
        logger.Logged += (s, e) => LogMessage?.Invoke(s, e);

        // A fresh Manager per run, kept local: cancellation flows through the token below
        // rather than a shared field, so a Cancel during setup can never target a stale run.
        var manager = new Manager(logger);
        manager.BruteForceProgress += (s, e) => Progress?.Invoke(s, e);
        manager.BruteForceStatusChanged += (s, e) => StatusChanged?.Invoke(s, e);
        manager.FileCopyProgress += (s, e) => FileCopyProgress?.Invoke(s, e);
        manager.CRCValidationProgress += (s, e) => CRCValidationProgress?.Invoke(s, e);
        manager.TimestampPreservationFailed += (s, e) => TimestampPreservationFailed?.Invoke(s, e);

        return await manager.BruteForceRARVersionAsync(options, cancellationToken);
    }
}
