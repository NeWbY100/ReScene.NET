# Beginner / Advanced Mode — Design

- **Date:** 2026-06-06
- **Status:** Approved (brainstorm), pending spec review → implementation plan
- **App:** ReScene.NET (WPF, .NET 10, CommunityToolkit.Mvvm)

## Summary

Add a user-selectable **Mode** with two values, **Beginner** and **Advanced**, that swaps the
entire shell:

- **Advanced** = today's exact 8-tab layout, unchanged.
- **Beginner** = a **Guided Hub**: a launcher of four task cards, each opening a single
  simplified screen that exposes only the essential inputs and lets every advanced option keep
  its existing default.

This replaces an earlier "wizard" idea that was rejected for not serving power users. By making
Beginner an *alternative* surface rather than the *only* surface, advanced users keep full
control and newcomers get a calm, guided experience.

## Background & problem

The current UI (`Views/MainWindow.xaml`) is a hardcoded `TabControl` with 8 tabs (Home,
Inspector, SRR Creator, SRS Creator, RAR Reconstructor, SRS Reconstructor, SRS Restorer,
Compare). It is excellent for power users but dense and intimidating for newcomers. The RAR
Reconstructor alone exposes 50+ options across 6 expanders. A pure wizard was rejected as too
limiting for advanced users; a Mode switch resolves the tension by serving both audiences from
one app.

## Goals

- A newcomer can complete the common tasks (create SRR, create SRS, reconstruct RAR, restore a
  sample) without confronting advanced options.
- Power users retain today's full layout, byte-for-byte, with no regressions.
- Switching modes is instant (no restart) and the choice persists.
- Reuse existing logic: **no second implementation** of the actual work — Beginner screens are
  alternate XAML bound to the existing task ViewModels.

## Non-goals

- No light theme / re-theming (Beginner reuses the existing dark token system in `Resources/Tokens.xaml`).
- No new business logic in the SRR/SRS/RAR engine (`ReScene.Lib`).
- No multi-step wizard flows (explicitly rejected).
- Inspector and Compare are **not** reimplemented for Beginner.

## Decisions (locked during brainstorm)

| Decision | Choice |
|---|---|
| Core model of Beginner mode | **Guided Hub** — task cards → one simplified screen each |
| RAR Reconstructor in Beginner | **Guided SRR import** — ask for .srr + release files + output; auto-run Import-from-SRR to configure everything; no manual options |
| Two sample-recovery tasks | **One smart "Restore a sample" card** that routes by file type (`.srr` → bulk restore, `.srs` → single rebuild) |
| Mode switch placement | **Always-visible top-bar toggle**, mirrored in Settings |
| Default mode | **New installs → Beginner; existing users keep current (Advanced)**; choice remembered |
| Inspector & Compare in Beginner | **Out** (Advanced-only) |
| Inspect / Recent Files in Beginner | **Dropped entirely** — no "open & inspect" or recent list in the hub |

## UX design

### Top-bar mode toggle

A compact `( Beginner | Advanced )` toggle sits in the window's top bar, always visible in both
modes, and is mirrored as a control in `Views/SettingsWindow.xaml`. Flipping it swaps the shell
live. Beginners can graduate and power users can escape instantly.

### Beginner Hub

Strictly four task cards (no inspect, no recent files, no Compare):

```
What would you like to do?
 ┌────────────────┐   ┌────────────────┐
 │ 📦 Create an    │   │ 🎬 Create a    │
 │    SRR          │   │    sample SRS  │
 └────────────────┘   └────────────────┘
 ┌────────────────┐   ┌────────────────┐
 │ 🔧 Reconstruct  │   │ ↺  Restore a   │
 │    RAR archives │   │    sample      │
 └────────────────┘   └────────────────┘
```

### Navigation model

Hub → click a card → simplified task screen → `‹ Tasks` returns to the hub. The top-bar mode
toggle is present on both the hub and task screens. Each task screen carries a subtle
**"Need full control? → Advanced"** affordance that flips to Advanced mode and lands on the
matching tab (acceptable because the same shared ViewModel state carries over).

### The four Beginner screens

Each Beginner view is **alternate XAML over an existing ViewModel**. The ViewModels already
default their advanced options sensibly, so a Beginner view simply does not render them. All
guidance uses the existing `Controls/FieldStatusLine.xaml` (✓/ℹ/⚠/✗) and the empty-only
auto-fill pattern.

| Card | ViewModel (existing, shared) | Beginner shows | Kept at default / hidden |
|---|---|---|---|
| **Create an SRR** | `CreatorViewModel` | input (SFV or first RAR), output | AutoIncludeFiles (on), AutoCreateSRS, CreateVobsubSRR, StoreFixRar, AllowCompressed, ComputeOSOHashes, GenerateLanguagesDiz, stored-files grid, AppName |
| **Create a sample SRS** | `SRSCreatorViewModel` | sample/ISO input, output | MainFilePath (match-offset), AppName; for ISO input, auto-select the media file |
| **Reconstruct RAR** | `ReconstructorViewModel` | the .srr, release files folder, output | auto-runs Import-from-SRR to set all 50+ options; brute-force progress window reused as-is |
| **Restore a sample** | smart router → `SampleRestorerViewModel` or `SRSReconstructorViewModel` | the file, media (folder for SRR / movie for SRS), output | file-type routing: `.srr` → bulk "Restore all N"; `.srs` → single rebuild |

#### Smart "Restore a sample" routing

A small pure helper inspects the chosen input file extension/content:
- `.srr` → present the bulk **Sample Restorer** flow (grid auto-selected, "Restore all"), with
  per-entry status shown but the editable media-path column simplified/read-only.
- `.srs` → present the single **SRS Reconstructor** flow (one sample rebuilt from the movie).

The router is the only genuinely new decision logic and must be unit-tested independently of WPF.

#### Reconstruct RAR (guided import)

Beginner collects only `.srr` + release files folder + output folder, then internally runs the
existing `ImportSRRAsync` to auto-configure every brute-force option, then `StartAsync`. If the
import detects a custom packer (e.g. RELOADED) that cannot be brute-forced, surface the existing
`CustomPackerWarning` in friendly language and disable Start (no silent failure).

## Technical design

### Architecture: shell container + two shell UserControls (chosen)

Refactor `MainWindow` from *being* the shell into *hosting* a shell:

- `Views/MainWindow.xaml` becomes a thin frame: top bar (title, mode toggle, menu/status as
  applicable) + a `ContentControl` bound to the current shell.
- Extract today's `TabControl` (and its 8 `TabItem`s) into a new **`AdvancedShellView`**
  UserControl, moved near-verbatim, bound to the existing `MainWindowViewModel` state.
- New **`BeginnerShellView`** + **`BeginnerShellViewModel`**: owns hub state (which card is open
  / hub-vs-task) and a Back command; hosts the simplified task views in an inner `ContentControl`.
- New simplified task views **`BeginnerCreatorView`**, **`BeginnerSrsCreatorView`**,
  **`BeginnerReconstructorView`**, **`BeginnerRestoreView`** — each binds to the **shared
  existing** task ViewModel instance owned by `MainWindowViewModel`.

Rejected alternatives: (2) conditional visibility / triggers hiding tabs — brittle, orphaned
chrome, Beginner never feels distinct; (3) swapping the `TabControl` ControlTemplate — fights WPF.

### Mode setting & persistence

- New `enum UserMode { Beginner, Advanced }`.
- Add **`UserMode? Mode { get; set; }`** (nullable, default `null`) to `Models/AppSettings.cs`.
  Nullable is deliberate: it lets `Load` distinguish "field absent in an existing file" from a
  brand-new install. The effective mode is resolved at load time (not by a property default):
  - **No `settings.json` at all** → brand-new install → **Beginner**.
  - **File present but `Mode` is `null`/absent** → pre-existing user → **Advanced** (don't demote).
  - **`Mode` has a value** → use it.
  Once the user toggles, a concrete value is always written back, so this resolution only matters
  on the very first load after upgrade/fresh-install.
- Persist via existing `Services/AppSettingsService.cs` (JSON at
  `%LOCALAPPDATA%\ReScene.NET\settings.json`).
- `MainWindowViewModel` exposes `Mode` + a toggle command and **subscribes to
  `IAppSettingsService.Changed`** (currently no live subscribers exist) to swap the hosted shell
  reactively. This is the live-switch mechanism.

### Reuse via alternate XAML

The Beginner task views introduce **no new commands or services**. They bind to the same
`CreatorViewModel`/`SRSCreatorViewModel`/`ReconstructorViewModel`/`SampleRestorerViewModel`/
`SRSReconstructorViewModel` instances already created eagerly in `MainWindowViewModel`. Hidden
options keep their constructor defaults.

### New / changed files (indicative)

- New: `Models/UserMode.cs`; `Views/AdvancedShellView.xaml(.cs)`; `Views/BeginnerShellView.xaml(.cs)`;
  `ViewModels/BeginnerShellViewModel.cs`; `Views/Beginner/*View.xaml(.cs)` (4 task views);
  `Helpers/SampleRestoreRouter.cs` (pure, file-type routing).
- Changed: `Views/MainWindow.xaml(.cs)` (frame + top-bar toggle, shortcut gating, tab-index
  clamp); `ViewModels/MainWindowViewModel.cs` (Mode, toggle command, Changed subscription);
  `Models/AppSettings.cs` + `ViewModels/SettingsViewModel.cs` + `Views/SettingsWindow.xaml`
  (Mode field + mirrored control).

## Edge cases & gotchas (from code analysis)

- **Tab-index clamp:** `WindowStateService` persists `SelectedTabIndex`; a saved Advanced index
  can be invalid in Beginner. Clamp/guard in `RestoreWindowState`.
- **Keyboard shortcuts:** `Ctrl+1–7` tab switching is hardcoded in `MainWindow.xaml.cs`; gate to
  Advanced mode only.
- **Existing-user default:** an existing `settings.json` without `Mode` must deserialize to
  Advanced (preserve current behavior), while a brand-new file defaults to Beginner.
- **ISO auto-select:** `SRSCreatorViewModel.CanCreateSRS()` is false if ISO source has no
  selected media file; Beginner must auto-select the first/most-likely media entry.
- **Custom-packer warning:** Reconstructor must surface `CustomPackerWarning` clearly in Beginner.
- **Title bar:** `DarkTitleBar.Enable()` is a one-time call; mode switching does not re-theme it
  (no light theme), so no change needed there.
- **VM lifetime:** ViewModels stay eagerly instantiated and shared between shells (simplest);
  revisit lazy instantiation only if startup cost becomes noticeable.

## Build phases (each independently shippable)

0. `UserMode` enum + `AppSettings.Mode` + persistence + `Changed` wiring. No visible change.
1. Shell refactor: extract `AdvancedShellView`, add the container `ContentControl` + top-bar
   toggle. Advanced works exactly as before; Beginner shows a stub hub.
2. Beginner hub: four cards + hub↔task navigation + Back.
3. The four simplified task views, easiest first (Restore → Create SRR → Create SRS →
   Reconstruct), each over its existing VM; wire `SampleRestoreRouter`.
4. First-run default, Settings mirror, shortcut gating, tab-index clamp, "→ Advanced" escape
   hatches, custom-packer messaging, polish.

## Testing

- Unit-test (`ReScene.NET.Tests`): `SampleRestoreRouter` (extension/content → flow), the
  existing-user-default deserialization rule, tab-index clamping, and any new `FieldGuidance`
  messages.
- ViewModel tests for `BeginnerShellViewModel` navigation (hub ↔ task, Back) and
  `MainWindowViewModel` mode-toggle / `Changed` reaction.
- Manual smoke: each of the four Beginner flows end-to-end; mode toggle round-trip; existing
  Advanced layout regression check.

## Out of scope / future

- Light/high-contrast theme.
- Per-task multi-step wizards.
- Bringing Inspector/Compare into Beginner (revisit only if users ask).
- Telemetry on mode usage.
