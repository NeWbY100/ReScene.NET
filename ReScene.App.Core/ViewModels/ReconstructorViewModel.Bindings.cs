using CommunityToolkit.Mvvm.ComponentModel;
using ReScene.App.Core.ViewModels.Reconstruction;
using ReScene.Core;

namespace ReScene.App.Core.ViewModels;

// The reconstructor's pinned binding surface: property declarations the view binds, filed away from
// the primary file so what remains there is behaviour. THIS IS FILING, NOT DECOMPOSITION - nothing
// here was rewritten, and none of it can move to a collaborator, because the view binds these names.
//
// Every generated member resolves ACROSS the partial boundary, in both directions. WinRARPath alone
// exercises the lot and was moved first as a feasibility probe:
//   * its [ObservableProperty] declaration lives here;
//   * OnWinRARPathChanged is implemented in the primary file;
//   * [NotifyCanExecuteChangedFor(StartCommand)] targets a command generated from StartAsync there;
//   * both [NotifyPropertyChangedFor] targets are computed properties declared there.
// The same holds for the toggle regions below, several of which have On<X>Changed hooks and
// computed-property notify targets left behind in the primary file.
public partial class ReconstructorViewModel
{
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    [NotifyPropertyChangedFor(nameof(PathsNeedAttention))]
    [NotifyPropertyChangedFor(nameof(PathsTabAccessibleName))]
    public partial string WinRARPath { get; set; } = string.Empty;

    // ── Compression Method ──

    [ObservableProperty] public partial bool SwitchM0 { get; set; }
    [ObservableProperty] public partial bool SwitchM1 { get; set; }
    [ObservableProperty] public partial bool SwitchM2 { get; set; }
    [ObservableProperty] public partial bool SwitchM3 { get; set; } = true;
    [ObservableProperty] public partial bool SwitchM4 { get; set; }
    [ObservableProperty] public partial bool SwitchM5 { get; set; }

    // ── Archive Format ──

    [ObservableProperty] public partial bool SwitchMA4 { get; set; }
    [ObservableProperty] public partial bool SwitchMA5 { get; set; }

    // ── Dictionary Size ──

    [ObservableProperty] public partial bool SwitchMD64K { get; set; }
    [ObservableProperty] public partial bool SwitchMD128K { get; set; }
    [ObservableProperty] public partial bool SwitchMD256K { get; set; }
    [ObservableProperty] public partial bool SwitchMD512K { get; set; }
    [ObservableProperty] public partial bool SwitchMD1024K { get; set; }
    [ObservableProperty] public partial bool SwitchMD2048K { get; set; }
    [ObservableProperty] public partial bool SwitchMD4096K { get; set; } = true;
    [ObservableProperty] public partial bool SwitchMD8M { get; set; }
    [ObservableProperty] public partial bool SwitchMD16M { get; set; }
    [ObservableProperty] public partial bool SwitchMD32M { get; set; }
    [ObservableProperty] public partial bool SwitchMD64M { get; set; }
    [ObservableProperty] public partial bool SwitchMD128M { get; set; }
    [ObservableProperty] public partial bool SwitchMD256M { get; set; }
    [ObservableProperty] public partial bool SwitchMD512M { get; set; }
    [ObservableProperty] public partial bool SwitchMD1G { get; set; }

    // ── Timestamps ──

    [ObservableProperty] public partial bool SwitchTSM0 { get; set; }
    [ObservableProperty] public partial bool SwitchTSM1 { get; set; }
    [ObservableProperty] public partial bool SwitchTSM2 { get; set; }
    [ObservableProperty] public partial bool SwitchTSM3 { get; set; }
    [ObservableProperty] public partial bool SwitchTSM4 { get; set; }

    [ObservableProperty] public partial bool SwitchTSC0 { get; set; }
    [ObservableProperty] public partial bool SwitchTSC1 { get; set; }
    [ObservableProperty] public partial bool SwitchTSC2 { get; set; }
    [ObservableProperty] public partial bool SwitchTSC3 { get; set; }
    [ObservableProperty] public partial bool SwitchTSC4 { get; set; }

    [ObservableProperty] public partial bool SwitchTSA0 { get; set; }
    [ObservableProperty] public partial bool SwitchTSA1 { get; set; }
    [ObservableProperty] public partial bool SwitchTSA2 { get; set; }
    [ObservableProperty] public partial bool SwitchTSA3 { get; set; }
    [ObservableProperty] public partial bool SwitchTSA4 { get; set; }

    // ── Other Options ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFileAttributesEnabled))]
    public partial bool SwitchAI { get; set; }

    [ObservableProperty] public partial bool SwitchR { get; set; } = true;
    [ObservableProperty] public partial bool SwitchDS { get; set; }
    [ObservableProperty] public partial bool SwitchS { get; set; }
    [ObservableProperty] public partial bool SwitchSDash { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMTRangeEnabled))]
    public partial bool SwitchMT { get; set; }

    [ObservableProperty] public partial int SwitchMTStart { get; set; } = 1;
    [ObservableProperty] public partial int SwitchMTEnd { get; set; } = Environment.ProcessorCount;

    // Volume
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVolumeOptionsEnabled))]
    public partial bool SwitchV { get; set; }

    [ObservableProperty] public partial string VolumeSize { get; set; } = DefaultVolumeSizeKb.ToString();
    [ObservableProperty] public partial int VolumeSizeUnitIndex { get; set; } = 1; // default KB
    [ObservableProperty] public partial bool UseOldVolumeNaming { get; set; }

    public static string[] VolumeSizeUnits { get; } = ["Bytes", "KB", "MB", "GB", "KiB", "MiB", "GiB"];

    // File attributes (null = Indeterminate)
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSwitchAIEnabled))]
    public partial bool? FileA { get; set; } = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSwitchAIEnabled))]
    public partial bool? FileI { get; set; } = false;

    // Output options
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDeleteDuplicateCRCEnabled))]
    public partial bool DeleteRARFiles { get; set; }

    [ObservableProperty] public partial bool DeleteDuplicateCRCFiles { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRenameEnabled))]
    public partial bool StopOnFirstMatch { get; set; } = true;

    [ObservableProperty] public partial bool CompleteAllVolumes { get; set; }
    [ObservableProperty] public partial bool RenameToReleaseNames { get; set; } = true;

    /// <summary>
    /// The rename option requires Stop-after-first-match, so when it is turned off it is cleared
    /// (not left checked-but-greyed). It cannot be turned on while it is off — the sub-item is
    /// disabled — so no reverse coupling is needed.
    /// </summary>
    partial void OnStopOnFirstMatchChanged(bool value)
    {
        if (!value)
        {
            RenameToReleaseNames = false;
        }
    }

    partial void OnSwitchSChanged(bool value)
    {
        if (value)
        {
            SwitchSDash = false;
        }
    }

    partial void OnSwitchSDashChanged(bool value)
    {
        if (value)
        {
            SwitchS = false;
        }
    }

    /// <summary>
    /// Clamps to 0..64 (the highest thread count any WinRAR version accepts) so an unbounded or
    /// pasted-in value (e.g. int.MaxValue) can never reach <see cref="RARCommandLineBuilder"/>.
    /// The builder itself re-clamps defensively, but catching it here gives immediate UI feedback.
    /// </summary>
    partial void OnSwitchMTStartChanged(int value)
    {
        int clamped = Math.Clamp(value, 0, RARCommandLineBuilder.MaxThreadCount);
        if (clamped != value)
        {
            SwitchMTStart = clamped;
        }
    }

    partial void OnSwitchMTEndChanged(int value)
    {
        int clamped = Math.Clamp(value, 0, RARCommandLineBuilder.MaxThreadCount);
        if (clamped != value)
        {
            SwitchMTEnd = clamped;
        }
    }

    public partial class VersionEntry : ObservableObject
    {
        [ObservableProperty] public partial string VersionName { get; set; } = "";
        [ObservableProperty] public partial string Status { get; set; } = "Testing";
        [ObservableProperty] public partial string Arguments { get; set; } = "";
        [ObservableProperty] public partial string Result { get; set; } = "";

        /// <summary>Label of the archive set this test belongs to (empty for single-set releases).</summary>
        public string SetText { get; set; } = "";

        /// <summary>
        /// Directory of the WinRAR version this entry tested; the run executes the RAR console
        /// binary (see <see cref="RarExecutable"/>) inside it.
        /// </summary>
        public string VersionDirectory { get; set; } = "";

        /// <summary>
        /// Working directory of this attempt's rar invocation — the run's prepared input-files copy.
        /// Empty when unknown (e.g. Phase-1 comment-filter rows). Note it is a temp directory the run
        /// may clean up afterwards.
        /// </summary>
        public string InputDirectory { get; set; } = "";

        /// <summary>Output archive path of this attempt's rar invocation; empty when unknown.</summary>
        public string OutputFilePath { get; set; } = "";

        /// <summary>
        /// The argument string rar was ACTUALLY invoked with — the display form plus engine-added
        /// switches (-cfg-, -ds with an explicit file order, -ma4 for 5.50–6.x, -vn, -z&lt;commentfile&gt;).
        /// Empty when unknown. The runnable
        /// copied command must use this; the grid column and "Testing …" log lines keep the display
        /// form (<see cref="Arguments"/>).
        /// </summary>
        public string ExecutedArguments { get; set; } = "";

        /// <summary>
        /// This attempt's rar INPUT operand, rendered the same way as <see cref="ExecutedArguments"/> —
        /// the explicit, SRR-ordered file-list tail passed after the output path when SRR-guided
        /// assembly supplied an archived-file order. Empty when this attempt used rar's own input mask
        /// (no order available, or assembly not engaged for this set); the copied command line then
        /// falls back to the platform mask exactly as before.
        /// </summary>
        public string InputFileArguments { get; set; } = "";

        /// <summary>
        /// The quoted RAR console binary followed by the switches — the terse per-attempt form used by
        /// the "Testing …" log lines (the merged log keeps its lines short; the temp paths would
        /// repeat on every line).
        /// </summary>
        public string ExeAndArguments => string.IsNullOrEmpty(VersionDirectory)
            ? Arguments
            : $"\"{RarExecutable.ResolveIn(VersionDirectory)}\" {Arguments}";

        /// <summary>
        /// The complete command line as executed, runnable as pasted (no trailing newline — pasting
        /// must never auto-execute): a shell prefix entering the invocation's working directory
        /// (pushd on Windows so cross-drive cd works in cmd — the Windows form is cmd dialect by
        /// choice; cd on Unix, valid in every POSIX shell), the quoted RAR console binary, the
        /// switches, the quoted output archive, and either <see cref="InputFileArguments"/> (when this
        /// attempt used an explicit SRR-ordered file list) or the platform input mask — mirroring
        /// <see cref="ReScene.Core.Diagnostics.RARProcess"/>'s composition. The prefix matters: rar
        /// stores entry names relative to its working directory, so running the same switches against
        /// an absolute path would archive different names. Falls back to
        /// <see cref="ExeAndArguments"/> when the invocation details are unknown (Phase-1 rows). The
        /// input directory is the run's temp working copy and may be cleaned up after the run.
        /// </summary>
        public string FullCommandLine
        {
            get
            {
                if (string.IsNullOrEmpty(VersionDirectory))
                {
                    return Arguments;
                }

                if (InputDirectory.Length == 0 || OutputFilePath.Length == 0)
                {
                    return ExeAndArguments;
                }

                // Compose with the EXECUTED argument string (engine-added -cfg-/-ds/-ma4/-vn/-z included): the
                // display form omits switches that change the produced bytes — e.g. rar 5.50-6.x
                // defaults to RAR5 format without -ma4 — so pasting it would silently build a
                // different archive than the run this line claims to reproduce.
                string effectiveArguments = ExecutedArguments.Length > 0 ? ExecutedArguments : Arguments;
                string invocation = $"\"{RarExecutable.ResolveIn(VersionDirectory)}\" {effectiveArguments}";

                // The tail reproduces the ACTUAL input rar was given: the explicit SRR-ordered file list
                // (already whole-token quoted, like ExecutedArguments) when this attempt used one, else
                // today's platform input mask unchanged — pasting the mask on a machine whose rarfiles.lst
                // or name-sort default differs could otherwise reorder a solid set's contents.
                string tail = InputFileArguments.Length > 0
                    ? InputFileArguments
                    : OperatingSystem.IsWindows() ? ".\\*" : "'./*'";
                return OperatingSystem.IsWindows()
                    ? $"pushd \"{InputDirectory}\" && {invocation} \"{OutputFilePath}\" {tail}"
                    : $"cd \"{InputDirectory}\" && {invocation} \"{OutputFilePath}\" {tail}";
            }
        }

        // ── Timing ──
        // StartedAt is stamped when the row is created (the tracker constructs a row exactly when
        // its test begins). EndedAt is stamped once, when Status first leaves "Testing".

        /// <summary>When this test started (row construction time).</summary>
        public DateTime StartedAt { get; } = DateTime.Now;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EndText))]
        [NotifyPropertyChangedFor(nameof(DurationText))]
        public partial DateTime? EndedAt { get; set; }

        /// <summary>Wall-clock start time, e.g. "22:13:28".</summary>
        public string StartText => StartedAt.ToString("HH:mm:ss");

        /// <summary>Wall-clock end time, or empty while the test is still running.</summary>
        public string EndText => EndedAt?.ToString("HH:mm:ss") ?? string.Empty;

        /// <summary>
        /// Elapsed test time: counts up live while the test runs, then freezes at the final duration
        /// once the row finishes. Driven once per second by <see cref="RefreshLiveDuration"/>.
        /// </summary>
        public string DurationText =>
            ReconstructorFormatting.FormatTimeSpan((EndedAt ?? DateTime.Now) - StartedAt);

        /// <summary>Raises a change for <see cref="DurationText"/> so the live value re-renders.</summary>
        public void RefreshLiveDuration() => OnPropertyChanged(nameof(DurationText));

        // Stamp the end time the moment the row leaves "Testing" (Complete / Cancelled / Error all
        // flow through this setter, set by the tracker). The null guard makes it idempotent.
        partial void OnStatusChanged(string value)
        {
            if (value != "Testing" && EndedAt is null)
            {
                EndedAt = DateTime.Now;
            }
        }
    }
}
