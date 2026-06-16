using ReScene.NET.Helpers;
using ReScene.NET.Models;
using ReScene.RAR;
using ReScene.SRR;
using ReScene.SRS;
using EBMLElement = ReScene.Core.Comparison.EBMLElement;
using MKVFileData = ReScene.Core.Comparison.MKVFileData;

namespace ReScene.NET.ViewModels.Inspection;

/// <summary>
/// Builds the Inspector structure tree from parsed file data. Each method returns the single root
/// node for its file type; the view-model owns the bound <c>TreeRoots</c> collection and adds the
/// returned root. The parsed data is passed per call (it is reassigned on every file load/close),
/// so the builder captures no view-model state. Node <c>Tag</c> values and <c>IsExpanded</c>
/// heuristics are reproduced verbatim from the previous inline view-model code.
/// </summary>
internal static class InspectorTreeBuilder
{
    public static TreeNodeViewModel BuildMkv(MKVFileData mkv)
    {
        var root = new TreeNodeViewModel
        {
            Text = $"MKV File ({mkv.TrackCount} track{(mkv.TrackCount == 1 ? "" : "s")})",
            Tag = "root",
            IsExpanded = true
        };

        AddMKVElements(root, mkv.Elements, depth: 0);
        return root;
    }

    private static void AddMKVElements(TreeNodeViewModel parentNode, IReadOnlyList<EBMLElement> elements, int depth)
    {
        foreach (EBMLElement element in elements)
        {
            string text = element.Value is { Length: > 0 } && element.Children.Count == 0
                ? $"{element.Name}: {element.Value}"
                : element.Name;

            var node = new TreeNodeViewModel
            {
                Text = text,
                Tag = element,
                IsExpanded = depth < 2
            };

            if (element.Children.Count > 0)
            {
                AddMKVElements(node, element.Children, depth + 1);
            }

            parentNode.Children.Add(node);
        }
    }

    public static TreeNodeViewModel BuildRar(IReadOnlyList<RARDetailedBlock> blocks)
    {
        bool isRAR5 = RarBlockLabel.IsRar5Signature(blocks);

        string rootName = isRAR5 ? $"RAR 5.x Archive ({blocks.Count} blocks)" : $"RAR 4.x Archive ({blocks.Count} blocks)";

        var root = new TreeNodeViewModel { Text = rootName, Tag = "root", IsExpanded = true };

        for (int i = 0; i < blocks.Count; i++)
        {
            root.Children.Add(new TreeNodeViewModel { Text = RarBlockLabel.FormatBlockLabel(i, blocks[i]), Tag = blocks[i] });
        }

        return root;
    }

    public static TreeNodeViewModel BuildSrr(SRRFileData srrData)
    {
        SRRFile srr = srrData.SRRFile;

        var root = new TreeNodeViewModel { Text = "SRR File", Tag = "root", IsExpanded = true };

        if (srr.HeaderBlock is not null)
        {
            root.Children.Add(new TreeNodeViewModel { Text = "SRR Header", Tag = srr.HeaderBlock });
        }

        if (srr.RARFiles.Count > 0)
        {
            root.Children.Add(new TreeNodeViewModel { Text = "RAR Archive Info", Tag = srr });
        }

        if (srr.OSOHashBlocks.Count > 0)
        {
            var osoNode = new TreeNodeViewModel
            {
                Text = $"OSO Hashes ({srr.OSOHashBlocks.Count})",
                Tag = "container"
            };
            foreach (SRROsoHashBlock oso in srr.OSOHashBlocks)
            {
                osoNode.Children.Add(new TreeNodeViewModel { Text = oso.FileName, Tag = oso });
            }

            root.Children.Add(osoNode);
        }

        if (srr.RARPaddingBlocks.Count > 0)
        {
            var paddingNode = new TreeNodeViewModel
            {
                Text = $"RAR Padding ({srr.RARPaddingBlocks.Count})",
                Tag = "container"
            };
            foreach (SRRRarPaddingBlock padding in srr.RARPaddingBlocks)
            {
                paddingNode.Children.Add(new TreeNodeViewModel { Text = padding.RARFileName, Tag = padding });
            }

            root.Children.Add(paddingNode);
        }

        if (srr.RARFiles.Count > 0)
        {
            var volumesNode = new TreeNodeViewModel
            {
                Text = $"RAR Volumes ({srr.RARFiles.Count})",
                Tag = "container"
            };
            foreach (SRRRarFileBlock rar in srr.RARFiles)
            {
                var volNode = new TreeNodeViewModel { Text = rar.FileName, Tag = rar };

                if (srrData.VolumeDetailedBlocks.TryGetValue(rar.FileName, out List<RARDetailedBlock>? detailedBlocks))
                {
                    for (int i = 0; i < detailedBlocks.Count; i++)
                    {
                        volNode.Children.Add(new TreeNodeViewModel { Text = RarBlockLabel.FormatBlockLabel(i, detailedBlocks[i]), Tag = detailedBlocks[i] });
                    }
                }

                volumesNode.Children.Add(volNode);
            }

            root.Children.Add(volumesNode);
        }

        if (srr.StoredFiles.Count > 0)
        {
            var storedNode = new TreeNodeViewModel
            {
                Text = $"Stored Files ({srr.StoredFiles.Count})",
                Tag = "container"
            };
            foreach (SRRStoredFileBlock stored in srr.StoredFiles)
            {
                storedNode.Children.Add(new TreeNodeViewModel { Text = stored.FileName, Tag = stored });
            }

            root.Children.Add(storedNode);
        }

        if (srr.ArchivedFiles.Count > 0)
        {
            var archivedNode = new TreeNodeViewModel
            {
                Text = $"Archived Files ({srr.ArchivedFiles.Count})",
                Tag = "container"
            };
            foreach (string? file in srr.ArchivedFiles.OrderBy(f => f))
            {
                string label = file;
                if (srr.ArchivedFileCrcs.TryGetValue(file, out string? crc))
                {
                    label = $"{file} [CRC: {crc}]";
                }

                archivedNode.Children.Add(new TreeNodeViewModel { Text = label, Tag = "archived" });
            }

            root.Children.Add(archivedNode);
        }

        return root;
    }

    public static TreeNodeViewModel BuildSrs(SRSFile srs)
    {
        var root = new TreeNodeViewModel
        {
            Text = $"SRS File ({srs.ContainerType})",
            Tag = srs,
            IsExpanded = true
        };

        if (srs.FileData is not null)
        {
            root.Children.Add(new TreeNodeViewModel
            {
                Text = $"FileData: {srs.FileData.FileName}",
                Tag = srs.FileData
            });
        }

        if (srs.Tracks.Count > 0)
        {
            var tracksNode = new TreeNodeViewModel
            {
                Text = $"Tracks ({srs.Tracks.Count})",
                Tag = "container"
            };
            foreach (SRSTrackDataBlock track in srs.Tracks)
            {
                tracksNode.Children.Add(new TreeNodeViewModel
                {
                    Text = $"Track {track.TrackNumber} ({FormatUtilities.FormatSize((long)track.DataLength)})",
                    Tag = track
                });
            }

            root.Children.Add(tracksNode);
        }

        if (srs.ContainerChunks.Count > 0)
        {
            var chunksNode = new TreeNodeViewModel
            {
                Text = $"Container Chunks ({srs.ContainerChunks.Count})",
                Tag = "container"
            };
            SrsChunkHierarchy.Build(chunksNode, srs.ContainerChunks, chunk => chunk);
            root.Children.Add(chunksNode);
        }

        return root;
    }
}
