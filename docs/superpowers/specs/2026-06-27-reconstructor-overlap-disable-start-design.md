# Reconstructor — Disable Start + Inline Warning on Release/Output Overlap (Design)

**Date:** 2026-06-27
**Status:** Approved (pending implementation plan)
**Branch:** `fix/v1.6.1-reconstructor` (rides with the v1.6.1 patch)
**Scope:** `ReconstructorFieldGuidance` and `ReconstructorViewModel` — a UX refinement of the
Release==Output guard already added in this branch.

## Background

The branch already added `PathsOverlap` and a `StartAsync` guard that blocks (with an error dialog)
when the Output folder is the same as, or nested with, the Release folder. The user wants earlier,
clearer feedback: the moment the two folders overlap, **Start should be disabled** and the Release
and Output fields should show a **red error** in place of their normal "Source files selected." /
"Output folder set." messages.

## Behaviour

When `PathsOverlap(ReleasePath, OutputPath)` is true (same folder, or one nested in the other):
- The **Release** and **Output** status lines both show a red `Error`:
  `Release and Output must be different folders.`
- The **Start** button is **disabled**.
- The **Paths** sub-tab header shows its ⚠ glyph (it already reflects "needs attention").

When the overlap is cleared (by changing either folder), the statuses fall back to their normal
values and Start re-enables.

## Architecture

### `ReconstructorFieldGuidance` — overlap-aware overloads

Add two 2-argument overloads that layer the overlap check on top of the existing single-path
evaluators:

```csharp
/// <summary>Overlap-aware release status: a red error when Release and Output overlap, else the single-path result.</summary>
public static FieldStatus EvaluateReleasePath(string releasePath, string outputPath)
    => PathsOverlap(releasePath, outputPath)
        ? FieldStatus.Error("Release and Output must be different folders.")
        : EvaluateReleasePath(releasePath);

/// <summary>Overlap-aware output status: a red error when Release and Output overlap, else the single-path result.</summary>
public static FieldStatus EvaluateOutputPath(string outputPath, string releasePath)
    => PathsOverlap(releasePath, outputPath)
        ? FieldStatus.Error("Release and Output must be different folders.")
        : EvaluateOutputPath(outputPath);
```

`PathsOverlap` returns false when either path is empty, so the overlap error only appears once both
folders are set (an empty field keeps its existing Required/None state).

Also make the Paths-tab glyph reflect the overlap by adding it to `PathsNeedAttention`:

```csharp
public static bool PathsNeedAttention(
    string winRarPath, string releasePath, string verificationPath, string outputPath)
{
    return NeedsAttention(EvaluateWinRarPath(winRarPath))
        || NeedsAttention(EvaluateReleasePath(releasePath))
        || NeedsAttention(EvaluateVerificationPath(verificationPath))
        || NeedsAttention(EvaluateOutputPath(outputPath))
        || PathsOverlap(releasePath, outputPath);
}
```

(The single-path `EvaluateReleasePath`/`EvaluateOutputPath` calls inside `PathsNeedAttention` stay
single-arg; the explicit `PathsOverlap` term covers the overlap case.)

### `ReconstructorViewModel` — cross-coupled statuses + disabled Start

- Replace the per-field hooks so changing **either** path recomputes **both** statuses (an overlap
  is a relationship, so a change to one field can turn the other red or clear it):

  ```csharp
  partial void OnReleasePathChanged(string value) => RefreshReleaseOutputStatuses();
  partial void OnOutputPathChanged(string value) => RefreshReleaseOutputStatuses();

  private void RefreshReleaseOutputStatuses()
  {
      ReleaseStatus = ReconstructorFieldGuidance.EvaluateReleasePath(ReleasePath, OutputPath);
      OutputStatus = ReconstructorFieldGuidance.EvaluateOutputPath(OutputPath, ReleasePath);
  }
  ```

- `RefreshPathStatuses()` (called at construction and on settings-changed) uses the overlap-aware
  pair too:

  ```csharp
  private void RefreshPathStatuses()
  {
      WinRarStatus = ReconstructorFieldGuidance.EvaluateWinRarPath(WinRarPath);
      VerifyStatus = ReconstructorFieldGuidance.EvaluateVerificationPath(VerificationPath);
      RefreshReleaseOutputStatuses();
  }
  ```

- Disable Start on overlap:

  ```csharp
  private bool CanStart() => !IsRunning
      && !string.IsNullOrWhiteSpace(WinRarPath)
      && !string.IsNullOrWhiteSpace(ReleasePath)
      && !string.IsNullOrWhiteSpace(OutputPath)
      && !ReconstructorFieldGuidance.PathsOverlap(ReleasePath, OutputPath);
  ```

  `WinRarPath`, `ReleasePath`, and `OutputPath` already carry
  `[NotifyCanExecuteChangedFor(nameof(StartCommand))]`, so the button re-evaluates the moment the
  overlap appears or clears.

- The existing `StartAsync` overlap guard + error dialog stays as a defense-in-depth backstop.

## Data Flow

`ReleasePath`/`OutputPath` setter → `OnReleasePathChanged`/`OnOutputPathChanged` →
`RefreshReleaseOutputStatuses()` (both statuses recomputed, overlap-aware) and, via the existing
notify attributes, `StartCommand.CanExecute` re-evaluated. The `PathsNeedAttention` computed
property (already raised when these paths change) now also reports the overlap, lighting the glyph.

## Error Handling

No new failure modes. `PathsOverlap` already swallows unparseable paths (returns false). The
overlap error is a display state only; the real protection (no deletion) remains the `StartAsync`
guard, which is now additionally unreachable through the UI because Start is disabled.

## Testing & Verification

- Unit tests on the overloads:
  - `EvaluateReleasePath(rel, out)` / `EvaluateOutputPath(out, rel)` → `FieldState.Error` with the
    overlap message when the two overlap (same folder, and nested).
  - Non-overlapping pair → falls through to the single-path result (`Ok` for a valid folder).
  - Empty Output → release falls through to its single-path result (no false overlap error).
- VM test: with valid WinRAR/Release/Output where Release == Output, `StartCommand.CanExecute(null)`
  is false; after changing Output to a separate folder, it becomes true. (Reuse the existing
  `ReconstructorViewModelDialogTests` harness.)
- Clean non-incremental build (0 warnings) + full suite green.
- Manual check: pick the same folder for Release and Output → both lines turn red with the message
  and Start greys out; change one → both clear and Start re-enables.

## Non-Goals

- No change to the `StartAsync` deletion guard (kept as the safety backstop).
- No new control or converter (reuses `FieldStatusLine` + the existing `Error` severity styling).

## File Structure

- `ReScene.NET/ViewModels/Reconstruction/ReconstructorFieldGuidance.cs` — two overloads + the
  `PathsNeedAttention` overlap term.
- `ReScene.NET/ViewModels/ReconstructorViewModel.cs` — `RefreshReleaseOutputStatuses`, the two
  changed hooks, `RefreshPathStatuses` update, and `CanStart` gate.
- `ReScene.NET.Tests/` — overload unit tests + the `CanStart` gating VM test.
