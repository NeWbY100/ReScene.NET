using ReScene.SRR;

namespace ReScene.NET.ViewModels.Reconstruction;

/// <summary>
/// Pure mapping from an imported <see cref="SRRFile"/> to the subset of RAR switch toggles the SRR
/// actually specifies (compression method, dictionary size, solid flag, archive format). The result
/// is a <em>partial</em> diff: each group is null when the SRR carries no information for it, so the
/// view-model leaves the corresponding toggles untouched rather than resetting them to defaults.
/// The mapper neither logs nor mutates bound state; the view-model applies the diff and emits the
/// import log lines, preserving their exact text and ordering.
/// </summary>
internal static class SrrSwitchMapper
{
    /// <summary>Compression method (-m0..-m5) the SRR specifies, plus its log label.</summary>
    public readonly record struct CompressionMap(int Method, string LogName);

    /// <summary>Dictionary size selection the SRR specifies, plus the size for the log line.</summary>
    public readonly record struct DictionaryMap(DictionarySwitch Switch, int SizeKb);

    /// <summary>Archive format (-ma4/-ma5) the SRR specifies, plus its log line (null = RAR7, no -ma).</summary>
    public readonly record struct FormatMap(bool MA4, bool MA5, string LogLine);

    /// <summary>Which single dictionary-size toggle to enable, or <see cref="None"/> for the deliberately unmapped 8M..1G range.</summary>
    public enum DictionarySwitch
    {
        None,
        MD64K,
        MD128K,
        MD256K,
        MD512K,
        MD1024K,
        MD2048K,
        MD4096K,
    }

    /// <summary>
    /// The partial set of switch values an SRR specifies. Every member is null when the SRR carries
    /// no information for that group, so applying the diff never clobbers an unspecified toggle.
    /// </summary>
    public readonly record struct SwitchDiff(
        CompressionMap? Compression,
        DictionaryMap? Dictionary,
        bool? SwitchSDash,
        FormatMap? Format);

    private static readonly string[] _compressionNames = ["Store", "Fastest", "Fast", "Normal", "Good", "Best"];

    /// <summary>Builds the partial switch diff from the SRR's detected metadata.</summary>
    public static SwitchDiff Map(SRRFile srr) => new(
        Compression: MapCompression(srr),
        Dictionary: MapDictionary(srr),
        SwitchSDash: srr.IsSolidArchive.HasValue ? !srr.IsSolidArchive.Value : null,
        Format: MapFormat(srr));

    private static CompressionMap? MapCompression(SRRFile srr)
    {
        if (!srr.CompressionMethod.HasValue)
        {
            return null;
        }

        int method = srr.CompressionMethod.Value;
        if (method is < 0 or > 5)
        {
            return null;
        }

        return new CompressionMap(method, _compressionNames[method]);
    }

    private static DictionaryMap? MapDictionary(SRRFile srr)
    {
        if (!srr.DictionarySize.HasValue)
        {
            return null;
        }

        int size = srr.DictionarySize.Value;

        // Note: the 8M..1G cases are deliberately not mapped here; the clear-then-set still runs so
        // those toggles are cleared, but none is re-enabled for a large dictionary.
        DictionarySwitch which = size switch
        {
            64 => DictionarySwitch.MD64K,
            128 => DictionarySwitch.MD128K,
            256 => DictionarySwitch.MD256K,
            512 => DictionarySwitch.MD512K,
            1024 => DictionarySwitch.MD1024K,
            2048 => DictionarySwitch.MD2048K,
            4096 => DictionarySwitch.MD4096K,
            _ => DictionarySwitch.None,
        };

        return new DictionaryMap(which, size);
    }

    private static FormatMap? MapFormat(SRRFile srr)
    {
        if (!srr.RARVersion.HasValue)
        {
            return null;
        }

        if (srr.RARVersion.Value < 50)
        {
            return new FormatMap(MA4: true, MA5: false, "Archive format: RAR4 (-ma4)");
        }

        if (srr.RARVersion.Value < 70)
        {
            return new FormatMap(MA4: false, MA5: true, "Archive format: RAR5 (-ma5)");
        }

        return new FormatMap(MA4: false, MA5: false, "Archive format: RAR7");
    }
}
