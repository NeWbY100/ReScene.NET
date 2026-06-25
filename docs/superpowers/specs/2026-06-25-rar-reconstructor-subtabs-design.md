# RAR Reconstructor — Sub-Tabbed Advanced Layout (Design)

**Date:** 2026-06-25
**Status:** Approved (pending implementation plan)
**Scope:** `ReScene.NET/Views/ReconstructorView.xaml` (Advanced-mode view) and a small, testable addition to `ReconstructorViewModel` / `ReconstructorFieldGuidance`.

## Problem

On short screens the advanced RAR Reconstructor is hard to use. It is one long vertical
stack — intro → Configuration → four Paths (each = description + textbox + status line) →
Options (tip + Import button + five collapsed expanders inside a `ScrollViewer`) → action row
→ splitter → log. The top block is pinned to `MinHeight="550"` and grows further as sections
expand, so it devours the available height: the settings expanders are clipped and the log is
squeezed to a sliver (see the reported screenshot, where "RAR 5.x" is cut off and the log has
almost no room).

## Goal

Reorganise the advanced view so each configuration section lives on its own sub-tab. Only one
section is laid out at a time, so the configuration area stays a fixed, modest height and the
log gets real room — without removing any existing control, binding, or behaviour.

## Non-Goals

- No change to the Beginner wizard (`ReconstructWizardBody`), which is already a step-based flow.
- No change to any other top-level tab (SRR Creator, SRS Reconstructor, etc.).
- No change to reconstruction logic, the brute-force service, or the option/command-line model.
- No new option, switch, or capability. This is a layout reorganisation plus one readiness cue.

## Architecture

This is overwhelmingly an **XAML restructure** of a single view. Every control keeps its exact
binding (`WinRarPath`, `Version3`, `SwitchM3`, `DeleteRARFiles`, …); only its *position* in the
visual tree changes. The themed `TabControl`/`TabItem` styles in `App.xaml` are inherited
automatically, so the nested sub-tabs match the app's look with no new styles.

The only view-model change is a single computed, unit-testable readiness flag used to badge the
Paths tab header (see "Paths readiness glyph").

### New view layout (top → bottom)

A root `Grid` with these rows:

1. **Intro paragraph** (`Auto`) — kept verbatim: the description plus the two WinRAR download
   hyperlinks (essential first-run guidance, with their existing `RequestNavigate` handler).
2. **Persistent action bar** (`Auto`) — a `DockPanel`:
   - Left: `Import Config` (GhostButton), `Import from SRR` (PrimaryButton).
   - Right: `Export Config` (GhostButton), `Start` (PrimaryButton).
   - Always visible on every sub-tab. `Start` keeps its `CanStart()` gating, so it auto-disables
     until WinRAR + Release + Output are set, exactly as today.
3. **"Import from SRR" tip caption** (`Auto`) — the existing one-line tip, kept as a slim caption
   under the action bar (it already duplicates the button's tooltip).
4. **Custom-packer warning banner** (`Auto`) — the existing amber banner, moved here (above the
   sub-tabs) so it is visible regardless of the active sub-tab. Collapses when there is no warning
   (`HasCustomPackerWarning`), as today.
5. **Settings sub-`TabControl`** (`*`, `MinHeight="220"`) — six `TabItem`s, 1:1 with today's
   sections. Each tab's content is wrapped in its own `ScrollViewer` so that, on a very short
   window, only the *active* section scrolls.
   - **Paths** (default / first) — WinRAR, Release, Verify, Output. Each keeps its
     description `TextBlock`, browse `DockPanel` (`TextBox` + Browse button), and
     `FieldStatusLine`. (Output has no status line today; unchanged.)
   - **Versions** — the RAR 2.x–7.x checkboxes and their intro caption.
   - **Compression** — Compression Method, Archive Format, and the Dictionary Size two-column
     grid (today's "Compression & Dictionary" expander), with its intro caption.
   - **Timestamps** — the -tsm / -tsc / -tsa three-column grid.
   - **Options** — -ai / -r / -ds / -s-, the -mt thread range, the -v volume row + -vn naming,
     and the File Attributes (A / I tri-state) block with its legend.
   - **Output** — the delete / stop-on-first-match / complete-volumes / rename / patch checkboxes.
6. **GridSplitter** (`Auto`) — kept (`ResizeBehavior="PreviousAndNext"`).
7. **Log panel** (`*`, `MinHeight="200"`) — kept verbatim: the Log header row (label,
   Auto-scroll, Save log…) and the System / Phase 1 / Phase 2 `TabControl`.

The old `MinHeight="550"` top block is gone; the config area's `MinHeight` drops to ~220 so the
splitter can give the log a usable share of a short window.

### Paths readiness glyph

Because the path status lines are hidden when another sub-tab is active, the **Paths** tab header
shows a small amber ⚠ glyph whenever a required path still needs attention, directing the user
back to it. The glyph clears once all paths are valid.

**Definition of "needs attention"** — any of the four paths required for a successful run is empty
or invalid:

- WinRAR — empty, or `EvaluateWinRarPath` returns `Error` (directory missing).
- Release — empty, or `EvaluateReleasePath` returns `Error` (path missing).
- Verify — empty, or `EvaluateVerificationPath` returns `Error` (file missing).
- Output — empty (Output has no existence validation today; it is created at Start).

This is exposed as a pure helper so it can be unit-tested:

```csharp
// ReconstructorFieldGuidance
public static bool PathsNeedAttention(
    string winRarPath, string releasePath, string verificationPath, string outputPath)
{
    return EvaluateWinRarPath(winRarPath).State is FieldState.None or FieldState.Error
        || EvaluateReleasePath(releasePath).State is FieldState.None or FieldState.Error
        || EvaluateVerificationPath(verificationPath).State is FieldState.None or FieldState.Error
        || string.IsNullOrWhiteSpace(outputPath);
}
```

`FieldState.None` is returned by the `Evaluate*` helpers for an empty/whitespace value, so the
`None or Error` test covers both "empty" and "invalid" without re-checking the strings. (A valid
value returns `Ok`/`Info`, so it does not trigger the glyph.)

The view-model surfaces it as a computed property:

```csharp
public bool PathsNeedAttention =>
    ReconstructorFieldGuidance.PathsNeedAttention(WinRarPath, ReleasePath, VerificationPath, OutputPath);
```

and re-raises it from the four path properties by adding
`[NotifyPropertyChangedFor(nameof(PathsNeedAttention))]` to `WinRarPath`, `ReleasePath`,
`VerificationPath`, and `OutputPath`.

The Paths `TabItem` uses a composite header:

```xml
<TabItem.Header>
  <StackPanel Orientation="Horizontal">
    <TextBlock Text="Paths" VerticalAlignment="Center" />
    <TextBlock Text="&#x26A0;" Margin="6,0,0,0" VerticalAlignment="Center"
               Foreground="{DynamicResource AccentWarning}"
               Visibility="{Binding PathsNeedAttention,
                            Converter={StaticResource BoolToVisibility}}" />
  </StackPanel>
</TabItem.Header>
```

The header's `DataContext` resolves to the `ReconstructorViewModel` (inherited from the
`TabControl`, whose `DataContext` is the view-model). `BoolToVisibility` is the existing
app-wide converter (already used by `FilePreviewWindow.xaml`).

## Data Flow

Unchanged. All commands (`ImportConfigCommand`, `ImportSRRCommand`, `ExportConfigCommand`,
`StartCommand`, `Browse*Command`, `SaveLogCommand`) and all bound properties are reused verbatim;
they simply live under new parents in the visual tree. The readiness glyph reads existing path
state and adds no new flow into the run.

## Error Handling

No new failure modes. Start validation, the subdirectory-timestamp warning, the
output-not-empty confirmation, and the custom-packer warning all behave exactly as before; the
custom-packer banner is merely relocated to stay visible across sub-tabs.

## Testing & Verification

- **Unit test** the new pure helper `ReconstructorFieldGuidance.PathsNeedAttention`
  (all-empty → true; Output empty only → true; a non-existent WinRAR dir → true; all four valid
  → false). Valid cases use temporary directories/files, matching existing IO-based test patterns.
- **Build verification:** a clean build of `ReScene.NET` with
  `dotnet build -p:BaseOutputPath=bin2/ --no-incremental` must succeed with **0 warnings**
  (the `--no-incremental` flag forces analyzers to re-run; bin2 avoids the running app's bin lock).
  This confirms the XAML compiles and every binding/resource (`AccentWarning`, `BoolToVisibility`,
  GhostButton/PrimaryButton styles) resolves.
- **Existing tests** stay green — there are no behavioural changes; the only VM addition is a
  computed property plus notify attributes.
- **Manual visual check** by the user in the running app (WPF layout is not snapshot/Playwright
  tested in this repo): confirm the six sub-tabs, the persistent action bar, Start auto-disabling,
  the relocated custom-packer banner, the Paths ⚠ glyph appearing/clearing, and that the log gets
  usable height on a short window.

## File Structure

- **Modify** `ReScene.NET/Views/ReconstructorView.xaml` — the layout restructure (the bulk of
  the work). No change needed to `ReconstructorView.xaml.cs` beyond the existing
  `OnHyperlinkRequestNavigate` handler, which is retained.
- **Modify** `ReScene.NET/ViewModels/Reconstruction/ReconstructorFieldGuidance.cs` — add
  `PathsNeedAttention`.
- **Modify** `ReScene.NET/ViewModels/ReconstructorViewModel.cs` — add the `PathsNeedAttention`
  computed property and the four `[NotifyPropertyChangedFor]` attributes.
- **Add/extend** a test for `ReconstructorFieldGuidance.PathsNeedAttention` in
  `ReScene.NET.Tests`.
