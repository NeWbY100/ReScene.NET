namespace ReScene.NET.Models;

/// <summary>
/// One contiguous byte range to highlight inside <see cref="Controls.HexViewControl"/>.
/// </summary>
public readonly record struct HexMatchRange(long Offset, int Length);
