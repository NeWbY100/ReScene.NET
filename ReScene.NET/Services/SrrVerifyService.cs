using ReScene.SRR;

namespace ReScene.NET.Services;

/// <summary>
/// Default <see cref="ISrrVerifyService"/> implementation that runs
/// <see cref="SRRVerifier.Verify"/> on a thread-pool thread.
/// </summary>
public class SrrVerifyService : ISrrVerifyService
{
    public Task<SrrVerifyResult> VerifyAsync(string srrFilePath, CancellationToken ct = default)
        => Task.Run(() => SRRVerifier.Verify(srrFilePath), ct);
}
