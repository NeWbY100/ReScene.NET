using ReScene.NET.Helpers;

namespace ReScene.NET.Models;

/// <summary>A stored file inside an SRR, for display in the editor list.</summary>
public sealed record StoredFileInfo(string Name, long Size)
{
    public string SizeText => FormatUtilities.FormatSize(Size);
}
