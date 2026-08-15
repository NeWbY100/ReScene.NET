namespace ReScene.App.Core.ViewModels.Reconstruction;

/// <summary>
/// The single filesystem move the verified-output relocation performs, isolated behind an interface
/// so tests can inject a mover that fails deterministically on a chosen move to exercise the
/// transactional rollback path (#3). The production implementation is <see cref="SystemFileMover"/>.
/// </summary>
internal interface IFileMover
{
    /// <summary>
    /// Moves <paramref name="source"/> to <paramref name="destination"/>, never overwriting an
    /// existing destination (the relocation pre-flights every destination as free first).
    /// </summary>
    public void Move(string source, string destination);
}
