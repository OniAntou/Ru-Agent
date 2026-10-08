---
status: pending
date: 2026-10-08
topic: right-side-panel
---

# Right side panel — card + details panel + title-bar toggle

Open design draft. Delete this file when the implementation lands (git history is the
archive). Guard: `tests/test_current_documentation.py
::test_completed_implementation_archives_are_not_kept_in_the_checkout` allows
`docs/plans/*.md` only while a non-completed `status:` marker is present.

## Goal

Give RuApp a DSH-style right side surface with two tiers: the existing compact floating
card stays the default summary, and a new full-height details panel (tab strip: Changes,
Tasks; resize; fullscreen) opens from it and collapses back to it. A new title-bar toggle
next to the caption buttons hides and restores the whole surface.

## Reference

DeepSeek Harness web client (`packages/client/ui-sidebar-right` in
`deepseek-ai/deepseek-harness`): the panel has no header row — the tab strip is its whole
top edge (tab chips with close controls, add control, then the panel controls); the panel
is a column of the page, not a card over it; push presentation; fullscreen presentation;
per-session state. We adopt the presentation ideas, not the dock system (no drag/drop,
split panes, floating tabs, or Guide tab).

Repository constraints that shape this design:

- Broker stays the only execution authority; background-task Stop keeps going through the
  Host's typed `/tasks kill` command. No new route, no direct Broker call.
- No changes to presets, approvals, protected paths, or provider behavior.
- Source-contract tests pin decisions; update them deliberately with the change, never
  weaken them.
- Bounded everything: list caps, width clamp, bounded per-session map.

## Current state

- Card: `Views/ChatView.xaml` (`SidePanelCard`, `SidePanelOpenButton`); layout in
  `Views/ChatView.xaml.cs` (`UpdateSidePanelLayout`, `EffectiveSidePanelReserve`,
  `PlaySidePanelEntrance`) and task/edit data in `Views/ChatView.Tasks.cs`
  (`UpdateSidePanel`, `RefreshSidePanelEdits`, `RefreshAgentTasksAsync`,
  `OnAgentTaskStopClick`).
- Width rules: `SidePanelMinRootWidth = 1120`, `SidePanelMargin = 16`; the reserve is the
  shift-only formula so a wide window keeps the chat column centered.
- Title bar: `MainWindow.AppTitleBar` (40px, `SetTitleBar`), themed caption colors in
  `ApplyTitleBarTheme`.
- Contracts pinning the current card: `gui-winui3.Tests/AgentTasksUiContractTests.cs`
  (`SidePanelCard`, `UpdateSidePanelLayout`, `SidePanelMinRootWidth`,
  `EffectiveSidePanelReserve`).

## Final model

Three per-session states: `Card` (default), `Panel`, `Hidden`.

- A session with messages on a wide-enough window shows `Card` unless the user hid the
  surface (or has the panel open).
- Card → Panel via the card's expand chevron.
- Panel → Card via the strip-end collapse control, or by closing the last open tab (the
  DSH rule).
- Card/Panel → Hidden via the title-bar toggle; Hidden → the previous visible state via
  the same toggle.
- Window narrower than `SidePanelMinRootWidth (1120)`: the surface cannot show — an open
  panel retreats to Card, and below the threshold the Card itself does not render either;
  the title-bar toggle is disabled.

### Card (kept, compact)

- Same floating placement: top-right of `RootGrid` row 0, width 280, `RuSurfaceBrush`
  with `RuLineBrush` border, margin 16, existing entrance animation.
- Contents: session title; `+X −Y` edits row; compact task rows (label + state detail
  text, no Stop); expand chevron button (opens Panel).
- No hide control on the card (the title-bar toggle owns hiding); the floating corner
  `SidePanelOpenButton` is removed.
- Reserve: keep the existing shift-only `EffectiveSidePanelReserve()` behavior.

### Panel

- A full-height column: `Grid.RowSpan=2` in `RootGrid`, right-aligned,
  `VerticalAlignment=Stretch`, variable `Width` (default 360), background `RuCanvasBrush`
  (the chat's ground, not a raised card), 1px `RuLineBrush` divider at its left edge.
  Chat, composer, and approval keep centering in the remaining width through the existing
  reserve mechanism (same `EffectiveSidePanelReserve()` formula with the panel width).
- Strip (height 40): tab chips `Changes` and `Tasks`, each a small leading glyph plus a
  label plus a close (×) shown on the active chip and on hover; a `+` add control opens a
  `MenuFlyout` listing the closed tab kinds and is drawn only while at least one kind is
  closed; at the far end, the fullscreen toggle and the collapse control (Panel → Card).
  Active chip: `RuSurfaceBrush` fill with a subtle border; inactive: transparent with a
  hover fill. Every strip control and the resize grip carry a tooltip and
  `AutomationProperties.Name`.
- Changes tab:
  - Rows built from `AgentActivities` edit rows (`Kind == Edit`), newest first, capped at
    100.
  - Header shows the session totals `+X −Y` (existing sum over all edit activities).
  - Each row: file name (`RuMonoFontFamily`), `+`/`−`, safe code; expanding a row reveals
    the before/after diff from the row's activity (the existing `SplitEditPreview` split,
    rendered with `FileEditPreviewControl`).
  - Empty state: "No changes yet."
- Tasks tab: the current card section moves here — bound to `AgentTasks` (cap 16), Stop
  per row through `/tasks kill` (authority unchanged), status/empty/error copy preserved;
  refresh on run start/settle and when the tab or panel opens.
- Resize: a 6px left-edge grip (`SidebarResizerControl` pattern, SizeWestEast cursor)
  only while the panel is in push mode; drag updates `Width` clamped to [280, 560]; on
  release the width is persisted. Chat reserve follows live.
- Fullscreen: the strip control makes the panel span the full `RootGrid` (both rows, full
  width, divider hidden); the chat is covered but retained; toggling returns to push. No
  animation on the mode switch in v1.
- Panel open on a too-narrow window ⇒ retreat to Card (`UpdateSidePanelLayout` rule).
- Entrance: slide+fade through `UiTiming` (reduced-motion aware); exits stay instant,
  matching current practice.

### Title-bar toggle

- `MainWindow.AppTitleBar` gets one interactive button, right-aligned so it sits
  immediately left of the minimize button: right margin =
  `AppWindow.TitleBar.RightInset / XamlRoot.RasterizationScale`; height 32, width 36,
  transparent background; hover `RuAccentSoftBrush`, pressed `RuLineBrush`, glyph
  `RuInkBrush` (same tokens the themed caption buttons use in `ApplyTitleBarTheme`),
  dimmed while the window is deactivated (`Window.Activated`).
- Glyph: a custom 16×16 `Path` matching the reference (rounded rectangle outline with a
  vertical divider right of center), independent of font glyph availability.
- WinUI requirement: interactive title-bar content needs explicit passthrough regions. In
  `AppTitleBar` Loaded/SizeChanged, call
  `InputNonClientPointerSource.GetForWindowId(AppWindow.Id)
  .SetRegionRects(NonClientRegionKind.Passthrough, [button rect])` with the rect
  converted by `XamlRoot.RasterizationScale`
  (docs: `windows/apps/develop/title-bar`, "Interactive content").
- Enabled only while the surface can show (`Messages.Count > 0` and width ≥ 1120) or the
  surface is currently visible; tooltip and `AutomationProperties.Name` are
  "Hide side panel" / "Show side panel"; keyboard focus keeps the system focus rectangle.
- Click delegates to the active `ChatView`; `ChatView` reports state back through
  `MainWindow.Instance` (the existing pattern) so the button's glyph, tooltip, and
  enabled state track Card/Panel/Hidden.

## State

- `AgentSidePanelPolicy` (new, pure logic, testable): per-session mode
  (`Card`/`Panel`/`Hidden`), open tab kinds, active kind, fullscreen flag; bounded to 8
  sessions (LRU, mirroring `ComposerDraftStore`). Defaults: no entry → Card mode (its
  visibility still follows the message/width rules); closing the active tab selects the
  neighbor; closing the last tab → Card; opening the panel with no open tabs seeds
  Changes; `Toggle()` hides or restores the previous mode.
- Width: `SidePanelPreferenceStore` (new), `%LOCALAPPDATA%\Ru\side-panel.json`,
  `PreferenceFile.ReadOrNull` bound, atomic temp+move write, fail-soft; clamped into
  [280, 560] on load; default 360.

## Components and files

New:

- `gui-winui3/Controls/AgentSidePanelControl.xaml(.cs)` — the panel column: strip, tab
  bodies, resize grip, fullscreen/collapse controls.
- `gui-winui3/Services/AgentSidePanelPolicy.cs`.
- `gui-winui3/Services/SidePanelPreferenceStore.cs`.

Modified:

- `gui-winui3/MainWindow.xaml` — the title-bar toggle button inside `AppTitleBar`.
- `gui-winui3/MainWindow.xaml.cs` — passthrough regions, right inset, activation dimming,
  click delegation.
- `gui-winui3/Views/ChatView.xaml` — replace the old card/open button pair with the card
  and `AgentSidePanelControl`.
- `gui-winui3/Views/ChatView.xaml.cs` plus a new partial `ChatView.SidePanel.cs` — layout,
  reserve, toggle, and state wiring (moved out of the main partial).
- `gui-winui3/Views/ChatView.Tasks.cs` — feeds `AgentTasks` for both card and panel (the
  card template renders the rows without Stop); edit totals move to the Changes tab.
- Call sites: `ChatView.AgentFeed.cs` (`RefreshSidePanelEdits` → Changes rebuild),
  `ChatView.AgentRun.cs` (`UpdateSidePanel`), `ChatView.SessionHistory.cs`
  (`_sidePanelRunRunning`, margins).

## Data flow

- Changes rows: derived from `AgentActivities` edit items, rebuilt on feed mutations,
  newest first, capped 100; totals sum the full activity set.
- Tasks rows: typed `/tasks` command response parsed as today into `AgentTasks` (cap 16);
  Stop posts `/tasks kill <execution_id>` through the existing controller.
- Panel/card visibility: `AgentSidePanelPolicy` state + `UpdateSidePanelLayout` width
  rules; the title-bar button mirrors the state through `MainWindow.Instance`.

## Error handling and edge cases

- No session or no messages: surface hidden; toggle disabled; nothing binds.
- Narrow window: panel retreats to Card; card hidden (existing rule); toggle disabled.
- Session switch: per-session state restores; stale callbacks from the previous session
  are dropped through the existing generation guards.
- Session delete: drop the policy entry.
- Session reopen: Changes rebuilds from replayed activities.
- Cap: rows past 100 are omitted from the list; totals still cover every edit.
- Reduced motion: entrance and toggle animations skip to the final state.
- DPI change: title-bar regions and inset recompute on `AppTitleBar.SizeChanged`.
- Window deactivated: toggle glyph dims; input still works on reactivation.
- Stop failure keeps the current copy ("Stop failed; the host did not answer.").
- Hiding while fullscreen preserves the mode for restore.

## Testing

- Update `gui-winui3.Tests/AgentTasksUiContractTests.cs` for the new decisions (card
  without Stop, panel strip, close-last-tab → Card, title-bar toggle and passthrough
  call, resize clamp).
- New `AgentSidePanelPolicyTests`: mode transitions, close-tab selection, seed default,
  toggle restore, 8-session bound, width clamp.
- New `SidePanelPreferenceStoreTests`: missing/oversized/garbage file → default; clamp;
  atomic write.
- Any MainWindow shell contract pinning `AppTitleBar` expectations updates to the new
  decision.
- Run `dotnet build gui-winui3/RuApp.csproj -c Debug`, then
  `.\scripts\test_dotnet.ps1 -Configuration Debug`, then `.venv\Scripts\python.exe -m
  pytest tests -q`.
- If the .NET test total changes, update `docs/operations/verification.md` with the fresh
  full-run record and the README counts (guard: `tests/test_readme_counts.py`).

## Docs

- `CHANGELOG.md`: one `feat(gui)` entry describing the card + panel + title-bar toggle.
- `docs/operations/coding-agent-capabilities.md`: replace the "compact right-hand card"
  background-task wording with the card + details panel + title-bar toggle.
- Delete this design file when the implementation lands.

## Out of scope

- DSH dockkit features: drag/drop, split panes, floating tabs, tab history, persisted
  layout per session, Guide tab, session inspector.
- Replacing the transcript's inline edit previews.
- CLI/TUI changes.
