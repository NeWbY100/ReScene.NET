# Design: In-place "Explain & Assist" for the five task forms

**Date:** 2026-05-24
**Status:** Approved (design); pending spec review

## Problem

A first-time user opening a task tab faces a wall of input fields and gets lost. Two
questions go unanswered at the point of decision:

1. **Purpose** — *"What is this field for?"*
2. **Selection** — *"What do I actually put here?"*

We initially considered a step-by-step wizard. We rejected it: it adds a second UI to
maintain, removes the dense single-screen power-user re-run loop, and the real value
(per-field explanation) can be delivered without it.

## Goal

For the five task forms, every input answers *what is this for?* and *what do I put here?*
without the user asking — and the app does as much of the work as it safely can, with
clear ✓ / ⚠ / ✗ feedback. No wizard, no new navigation. Each form keeps its current
structure, log, grids, and quick re-run loop.

## Scope

In scope — five forms:

- SRR Creator (`CreatorView`)
- SRS Creator (`SRSCreatorView`)
- RAR Reconstructor (`ReconstructorView`)
- SRS Reconstructor (`SRSReconstructorView`)
- SRS Restorer (`SampleRestorerView`)

Out of scope — Home, Inspector, Compare tabs; telemetry; a "hide captions" setting;
localization; any rework of the log / grid / expander areas.

## Context: this completes a pattern that already exists

- **Explanations** — every form already has a tab-level description. The SRR Creator's
  options and the *entire* RAR Reconstructor options block are already richly captioned
  (e.g. "-m0: Store (no compression)…"). The consistent gap is the **path / input fields
  at the top of each form**, which have no captions and no validation feedback.
- **Assist logic** — much already exists but is not surfaced as guidance: the Restorer
  matches media files to samples (`MatchMediaFiles`), the Creator scans a release dir for
  `.nfo` / `.sfv` / proof files (auto-include), and the Reconstructor has "Import from SRR"
  (auto-configures versions / compression / dictionary / timestamps / Host OS) and
  "Import Config".

This work systematizes that pattern across the five forms rather than inventing a new one.

## Design

### Two ingredients added to each field

1. **Caption** — an always-visible muted line under the field label (the chosen layout).
   Pure XAML, static text, styled with the existing `ForegroundSecondary` /
   `FontSizeCaption` resources so it matches the checkbox descriptions already in use.
2. **Status line** — a dynamic ✓ / ⚠ / ✗ icon + message reflecting detection and
   validation, bound to per-field state on the ViewModel.

### Reusable mechanism (build once, use on all five)

- **`FieldStatus`** — a small value exposed per relevant field on each ViewModel:
  - `State`: `None | Ok | Info | Warning | Error`
  - `Message`: string (empty when `None`)
- **`FieldStatusLine`** — a single reusable WPF control (or DataTemplate + style) that
  renders the icon and colored message for a `FieldStatus`. Used identically on every
  form so the ✓/⚠/✗ markup and color mapping are written once, not ~15 times.
- **Captions** stay plain styled `TextBlock`s initially (least churn). Extract a
  `LabeledField` template only if repetition proves painful.

### Per-form assist

| Form | Explain (captions on paths) | Assist (detect / suggest / validate) |
|---|---|---|
| SRR Creator | Input = release folder holding the RAR set; Output = `.srr` destination | On Input pick: scan for RAR volumes → "✓ Found N volumes (M set)" / "✗ No RAR volumes here". Auto-suggest Output `<release>.srr` beside input. Surface existing auto-include scan results. |
| SRS Creator | Sample = the small sample clip; Main = optional full movie (MatchOffset) | On Sample pick: identify container → "✓ MKV sample, 24.0 MB"; warn if unrecognized media type. Auto-suggest Output `<sample>.srs`. |
| RAR Reconstructor | Caption the 4 paths (WinRAR / Release / Verify / Output) | On WinRAR pick: "✓ WinRAR 6.11 detected". Promote **Import from SRR** as the recommended first step. Validate Release / Output present. |
| SRS Reconstructor | SRS / Media / Output | On SRS pick: read expected sample name + size, auto-suggest Output. Sanity-check Media File ("✓ media larger than sample" / "⚠ media smaller than sample, likely wrong file"). |
| SRS Restorer | SRR / Media dir / Output dir | Keep media-matching; show "✓ matched K of N samples". Auto-suggest Output dir. |

### Auto-fill policy

- Fill only **empty** fields. Never overwrite a value the user typed.
- Auto-filled values remain editable and render normally (not locked, not greyed).

### Action gating

- Keep the existing `CanExecute` gating on the action commands.
- Add a single "what's needed" line near the action button when it is disabled
  (e.g. "Select an output folder to continue"), derived from the same `FieldStatus` set —
  so the user knows *why* they cannot proceed.

## Testing

- Detection / validation logic lives in the ViewModel or small private helpers and is
  unit-tested with xUnit, consistent with the existing test project. Examples:
  - "folder with 3 `.rar` volumes → `Ok` status with count 3"
  - "missing output path → `Error` status with the expected reason"
  - "auto-suggest does not overwrite a user-typed output path"
- Caption-only XAML additions need no tests.
- `FieldStatus` → icon/color mapping (converter or control) is lightly tested or
  visually verified.

## Risks / trade-offs

- **Form height** — always-visible captions make forms taller. Accepted; mitigated by
  keeping captions to one concise line and reusing the compact caption style.
- **Two UIs avoided** — by enhancing in place rather than adding a wizard, there is no
  second UI to keep in sync.
- **Detection reliability** — assist messages must fail soft: when detection is
  uncertain, show `Info`/`Warning` guidance rather than a false `Ok`/`Error`.
