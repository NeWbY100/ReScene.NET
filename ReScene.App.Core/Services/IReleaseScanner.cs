namespace ReScene.App.Core.Services;

/// <summary>
/// Classifies a scene release folder's files into main RAR sets, samples, subtitle/nested-SRR
/// candidates, stored files, and music sets — the App.Core port of pyrescene's release-folder
/// classification.
/// </summary>
public interface IReleaseScanner
{
    /// <summary>
    /// Scans <paramref name="releaseRoot"/> and classifies every file it contains. Never throws
    /// for scan-time I/O failures — those degrade to
    /// <see cref="ReleaseScanResult.Warnings"/>, or, when the root itself cannot be enumerated, to
    /// <see cref="ReleaseScanResult.RootError"/>. <paramref name="ct"/> is honored promptly and
    /// throws <see cref="OperationCanceledException"/> like any other cancellable operation.
    /// </summary>
    public ReleaseScanResult Scan(string releaseRoot, CancellationToken ct = default);
}
