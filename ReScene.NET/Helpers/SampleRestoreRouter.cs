namespace ReScene.NET.Helpers;

/// <summary>Which restore flow an input file maps to.</summary>
public enum SampleRestoreKind
{
    Unknown,
    Srr,
    Srs,
}

/// <summary>
/// Routes a chosen file to the right Beginner restore flow: an .srr triggers bulk restore of
/// every embedded sample; a standalone .srs triggers a single sample rebuild.
/// </summary>
public static class SampleRestoreRouter
{
    public static SampleRestoreKind Route(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return SampleRestoreKind.Unknown;
        }

        string ext = Path.GetExtension(path);
        if (ext.Equals(".srr", StringComparison.OrdinalIgnoreCase))
        {
            return SampleRestoreKind.Srr;
        }

        if (ext.Equals(".srs", StringComparison.OrdinalIgnoreCase))
        {
            return SampleRestoreKind.Srs;
        }

        return SampleRestoreKind.Unknown;
    }
}
