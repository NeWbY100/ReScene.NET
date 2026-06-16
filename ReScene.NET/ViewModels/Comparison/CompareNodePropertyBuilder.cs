using System.Text;
using ReScene.Core.Comparison;
using ReScene.NET.Helpers;
using ReScene.NET.Models;
using ReScene.RAR;
using ReScene.SRR;
using ReScene.SRS;
using EBMLElement = ReScene.Core.Comparison.EBMLElement;

namespace ReScene.NET.ViewModels.Comparison;

/// <summary>
/// Builds the property rows shown for a selected comparison-tree node, including the
/// diff-highlighting (<c>IsDifferent</c>) that depends on the matching block/track on the other
/// side. The comparison state (the <see cref="CompareResult"/>, both sides' parsed data, detailed
/// RAR blocks, and file paths) is reassigned on every load/close/swap, so it is supplied to the
/// constructor per <c>ShowProperties</c> call rather than captured once. The view-model clears and
/// re-fills its bound <c>LeftProperties</c>/<c>RightProperties</c> collection from
/// <see cref="Build"/>'s result. Labels, ordering, values, byte ranges, and diff markers are
/// reproduced verbatim from the previous inline view-model code.
/// </summary>
internal sealed class CompareNodePropertyBuilder(
    CompareResult? compareResult,
    IReadOnlyList<RARDetailedBlock>? leftDetailedBlocks,
    IReadOnlyList<RARDetailedBlock>? rightDetailedBlocks,
    object? leftData,
    object? rightData,
    string? leftFilePath,
    string? rightFilePath)
{
    private const int MaxBlockCompareSize = 10 * 1024 * 1024;

    private readonly CompareResult? _compareResult = compareResult;
    private readonly IReadOnlyList<RARDetailedBlock>? _leftDetailedBlocks = leftDetailedBlocks;
    private readonly IReadOnlyList<RARDetailedBlock>? _rightDetailedBlocks = rightDetailedBlocks;
    private readonly object? _leftData = leftData;
    private readonly object? _rightData = rightData;
    private readonly string? _leftFilePathInternal = leftFilePath;
    private readonly string? _rightFilePathInternal = rightFilePath;

    private readonly List<PropertyItem> _properties = [];

    /// <summary>Builds the property rows for the selected node; returns an empty list if the node has none.</summary>
    public IReadOnlyList<PropertyItem> Build(CompareNodeData nodeData, bool isLeft)
    {
        switch (nodeData.NodeType)
        {
            case CompareNodeType.DetailedBlock:
                if (nodeData.Data is RARDetailedBlock detailedBlock)
                {
                    ShowDetailedBlockProperties(detailedBlock, isLeft);
                }

                break;

            case CompareNodeType.ArchiveInfo:
                if (nodeData.Data is SRRFile srr)
                {
                    ShowSRRArchiveProperties(srr);
                }
                else if (nodeData.Data is RARFileData rar)
                {
                    ShowRARArchiveProperties(rar);
                }

                break;

            case CompareNodeType.ArchivedFile:
                if (nodeData.Data is RARFileHeader fileHeader)
                {
                    ShowRAR4FileProperties(fileHeader);
                }
                else if (nodeData.Data is RAR5FileInfo fileInfo)
                {
                    ShowRAR5FileProperties(fileInfo);
                }
                else if (nodeData.Data is SRRFile srrFile && nodeData.FileName is not null)
                {
                    ShowSRRArchivedFileProperties(srrFile, nodeData.FileName);
                }

                break;

            case CompareNodeType.StoredFile:
                if (nodeData.Data is SRRStoredFileBlock stored)
                {
                    ShowStoredFileProperties(stored);
                }

                break;

            case CompareNodeType.RARVolume:
                if (nodeData.Data is SRRRarFileBlock rarFile)
                {
                    ShowRarVolumeProperties(rarFile);
                }

                break;

            case CompareNodeType.SRSFileInfo:
                if (nodeData.Data is SRSFileDataBlock fd)
                {
                    ShowSRSFileInfoProperties(fd);
                }

                break;

            case CompareNodeType.SRSTrack:
                if (nodeData.Data is SRSTrackDataBlock track)
                {
                    ShowSRSTrackProperties(track, nodeData.FileName);
                }

                break;

            case CompareNodeType.SRSContainerChunks:
                if (nodeData.Data is SRSContainerChunk chunk)
                {
                    ShowSRSContainerChunkProperties(chunk);
                }

                break;

            case CompareNodeType.OSOHash:
                if (nodeData.Data is SRROsoHashBlock oso)
                {
                    ShowOSOHashProperties(oso);
                }

                break;

            case CompareNodeType.MKVElement:
                if (nodeData.Data is EBMLElement el)
                {
                    ShowMKVElementProperties(el, nodeData.FileName);
                }

                break;
        }

        return _properties;
    }

    private void ShowMKVElementProperties(EBMLElement element, string? path)
    {
        IReadOnlyList<PropertyDifference>? diffs = path is not null ? GetTrackDiffs(path) : null;

        _properties.Add(new PropertyItem { Name = "Element", Value = element.Name });
        _properties.Add(new PropertyItem { Name = "Element ID", Value = $"0x{element.ElementId:X}" });
        _properties.Add(new PropertyItem { Name = "Type", Value = element.ValueType.ToString() });

        _properties.Add(new PropertyItem
        {
            Name = "Value",
            Value = element.Value ?? "",
            // "Data" marks same-size payloads whose raw bytes differ (e.g., cluster A/V content);
            // the value row is the closest visible stand-in for that payload.
            IsDifferent = diffs?.Any(d => d.PropertyName is "Value" or "Data") == true,
            // The value occupies the element's data region (after its id + size header).
            ByteRange = new ByteRange { PropertyName = "Value", Offset = element.Position + element.HeaderSize, Length = element.DataSize }
        });

        _properties.Add(new PropertyItem { Name = "Position", Value = $"0x{element.Position:X}" });
        _properties.Add(new PropertyItem { Name = "Header Size", Value = $"{element.HeaderSize} bytes" });
        _properties.Add(new PropertyItem
        {
            Name = "Data Size",
            Value = $"{element.DataSize:N0} bytes",
            IsDifferent = diffs?.Any(d => d.PropertyName == "Data Size") == true
        });
        _properties.Add(new PropertyItem { Name = "Total Size", Value = $"{element.TotalSize:N0} bytes" });
    }

    private void ShowDetailedBlockProperties(RARDetailedBlock block, bool isLeft)
    {
        _properties.Add(new PropertyItem { Name = "Block Type", Value = block.BlockType });
        _properties.Add(new PropertyItem { Name = "Start Offset", Value = $"0x{block.StartOffset:X8}" });
        _properties.Add(new PropertyItem { Name = "Header Size", Value = $"{block.HeaderSize} bytes" });
        _properties.Add(new PropertyItem { Name = "Total Size", Value = $"{block.TotalSize:N0} bytes" });

        if (block.HasData)
        {
            _properties.Add(new PropertyItem { Name = "Data Size", Value = $"{block.DataSize:N0} bytes" });
        }

        _properties.Add(new PropertyItem { Name = "--- Fields ---", Value = "", IsSeparator = true });

        RARDetailedBlock? otherBlock = _compareResult is not null ? FindMatchingDetailedBlock(block, isLeft) : null;

        foreach (RARHeaderField field in block.Fields)
        {
            string value = field.Value;
            if (!string.IsNullOrEmpty(field.Description) && field.Description != field.Value)
            {
                value = $"{field.Value} ({field.Description})";
            }

            bool isDiff = false;
            if (_compareResult is not null && otherBlock is not null)
            {
                RARHeaderField? otherField = otherBlock.Fields.FirstOrDefault(f => f.Name == field.Name);
                if (otherField is not null)
                {
                    isDiff = otherField.Value != field.Value;
                }
            }

            _properties.Add(new PropertyItem
            {
                Name = field.Name,
                Value = value,
                IsDifferent = isDiff,
                ByteRange = new ByteRange
                {
                    PropertyName = field.Name,
                    Offset = field.Offset,
                    Length = field.Length
                }
            });

            foreach (RARHeaderField child in field.Children)
            {
                bool childDiff = false;
                if (_compareResult is not null && otherBlock is not null)
                {
                    RARHeaderField? otherParent = otherBlock.Fields.FirstOrDefault(f => f.Name == field.Name);
                    RARHeaderField? otherChild = otherParent?.Children.FirstOrDefault(c => c.Name == child.Name);
                    if (otherChild is not null)
                    {
                        childDiff = otherChild.Value != child.Value;
                    }
                }

                long childOffset = child.Length > 0 ? child.Offset : field.Offset;
                int childLength = child.Length > 0 ? child.Length : field.Length;

                _properties.Add(new PropertyItem
                {
                    Name = child.Name,
                    Value = child.Value,
                    IsIndented = true,
                    IsDifferent = childDiff,
                    ByteRange = childLength > 0
                        ? new ByteRange { PropertyName = child.Name, Offset = childOffset, Length = childLength }
                        : null
                });
            }
        }

        if (block.HasData && block.DataSize > 0)
        {
            long dataOffset = block.StartOffset + block.HeaderSize;
            bool dataDiff = false;

            if (_compareResult is not null && otherBlock is { HasData: true })
            {
                string? thisFilePath = isLeft ? _leftFilePathInternal : _rightFilePathInternal;
                string? otherFilePath = isLeft ? _rightFilePathInternal : _leftFilePathInternal;
                if (thisFilePath is not null && otherFilePath is not null)
                {
                    long otherDataOffset = otherBlock.StartOffset + otherBlock.HeaderSize;
                    if (block.DataSize != otherBlock.DataSize)
                    {
                        dataDiff = true;
                    }
                    else if (block.DataSize <= MaxBlockCompareSize) // Compare up to 10 MB
                    {
                        byte[]? thisData = ReadFileSlice(thisFilePath, dataOffset, (int)block.DataSize);
                        byte[]? otherData = ReadFileSlice(otherFilePath, otherDataOffset, (int)otherBlock.DataSize);
                        dataDiff = thisData is null || otherData is null ||
                            !thisData.AsSpan().SequenceEqual(otherData.AsSpan());
                    }
                }
            }

            _properties.Add(new PropertyItem
            {
                Name = "Data",
                Value = $"{block.DataSize:N0} bytes (offset 0x{dataOffset:X8})",
                IsDifferent = dataDiff,
                ByteRange = new ByteRange
                {
                    PropertyName = "Data",
                    Offset = dataOffset,
                    Length = (int)Math.Min(block.DataSize, int.MaxValue)
                }
            });
        }
    }

    private RARDetailedBlock? FindMatchingDetailedBlock(RARDetailedBlock block, bool isLeft)
    {
        IReadOnlyList<RARDetailedBlock>? otherBlocks = isLeft ? _rightDetailedBlocks : _leftDetailedBlocks;
        if (otherBlocks is not null)
        {
            RARDetailedBlock? match = otherBlocks.FirstOrDefault(b =>
                b.BlockType == block.BlockType && b.ItemName == block.ItemName);
            if (match is not null)
            {
                return match;
            }
        }

        object? otherData = isLeft ? _rightData : _leftData;
        if (otherData is ReScene.Core.Comparison.SRRFileData otherSRRData)
        {
            foreach (List<RARDetailedBlock> volumeBlocks in otherSRRData.VolumeDetailedBlocks.Values)
            {
                RARDetailedBlock? match = volumeBlocks.FirstOrDefault(b =>
                    b.BlockType == block.BlockType && b.ItemName == block.ItemName);
                if (match is not null)
                {
                    return match;
                }
            }
        }

        return null;
    }

    private void ShowSRRArchiveProperties(SRRFile srr)
    {
        AddComparedProperty("App Name", srr.HeaderBlock?.AppName ?? "N/A", "App Name");
        AddComparedProperty("RAR Version", FileComparer.FormatRARVersion(srr.RARVersion), "RAR Version");
        AddComparedProperty("Compression Method", FileComparer.GetCompressionMethodName(srr.CompressionMethod), "Compression Method");
        AddComparedProperty("Dictionary Size", FileComparer.FormatDictionarySize(srr.DictionarySize), "Dictionary Size");
        AddComparedProperty("Solid Archive", FileComparer.FormatBool(srr.IsSolidArchive), "Solid Archive");
        AddComparedProperty("Volume Archive", FileComparer.FormatBool(srr.IsVolumeArchive), "Volume Archive");
        AddComparedProperty("Recovery Record", FileComparer.FormatBool(srr.HasRecoveryRecord), "Recovery Record");
        AddComparedProperty("Encrypted Headers", FileComparer.FormatBool(srr.HasEncryptedHeaders), "Encrypted Headers");
        AddComparedProperty("RAR Volumes", srr.RARFiles.Count.ToString(), "RAR Volumes Count");
        AddComparedProperty("Stored Files", srr.StoredFiles.Count.ToString(), "Stored Files Count");
        AddComparedProperty("Archived Files", srr.ArchivedFiles.Count.ToString(), "Archived Files Count");
        AddComparedProperty("Header CRC Errors", srr.HeaderCRCMismatches.ToString(), "Header CRC Errors");
        AddComparedProperty("Has Comment", FileComparer.FormatBool(!string.IsNullOrEmpty(srr.ArchiveComment)), "Has Comment");

        _properties.Add(new PropertyItem { Name = "--- Reconstruction Hints ---", Value = "", IsSeparator = true });

        if (srr.DetectedHostOS.HasValue)
        {
            AddComparedProperty("Host OS (files)", $"{srr.DetectedHostOSName} (0x{srr.DetectedHostOS:X2})", "Host OS");
        }

        if (srr.CmtHostOS.HasValue)
        {
            AddComparedProperty("CMT Host OS", $"{srr.CmtHostOSName} (0x{srr.CmtHostOS:X2})", "CMT Host OS");
        }

        if (srr.CmtFileTimeDOS.HasValue)
        {
            string timeMode = srr.CmtHasZeroedFileTime
                ? "Zeroed (0x00000000)"
                : $"0x{srr.CmtFileTimeDOS:X8}";
            AddComparedProperty("CMT Timestamp", timeMode, "CMT Timestamp");
            AddComparedProperty("CMT Time Mode", srr.CmtTimestampMode, "CMT Time Mode");
        }

        if (srr.CmtFileAttributes.HasValue)
        {
            AddComparedProperty("CMT Attributes", $"0x{srr.CmtFileAttributes:X8}", "CMT Attributes");
        }
    }

    private void ShowRARArchiveProperties(RARFileData rar)
    {
        AddComparedProperty("Format", rar.IsRAR5 ? "RAR 5.x" : "RAR 4.x", "Format");

        if (rar.IsRAR5 && rar.RAR5ArchiveInfo is not null)
        {
            AddComparedProperty("Volume", FileComparer.FormatBool(rar.RAR5ArchiveInfo.IsVolume), "Volume");
            AddComparedProperty("Solid", FileComparer.FormatBool(rar.RAR5ArchiveInfo.IsSolid), "Solid");
            AddComparedProperty("Recovery Record", FileComparer.FormatBool(rar.RAR5ArchiveInfo.HasRecoveryRecord), "Recovery Record");
            AddComparedProperty("Locked", FileComparer.FormatBool(rar.RAR5ArchiveInfo.IsLocked), "Locked");
            AddComparedProperty("File Count", rar.RAR5FileInfos.Count.ToString(), "File Count");
        }
        else if (!rar.IsRAR5 && rar.ArchiveHeader is not null)
        {
            AddComparedProperty("Volume", FileComparer.FormatBool(rar.ArchiveHeader.IsVolume), "Volume");
            AddComparedProperty("Solid", FileComparer.FormatBool(rar.ArchiveHeader.IsSolid), "Solid");
            AddComparedProperty("Recovery Record", FileComparer.FormatBool(rar.ArchiveHeader.HasRecoveryRecord), "Recovery Record");
            AddComparedProperty("Locked", FileComparer.FormatBool(rar.ArchiveHeader.IsLocked), "Locked");
            AddComparedProperty("Encrypted Headers", FileComparer.FormatBool(rar.ArchiveHeader.HasEncryptedHeaders), "Encrypted Headers");
            AddComparedProperty("File Count", rar.FileHeaders.Count.ToString(), "File Count");
        }

        AddComparedProperty("Has Comment", FileComparer.FormatBool(!string.IsNullOrEmpty(rar.Comment)), "Has Comment");
    }

    private void ShowRAR4FileProperties(RARFileHeader header)
    {
        _properties.Add(new PropertyItem { Name = "File Name", Value = header.FileName });
        _properties.Add(new PropertyItem { Name = "Type", Value = header.IsDirectory ? "Directory" : "File" });
        AddComparedProperty("Unpacked Size", $"{header.UnpackedSize:N0} bytes", "Unpacked Size");
        AddComparedProperty("Packed Size", $"{header.PackedSize:N0} bytes", "Packed Size");
        AddComparedProperty("CRC32", header.FileCRC.ToString("X8"), "CRC");
        AddComparedProperty("Compression Method", FileComparer.GetCompressionMethodName((int?)header.CompressionMethod), "Compression Method");
        _properties.Add(new PropertyItem { Name = "Dictionary Size", Value = $"{header.DictionarySizeKB} KB" });
        AddComparedProperty("Modified Time", header.ModifiedTime?.ToString("yyyy-MM-dd HH:mm:ss") ?? "N/A", "Modified Time");
        _properties.Add(new PropertyItem { Name = "Split Before", Value = FileComparer.FormatBool(header.IsSplitBefore) });
        _properties.Add(new PropertyItem { Name = "Split After", Value = FileComparer.FormatBool(header.IsSplitAfter) });
    }

    private void ShowRAR5FileProperties(RAR5FileInfo info)
    {
        _properties.Add(new PropertyItem { Name = "File Name", Value = info.FileName });
        _properties.Add(new PropertyItem { Name = "Type", Value = info.IsDirectory ? "Directory" : "File" });
        AddComparedProperty("Unpacked Size", $"{info.UnpackedSize:N0} bytes", "Unpacked Size");
        AddComparedProperty("CRC32", info.FileCRC?.ToString("X8") ?? "N/A", "CRC");
        AddComparedProperty("Compression Method", FileComparer.GetCompressionMethodName(info.CompressionMethod), "Compression Method");
        _properties.Add(new PropertyItem { Name = "Dictionary Size", Value = $"{info.DictionarySizeKB} KB" });
        if (info.ModificationTime.HasValue)
        {
            DateTime dt = DateTimeOffset.FromUnixTimeSeconds(info.ModificationTime.Value).LocalDateTime;
            _properties.Add(new PropertyItem { Name = "Modified Time", Value = dt.ToString("yyyy-MM-dd HH:mm:ss") });
        }

        _properties.Add(new PropertyItem { Name = "Split Before", Value = FileComparer.FormatBool(info.IsSplitBefore) });
        _properties.Add(new PropertyItem { Name = "Split After", Value = FileComparer.FormatBool(info.IsSplitAfter) });
    }

    private void ShowSRRArchivedFileProperties(SRRFile srr, string fileName)
    {
        _properties.Add(new PropertyItem { Name = "File Name", Value = fileName });

        if (srr.ArchivedFileCrcs.TryGetValue(fileName, out string? crc))
        {
            AddComparedProperty("CRC32", crc, "CRC");
        }

        if (srr.ArchivedFileTimestamps.TryGetValue(fileName, out DateTime modTime))
        {
            AddComparedProperty("Modified Time", modTime.ToString("yyyy-MM-dd HH:mm:ss"), "Modified Time");
        }

        if (srr.ArchivedFileCreationTimes.TryGetValue(fileName, out DateTime createTime))
        {
            _properties.Add(new PropertyItem { Name = "Creation Time", Value = createTime.ToString("yyyy-MM-dd HH:mm:ss") });
        }

        if (srr.ArchivedFileAccessTimes.TryGetValue(fileName, out DateTime accessTime))
        {
            _properties.Add(new PropertyItem { Name = "Access Time", Value = accessTime.ToString("yyyy-MM-dd HH:mm:ss") });
        }
    }

    private void ShowStoredFileProperties(SRRStoredFileBlock stored)
    {
        long p = stored.BlockPosition;
        int nameLen = Encoding.UTF8.GetByteCount(stored.FileName);

        _properties.Add(new PropertyItem
        {
            Name = "File Name",
            Value = stored.FileName,
            ByteRange = new ByteRange { Offset = p + 7 + 4 + 2, Length = nameLen }
        });
        _properties.Add(new PropertyItem
        {
            Name = "File Size",
            Value = $"{stored.FileLength:N0} bytes",
            ByteRange = new ByteRange { Offset = stored.DataOffset, Length = (int)Math.Min(stored.FileLength, int.MaxValue) }
        });
        _properties.Add(new PropertyItem
        {
            Name = "Data Offset",
            Value = $"0x{stored.DataOffset:X8}"
        });
    }

    private void ShowRarVolumeProperties(SRRRarFileBlock rarFile)
    {
        long p = rarFile.BlockPosition;
        int nameLen = Encoding.UTF8.GetByteCount(rarFile.FileName);

        _properties.Add(new PropertyItem
        {
            Name = "Volume Name",
            Value = rarFile.FileName,
            ByteRange = new ByteRange { Offset = p + 7 + 2, Length = nameLen }
        });
        _properties.Add(new PropertyItem
        {
            Name = "Block Position",
            Value = $"0x{rarFile.BlockPosition:X8}"
        });
        _properties.Add(new PropertyItem
        {
            Name = "Header CRC",
            Value = $"0x{rarFile.CRC:X4}",
            ByteRange = new ByteRange { Offset = p, Length = 2 }
        });
    }

    private void ShowSRSFileInfoProperties(SRSFileDataBlock fd)
    {
        AddComparedProperty("App Name", fd.AppName, "App Name");

        _properties.Add(new PropertyItem
        {
            Name = "File Name",
            Value = fd.FileName,
            ByteRange = new ByteRange { PropertyName = "File Name", Offset = fd.FileNameOffset, Length = fd.FileNameSize }
        });

        AddComparedProperty("Sample Size", $"{fd.SampleSize:N0} bytes", "Sample Size");
        AddComparedProperty("CRC32", fd.CRC32.ToString("X8"), "CRC32");
        AddComparedProperty("Flags", $"0x{fd.Flags:X4}", "Flags");

        _properties.Add(new PropertyItem
        {
            Name = "Block Position",
            Value = $"0x{fd.BlockPosition:X8}"
        });

        _properties.Add(new PropertyItem
        {
            Name = "Block Size",
            Value = $"{fd.BlockSize:N0} bytes"
        });
    }

    private void ShowSRSTrackProperties(SRSTrackDataBlock track, string? trackName)
    {
        IReadOnlyList<PropertyDifference>? trackDiffs = trackName is not null ? GetTrackDiffs(trackName) : null;

        _properties.Add(new PropertyItem
        {
            Name = "Track Number",
            Value = track.TrackNumber.ToString(),
            ByteRange = new ByteRange { PropertyName = "Track Number", Offset = track.TrackNumberOffset, Length = track.TrackNumberFieldSize }
        });

        _properties.Add(new PropertyItem
        {
            Name = "Data Length",
            Value = $"{track.DataLength:N0} bytes",
            IsDifferent = trackDiffs?.Any(d => d.PropertyName == "Data Length") == true,
            ByteRange = new ByteRange { PropertyName = "Data Length", Offset = track.DataLengthOffset, Length = track.DataLengthFieldSize }
        });

        _properties.Add(new PropertyItem
        {
            Name = "Match Offset",
            Value = $"0x{track.MatchOffset:X}",
            IsDifferent = trackDiffs?.Any(d => d.PropertyName == "Match Offset") == true,
            ByteRange = new ByteRange { PropertyName = "Match Offset", Offset = track.MatchOffsetOffset, Length = 8 }
        });

        _properties.Add(new PropertyItem
        {
            Name = "Signature Size",
            Value = track.SignatureSize.ToString(),
            IsDifferent = trackDiffs?.Any(d => d.PropertyName == "Signature Size") == true,
            ByteRange = new ByteRange { PropertyName = "Signature Size", Offset = track.SignatureSizeOffset, Length = 2 }
        });

        _properties.Add(new PropertyItem
        {
            Name = "Flags",
            Value = $"0x{track.Flags:X4}",
            IsDifferent = trackDiffs?.Any(d => d.PropertyName == "Flags") == true,
            ByteRange = new ByteRange { PropertyName = "Flags", Offset = track.FlagsOffset, Length = 2 }
        });

        if (track.Signature.Length > 0)
        {
            const int maxSignatureDisplayBytes = 32;
            string sigHex = Convert.ToHexString(track.Signature.Span[..Math.Min(maxSignatureDisplayBytes, track.Signature.Length)]);
            if (track.Signature.Length > maxSignatureDisplayBytes)
            {
                sigHex += "...";
            }

            _properties.Add(new PropertyItem
            {
                Name = "Signature",
                Value = sigHex,
                IsDifferent = trackDiffs?.Any(d => d.PropertyName == "Signature") == true,
                ByteRange = new ByteRange { PropertyName = "Signature", Offset = track.SignatureOffset, Length = track.SignatureSize }
            });
        }

        _properties.Add(new PropertyItem
        {
            Name = "Block Position",
            Value = $"0x{track.BlockPosition:X8}"
        });

        _properties.Add(new PropertyItem
        {
            Name = "Block Size",
            Value = $"{track.BlockSize:N0} bytes",
            IsDifferent = trackDiffs?.Any(d => d.PropertyName == "Block Size") == true
        });
    }

    private void ShowSRSContainerChunkProperties(SRSContainerChunk chunk)
    {
        _properties.Add(new PropertyItem { Name = "Label", Value = chunk.Label });
        _properties.Add(new PropertyItem { Name = "Chunk ID", Value = chunk.ChunkId });
        _properties.Add(new PropertyItem { Name = "Position", Value = $"0x{chunk.BlockPosition:X8}" });
        _properties.Add(new PropertyItem { Name = "Header Size", Value = $"{chunk.HeaderSize:N0} bytes ({FormatUtilities.FormatSize(chunk.HeaderSize)})" });
        _properties.Add(new PropertyItem { Name = "Payload Size", Value = $"{chunk.PayloadSize:N0} bytes ({FormatUtilities.FormatSize(chunk.PayloadSize)})" });
        _properties.Add(new PropertyItem { Name = "Total Size", Value = $"{chunk.BlockSize:N0} bytes ({FormatUtilities.FormatSize(chunk.BlockSize)})" });
    }

    private void ShowOSOHashProperties(SRROsoHashBlock oso)
    {
        long p = oso.BlockPosition + 7; // skip base header (CRC + type + flags + headerSize)

        _properties.Add(new PropertyItem
        {
            Name = "File Name",
            Value = oso.FileName,
            ByteRange = new ByteRange { Offset = p + 16 + 2, Length = Encoding.UTF8.GetByteCount(oso.FileName) }
        });
        _properties.Add(new PropertyItem
        {
            Name = "File Size",
            Value = $"{oso.FileSize:N0} bytes",
            ByteRange = new ByteRange { Offset = p, Length = 8 }
        });
        _properties.Add(new PropertyItem
        {
            Name = "OSO Hash",
            Value = Convert.ToHexString(oso.OSOHash.Span),
            ByteRange = new ByteRange { Offset = p + 8, Length = 8 }
        });
    }

    private static byte[]? ReadFileSlice(string? filePath, long offset, int length)
    {
        if (filePath is null || length <= 0)
        {
            return null;
        }

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (offset >= fs.Length)
        {
            return null;
        }

        fs.Seek(offset, SeekOrigin.Begin);
        int toRead = (int)Math.Min(length, fs.Length - offset);
        byte[] buffer = new byte[toRead];
        int totalRead = 0;
        while (totalRead < toRead)
        {
            int read = fs.Read(buffer, totalRead, toRead - totalRead);
            if (read == 0)
            {
                break;
            }
            totalRead += read;
        }

        if (totalRead < toRead)
        {
            Array.Resize(ref buffer, totalRead);
        }

        return buffer;
    }

    private void AddComparedProperty(string name, string value, string? diffPropertyName)
    {
        bool isDiff = false;
        if (diffPropertyName is not null && _compareResult is not null)
        {
            isDiff = _compareResult.ArchiveDifferences.Any(d => d.PropertyName == diffPropertyName);
        }

        _properties.Add(new PropertyItem
        {
            Name = name,
            Value = value,
            IsDifferent = isDiff
        });
    }

    private IReadOnlyList<PropertyDifference>? GetTrackDiffs(string trackName)
    {
        FileDifference? fileDiff = _compareResult?.FileDifferences
            .FirstOrDefault(d => d.FileName == trackName && d.Type == DifferenceType.Modified);
        return fileDiff?.PropertyDifferences;
    }
}
