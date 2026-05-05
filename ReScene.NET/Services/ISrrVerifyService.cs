using ReScene.SRR;

namespace ReScene.NET.Services;

/// <summary>
/// Wraps <see cref="SRRVerifier"/> for ViewModel consumption with async semantics.
/// </summary>
public interface ISrrVerifyService
{
    /// <summary>
    /// Verifies the structural integrity of the SRR file at the given path.
    /// </summary>
    /// <param name="srrFilePath">
    /// Absolute path to the SRR file to verify.
    /// </param>
    /// <param name="ct">
    /// Cancellation token.
    /// </param>
    /// <returns>
    /// A <see cref="SrrVerifyResult"/> describing the outcome.
    /// </returns>
    public Task<SrrVerifyResult> VerifyAsync(string srrFilePath, CancellationToken ct = default);
}
