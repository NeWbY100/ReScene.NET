using ReScene.NET.Services;
using ReScene.RAR;

namespace ReScene.NET.ViewModels.Comparison;

/// <summary>
/// Holds the parsed state for one side (left or right) of the file comparison: the loaded path,
/// file size, parsed data, detailed RAR blocks, and the memory-mapped hex source. The view-model
/// owns two of these and reassigns them on load/close/swap. Bound properties (the displayed paths,
/// sizes, and hex sources) stay on the view-model; this holder carries only the internal twin
/// state that was previously a pair of <c>_leftX</c>/<c>_rightX</c> fields.
/// </summary>
internal sealed class ComparePane
{
    public object? Data { get; set; }
    public string? Path { get; set; }
    public long FileSize { get; set; }
    public IReadOnlyList<RARDetailedBlock>? Blocks { get; set; }
    public MemoryMappedDataSource? Source { get; set; }

    /// <summary>
    /// Disposes the memory-mapped source and resets every field to the unloaded defaults. The
    /// caller is responsible for first clearing any bound hex source that wraps
    /// <see cref="Source"/>, so a pending render cannot touch the disposed mapping.
    /// </summary>
    public void DisposeAndReset()
    {
        Source?.Dispose();
        Source = null;
        Data = null;
        Path = null;
        FileSize = 0;
        Blocks = null;
    }
}
