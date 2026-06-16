using ReScene.Core.Comparison;
using ReScene.NET.Helpers;
using ReScene.RAR;
using ReScene.SRR;
using ReScene.SRS;

namespace ReScene.NET.ViewModels.Comparison;

/// <summary>
/// Builds the side-by-side comparison structure tree from parsed file data. Each method returns the
/// single root node for its file type; the view-model owns the bound <c>LeftTreeRoots</c>/
/// <c>RightTreeRoots</c> collections and adds the returned root. The parsed data, the
/// <c>isLeft</c> side flag, and the side's detailed RAR blocks are all passed per call (they are
/// reassigned on every load/close/swap), so the builder captures no view-model state. Node
/// <c>Tag</c> (<see cref="CompareNodeData"/>) values and <c>IsExpanded</c> heuristics are reproduced
/// verbatim from the previous inline view-model code.
/// </summary>
internal static class FileCompareTreeBuilder
{
    public static TreeNodeViewModel BuildDetailed(IReadOnlyList<RARDetailedBlock> blocks, bool isLeft)
    {
        bool isRAR5 = RarBlockLabel.IsRar5Signature(blocks);

        string rootName = isRAR5 ? $"RAR 5.x Archive ({blocks.Count} blocks)" : $"RAR 4.x Archive ({blocks.Count} blocks)";

        var rootNode = new TreeNodeViewModel
        {
            Text = rootName,
            Tag = new CompareNodeData { NodeType = CompareNodeType.Root, IsLeft = isLeft },
            IsExpanded = true
        };

        for (int i = 0; i < blocks.Count; i++)
        {
            RARDetailedBlock block = blocks[i];
            rootNode.Children.Add(new TreeNodeViewModel
            {
                Text = RarBlockLabel.FormatBlockLabel(i, block),
                Tag = new CompareNodeData
                {
                    NodeType = CompareNodeType.DetailedBlock,
                    Data = block,
                    FileName = block.ItemName,
                    IsLeft = isLeft
                }
            });
        }

        return rootNode;
    }

    public static TreeNodeViewModel BuildSrr(SRRFileData srrData, bool isLeft)
    {
        SRRFile srr = srrData.SRRFile;

        var rootNode = new TreeNodeViewModel
        {
            Text = "SRR File",
            Tag = new CompareNodeData { NodeType = CompareNodeType.Root, Data = srr, IsLeft = isLeft },
            IsExpanded = true
        };

        rootNode.Children.Add(new TreeNodeViewModel
        {
            Text = "Archive Info",
            Tag = new CompareNodeData { NodeType = CompareNodeType.ArchiveInfo, Data = srr, IsLeft = isLeft }
        });

        if (srr.RARFiles.Count > 0)
        {
            var volumesNode = new TreeNodeViewModel
            {
                Text = $"RAR Volumes ({srr.RARFiles.Count})",
                Tag = new CompareNodeData { NodeType = CompareNodeType.RARVolumes, Data = srr.RARFiles, IsLeft = isLeft }
            };

            foreach (SRRRarFileBlock rar in srr.RARFiles)
            {
                var volNode = new TreeNodeViewModel
                {
                    Text = rar.FileName,
                    Tag = new CompareNodeData { NodeType = CompareNodeType.RARVolume, Data = rar, IsLeft = isLeft }
                };

                if (srrData.VolumeDetailedBlocks.TryGetValue(rar.FileName, out List<RARDetailedBlock>? detailedBlocks))
                {
                    for (int i = 0; i < detailedBlocks.Count; i++)
                    {
                        RARDetailedBlock block = detailedBlocks[i];
                        volNode.Children.Add(new TreeNodeViewModel
                        {
                            Text = RarBlockLabel.FormatBlockLabel(i, block),
                            Tag = new CompareNodeData
                            {
                                NodeType = CompareNodeType.DetailedBlock,
                                Data = block,
                                FileName = block.ItemName,
                                IsLeft = isLeft
                            }
                        });
                    }
                }

                volumesNode.Children.Add(volNode);
            }

            rootNode.Children.Add(volumesNode);
        }

        if (srr.StoredFiles.Count > 0)
        {
            var storedNode = new TreeNodeViewModel
            {
                Text = $"Stored Files ({srr.StoredFiles.Count})",
                Tag = new CompareNodeData { NodeType = CompareNodeType.StoredFiles, Data = srr.StoredFiles, IsLeft = isLeft }
            };

            foreach (SRRStoredFileBlock stored in srr.StoredFiles)
            {
                storedNode.Children.Add(new TreeNodeViewModel
                {
                    Text = stored.FileName,
                    Tag = new CompareNodeData { NodeType = CompareNodeType.StoredFile, Data = stored, FileName = stored.FileName, IsLeft = isLeft }
                });
            }

            rootNode.Children.Add(storedNode);
        }

        if (srr.ArchivedFiles.Count > 0)
        {
            var archivedNode = new TreeNodeViewModel
            {
                Text = $"Archived Files ({srr.ArchivedFiles.Count})",
                Tag = new CompareNodeData { NodeType = CompareNodeType.ArchivedFiles, Data = srr, IsLeft = isLeft }
            };

            foreach (string? file in srr.ArchivedFiles.OrderBy(f => f))
            {
                string displayName = file;
                if (srr.ArchivedFileCrcs.TryGetValue(file, out string? crc))
                {
                    displayName = $"{file} [CRC: {crc}]";
                }

                archivedNode.Children.Add(new TreeNodeViewModel
                {
                    Text = displayName,
                    Tag = new CompareNodeData { NodeType = CompareNodeType.ArchivedFile, Data = srr, FileName = file, IsLeft = isLeft }
                });
            }

            rootNode.Children.Add(archivedNode);
        }

        if (srr.OSOHashBlocks.Count > 0)
        {
            var osoNode = new TreeNodeViewModel
            {
                Text = $"OSO Hashes ({srr.OSOHashBlocks.Count})",
                Tag = new CompareNodeData { NodeType = CompareNodeType.OSOHashes, Data = srr.OSOHashBlocks, IsLeft = isLeft }
            };

            foreach (SRROsoHashBlock oso in srr.OSOHashBlocks)
            {
                osoNode.Children.Add(new TreeNodeViewModel
                {
                    Text = oso.FileName,
                    Tag = new CompareNodeData
                    {
                        NodeType = CompareNodeType.OSOHash,
                        Data = oso,
                        FileName = oso.FileName,
                        IsLeft = isLeft
                    }
                });
            }

            rootNode.Children.Add(osoNode);
        }

        return rootNode;
    }

    public static TreeNodeViewModel BuildSrs(SRSFile srs, bool isLeft)
    {
        var rootNode = new TreeNodeViewModel
        {
            Text = $"SRS File ({srs.ContainerType})",
            Tag = new CompareNodeData { NodeType = CompareNodeType.Root, Data = srs, IsLeft = isLeft },
            IsExpanded = true
        };

        if (srs.FileData is { } fd)
        {
            rootNode.Children.Add(new TreeNodeViewModel
            {
                Text = $"File Info: {fd.FileName}",
                Tag = new CompareNodeData { NodeType = CompareNodeType.SRSFileInfo, Data = fd, IsLeft = isLeft }
            });
        }

        if (srs.Tracks.Count > 0)
        {
            var tracksNode = new TreeNodeViewModel
            {
                Text = $"Tracks ({srs.Tracks.Count})",
                Tag = new CompareNodeData { NodeType = CompareNodeType.Root, IsLeft = isLeft }
            };

            foreach (SRSTrackDataBlock track in srs.Tracks)
            {
                tracksNode.Children.Add(new TreeNodeViewModel
                {
                    Text = $"Track {track.TrackNumber}",
                    Tag = new CompareNodeData
                    {
                        NodeType = CompareNodeType.SRSTrack,
                        Data = track,
                        FileName = $"Track {track.TrackNumber}",
                        IsLeft = isLeft
                    }
                });
            }

            rootNode.Children.Add(tracksNode);
        }

        if (srs.ContainerChunks.Count > 0)
        {
            var chunksNode = new TreeNodeViewModel
            {
                Text = $"Container Structure ({srs.ContainerChunks.Count})",
                Tag = new CompareNodeData { NodeType = CompareNodeType.SRSContainerChunks, Data = srs.ContainerChunks, IsLeft = isLeft }
            };
            SrsChunkHierarchy.Build(chunksNode, srs.ContainerChunks,
                chunk => new CompareNodeData { NodeType = CompareNodeType.SRSContainerChunks, Data = chunk, IsLeft = isLeft });
            rootNode.Children.Add(chunksNode);
        }

        return rootNode;
    }

    public static TreeNodeViewModel BuildMkv(MKVFileData mkv, bool isLeft)
    {
        var rootNode = new TreeNodeViewModel
        {
            Text = $"MKV File ({mkv.TrackCount} track{(mkv.TrackCount == 1 ? "" : "s")})",
            Tag = new CompareNodeData { NodeType = CompareNodeType.Root, Data = mkv, IsLeft = isLeft },
            IsExpanded = true
        };

        AddMKVElements(rootNode, "", mkv.Elements, isLeft, depth: 0);

        return rootNode;
    }

    /// <summary>
    /// Recursively adds EBML elements as tree nodes. The path key is built with the identical scheme
    /// as <see cref="FileComparer.ElementPath"/> so tree nodes line up with comparison diff keys:
    /// <c>parentPath + "/" + Name</c>, with a <c>[index]</c> suffix for non-first same-named siblings;
    /// root elements use <c>parentPath</c> = "".
    /// </summary>
    private static void AddMKVElements(TreeNodeViewModel parentNode, string parentPath,
        IReadOnlyList<EBMLElement> elements, bool isLeft, int depth)
    {
        var occurrence = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (EBMLElement element in elements)
        {
            occurrence.TryGetValue(element.Name, out int index);
            occurrence[element.Name] = index + 1;

            string path = FileComparer.ElementPath(parentPath, element, index);

            string text = element.Value is { Length: > 0 } && element.Children.Count == 0
                ? $"{element.Name}: {element.Value}"
                : element.Name;

            var node = new TreeNodeViewModel
            {
                Text = text,
                Tag = new CompareNodeData
                {
                    NodeType = CompareNodeType.MKVElement,
                    Data = element,
                    FileName = path,
                    IsLeft = isLeft
                },
                IsExpanded = depth < 2
            };

            if (element.Children.Count > 0)
            {
                AddMKVElements(node, path, element.Children, isLeft, depth + 1);
            }

            parentNode.Children.Add(node);
        }
    }

    public static TreeNodeViewModel BuildRar(RARFileData rar, bool isLeft)
    {
        int fileCount = rar.IsRAR5 ? rar.RAR5FileInfos.Count : rar.FileHeaders.Count;
        int blockCount = 2 + fileCount + 1 + (string.IsNullOrEmpty(rar.Comment) ? 0 : 1);

        var rootNode = new TreeNodeViewModel
        {
            Text = rar.IsRAR5 ? $"RAR 5.x Archive (~{blockCount} blocks)" : $"RAR 4.x Archive (~{blockCount} blocks)",
            Tag = new CompareNodeData { NodeType = CompareNodeType.Root, Data = rar, IsLeft = isLeft },
            IsExpanded = true
        };

        int blockIndex = 0;

        rootNode.Children.Add(new TreeNodeViewModel
        {
            Text = $"[{blockIndex++}] Signature",
            Tag = new CompareNodeData { NodeType = CompareNodeType.Root, IsLeft = isLeft }
        });

        rootNode.Children.Add(new TreeNodeViewModel
        {
            Text = $"[{blockIndex++}] Archive Header",
            Tag = new CompareNodeData { NodeType = CompareNodeType.ArchiveInfo, Data = rar, IsLeft = isLeft }
        });

        if (rar.IsRAR5)
        {
            foreach (RAR5FileInfo file in rar.RAR5FileInfos)
            {
                rootNode.Children.Add(new TreeNodeViewModel
                {
                    Text = $"[{blockIndex++}] File Header: {file.FileName}",
                    Tag = new CompareNodeData { NodeType = CompareNodeType.ArchivedFile, Data = file, FileName = file.FileName, IsLeft = isLeft }
                });
            }
        }
        else
        {
            foreach (RARFileHeader file in rar.FileHeaders)
            {
                rootNode.Children.Add(new TreeNodeViewModel
                {
                    Text = $"[{blockIndex++}] File Header: {file.FileName}",
                    Tag = new CompareNodeData { NodeType = CompareNodeType.ArchivedFile, Data = file, FileName = file.FileName, IsLeft = isLeft }
                });
            }
        }

        if (!string.IsNullOrEmpty(rar.Comment))
        {
            rootNode.Children.Add(new TreeNodeViewModel
            {
                Text = $"[{blockIndex++}] Service Block: CMT",
                Tag = new CompareNodeData { NodeType = CompareNodeType.Root, IsLeft = isLeft }
            });
        }

        rootNode.Children.Add(new TreeNodeViewModel
        {
            Text = $"[{blockIndex}] End Archive",
            Tag = new CompareNodeData { NodeType = CompareNodeType.Root, IsLeft = isLeft }
        });

        return rootNode;
    }
}
