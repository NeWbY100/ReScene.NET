using ReScene.Core.Comparison;
using ReScene.RAR;
using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.NET.Models;

/// <summary>
/// Single source of truth for mapping an Inspector tree node's <c>Tag</c> object to its
/// byte range (offset/length) and block-type label. Previously the property-export service
/// and the inspector view model each carried their own copy of this mapping, and they had
/// drifted (the export path was missing the EBML element case).
/// </summary>
public static class BlockMetadata
{
    /// <summary>
    /// Returns the byte range (file offset and total length) of the block described by
    /// <paramref name="tag"/>, or <c>(null, null)</c> when the tag has no associated range.
    /// </summary>
    public static (long? Offset, long? Length) RangeOf(object? tag) => tag switch
    {
        SRRHeaderBlock b => (b.BlockPosition, b.HeaderSize),
        SRRStoredFileBlock b => (b.BlockPosition, (long)b.HeaderSize + b.AddSize),
        SRROsoHashBlock b => (b.BlockPosition, b.HeaderSize),
        SRRRarPaddingBlock b => (b.BlockPosition, (long)b.HeaderSize + b.AddSize),
        SRRRarFileBlock b => (b.BlockPosition, (long)b.HeaderSize + b.AddSize),
        RARDetailedBlock b => (b.StartOffset, b.TotalSize),
        SRSFileDataBlock b => (b.BlockPosition, b.BlockSize),
        SRSTrackDataBlock b => (b.BlockPosition, b.BlockSize),
        SRSContainerChunk b => (b.BlockPosition, b.BlockSize),
        EBMLElement b => (b.Position, b.TotalSize),
        _ => (null, null)
    };

    /// <summary>
    /// Returns the human-readable block-type label for <paramref name="tag"/>, or
    /// <see langword="null"/> when the tag has no associated block type.
    /// </summary>
    public static string? BlockTypeOf(object? tag) => tag switch
    {
        SRRHeaderBlock => "SRR Header",
        SRRStoredFileBlock => "SRR Stored File",
        SRROsoHashBlock => "SRR OSO Hash",
        SRRRarPaddingBlock => "SRR RAR Padding",
        SRRRarFileBlock => "SRR RAR File",
        SRRFile => "SRR Archive",
        RARDetailedBlock b => b.BlockType,
        SRSFileDataBlock => "SRS File Data",
        SRSTrackDataBlock => "SRS Track Data",
        SRSContainerChunk => "SRS Container Chunk",
        SRSFile => "SRS File",
        EBMLElement => "EBML Element",
        _ => null
    };
}
