using ReScene.App.Core.ViewModels.Reconstruction;
using ReScene.Core;

namespace ReScene.App.Core.ViewModels;

// The reconstruction engine's progress handlers. Filed here to keep the primary file readable;
// this is not a decomposition and nothing about the code changed in the move. A file comment rather
// than XML documentation: it describes this file, not the whole ReconstructorViewModel type.
//
// THE INVOKE-VS-POST MIX IS LOAD-BEARING. Both marshal onto the UI thread - neither runs the action
// on the engine's callback thread. What differs is whether the engine's callback WAITS:
//
//   OnProgress               Invoke - blocks the engine callback until the UI applies the update, so
//                                     the main progress figures are current the moment it returns.
//   OnFileCopyProgress       Post   - queues and lets the callback return at once, so a burst of
//   OnCRCValidationProgress           per-file events cannot stall the engine on the UI thread.
//   OnElapsedTimerTick       neither - IUiTimer already guarantees UI-thread ticks.
//
// The two `if (!IsRunning) return;` gates read the LIVE flag, never a snapshot: the run's finally
// clears IsRunning before the guarded busy flags precisely so a late queued event is rejected here
// rather than re-opening a progress window that has already closed. All of this is pinned by
// ReconstructorLoggingProgressTests.
public partial class ReconstructorViewModel
{
    // ── Event Handlers ──

    private void OnFileCopyProgress(object? _, FileCopyProgressEventArgs e)
    {
        _uiDispatcher.Post(() =>
        {
            // A queued progress event can arrive after a cancelled run already cleaned up;
            // re-raising IsCopying then would re-open (and strand) the copy progress window.
            if (!IsRunning)
            {
                return;
            }

            if (!IsCopying)
            {
                IsCopying = true;
                _progress.StartCopy();
            }

            CopyProgressUpdate u = _progress.ApplyCopyProgress(e);
            CopyHeadingText = u.HeadingText;
            CopySourceText = u.SourceText;
            CopyDestText = u.DestText;
            CopyProgressPercent = u.ProgressPercent;
            CopyProgressPercentText = u.ProgressPercentText;
            CopyCurrentFileText = u.CurrentFileText;
            CopyRemainingText = u.RemainingText;
            CopyElapsedText = u.ElapsedText;
            if (u.HasSpeed)
            {
                CopySpeedText = u.SpeedText;
                if (u.HasEta)
                {
                    CopyTimeRemainingText = u.TimeRemainingText;
                    CopyEtaText = u.EtaText;
                }
            }

            if (u.IsComplete)
            {
                IsCopying = false;
            }
        });
    }


    private void OnCRCValidationProgress(object? _, CRCValidationProgressEventArgs e)
    {
        _uiDispatcher.Post(() =>
        {
            // A queued progress event can arrive after a cancelled run already cleaned up;
            // re-raising IsVerifying then would re-open (and strand) the CRC progress window.
            if (!IsRunning)
            {
                return;
            }

            if (!IsVerifying)
            {
                IsVerifying = true;
                _progress.StartVerify();
            }

            VerifyProgressUpdate u = _progress.ApplyVerifyProgress(e);
            VerifyHeadingText = u.HeadingText;
            VerifyProgressPercent = u.ProgressPercent;
            VerifyProgressPercentText = u.ProgressPercentText;
            VerifyCurrentFileText = u.CurrentFileText;
            VerifyRemainingText = u.RemainingText;
            VerifyElapsedText = u.ElapsedText;
            if (u.HasSpeed)
            {
                VerifySpeedText = u.SpeedText;
                if (u.HasEta)
                {
                    VerifyTimeRemainingText = u.TimeRemainingText;
                    VerifyEtaText = u.EtaText;
                }
            }

            if (u.IsComplete)
            {
                IsVerifying = false;
            }
        });
    }


    private void OnProgress(object? _, BruteForceProgressEventArgs e)
    {
        _uiDispatcher.Invoke(() =>
        {
            BruteForceProgressUpdate u = _progress.ApplyProgress(e);

            ProgressPercent = u.ProgressPercent;
            PhaseDescription = u.PhaseDescription;
            ProgressMessage = ComposeProgressMessage(u.ProgressMessage);
            TestCountText = u.TestCountText;
            ProgressPercentText = u.ProgressPercentText;
            CurrentDetailText = u.CurrentDetailText;
            ElapsedText = u.ElapsedText;
            if (u.HasTiming)
            {
                RemainingText = u.RemainingText;
                SpeedText = u.SpeedText;
                EtaText = u.EtaText;
            }
        });
    }


    private void OnElapsedTimerTick()
    {
        ElapsedTick tick = _progress.Tick();
        ElapsedText = tick.ElapsedText;

        if (tick.HasTiming)
        {
            RemainingText = tick.RemainingText;
            EtaText = tick.EtaText;
        }

        if (VersionEntries.Count > 0)
        {
            VersionEntries[^1].RefreshLiveDuration();
        }
    }
}
