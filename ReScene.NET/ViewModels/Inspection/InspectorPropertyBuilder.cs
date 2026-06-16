using System.Text;
using ReScene.Core.Comparison;
using ReScene.NET.Helpers;
using ReScene.NET.Models;
using ReScene.RAR;
using ReScene.SRR;
using ReScene.SRS;
using EBMLElement = ReScene.Core.Comparison.EBMLElement;

namespace ReScene.NET.ViewModels.Inspection;

/// <summary>
/// Builds the Inspector property rows for a selected tree node. <see cref="Build"/> dispatches on
/// the node's <c>Tag</c> and returns the rows in display order; the view-model clears and re-fills
/// its bound <c>Properties</c> collection from the result and keeps ownership of hex/selection
/// state. The property labels, ordering, values, byte ranges, the in-list "Data" dedup, and warning
/// flags are reproduced verbatim from the previous inline view-model code. Returns an empty list for
/// tags that produced no properties (the "full hex / no properties" cases).
/// </summary>
internal sealed class InspectorPropertyBuilder
{
    private readonly List<PropertyItem> _properties = [];

    /// <summary>
    /// Builds the property rows for the given tree-node tag. Returns an empty list when the tag has
    /// no property view (matching the view-model's previous "ShowFullHex only" branches).
    /// </summary>
    public IReadOnlyList<PropertyItem> Build(object? tag)
    {
        switch (tag)
        {
            case RARDetailedBlock detailedBlock:
                ShowDetailedBlockProperties(detailedBlock);
                break;
            case SRRHeaderBlock header:
                ShowSRRHeaderProperties(header);
                break;
            case SRROsoHashBlock oso:
                ShowOSOHashProperties(oso);
                break;
            case SRRRarPaddingBlock padding:
                ShowRarPaddingProperties(padding);
                break;
            case SRRStoredFileBlock stored:
                ShowStoredFileProperties(stored);
                break;
            case SRRRarFileBlock rar:
                ShowRarFileProperties(rar);
                break;
            case SRRFile srr:
                ShowArchiveInfoProperties(srr);
                break;
            case SRSFile srsFile:
                ShowSRSSummaryProperties(srsFile);
                break;
            case SRSFileDataBlock srsFileData:
                ShowSRSFileDataProperties(srsFileData);
                break;
            case SRSTrackDataBlock srsTrack:
                ShowSRSTrackDataProperties(srsTrack);
                break;
            case SRSContainerChunk srsChunk:
                ShowSRSChunkProperties(srsChunk);
                break;
            case EBMLElement ebmlElement:
                ShowEBMLElementProperties(ebmlElement);
                break;
        }

        return _properties;
    }

    private void AddProperty(string name, string value, ByteRange? range = null, bool indented = false, bool warning = false)
    {
        _properties.Add(new PropertyItem
        {
            Name = name,
            Value = value,
            ByteRange = range,
            IsIndented = indented,
            IsWarning = warning
        });
    }

    private void ShowSRSSummaryProperties(SRSFile srs)
    {
        AddProperty("Container Type", srs.ContainerType.ToString());

        if (srs.FileData is not null)
        {
            AddProperty("Sample File", srs.FileData.FileName);
            AddProperty("Sample Size", $"{srs.FileData.SampleSize:N0} bytes ({FormatUtilities.FormatSize((long)srs.FileData.SampleSize)})");
            AddProperty("Sample CRC32", $"0x{srs.FileData.CRC32:X8}");
            if (!string.IsNullOrEmpty(srs.FileData.AppName))
            {
                AddProperty("App Name", srs.FileData.AppName);
            }
        }

        AddProperty("Track Count", srs.Tracks.Count.ToString());
        AddProperty("Container Chunks", srs.ContainerChunks.Count.ToString());
    }

    private void ShowSRSFileDataProperties(SRSFileDataBlock block)
    {
        long p = block.FrameOffset;
        AddProperty("Frame Offset", $"0x{block.FrameOffset:X8}",
            new ByteRange { Offset = p, Length = block.FrameHeaderSize });
        AddProperty("Frame Header Size", $"{block.FrameHeaderSize} bytes");
        AddProperty("Block Size", $"{block.BlockSize:N0} bytes");

        AddProperty("Flags", $"0x{block.Flags:X4}",
            new ByteRange { Offset = block.FlagsOffset, Length = 2 });
        AddProperty("App Name Size", $"{block.AppNameSize}",
            new ByteRange { Offset = block.AppNameSizeOffset, Length = 2 });
        if (block.AppNameSize > 0)
        {
            AddProperty("App Name", block.AppName,
                new ByteRange { Offset = block.AppNameOffset, Length = block.AppNameSize });
        }

        AddProperty("File Name Size", $"{block.FileNameSize}",
            new ByteRange { Offset = block.FileNameSizeOffset, Length = 2 });
        AddProperty("File Name", block.FileName,
            new ByteRange { Offset = block.FileNameOffset, Length = block.FileNameSize });
        AddProperty("Sample Size", $"{block.SampleSize:N0} bytes ({FormatUtilities.FormatSize((long)block.SampleSize)})",
            new ByteRange { Offset = block.SampleSizeOffset, Length = 8 });
        AddProperty("CRC-32", $"0x{block.CRC32:X8}",
            new ByteRange { Offset = block.CRC32Offset, Length = 4 });
    }

    private void ShowSRSTrackDataProperties(SRSTrackDataBlock block)
    {
        long p = block.FrameOffset;
        AddProperty("Frame Offset", $"0x{block.FrameOffset:X8}",
            new ByteRange { Offset = p, Length = block.FrameHeaderSize });
        AddProperty("Frame Header Size", $"{block.FrameHeaderSize} bytes");
        AddProperty("Block Size", $"{block.BlockSize:N0} bytes");

        AddProperty("Flags", $"0x{block.Flags:X4}",
            new ByteRange { Offset = block.FlagsOffset, Length = 2 });

        string trackLabel = (block.Flags & 0x8) != 0 ? "Track Number (32-bit)" : "Track Number (16-bit)";
        AddProperty(trackLabel, block.TrackNumber.ToString(),
            new ByteRange { Offset = block.TrackNumberOffset, Length = block.TrackNumberFieldSize });

        string dataLabel = (block.Flags & 0x4) != 0 ? "Data Length (64-bit)" : "Data Length (32-bit)";
        AddProperty(dataLabel, $"{block.DataLength:N0} bytes ({FormatUtilities.FormatSize((long)block.DataLength)})",
            new ByteRange { Offset = block.DataLengthOffset, Length = block.DataLengthFieldSize });

        AddProperty("Match Offset", $"0x{block.MatchOffset:X}",
            new ByteRange { Offset = block.MatchOffsetOffset, Length = 8 });
        AddProperty("Signature Size", $"{block.SignatureSize} bytes",
            new ByteRange { Offset = block.SignatureSizeOffset, Length = 2 });

        if (block.SignatureSize > 0)
        {
            string sigHex = BitConverter.ToString(block.Signature.ToArray()).Replace("-", " ", StringComparison.Ordinal);
            const int maxSignatureDisplayLength = 80;
            if (sigHex.Length > maxSignatureDisplayLength)
            {
                sigHex = sigHex[..maxSignatureDisplayLength] + "...";
            }
            AddProperty("Signature", sigHex,
                new ByteRange { Offset = block.SignatureOffset, Length = block.SignatureSize });
        }
    }

    private void ShowEBMLElementProperties(EBMLElement element)
    {
        AddProperty("Element", element.Name);
        AddProperty("Element ID", $"0x{element.ElementId:X}",
            new ByteRange { Offset = element.Position, Length = element.HeaderSize });
        AddProperty("Type", element.ValueType.ToString());

        if (element.Value is { Length: > 0 })
        {
            AddProperty("Value", element.Value,
                new ByteRange { Offset = element.Position + element.HeaderSize, Length = element.DataSize });
        }

        AddProperty("Position", $"0x{element.Position:X}");
        AddProperty("Header Size", $"{element.HeaderSize} bytes");
        AddProperty("Data Size", $"{element.DataSize:N0} bytes ({FormatUtilities.FormatSize(element.DataSize)})",
            new ByteRange { Offset = element.Position + element.HeaderSize, Length = element.DataSize });
        AddProperty("Total Size", $"{element.TotalSize:N0} bytes");

        if (element.Children.Count > 0)
        {
            AddProperty("Children", element.Children.Count.ToString());
        }
    }

    private void ShowSRSChunkProperties(SRSContainerChunk chunk)
    {
        AddProperty("Label", chunk.Label);
        AddProperty("Chunk ID", chunk.ChunkId);
        AddProperty("Position", $"0x{chunk.BlockPosition:X8}",
            new ByteRange { Offset = chunk.BlockPosition, Length = chunk.HeaderSize });
        AddProperty("Total Size", $"{chunk.BlockSize:N0} bytes ({FormatUtilities.FormatSize(chunk.BlockSize)})");
        AddProperty("Header Size", $"{chunk.HeaderSize:N0} bytes ({FormatUtilities.FormatSize(chunk.HeaderSize)})");
        AddProperty("Payload Size", $"{chunk.PayloadSize:N0} bytes ({FormatUtilities.FormatSize(chunk.PayloadSize)})");
    }

    private void ShowSRRHeaderProperties(SRRHeaderBlock header)
    {
        long pos = header.BlockPosition;

        AddProperty("Header CRC", $"0x{header.CRC:X4}",
            new ByteRange { Offset = pos, Length = 2 });
        AddProperty("Block Type", $"0x{(byte)header.BlockType:X2} ({header.BlockType})",
            new ByteRange { Offset = pos + 2, Length = 1 });
        AddProperty("Flags", $"0x{header.Flags:X4}",
            new ByteRange { Offset = pos + 3, Length = 2 });
        AddProperty("Header Size", $"{header.HeaderSize} bytes",
            new ByteRange { Offset = pos + 5, Length = 2 });

        if (!string.IsNullOrEmpty(header.AppName))
        {
            AddProperty("App Name", header.AppName,
                new ByteRange { Offset = pos + 7, Length = header.AppName.Length + 2 });
        }
    }

    private void ShowStoredFileProperties(SRRStoredFileBlock stored)
    {
        long p = stored.BlockPosition;

        AddProperty("Header CRC", $"0x{stored.CRC:X4}",
            new ByteRange { Offset = p, Length = 2 });
        AddProperty("Block Type", $"0x{(byte)stored.BlockType:X2} ({stored.BlockType})",
            new ByteRange { Offset = p + 2, Length = 1 });
        AddProperty("Flags", $"0x{stored.Flags:X4}",
            new ByteRange { Offset = p + 3, Length = 2 });
        AddProperty("Header Size", $"{stored.HeaderSize} bytes",
            new ByteRange { Offset = p + 5, Length = 2 });
        p += 7;

        AddProperty("Add Size", $"{stored.AddSize} bytes",
            new ByteRange { Offset = p, Length = 4 });
        p += 4;

        int nameLen = Encoding.UTF8.GetByteCount(stored.FileName);
        AddProperty("Name Length", $"{nameLen} bytes",
            new ByteRange { Offset = p, Length = 2 });
        p += 2;
        AddProperty("File Name", stored.FileName,
            new ByteRange { Offset = p, Length = nameLen });

        if (stored.FileLength > 0)
        {
            AddProperty("File Data", $"{stored.FileLength:N0} bytes ({FormatUtilities.FormatSize(stored.FileLength)})",
                new ByteRange { Offset = stored.DataOffset, Length = (int)Math.Min(stored.FileLength, int.MaxValue) });
        }
    }

    private void ShowRarFileProperties(SRRRarFileBlock rar)
    {
        long p = rar.BlockPosition;

        AddProperty("Header CRC", $"0x{rar.CRC:X4}",
            new ByteRange { Offset = p, Length = 2 });
        AddProperty("Block Type", $"0x{(byte)rar.BlockType:X2} ({rar.BlockType})",
            new ByteRange { Offset = p + 2, Length = 1 });
        AddProperty("Flags", $"0x{rar.Flags:X4}",
            new ByteRange { Offset = p + 3, Length = 2 });
        AddProperty("Header Size", $"{rar.HeaderSize} bytes",
            new ByteRange { Offset = p + 5, Length = 2 });
        p += 7;

        if ((rar.Flags & (ushort)SRRBlockFlags.LongBlock) != 0)
        {
            AddProperty("Add Size", $"{rar.AddSize} bytes",
                new ByteRange { Offset = p, Length = 4 });
            p += 4;
        }

        int nameLen = Encoding.UTF8.GetByteCount(rar.FileName);
        AddProperty("Name Length", $"{nameLen} bytes",
            new ByteRange { Offset = p, Length = 2 });
        p += 2;
        AddProperty("RAR Volume", rar.FileName,
            new ByteRange { Offset = p, Length = nameLen });
    }

    private void ShowOSOHashProperties(SRROsoHashBlock oso)
    {
        long p = oso.BlockPosition;

        AddProperty("Header CRC", $"0x{oso.CRC:X4}",
            new ByteRange { Offset = p, Length = 2 });
        AddProperty("Block Type", $"0x{(byte)oso.BlockType:X2} ({oso.BlockType})",
            new ByteRange { Offset = p + 2, Length = 1 });
        AddProperty("Flags", $"0x{oso.Flags:X4}",
            new ByteRange { Offset = p + 3, Length = 2 });
        AddProperty("Header Size", $"{oso.HeaderSize} bytes",
            new ByteRange { Offset = p + 5, Length = 2 });
        p += 7;

        if ((oso.Flags & (ushort)SRRBlockFlags.LongBlock) != 0)
        {
            AddProperty("Add Size", $"{oso.AddSize} bytes",
                new ByteRange { Offset = p, Length = 4 });
            p += 4;
        }

        // Binary order: FileSize(8), OSOHash(8), NameLen(2), FileName(var)
        AddProperty("File Size", $"{oso.FileSize:N0} bytes ({FormatUtilities.FormatSize((long)oso.FileSize)})",
            new ByteRange { Offset = p, Length = 8 });
        p += 8;
        AddProperty("OSO Hash", Convert.ToHexString(oso.OSOHash.Span),
            new ByteRange { Offset = p, Length = 8 });
        p += 8;

        int nameLen = Encoding.UTF8.GetByteCount(oso.FileName);
        AddProperty("Name Length", $"{nameLen} bytes",
            new ByteRange { Offset = p, Length = 2 });
        p += 2;
        AddProperty("File Name", oso.FileName,
            new ByteRange { Offset = p, Length = nameLen });
    }

    private void ShowRarPaddingProperties(SRRRarPaddingBlock padding)
    {
        long p = padding.BlockPosition;

        AddProperty("Header CRC", $"0x{padding.CRC:X4}",
            new ByteRange { Offset = p, Length = 2 });
        AddProperty("Block Type", $"0x{(byte)padding.BlockType:X2} ({padding.BlockType})",
            new ByteRange { Offset = p + 2, Length = 1 });
        AddProperty("Flags", $"0x{padding.Flags:X4}",
            new ByteRange { Offset = p + 3, Length = 2 });
        AddProperty("Header Size", $"{padding.HeaderSize} bytes",
            new ByteRange { Offset = p + 5, Length = 2 });
        p += 7;

        if ((padding.Flags & (ushort)SRRBlockFlags.LongBlock) != 0)
        {
            AddProperty("Add Size", $"{padding.AddSize} bytes",
                new ByteRange { Offset = p, Length = 4 });
            p += 4;
        }

        int nameLen = Encoding.UTF8.GetByteCount(padding.RARFileName);
        AddProperty("Name Length", $"{nameLen} bytes",
            new ByteRange { Offset = p, Length = 2 });
        p += 2;
        AddProperty("RAR File Name", padding.RARFileName,
            new ByteRange { Offset = p, Length = nameLen });
        AddProperty("Padding Size", $"{padding.PaddingSize:N0} bytes");
    }

    private void ShowDetailedBlockProperties(RARDetailedBlock block)
    {
        foreach (RARHeaderField field in block.Fields)
        {
            string value = field.Value;
            bool isWarning = field.Description is not null && field.Description.Contains("Custom packer sentinel", StringComparison.Ordinal);
            if (!string.IsNullOrEmpty(field.Description) && field.Description != field.Value)
            {
                value = $"{field.Value} ({field.Description})";
            }

            ByteRange? range = field.Length > 0
                ? new ByteRange { PropertyName = field.Name, Offset = field.Offset, Length = field.Length }
                : null;

            AddProperty(field.Name, value, range, warning: isWarning);

            foreach (RARHeaderField child in field.Children)
            {
                long childOffset = child.Length > 0 ? child.Offset : field.Offset;
                int childLength = child.Length > 0 ? child.Length : field.Length;

                ByteRange? childRange = childLength > 0
                    ? new ByteRange { PropertyName = child.Name, Offset = childOffset, Length = childLength }
                    : null;

                AddProperty($"  {child.Name}", child.Value, childRange, indented: true);
            }
        }

        // Add data row if not already present
        if (block.HasData && block.DataSize > 0)
        {
            bool hasData = false;
            foreach (PropertyItem p in _properties)
            {
                if (p.Name == "Data")
                {
                    hasData = true;
                    break;
                }
            }

            if (!hasData)
            {
                long dataOffset = block.StartOffset + block.HeaderSize;
                AddProperty("Data", $"{block.DataSize:N0} bytes (offset 0x{dataOffset:X8})",
                    new ByteRange { PropertyName = "Data", Offset = dataOffset, Length = (int)Math.Min(block.DataSize, int.MaxValue) });
            }
        }
    }

    private void ShowArchiveInfoProperties(SRRFile srr)
    {
        AddProperty("RAR Version", srr.RARVersion.HasValue
            ? (srr.RARVersion == 50 ? "RAR 5.0" : $"RAR {srr.RARVersion.Value / 10}.{srr.RARVersion.Value % 10}")
            : "Unknown");

        if (srr.CompressionMethod.HasValue)
        {
            AddProperty("Compression Method", FileComparer.GetCompressionMethodName((byte)srr.CompressionMethod.Value));
        }

        if (srr.DictionarySize.HasValue)
        {
            AddProperty("Dictionary Size", $"{srr.DictionarySize.Value} KB");
        }

        AddProperty("Solid Archive", FormatBool(srr.IsSolidArchive));
        AddProperty("Volume Archive", FormatBool(srr.IsVolumeArchive));
        AddProperty("Recovery Record", FormatBool(srr.HasRecoveryRecord));
        AddProperty("Encrypted Headers", FormatBool(srr.HasEncryptedHeaders));
        AddProperty("New Volume Naming", FormatBool(srr.HasNewVolumeNaming));
        AddProperty("First Volume Flag", FormatBool(srr.HasFirstVolumeFlag));
        AddProperty("Large Files (64-bit)", FormatBool(srr.HasLargeFiles));
        AddProperty("Unicode Names", FormatBool(srr.HasUnicodeNames));
        AddProperty("Extended Time", FormatBool(srr.HasExtendedTime));

        if (srr.VolumeSizeBytes.HasValue)
        {
            AddProperty("Volume Size", $"{srr.VolumeSizeBytes.Value:N0} bytes ({FormatUtilities.FormatSize(srr.VolumeSizeBytes.Value)})");
        }

        if (srr.RARVolumeSizes.Count > 0)
        {
            AddProperty("Volume Sizes Count", srr.RARVolumeSizes.Count.ToString());
            var uniqueSizes = srr.RARVolumeSizes.Distinct().OrderByDescending(s => s).ToList();
            for (int i = 0; i < Math.Min(uniqueSizes.Count, 5); i++)
            {
                AddProperty($"  Unique Size {i + 1}",
                    $"{uniqueSizes[i]:N0} bytes ({FormatUtilities.FormatSize(uniqueSizes[i])})", indented: true);
            }

            if (uniqueSizes.Count > 5)
            {
                AddProperty("  ...", $"({uniqueSizes.Count - 5} more)", indented: true);
            }
        }

        AddProperty("RAR Volumes", srr.RARFiles.Count.ToString());
        AddProperty("Stored Files", srr.StoredFiles.Count.ToString());
        AddProperty("Archived Files", srr.ArchivedFiles.Count.ToString());
        AddProperty("Archived Directories", srr.ArchivedDirectories.Count.ToString());

        AddProperty("File Timestamps", srr.ArchivedFileTimestamps.Count.ToString());
        AddProperty("File Creation Times", srr.ArchivedFileCreationTimes.Count.ToString());
        AddProperty("File Access Times", srr.ArchivedFileAccessTimes.Count.ToString());
        AddProperty("Dir Timestamps", srr.ArchivedDirectoryTimestamps.Count.ToString());
        AddProperty("Dir Creation Times", srr.ArchivedDirectoryCreationTimes.Count.ToString());
        AddProperty("Dir Access Times", srr.ArchivedDirectoryAccessTimes.Count.ToString());

        AddProperty("File CRCs", srr.ArchivedFileCrcs.Count.ToString());
        AddProperty("Header CRC Errors", srr.HeaderCRCMismatches.ToString());
        AddProperty("Has Comment", !string.IsNullOrEmpty(srr.ArchiveComment) ? "Yes" : "No");

        if (!string.IsNullOrEmpty(srr.ArchiveComment))
        {
            AddProperty("Comment", srr.ArchiveComment);
        }
    }

    private static string FormatBool(bool? value) =>
        value.HasValue ? (value.Value ? "Yes" : "No") : "Unknown";
}
