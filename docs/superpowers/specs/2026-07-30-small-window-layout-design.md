# Small-Window Layout Degradation — Design

Status: rev 15 — per-view threshold constants replaced by derived switch heights, and the
pinned-band bound restated after app-wide content text moved to 13px (both 2026-08-02); see
[Amendment 2026-08-02](#amendment-2026-08-02--derived-thresholds) and
[Amendment 2026-08-02b](#amendment-2026-08-02b--pinned-band-bound-restated-after-the-13px-content-text-change).
Everything below that the amendments do not supersede stands as rev 13 left it.

Status: rev 13 — implemented d045ea6. Task 7 (Settings audit + whole-board close) verified
the feature end-to-end: SettingsWindow's own 560×360 minimum audited (criterion C Tab-walk
passes with no compact machinery needed), a cross-view board (font-source enlargement,
RenderScaling 1.25/1.5x, five-view invariant-coverage guard) added, full suite green
(Manager 427/427, App.Core 712/712, 0W/0E), and an ava-desktop runtime pass confirmed
compact chrome, Help open/close, and continued-resize focus stability (the item flagged
OWED after Task 6 fix-7) on the real app at 700×450 and at native size. Note: this repo's
own task briefs anticipated "rev 10"/"rev 11" for this status line, written before rounds
6-12 of review landed; rev 13 is the correct next number given the rev 12 this document
actually reached.

## Coordinate space (normative for every figure in this document)

All heights are **inner-content DIPs**: the height of each view's inner layout root (the
Grid/DockPanel inside the 12px PageMargin). `CompactHeightBehavior` attaches to that inner
root and compares ITS `Bounds.Height`; the `compactHeight` class is set on the same
element (it ancestors all styled content). The threshold-invariant test computes floors in
this same space. Window↔inner conversion at minimum size, measured: window 450 − menu 26 −
wrapped shell strip 58 (700w: the 8 shell tabs need ~715px and wrap to two rows) − status
23 − PageMargin 24 = **319 inner DIPs available at 700×450**. (At widths ≥ ~720 the strip
is one row and the same window height yields 347.)

## Problem

Below each view's floor nothing scrolls: layout overflows and is clipped by the shell,
leaving the page tail unreachable while Tab still moves focus into the clipped region
(WCAG 2.4.11 AA). Floors also grow at runtime (conditional rows). Measured at 700×450
(base state, inner width 676):

| View | Measured base composition | Fate at 319 |
|---|---|---|
| Reconstructor | header 73 + toolbar 26 + tip 35 + margins + TabControl 220(min) + splitter 8 + log 140(min) ≈ 516 | log + splitter + 60px of tabs below the clip; warning row adds 31–35 |
| Creator | intro 35 + input 65 + options 46 + StoredFiles grid 150(fixed) + splitter 6 + bottom grid crushed to 100 (natural ≈ 325) | bottom half crushed AND clipped; detected-sets adds ~96; Create unreachable |
| SRSCreator | docked stack ≈ 329 + log fill | log = 2px; worst rows (+~92) clip the action row |
| SRSReconstructor | stack ≈ 245 + log 74 | worst rows (+~90) drive log to 0 then clip |
| SampleRestorer | stack + grid 100(min) ≥ 319 | **action row and log measure 0px at BASE — Restore unreachable today** |

## Approach (user-selected 2026-07-30)

Shrink panes first; no page-level scrollbar; header chrome auto-collapses below a per-view
threshold; pixel-identical at normal sizes. With 319 available at minimum, compact is part
of the fit mechanism and is always active at 700×450.

**Universal mechanism rule (rev 3):** every structure is ALWAYS PRESENT in the visual
tree; mode changes ONLY sizing constraints and visibility — no reparenting, no duplicated
content, ever. Styles carry all changes selectors can reach; `RowDefinition` is not
styleable (no Classes — a11y rev-2 NEW-2), so row Height/MinHeight mode values are applied
by `CompactHeightBehavior` from a per-view declarative map (see §1), preserving a
user-dragged splitter height across a compact round-trip.

## Design

### 1. `CompactHeightBehavior`

Attached to the inner layout root; properties: `Threshold` (inner DIPs), optional
`RowSizes` map (`rowIndex → (normalHeight, compactHeight, compactMinHeight)`).

- **Boundary convention (used verbatim everywhere in this spec and its tests):** compact
  iff `height < Threshold`; restore iff `height >= Threshold + 12` (hysteresis —
  restore-only, the safe direction; swallows fractional-DIP jitter at 125/150%).
  A FRESH view whose first real measure lands anywhere `>= Threshold` starts expanded —
  hysteresis applies only to an instance already compact (codex rev-2 NEW-B2: the matrix
  tests fresh instances at `Threshold+1` = expanded; restoration transitions are tested at
  `>= Threshold+12`).
- `height <= 0` ignored; subscriptions follow Attached/DetachedFromVisualTree with
  re-evaluation on re-attach; per-layout-pass coalescing via one posted dispatcher update;
  other classes untouched.
- Row application: on mode change, applies the `RowSizes` map (behavior-owned because
  selectors cannot reach RowDefinitions); a splitter-modified height is captured before
  compact and restored on expand.
- **Staged focus transition (rev 7 — executable form; replaces the rev-3 wording):**
  the behavior uses TWO named, direction-specific targets:
  the COMPACT-direction target is DERIVED — the Help expander's realized header
  ToggleButton (the Expander control itself is not focusable; the toggle announces its
  expanded/collapsed state through its own Toggle pattern, while the Expander's stock
  ExpanderAutomationPeer continues to expose ExpandCollapse to the UIA tree — the two
  are complementary, not duplicated); the RESTORE-direction target is the attached
  `RestoreFocusTarget` (a per-view named control that exists and is focusable at normal
  size: Reconstructor = the first link Button; the three-band views and Creator = the
  view's first input TextBox).
  Transition algorithm, both directions: (1) CAPTURE the currently-focused element
  BEFORE any change; (2) apply styles/rows; (3) run a layout pass; (4) decide
  obscurement — an element is obscured iff it is detached, `IsVisible==false` anywhere
  in its chain, OR its rendered bounds do not intersect the intersection of every
  clipping ancestor's viewport (`IsEffectivelyVisible` alone is NOT sufficient — it
  ignores clipping); (5) if the captured element is obscured, first call
  `BringIntoView()` on it and re-run the check — scrollable ancestors may recover it;
  (6) only if still obscured, focus the direction's target (entering compact → the
  DERIVED header toggle; leaving → RestoreFocusTarget), through the fallback chain
  below. No focus change otherwise.
  Three riders (a11y rev-7 review):
  — PRECONDITION: steps 4–6 run only if the captured element was focused AND is a
    descendant of THIS view root. RELOCATION TRIGGER (codex round-4): steps 5–6 fire
    when the captured element is obscured OR is no longer focusable (`Focusable` or
    effective enablement lost — e.g. a compact-only-focusable helpBody scroller after
    restore); an unfocusable focus-holder is stranding even when fully visible. A resize while focus sits in the shell menu, the tab
    strip, another window, or nowhere must never pull focus into the view (focus theft
    is worse than the stranding it would fix, and fires on an event the user did not
    initiate).
  — TARGET RESOLUTION: a target can resolve null or unfocusable (the compact target is
    a TEMPLATED part — the header ToggleButton exists only after template application,
    so an early or re-attach pass can miss it). Fallback chain, in order: the resolved
    direction target → the first FOCUSABLE descendant of the view root (the search
    includes the header toggle and the RestoreFocusTarget) → the VIEW ROOT as the
    guaranteed terminal: the behavior sets `Focusable=true` on the root ONLY for the
    hand-off (TopLevel is not focusable by default — codex round-4), focuses it, and
    resets `Focusable=false` when the root loses focus, so no permanent Tab stop is
    added and the next Tab lands deterministically inside the view. A silent no-op is
    forbidden at every step; a dedicated test with a focusable-descendant-free view
    forces the chain to the terminal and asserts the transient focusability round-trip.
  — DELIBERATE ASYMMETRY (do not "harmonize"): relocation triggers on ENTIRELY obscured
    (bounds not intersecting the clip intersection — the WCAG 2.4.11 AA line), while
    criterion C asserts the stricter FULLY WITHIN. Both are correct: C's Tab walk lets
    BringIntoView resolve partial clipping first; the relocation threshold only catches
    what scrolling cannot recover.

**Threshold invariant (executable, ONE budget sum — a11y rev-3 NEW-5):** per view, a unit
test that renders the view asserts, all in inner space with conditional rows forced
visible and links wrapped at 700w:

1. `Threshold >= rendered expanded-mode worst floor`;
2. `compact worst floor (Help closed) <= 307`;
3. `compact worst floor with Help OPEN — donated minimums in effect PLUS the body's
   MaxHeight — <= 307` (a single sum, never two independent checks: the body budget and
   the band minimums spend the same pixels);
4. pinned-band worst height fits its headroom within the same sums.

The 307 bound is 319 minus the 12-DIP jitter allowance (a11y rev-3 advisory): fractional
DIPs at 125/150% and the warning row's 31–35 spread must fail in CI, not on a user's
screen. Thresholds cannot drift unsafe, and compact feasibility is proven, not assumed.

> **SUPERSEDED 2026-08-18 — Help is always visible, so items 2 and 3 of the invariant above are one check, and the donation rule below is gone — its donated minimums are simply the compact minimums.** See
> [Amendment 2026-08-18](#amendment-2026-08-18--help-is-always-visible-the-disclosure-and-the-donation-rule-are-gone).

**Donation rule:** while the Help body is expanded in compact mode, the primary work band
donates height — its compact minimum drops further (Reconstructor TabControl 96 → 60;
three-band config 110 → 80), behavior-applied together with the expander state. The body's
`MaxHeight` equals the donated budget at the minimum window (Reconstructor ≈38, three-band
≈40 — test-computed, scrolling internally); closing Help restores the compact minimums. Help is transient reference
content — briefly shrinking the work pane is the correct trade.

Per-view figures (inner DIPs; log band floor **80** = header 28 + 2×20 rows + 12
horizontal-scrollbar allowance — corrected arithmetic, codex rev-2 NEW-B1; all numbers
re-verified by the invariant test at implementation):

Compact minimums are two-tier: the values below are the compact-mode floor; while Help is
open the work band's minimum drops to 80 (donation rule). All floors are design targets —
the invariant test measures the rendered truth against the 307 bound.

| View | Compact worst floor, Help closed (≤ 307) | Expanded worst floor | Threshold (floor+20) |
|---|---|---|---|
| Reconstructor | expander hdr 24 + toolbar 26 + tip (1-line) 18 + warning 35 + TabControl **96** + splitter 8 + log 80 + margins ~18 ≈ **305** | 73+26+35+31+130+8+80+margins ≈ 401 | **421** |
| Creator | hdr 24 + config scroll **110** (inputs+detected+grid+output+options inside) + action ≤75 + log 80 + margins ~8 ≈ **297** | natural stack ≈ 161+96+150+6+325 with new log floor ≈ 700 | **720** |
| SRSCreator | hdr 24 + config **110** + action ≤75 + log 80 + ~8 ≈ **297** | ≈ 330 stack + 84 + 80 ≈ 500 | **520** |
| SRSReconstructor | same shape ≈ **297** | ≈ 265 + 84 + 80 ≈ 430 | **450** |
| SampleRestorer | same shape (grid inside config) ≈ **297** | ≈ 350 + 84 + 80 ≈ 515 | **535** |

(Creator's large threshold simply means Creator is compact in most real windows — correct,
given its content volume.)

> **SUPERSEDED 2026-08-02 — the Threshold column above is no longer the switch height.** The
> per-view constants are gone; each view now derives its own. The compact-floor column and the
> 307 bound are unchanged and still normative. See
> [Amendment 2026-08-02](#amendment-2026-08-02--derived-thresholds) for what replaced them; the
> table is retained because the amendment's authored minimums are back-derived from it.

> **SUPERSEDED 2026-08-18 — the disclosure is removed; Help renders directly in both modes. Everything in this section is superseded EXCEPT the keyboard-scrolling contract, which survives unchanged.** See
> [Amendment 2026-08-18](#amendment-2026-08-18--help-is-always-visible-the-disclosure-and-the-donation-rule-are-gone).

### 2. Chrome — the "Help" disclosure (always-present, single instance)

One inline `Expander` per view, ALWAYS in the tree, holding the view's intro prose and
link controls (Reconstructor) — the single instance of that content; no second copy exists
anywhere (a11y rev-2 NEW-3). The Reconstructor TIP line ("Import from SRR…") is NOT in the
body (rev 5): it renders AFTER the toolbar today, so moving it into the body would change
normal-mode reading order (criterion F). It stays always-present in its own row in both
modes; under `.compactHeight` it is styled to a single line — APPROVED with conditions
(a11y rev-5 ruling), all binding:

1. Trimming is VISUAL-ONLY: `TextTrimming` over the full bound text — never a shortened
   string in VM or XAML. Asserted: in compact, the rendered tip's UIA Name equals the
   full tip text (a pre-truncated binding would silently reinstate the deletion defect).
2. `ToolTip.Tip` (pointer users) AND `AutomationProperties.HelpText` (AT description) both
   carry the full text — tooltips are not a keyboard/AT path.
3. Accepted residue, recorded: keyboard-only sighted users at compact size see one trimmed
   line with no route to the remainder; mitigation — the same guidance already lives on
   the Import-from-SRR button's own tooltip.
4. The tip is never the budget donor: if its measured one-line height exceeds 18 DIPs, the
   TabControl minimum gives way; the tip never becomes `IsVisible=false` under budget
   pressure.

- **Normal mode (styles):** the Expander renders "flat" — header row hidden
  (`IsVisible=false` via style), body force-expanded and unconstrained → visually today's
  header block. Criterion F's pixel rig covers this region specifically; if Fluent's
  Expander template chrome breaks pixel parity, the fallback (implementation decision,
  rig-evidenced) is a two-slot custom header control with the identical single-instance +
  visibility contract — the spec requirement is the contract, not the template.
- **Compact mode:** header visible ("Help & links" on Reconstructor, "Help" on the other
  views — codex rev-2 advisory; AutomationProperties.Name = the same text, no glyph),
  body collapsed by default, stock ExpandCollapse peer announces state. The USER's
  expand/collapse choice is durable within a CONTINUOUS compact session only — re-entering
  compact starts with Help collapsed (a11y rev-5 condition 5: with the 60-DIP help-open
  work band, session-durable expansion would turn one transient Help click into a
  permanent ~30px work pane on every later small window; codex rev-2 #8's durability is
  narrowed accordingly).
- Body budget: the **donation rule** of §1 — the body's `MaxHeight` equals the height the
  work band donates while Help is open (≈ 40–50 DIPs at the minimum window; the invariant
  test's one-sum check #3 is the authority), with internal scrolling (inset on the content
  panel). Expanding therefore consumes the donated space and never pushes any band below
  its Help-open minimum.
- **Keyboard scrolling of the capped body (codex round-2):** bodies containing only
  prose (every view but the Reconstructor) have no focusable children to chain
  BringIntoView, so the body's ScrollViewer becomes focusable IN COMPACT ONLY — a
  class-scoped style (`.compactHeight … ScrollViewer.helpBody { Focusable: True }`;
  base style False) so NO normal-mode Tab stop is added (criterion F — codex round-3).
  In compact it takes Tab focus after the header toggle and scrolls with
  PageUp/PageDown (plus Home/End) — Avalonia's ScrollViewer handles PAGE keys, not
  arrows (codex round-5). ERRATUM (Task 3, decompile-confirmed): Avalonia 11.3 handles
  PageUp/PageDown ONLY — Home/End are NOT stock; the shared `ScrollViewerHomeEndKeys`
  behavior supplies them via the helpBody compact style. It carries
  `AutomationProperties.Name="Help content"` (a focusable element must announce as
  something — codex round-4); asserted per view with real key input in compact AND its
  absence from the normal-mode tab-order snapshot. The Reconstructor's body is NEVER
  focusable and does NOT take the `helpBody` class (its link buttons are the keyboard
  route, chaining BringIntoView as focus moves through them).
- Compact order: disclosure header → (body when expanded: intro → links in existing
  order) → toolbar → tip (single-line) / warning → work area → … Collapsed body is `IsVisible=false` ⇒ out of
  Tab and UIA. Toolbar and the conditional warning row are content — never collapsed.
- Normal-mode order: identical to today (the hidden header contributes nothing).
  Criterion F snapshots BOTH modes (a11y rev-2 NEW-3).

> **SUPERSEDED 2026-08-18 — the "while Help is open" tiers here and in §4 are now the unconditional compact minimums.** See
> [Amendment 2026-08-18](#amendment-2026-08-18--help-is-always-visible-the-disclosure-and-the-donation-rule-are-gone).

### 3. Minimum relaxation and local-value audit

- Reconstructor TabControl MinHeight 220 → **130** normal-relaxed (row + control,
  strip-inclusive: ~30 strip + ~100 page), **96** in compact, **60** while Help is open
  (the latter two via the behavior's RowSizes/donation application; rev 5 — the
  always-visible single-line tip is paid for by the work band, which stays scrollable).
- Log bands: MinHeight **80** everywhere (list is the shrinking part; header row never
  shrinks; CreatorView's log adopts the same 80 — its current 40 fails the ≥2-rows rule).
- Splitters operate strictly between minimums; their local `Background="Transparent"`
  moves into a style (base + `:focus` both style-supplied so the focus style wins);
  `IsVisible` locals inside chrome regions move to styles. All state verification is of
  RENDERED results (bounds/brushes on rendered nodes), never selector presence.

### 4. Band structure (always-present; constraints per mode)

**Three-band views (SRSCreator, SRSReconstructor, SampleRestorer)** — the DockPanel is
replaced by a Grid whose rows are (codex rev-2 #3):

- Normal mode: `Auto / Auto / *(min 80)` — config renders at natural height, log fills:
  today's rendering exactly (parity).
- Compact mode (behavior-applied rows): `*(min 110) / Auto / *(min 80)` — config band
  becomes the squeezed, scrolling region (min 80 while Help is open, per the donation
  rule).
- Band 1: an always-present ScrollViewer hosting the existing config stack unchanged
  (bindings/tooltips/validation intact — nothing reparents at runtime). At natural height
  it shows no scrollbar and renders identically.
- Band 2 (pinned, Auto): per-view feedback inventory (codex rev-2 advisory) —
  SRSCreator: Create/Cancel row + ProgressMessage + ProgressBar (NO result banner —
  the outcome lands in the log; corrected inventory);
  SRSReconstructor: Reconstruct row + result Border (its capped two-line summary's
  FULL text reaches sighted keyboard users through the LOG, which always carries the
  complete result line — asserted; ToolTip serves pointer users, HelpText serves AT;
  completion/failure is ANNOUNCED via the app's established always-in-tree polite
  pattern (a separate result-status TextBlock that is ALWAYS in the tree with
  `LiveSetting="Polite"`, empty text rendering nothing — the SaveLogStatus pattern;
  setting text on a collapsed element then showing it races/loses the announcement,
  codex round-4 — the visual banner keeps its IsVisible binding and stays
  announcement-free). RE-ARM (codex round-5): the VM clears `ResultSummary` at RUN
  START, so a second identical outcome still transitions empty→text and announces —
  live regions fire on CHANGE, and an uncleared summary would both suppress the repeat
  announcement and show stale status;
  SampleRestorer: Restore row + ProgressBar + progress text.
  The band's worst height is asserted ≤ its headroom (319 − 24 − 120 − 80 − margins ≈ 84;
  a11y rev-2 NEW-4); the result banner gets `MaxHeight` + internal scroll/trimming.
  Overlay/adorner implementations forbidden (2.4.11).
- SampleRestorer's `SRSEntriesGrid` stays inside band 1 with boundary contracts: wheel
  hand-off at the grid's extents, cell-focus BringIntoView chaining to the outer viewer,
  inner (cell navigation) and outer (Tab-through) keyboard tests.
- **CreatorView** adopts the same pattern generalized (codex rev-2 #1/#2 — its previous
  compact plan measurably exceeded 319): band 1 hosts everything above the action area —
  inputs, detected-sets region, StoredFiles grid (grid keeps its 150 height normally via
  the RowSizes map → 80 compact; splitter between grid and lower content lives inside
  band 1 and keeps working — pixel rows via the map), output row, options stack. Bands
  2/3 as above. Normal mode: natural Auto sizing ⇒ today's rendering (rig-verified);
  compact: `*(110)/Auto/*(80)`. Detected-sets keeps a MaxHeight + internal scroll.
  The in-scroller splitter (a11y rev-3 advisory): criterion E's pane-minimum bound applies
  to it at NORMAL size only — in compact it sits inside a scrolling region where it
  adjusts natural heights rather than a visible split; it remains focusable and
  keyboard-operable in both modes.
- Any label gaining `TextWrapping` takes `Classes="wrapLabel"` (standing glyph-work rule).

### 5. Splitter polish

Per-view `AutomationProperties.Name`: Reconstructor "Resize options and log"; Creator
"Resize stored files and output". Visible `:focus` style ≥3:1 against both adjacent panes,
verified by an executable assertion (contrast computed from the rendered focus brush vs
both pane backgrounds), plus a high-contrast-theme smoke capture.

## Acceptance criteria

A. At 700×450, on every sub-tab of every task view, every control is reachable and fully
   visible once scrolled into view via scrollbar drag, wheel, AND keyboard — verified for
   the last control on Reconstructor Options and Output and each view's primary action.
   Wheel and scrollbar-thumb paths exercised with genuine input events (codex rev-2 #7).
B. No content outside its scrollable ancestor's visible clip — worst case: warning row +
   all statuses + progress + result visible, populated DataGrid, 150% render scaling.
C. Real Tab/Shift+Tab traversal from a sentinel in a real MainWindow at 700×450; after
   every step the focused control's bounds lie within the intersection of every clipping
   ancestor's viewport and the window.
D. Log reachable at all sizes: header row (title, status, Save log; Auto-scroll where
   exposed — Reconstructor only) visible and operable; list ≥2 rows, never clipped.
E. Splitters tab-reachable, Up/Down-resizable, bounded by pane minimums, visible ≥3:1
   focus indication (executable check + high-contrast smoke).
F. Normal size: tab order, reading order, and pixels unchanged — ordered tab-order
   snapshot (type + automation name) before/after per touched view + frame-rig pixel
   parity for ALL FIVE views (each is structurally touched); compact: the §2/§4 orders,
   snapshot-locked (both modes).

## Testing

- Threshold-invariant test per view (§1): the four one-sum checks — expanded worst floor
  < Threshold; compact floor (Help closed) ≤ 307; compact floor with Help open + body
  MaxHeight ≤ 307 as one sum; pinned-band worst within the same sums.
- Rendered matrix (restored from rev 2 — a11y rev-3 advisory): run criteria A/B/C as
  RENDERED runs at 700×450 (compact) AND at each view's `Threshold+1` with expanded
  chrome + all worst conditional rows — the expanded fit path must be verified by
  rendering, not only by the computed floor.
- Behavior tests: boundary (T−1/T/T+1 fresh instances — T+1 is EXPANDED), restoration at
  ≥T+12, rapid crossing, window-restore burst, reload/reattach, render scales 1.0/1.25/1.5.
- Chrome tests: single-instance (one link set in the tree in both modes); prose+links
  invocable in compact via the expander; expand state durable within a continuous compact
  session AND reset on compact re-entry; compact tip UIA Name == full tip text (condition
  1) + HelpText present (condition 2); staged focus both directions (collapse: focus →
  header; expand: focus → restored header region when the header hides).
- Criterion C Tab-walk; F snapshots (both modes) + five-view pixel parity;
  splitter floor/focus tests; three-band views: pinned band visible with all feedback
  forced while band 1 is scrolled to both extremes; SampleRestorer inner/outer hand-off.
- Font-enlargement test distinct from RenderScaling: a FontSize bump (12→16 via the
  Density resource) at 700×450 must not clip the pinned band or log header (text growth is
  absorbed by scrolling regions).
- LabeledBy/name audit for the log list and DataGrids ("Log", "Embedded SRS Files" etc.)
  retained or added on the touched surfaces.
- Full Manager suite on forced rebuilds (stale-XAML hazard); runtime ava-desktop pass at
  the VM size with before/after captures.

## Amendment 2026-08-02 — derived thresholds

The five per-view threshold constants are replaced by a switch height each view derives from its
own measured content. Everything else in this document stands: the compact floors, the 307 bound,
the donation rule, the staged-focus contract, criteria A–F.

### Why the constants had to go

The constants were calibrated on Windows and are wrong on other platforms. Measured: the
Reconstructor's expanded floor is **419** inner DIPs on Windows against a threshold of 421 — two
DIPs of headroom — and **438** on Linux CI, i.e. 17 DIPs ABOVE its own threshold. Three tests fail
there, and the failure is not a test artifact: a window between 421 and 438 renders expanded mode
with content the window cannot fit, which is exactly the clipped-and-unreachable state §Problem
exists to eliminate. Font metrics differ per platform; a constant cannot.

A threshold is also not a constant in time. Floors grow at runtime as conditional rows appear, and
a number written in a constructor cannot follow them.

### The invariant (normative; replaces "Threshold ≥ rendered expanded worst floor")

> At every window height, whichever mode a view is in must FIT: no always-visible, non-scrolling
> content clipped, on any platform, with any font stack.

The old check compared one measurement against one constant, which is only as good as the constant.
The new one is a per-view sweep of fresh instances at heights either side of that view's own
derived switch point, asserting at each that the active mode renders without clipping
(`CompactInvariantRig.AssertActiveModeFitsAroundSwitchPoint`). Every height it visits is derived
from the switch point and every verdict is about the rendered result, so a platform that needs
40 more DIPs moves the switch point and the swept band together and the same assertion still
describes the same promise. The sweep is platform-independent by construction rather than by
calibration. The old numbers are no longer normative; a few DIPs of drift from them is expected
and fine.

### Deriving the floor: measure what varies by platform, author what is design intent

Effective threshold = `max(explicit minimum, measured expanded floor + 20)`.

The floor is a sum over the root Grid's rows, each row being one of two kinds:

- **GIVABLE** — the row's content can scroll, so the floor owes only the minimum the design insists
  on seeing, never the content height. Two ways to qualify: a **Star row**, which gives by
  construction and is owed its `MinHeight`; or a row whose `CompactRowSize` declares an
  **`ExpandedMinHeight`**, which is owed exactly that. The declaration wins over the row's kind —
  it is the more specific statement, and the only one available for the three-band views' config
  band, which is a plain Auto row at expanded size.
- **FIXED** — everything else: chrome shown whole or not at all. Pixel rows contribute their
  height, Auto rows the tallest desired height among their children including margins. This is the
  part that must be measured, because it is the part that moves with the platform's fonts.

Counting a scrollable band at its content height instead is not merely pessimistic, it is
divergent: for a band the view caps to the room left over (Creator, SampleRestorer — see below),
the content height is a function of the current window height, so the floor chases the height it is
being compared against and no window is ever tall enough. Measured before the fix: Creator's floor
at a 721-DIP window came out at 717, giving a threshold of 737 — above the window that produced it.

`ExpandedMinHeight` is an authored design value, not a measurement. It answers "how little of this
band is still worth staying expanded for", which no amount of measuring content can decide.

**A band is only givable if something actually makes it give.** Creator and SampleRestorer cap
their config ScrollViewer's `MaxHeight` to the room remaining after chrome, the pinned band and the
log minimum (per-view code-behind, on every layout pass) — that cap is what makes the claim true,
and both declare an `ExpandedMinHeight`. SRSCreator and SRSReconstructor have no such cap: at
expanded size their config row is a plain Auto row that takes its full desired height, so it does
NOT give and its measured content height genuinely is what the floor owes it. They declare no
expanded minimum. Declaring one on a row that cannot give moves the switch point below the height
the row's content actually needs — the sweep catches it: a throwaway sabotage declaring SRSCreator
givable at 100 fails with `SRSCreator at inner height 315 in EXPANDED mode: ScrollViewer bottom
337.0 exceeds 315`.

Help state does not enter the expanded floor. The donation rule is a compact-mode mechanism and
`HelpOpen` is false throughout expanded mode by construction; expanded mode renders the Help body
flat, expanded and unconstrained, which is the largest it ever is, and the floor already carries
that as measured chrome. So the expanded floor is both Help-state-correct and conservative without
a second set of minimums.

### Authored minimums, back-derived from the table above

Each is the share of the old constant that the design attributed to that band:
`(old constant − 20 margin) − measured chrome − measured pinned band − log minimum 80`, rounded.
Windows switch points therefore land near the old numbers, with the invariant — not the numbers —
now normative.

| View | Givable band | Authored `ExpandedMinHeight` | Derived switch point (Windows) | Old constant |
|---|---|---|---|---|
| Reconstructor | TabControl + log (Star rows, `MinHeight` 130 / 80 in XAML) | none needed | **439** | 421 |
| SRSCreator | none (config row is uncapped Auto) | none | **511** | 520 |
| SRSReconstructor | none (config row is uncapped Auto) | none | **456** | 450 |
| SampleRestorer | config band (row 1) | **320** | **535** | 535 |
| Creator | config band (row 1) | **500** | **715** | 720 |

The Reconstructor's +18 is the honest correction: its floor really is 419 on Windows, and 421 left
two DIPs — which is how Linux's 438 ended up below the switch point.

### Capture, hysteresis, anti-flap

- **Capture.** The floor is read from the desired sizes the last real layout pass produced, never
  by re-measuring — a `Measure(∞)` would report content height for givable rows and would dirty the
  live layout to ask. Read only while EXPANDED, the only state in which the expanded layout exists;
  the value is held across a compact session.
- **Two triggers, complementary.** (1) After every change the behavior itself makes that leaves the
  view expanded — a restore, or the first evaluation at normal height, where flat mode has just
  forced the Help body open and the capture ran before the body existed — a re-evaluation is posted
  at `Loaded`, below the layout-driving priorities, so it reads the settled tree. This is not left
  to layout notification, because those changes do not always invalidate layout. (2) `LayoutUpdated`
  re-captures continuously, for the changes the behavior does NOT make: content arriving, prose
  rewrapping, a font growing. None of those resize the root — its height is the window's to decide —
  so none raise a bounds change, and an evaluation-only capture would keep quoting a floor the
  layout has already outgrown. Only a GROWN floor can change the verdict from expanded, so an
  evaluation is queued for that case alone; the ordinary pass costs one row walk.
- **Hysteresis** is unchanged and now applies to the derived value: compact below the effective
  threshold, restore at effective + 12, restore-only, so a fresh instance at the threshold starts
  expanded.
- **Anti-flap, by construction.** A floor that grew while compact is invisible until the expanded
  layout is back, so a restore CAN turn out to be wrong. When it does, the re-validation returns the
  view to compact — and the newly-measured floor has raised the threshold ABOVE the very height that
  produced the failed restore, so restoring again would need a strictly greater height. One flip,
  then rest. Pinned by `RestoreThatNoLongerFits_ReturnsToCompact_AndThenRests`, which asserts
  exactly two class changes and no further movement across five more dispatcher turns.

### `Threshold` survives as an optional minimum

The attached property still exists and still binds — but only UPWARD: the effective threshold is
the larger of it and the derived floor plus margin, so a view can choose to go compact earlier than
its content strictly requires and can never be held expanded in a window its content does not fit.
Derivation is therefore not opt-in; an invariant a caller can decline is not one. `Enabled` is the
attach trigger for a view that names no minimum, since `Threshold`'s default is already NaN and
assigning NaN raises no change. No shipped view sets a minimum.

`CompactHeightBehavior.GetEffectiveThreshold` exposes the switch height read-only, so tests derive
their heights from it instead of restating a number that can drift from the one the behavior uses.
No per-view switch height is written down anywhere in the test suite.

## Amendment 2026-08-02b — pinned-band bound restated after the 13px content-text change

App-wide content text moved 12 → 13px (user decision, "Commit 13px", after a 12/13/14 side-by-side).
This section records the one bound that had to move with it, and the measurements that say nothing
else did.

**The tests were tighter than this document.** They asserted the pinned action band at ≤ 75, taken
from the per-view compact-floor TARGETS in §1's table ("action ≤ 75") — figures for the floor SUM,
not bounds on the band. §4's own assertion is against the band's HEADROOM: `319 − 24 header −
120 config − 80 log − margins ≈ 84`. The bound is now that 84, shared as
`CompactInvariantRig.PinnedBandCeiling`, and the four three-band views assert against it.

Measured, worst case, inner width 676 at the 700×450 minimum (12px → 13px):

| View | Compact floor, Help closed | Help open | Pinned band | Derived switch point |
|---|---|---|---|---|
| Reconstructor | 293 → **295** | 295 → **297** | 22 → **22** | 439 → **441** |
| SRSCreator | 276 → **278** | 281 → **283** | 68 → **70** | 511 → **523** |
| SRSReconstructor | 280 → **285** | 285 → **290** | 72 → **77** | 456 → **467** |
| SampleRestorer | 268 → **270** | 273 → **275** | 60 → **62** | 535 → **537** |
| Creator | 268 → **270** | 273 → **275** | 60 → **62** | 715 → **717** |

The one-sum compact invariant is unaffected: the worst floor is 297 against the 307 CI bound, with
10 DIPs of headroom, Help open or closed, on every view. Only SRSReconstructor's band crosses the
old 75, and at 77 it is well inside the 84 the design actually allows. Reclaiming two DIPs from that
one view's padding was considered and rejected: it would shave a real visual design to satisfy a
number the document never asserted, in one view out of five, while the budget it exists to protect
measurably holds.

Derived thresholds absorbed the change with no code edit, which is what they are for — switch points
moved +2 to +12 DIPs, and the sweep invariants are stated in terms of each view's own switch point
so none of them needed touching.

Two other recalibrations, both from measured 13px geometry rather than widened tolerances:

- The dense versions list now realizes its rows at 18 rather than 16 (the text sizes the row; the
  scoped `MinHeight` 16 no longer binds), so its pitch is 20 rather than v1.9's 18. That moves the
  row AWAY from the 2.5.8 target-size deviation §3 granted this list, not deeper into it, and the
  style's stated invariants (header toggle `MinHeight` ≥ 24, every leaf keyboard-reachable) are
  untouched. The guard against a Fluent bump restoring the 20px primitive floor still
  discriminates: 20 > 18.
- The 14px check glyph still centres exactly — at 13px the row is 18 and the glyph sits 2.00 from
  the top, where the same rule gave 1.00 against a 16px row at 12px. The test now derives the
  expected offset from the measured slack, so it asserts CENTRING rather than a particular font
  size, and asserts the slack is large enough for centred and top-aligned to be distinguishable.

## Amendment 2026-08-18 — Help is always visible; the disclosure and the donation rule are gone

The `Expander` disclosure of §2 is removed. Every view now renders its Help content directly,
in both modes, with no header, no toggle and no expand/collapse state. User decision ("no
'help' dropdown to show the help, and instead, always just show the help"), taken because the
SRR Creator tab was the only one whose help sat behind a control — the inconsistency that
prompted this.

**What it supersedes**

- §1's **donation rule**, and with it the two-tier compact minimums. There is no Help-open
  tier and no Help-closed tier: the donated value IS the compact minimum (Reconstructor
  TabControl **60**, three-band config **80**). `CompactRowSize` collapsed its two minimum
  fields into one and kept the donated number — the value each view had already been proven
  to satisfy the 307 bound with, so the bound is preserved by construction rather than by
  re-derivation.
- §1's threshold invariant, items **2 and 3**, which measured a Help-closed floor and a
  Help-open floor as separate checks. One state exists, so one check remains: the compact
  floor, body budget included, against the 307 bound.
- §2 entire, EXCEPT the keyboard-scrolling contract, which survives unchanged —
  `ScrollViewer.helpBody` focusable in compact only, PageUp/PageDown plus the shared
  `ScrollViewerHomeEndKeys` behavior for Home/End, `AutomationProperties.Name="Help content"`,
  and the Reconstructor's body never focusable because its link buttons are the keyboard route
  (it keeps its own `AutomationProperties.Name="Help & links"`). The compact order loses its
  leading disclosure header, and the body is never `IsVisible=false`.
- §3's "**60** while Help is open" and §4's "min 80 while Help is open" — both unconditional
  compact minimums now.

**Mechanism removed.** `HelpDisclosure` and its automation peer are deleted, as are the
behavior's `HelpOpen` and `HelpExpander` attached properties and the recompute path that kept
them in sync with expander state. One `HelpBody` attached property replaces all of it, carrying
the compact `MaxHeight` cap and the compact focus target.

**Measured compact floors** — inner width 676 at the 700×450 minimum, conditional rows forced
visible, body budget included. These are the executable invariant's own numbers, not arithmetic:

| View | Compact floor (≤ 307) | Was, Help open (2026-08-02b) |
|---|---|---|
| Reconstructor | **271** | 297 |
| SRSReconstructor | **272** | 290 |
| SRSCreator | **265** | 283 |
| SampleRestorer | **257** | 275 |
| Creator | **257** | not separately measured |

Every view with a prior measurement got CHEAPER, which is the result to check rather than
assume: the always-present body costs nothing new, because donation had already budgeted it,
while the disclosure header and its chrome — paid for in both states — are gone. Headroom
against the 307 bound is now 35–50 DIPs per view.

## Out of scope

Unchanged from rev 2: 24px checkbox pitch (2.5.8) — separate user decision; legend
tri-state text alternative (tracked); any normal-size content/behavior change.

## Rollout

One plan: CompactHeightBehavior + invariant-test rig → Reconstructor (template: expander,
minimums, splitter polish) → the three three-band views (one task each) → CreatorView
(largest restructure, benefits from the pattern being proven) → Settings audit +
whole-board verification. Gates per task: codex diff review; a11y-lead final review
against A–F.
