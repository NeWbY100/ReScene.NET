using ReScene.App.Core.Services;
using ReScene.Core;

namespace ReScene.App.Core.ViewModels.Reconstruction;

/// <summary>
/// The reconstructor's start gauntlet: every reject-the-run decision, the verification-file parse,
/// and the output-directory preparation, in that order.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two orderings here are safety properties, not tidiness.</b> Every reject decision runs BEFORE
/// the destructive <see cref="ReservedOutputTreeManager.ClearReservedSubtrees"/>, so a run that will
/// be refused never erases existing output (#3, #17). And the verification file is parsed BEFORE the
/// output cleanup, because the cleanup may delete it (#14).
/// </para>
/// <para>
/// <b>The snapshot is handed over through <c>onParsed</c> at the parse point, not returned in the
/// result.</b> The original assigns it there, before the later rejections for missing imported files,
/// a declined cleanup confirmation, or a cleanup failure - so a rejected run still replaces the
/// previous run's snapshot. Returning it only on acceptance would silently change that, and deferring
/// the write past an awaited confirmation would change its observable timing.
/// <c>ReconstructorStartValidationTests</c> pins both halves. "Retain snapshots only from accepted
/// starts" would be a separate behaviour fix.
/// </para>
/// <para>
/// Two documented deviations from the spec's sketch. It is a sealed instance rather than a static
/// class, because the stable collaborators belong in a constructor, which keeps the per-run input
/// down to state alone; none of the behavioural constraints depends on static storage. And it returns
/// <see langword="bool"/> rather than a result struct, because there is exactly one bit to report -
/// every rejection has already surfaced its own message - and a one-field wrapper would invite
/// someone to return the snapshot through it, which is the mistake the parse-point handoff exists to
/// prevent.
/// </para>
/// </remarks>
/// <param name="fileDialog">Used directly rather than through wrapped callbacks - ShowError, ShowWarning and ShowConfirmAsync are all called.</param>
/// <param name="log">Appends to the run log. Every original call passed <see cref="LogTarget.System"/>.</param>
/// <param name="evaluateRunPreflight">
/// The view-model's plan-before-mutate preflight. Stays a callback: the Beginner wizard calls the same
/// forwarder from production, and it reads broader live state than this validator sees.
/// </param>
/// <param name="subdirTimestampWarningText">The view-model's shared warning constant.</param>
internal sealed class ReconstructorStartValidator(
    IFileDialogService fileDialog,
    Action<string> log,
    Func<string?> evaluateRunPreflight,
    string subdirTimestampWarningText)
{
    /// <summary>
    /// The run state the gauntlet reads. LIVE accessors, invoked at each original read site, because
    /// none of these controls is disabled while a start is being validated and two of them
    /// (<see cref="VerificationPath"/>, <see cref="OutputPath"/>) are read again after an awaited
    /// confirmation. Named required properties rather than a positional record: four adjacent
    /// <c>Func&lt;string&gt;</c> arguments could be swapped silently and still compile.
    /// </summary>
    internal sealed record Inputs
    {
        public required Func<string> WinRARPath { get; init; }

        public required Func<bool> HasScannedVersions { get; init; }

        /// <summary>
        /// The version tree itself, not a count: the gauntlet checks <c>Count</c> AND enumerates every
        /// leaf's checked state. A stable collection reference whose CONTENTS stay live.
        /// </summary>
        public required IReadOnlyList<RARVersionGroup> VersionGroups { get; init; }

        public required Func<string> ReleasePath { get; init; }

        public required Func<string> OutputPath { get; init; }

        public required Func<string> VerificationPath { get; init; }

        /// <summary>
        /// The imported-SRR state. An accessor, invoked at each original read: the holder is replaced
        /// wholesale by the <c>SetImportStateForTest</c> seam, so caching it here would pin whichever
        /// instance happened to exist when validation began.
        /// </summary>
        public required Func<ReconstructionImportState> Import { get; init; }

        /// <summary>
        /// The one-shot wizard confirmations, genuinely snapshotted: the view-model consumes and
        /// clears them before validation begins, so a stale flag can never suppress a later prompt.
        /// </summary>
        public required bool SubdirTimestampsConfirmed { get; init; }

        public required bool OutputNotEmptyConfirmed { get; init; }
    }

    /// <summary>
    /// Runs the gauntlet. Returns whether the run may start; every rejection has already surfaced its
    /// own message. <paramref name="onParsed"/> receives the verification snapshot at the parse point
    /// - see the remarks on this class for why that is not a return value.
    /// </summary>
    public async Task<bool> ValidateAsync(Inputs inputs, Action<VerificationSnapshot> onParsed)
    {
        // ── Path validation ──

        if (string.IsNullOrWhiteSpace(inputs.WinRARPath()))
        {
            log("Invalid WinRAR directory.");
            fileDialog.ShowError("Validation Error", "Invalid WinRAR directory.");
            return false;
        }

        if (!Directory.Exists(inputs.WinRARPath()))
        {
            log("WinRAR directory does not exist.");
            fileDialog.ShowError("Validation Error", "WinRAR directory does not exist.");
            return false;
        }

        // A real scan that found zero valid version subfolders — block with a clear message so the
        // user knows to add a version subfolder. The no-scan fallback (HasScannedVersions == false)
        // still uses the broad major-version range and must not be blocked here.
        if (inputs.HasScannedVersions() && inputs.VersionGroups.Count == 0)
        {
            log("No WinRAR versions found in the selected folder.");
            fileDialog.ShowError("Validation Error",
                $"No WinRAR versions were found in the WinRAR versions folder. Add a version subfolder containing {RarExecutable.FileName}, then click Rescan.");
            return false;
        }

        // A materialised tree with nothing ticked would brute-force zero versions — block it with a
        // clear message. The no-scan case (empty tree) is unaffected and uses the broad fallback.
        if (inputs.VersionGroups.Count > 0 && inputs.VersionGroups.SelectMany(g => g.Leaves).All(l => !l.IsChecked))
        {
            log("No WinRAR versions selected.");
            fileDialog.ShowError("Validation Error", "Select at least one WinRAR version.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(inputs.ReleasePath()))
        {
            log("Invalid release directory.");
            fileDialog.ShowError("Validation Error", "Invalid release directory.");
            return false;
        }

        if (!Directory.Exists(inputs.ReleasePath()))
        {
            log("Release directory does not exist.");
            fileDialog.ShowError("Validation Error", "Release directory does not exist.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(inputs.OutputPath()))
        {
            log("Invalid output directory.");
            fileDialog.ShowError("Validation Error", "Invalid output directory.");
            return false;
        }

        // ── Plan before mutate ──
        //
        // Make every reject-the-run decision (multi-set custom packer, reserved-root distinctness,
        // live-input overlap, and — with no archive file list — release/output self-inclusion) BEFORE
        // the destructive output cleanup below and before any confirm dialog, so an already-known
        // unsupported run never erases existing output (#3, #1, #17).
        if (evaluateRunPreflight() is { } rejection)
        {
            log($"Cannot start: {rejection}");
            fileDialog.ShowError("Validation Error", rejection);
            return false;
        }

        // ── Subdirectory timestamp warning ──

        bool releaseHasSubdirectories;
        try
        {
            releaseHasSubdirectories = Directory.EnumerateDirectories(inputs.ReleasePath()).Any();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            log($"Could not inspect the release directory: {ex.Message}");
            fileDialog.ShowError("Validation Error", $"Could not inspect the release directory:\n{ex.Message}");
            return false;
        }

        if (releaseHasSubdirectories && inputs.Import().DirTimestamps.Count == 0)
        {
            bool proceed = inputs.SubdirTimestampsConfirmed || await fileDialog.ShowConfirmAsync("Warning: modified date",
                subdirTimestampWarningText);
            if (!proceed)
            {
                log("Cancelled: subdirectory timestamp warning.");
                return false;
            }
        }

        // ── Verification file validation ──
        //
        // Parsed once, here, into an immutable snapshot — BEFORE the output-directory cleanup below
        // (which deletes the file if it happens to sit inside OutputPath) and before any per-set
        // work-dir cleanup. Every downstream verification read (per-set CRCs, first-volume gate
        // hashes, flat-set fallback names) draws from this snapshot; the file itself is never
        // re-read after this point (#14).

        if (string.IsNullOrWhiteSpace(inputs.VerificationPath()))
        {
            log("Invalid verification file path.");
            fileDialog.ShowError("Validation Error", "Invalid verification file path.");
            return false;
        }

        if (!File.Exists(inputs.VerificationPath()))
        {
            log("Verification file does not exist.");
            fileDialog.ShowError("Validation Error", "Verification file does not exist.");
            return false;
        }

        string verificationExt = Path.GetExtension(inputs.VerificationPath()).ToLowerInvariant();
        if (verificationExt is not ".sfv" and not ".sha1")
        {
            log("Invalid verification file type.");
            fileDialog.ShowError("Validation Error", "Invalid verification file type. Use .sfv or .sha1 files.");
            return false;
        }

        VerificationSnapshot snapshot;
        try
        {
            snapshot = VerificationSnapshot.Load(inputs.VerificationPath());
        }
        catch (Exception ex)
        {
            log($"Failed to parse verification file: {ex.Message}");
            fileDialog.ShowError("Validation Error", $"Failed to parse verification file:\n{ex.Message}");
            return false;
        }

        if (snapshot.Entries.Count == 0)
        {
            log("No hashes found in verification file.");
            fileDialog.ShowError("Validation Error", "No hashes found in verification file.");
            return false;
        }

        onParsed(snapshot);

        // ── Input file existence check ──
        //
        // The verify file (.sfv/.sha1) lists the OUTPUT archives we're trying to produce,
        // so it isn't useful as an input check. The imported SRR's archived files ARE the
        // expected input contents — verify those exist in the release directory. If no SRR
        // has been imported, skip this pre-flight; Manager.ValidateInputFiles will run later.
        if (inputs.Import().ArchiveFiles.Count > 0)
        {
            try
            {
                var missingFiles = new List<string>();
                foreach (string archiveFile in inputs.Import().ArchiveFiles)
                {
                    string fullPath = Path.Combine(inputs.ReleasePath(), archiveFile);
                    if (!File.Exists(fullPath))
                    {
                        missingFiles.Add(archiveFile);
                    }
                }

                if (missingFiles.Count > 0)
                {
                    string fileList = string.Join("\n", missingFiles);
                    log($"Missing {missingFiles.Count} input file(s) in release directory.");
                    fileDialog.ShowWarning(
                        "Missing Input Files",
                        $"The following {missingFiles.Count} file(s) listed in the imported SRR are missing from the release directory:\n\n{fileList}\n\nThe release directory should contain the unpacked archive contents (the files that originally went into the RARs).");
                    return false;
                }
            }
            catch (Exception ex)
            {
                log($"Failed to validate input files: {ex.Message}");
            }
        }

        // ── Output directory validation & cleanup ──
        //
        // Reconstruction only ever writes into (and only ever clears) the two reserved subtrees under
        // OutputPath — the final `output` tree and the `.rescene-work` scratch tree. Unrelated files at
        // the OutputPath root are preserved (#4).

        if (!Directory.Exists(inputs.OutputPath()))
        {
            try
            {
                Directory.CreateDirectory(inputs.OutputPath());
                log($"Created output directory: {inputs.OutputPath()}");
            }
            catch (Exception ex)
            {
                log($"Failed to create output directory: {ex.Message}");
                fileDialog.ShowError("Validation Error", $"Failed to create output directory:\n{ex.Message}");
                return false;
            }
        }
        else if (ReservedOutputTreeManager.HasReconstructionArtifacts(inputs.OutputPath()))
        {
            bool proceed = inputs.OutputNotEmptyConfirmed || await fileDialog.ShowConfirmAsync("Output Directory Not Empty",
                ReservedOutputTreeManager.ConfirmText(inputs.OutputPath()));
            if (!proceed)
            {
                log("Cancelled: output directory not empty.");
                return false;
            }

            if (!ReservedOutputTreeManager.ClearReservedSubtrees(inputs.OutputPath(), log, fileDialog.ShowError))
            {
                return false;
            }
        }

        return true;
    }
}
