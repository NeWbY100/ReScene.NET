using ReScene.SRR;

namespace ReScene.NET.Services;

public class SrrVerifyService : ISrrVerifyService
{
    public Task<SrrVerifyResult> VerifyAsync(string srrFilePath, CancellationToken ct = default)
        => Task.Run(() => SRRVerifier.Verify(srrFilePath), ct);
}
