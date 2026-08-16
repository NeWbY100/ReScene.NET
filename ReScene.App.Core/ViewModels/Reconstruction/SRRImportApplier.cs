using ReScene.SRR;

namespace ReScene.App.Core.ViewModels.Reconstruction;

/// <summary>
/// The SRR import's pure DECISIONS: which RAR majors an SRR's metadata implies, and which unit a
/// volume size is best expressed in. Each returns what to do; the view-model still does it, at the
/// call site the original used.
/// </summary>
/// <remarks>
/// <para>
/// The application deliberately stays behind. The decisions are interleaved with each other during
/// an import and their ORDER is part of the contract — pinned by
/// <c>ImportSRR_AppliesItsDecisionsInAFixedOrder</c>. Computing one combined diff and applying it in
/// a single pass would produce identical final values while reordering the log lines and the
/// property notifications.
/// </para>
/// <para>
/// The switch decision is NOT here: <see cref="SRRSwitchMapper"/> already owns it, and
/// <c>ApplySwitchDiff</c> is purely the assignment table that consumes it. The timestamp mapping is
/// not here either — it was already a pure static taking setter callbacks, so moving it would be
/// filing, not extraction.
/// </para>
/// </remarks>
internal static class SRRImportApplier
{
    /// <summary>
    /// Which RAR majors to write after the caller's blanket clear, and the line to log.
    /// </summary>
    /// <remarks>
    /// The flags are NULLABLE, and null means "do not write this one at all" - which is NOT the same
    /// as writing false. After the clear, each branch of the original writes only its own flags: the
    /// 7.x branch writes <c>Version7</c> alone, the 5.x/6.x and large-dictionary branches write
    /// <c>Version5</c> and <c>Version6</c>, only the legacy branch writes <c>Version2</c>-<c>Version4</c>,
    /// and no branch ever writes <c>Version7 = false</c>.
    /// <para>
    /// That distinction is observable: PropertyChanged is synchronous, so a subscriber that edits a
    /// flag during the clear keeps its edit if the chosen branch does not write that flag again.
    /// Collapsing this to six plain bools erases such an edit while producing identical values in
    /// identical order - pinned by
    /// <c>SetRARVersionsFromSRR_OnlyWritesTheFlagsItsBranchOwns</c>.
    /// </para>
    /// </remarks>
    internal readonly record struct RARVersionSelection(
        bool? Version2, bool? Version3, bool? Version4, bool? Version5, bool? Version6, bool? Version7, string LogLine);

    /// <summary>
    /// The majors implied by <paramref name="srr"/>, or <see langword="null"/> when it carries no RAR
    /// version at all — in which case the caller must change nothing and log nothing.
    /// </summary>
    public static RARVersionSelection? SelectRARVersions(SRRFile srr)
    {
        if (!srr.RARVersion.HasValue)
        {
            return null;
        }

        int unpVer = srr.RARVersion.Value;

        if (unpVer >= 70)
        {
            return new RARVersionSelection(null, null, null, null, null, true, "RAR versions: 7.x");
        }

        if (unpVer >= 50)
        {
            return new RARVersionSelection(null, null, null, true, true, null, "RAR versions: 5.x, 6.x");
        }

        if (srr.DictionarySize.HasValue && srr.DictionarySize.Value > 4096)
        {
            return new RARVersionSelection(null, null, null, true, true, null,
                $"Large dictionary ({srr.DictionarySize.Value} KB) — RAR 5.x, 6.x");
        }

        bool isRAR2 = unpVer <= 29;
        bool isRAR3 = unpVer is >= 20 and <= 36;
        bool isRAR4 = unpVer is >= 26 and <= 36;

        if (srr.HasFirstVolumeFlag == true || srr.HasUnicodeNames == true)
        {
            isRAR2 = false;
        }

        // Redundant in practice — the ranges above already yield exactly this for 36 — but preserved
        // verbatim rather than "simplified", because nothing distinguishes its presence.
        if (unpVer == 36)
        {
            isRAR2 = false;
            isRAR3 = true;
            isRAR4 = true;
        }

        List<string> selected = [];
        if (isRAR2)
        {
            selected.Add("2.x");
        }

        if (isRAR3)
        {
            selected.Add("3.x");
        }

        if (isRAR4)
        {
            selected.Add("4.x");
        }

        selected.Add("5.x");
        selected.Add("6.x");

        // 5.x and 6.x are always selected here: a RAR4-format archive can be produced by 5.x/6.x
        // with -ma4.
        // The legacy branch is the only one that writes 2-4, and it never writes 7.
        return new RARVersionSelection(isRAR2, isRAR3, isRAR4, true, true, null,
            $"RAR versions: {string.Join(", ", selected)}");
    }

    /// <summary>The size text and unit index a byte count is best expressed in.</summary>
    internal readonly record struct VolumeSizeSelection(string Size, int UnitIndex);

    /// <summary>
    /// The largest unit that divides <paramref name="sizeBytes"/> EXACTLY — decimal units before
    /// binary ones, falling through to raw bytes — or <see langword="null"/> for a nonpositive size,
    /// which the caller must ignore entirely.
    /// </summary>
    public static VolumeSizeSelection? SelectVolumeSize(long sizeBytes)
    {
        if (sizeBytes <= 0)
        {
            return null;
        }

        if (sizeBytes % 1_000_000_000 == 0)
        {
            return new VolumeSizeSelection((sizeBytes / 1_000_000_000).ToString(), 3);
        }

        if (sizeBytes % 1_000_000 == 0)
        {
            return new VolumeSizeSelection((sizeBytes / 1_000_000).ToString(), 2);
        }

        if (sizeBytes % 1_000 == 0)
        {
            return new VolumeSizeSelection((sizeBytes / 1_000).ToString(), 1);
        }

        if (sizeBytes % (1024L * 1024 * 1024) == 0)
        {
            return new VolumeSizeSelection((sizeBytes / (1024L * 1024 * 1024)).ToString(), 6);
        }

        if (sizeBytes % (1024L * 1024) == 0)
        {
            return new VolumeSizeSelection((sizeBytes / (1024L * 1024)).ToString(), 5);
        }

        if (sizeBytes % 1024 == 0)
        {
            return new VolumeSizeSelection((sizeBytes / 1024).ToString(), 4);
        }

        return new VolumeSizeSelection(sizeBytes.ToString(), 0);
    }
}
