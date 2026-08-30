# Responsive WinUI Layout Design

**Date:** 2026-08-20  
**Status:** Historical implementation record. The responsive shell is shipped;
use [`docs/operations/runtime-behavior.md`](../../operations/runtime-behavior.md)
for the current breakpoints and acceptance boundary.
**Scope:** `gui-winui3` shell and views only

## Decision summary

Ru's WinUI shell will support two responsive behaviors together:

1. The existing resizable sidebar remains available at comfortable widths.
2. The shell and content pages switch to compact/narrow layout tiers when the
   window becomes smaller, so controls reflow instead of clipping or forcing a
   horizontal scrollbar.

The implementation will use XAML visual states for presentation changes and a
small pure C# responsive policy for breakpoint selection and sidebar bounds.
No third-party UI dependency, navigation-framework migration, or backend
change is needed.

## Context

The current WinUI shell is visually tuned for a wide desktop window:

- `MainWindow.xaml` has a 250 px sidebar with a 180 px minimum and a draggable
  divider.
- `DiagnosticsView` and `LogView` place several filters in one fixed header row.
- `VoiceView` and `SettingsView` use many `Grid` rows with fixed-width controls
  in the `Auto` column.
- `ChatView` already recalculates the message/input width, but its message
  margins and welcome suggestions remain wide-screen biased.

At smaller widths these assumptions make the content feel rigid. This design
keeps the current visual language and behavior while making the layout adapt to
the available space.

## Goals

- Make the shell usable at approximately 1440, 1024, 800, and 640 px wide.
- Keep every existing navigation item and functional control reachable.
- Reflow filters, cards, setting rows, action buttons, and status indicators
  without horizontal clipping.
- Preserve sidebar dragging when the current width permits it and restore the
  user's last expanded width after leaving narrow mode.
- Keep diagnostics/log lists stable while resizing; do not clear and rebuild
  collections merely because the window changed size.
- Ensure layout changes are local UI work and do not add request, polling,
  audio, WebSocket, or backend latency.
- Provide deterministic unit/contract coverage for the responsive policy and
  XAML states.

## Non-goals

- No change to ASP.NET Core routes, Python worker behavior, API contracts,
  logging timers, voice playback, STT/VTS behavior, or memory/business logic.
- No replacement of the current shell with `NavigationView`.
- No new persisted UI preference in this slice. The expanded sidebar width is
  remembered only for the lifetime of the window.
- No visual redesign of colors, typography, or branding.
- No automated control of the user's desktop, RuApp, or VTube Studio. Manual
  visual acceptance remains a user-run step.

## Responsive model

Introduce a shared `ResponsiveLayoutTier` and a pure
`ResponsiveLayoutPolicy` under `gui-winui3/Services`. The policy takes the
client/root width in device-independent pixels and returns the tier plus the
sidebar bounds for that tier.

| Tier | Width | Shell intent |
| --- | ---: | --- |
| `Wide` | `>= 1280` | Full labels, normal spacing, user-resizable 180–400 px sidebar. |
| `Compact` | `900–1279` | Reduced shell spacing, labels retained, tighter sidebar range and page padding. |
| `Narrow` | `< 900` | 72 px icon rail, labels/tool text hidden, controls stack or wrap, no sidebar drag. |

The exact comparison belongs to the policy rather than being duplicated in
XAML. A `SizeChanged` handler should apply visual states only when the tier
changes, not execute a full layout mutation for every pixel of a drag. The
current resizer may continue to update the `GridLength` while the tier is
unchanged.

When entering `Narrow`, save the current expanded sidebar width and clamp the
sidebar to the icon rail width. When returning to `Compact` or `Wide`, restore
the saved width after clamping it to that tier's legal range. If the user never
resized the sidebar, use the existing 250 px default.

## Shell behavior

### Wide

- Keep the current 250 px default and 180–400 px drag range.
- Show navigation labels, the recent-chat heading, session titles, and full
  service badge text.
- Show the title-bar `Live2D Companion` badge.
- Keep the existing page padding and status-bar density.

### Compact

- Keep navigation labels and recent session titles visible, but reduce button
  padding/gaps and use a tighter sidebar range (approximately 180–320 px).
- Keep the divider draggable within the compact range.
- Reduce content-page side padding from the current 36 px to a compact token
  (approximately 24 px).
- Keep service badge text visible, allowing badge content to trim with a
  tooltip rather than expanding the window.
- Keep the title-bar badge visible unless the measured title-bar space is too
  small; the state must never overlap the system caption buttons.

### Narrow

- Use a fixed 72 px icon rail and hide the divider hit target.
- Hide navigation labels and the recent heading; every icon-only navigation
  button gets a meaningful tooltip/automation name.
- Keep recent sessions selectable as compact chat-icon items with the session
  title available through its tooltip. Hide the per-item delete affordance when
  there is not enough room; deletion remains available after returning to a
  wider tier.
- Hide the text portion of the status summary and retain status dots with
  tooltips. Service badges become dot-first compact indicators with their full
  meaning in tooltips.
- Hide the title-bar `Live2D Companion` badge but retain the `Ru` identity.
- Reduce page side padding to approximately 12–16 px.
- Preserve keyboard navigation and focus order when labels change visibility.

## Page layout behavior

All page changes are presentation-only. Existing event handlers, bindings,
timers, and data sources remain intact.

### Chat

- Keep `ChatScrollHost` vertically scrollable and horizontal scrolling disabled.
- Reduce user/assistant bubble side margins at compact and narrow tiers so the
  message content gets the available width.
- Keep the existing width calculation for `InputBorder`, `ChatContentGrid`,
  and `ChatListView`, but use tier-aware side insets and do not allow the input
  capsule to exceed its parent.
- Allow welcome suggestion buttons to wrap/stack at narrow widths instead of
  extending past the card.

### Activity log and diagnostics

- Replace the assumption that the header filters must occupy one row. The
  title remains first; filter/action controls move to a second row in compact
  mode and to a wrapped/stacked arrangement in narrow mode.
- Remove fixed header widths where they prevent the grid from shrinking; use
  minimum usable widths and stretch remaining controls.
- Change the four-column diagnostics status card to two columns at compact
  width and one column at narrow width.
- Let timeline/diagnostic item metadata become a compact metadata row above
  the summary at narrow width. Details remain wrapped and readable; do not
  reduce text to the point of truncation.
- Keep list scroll viewers vertical-only. Resizing must not replace the item
  source or reset the current scroll position.

### Voice and VTS

- Keep the current two-column label/control arrangement in wide and compact
  tiers where it fits.
- At narrow width, each setting row becomes a vertical label/description then
  control arrangement. Fixed controls (combos, sliders, text boxes) use the
  available width with a safe minimum rather than overflowing the card.
- Wrap VTS action buttons and stack the test-voice row when necessary.
- Keep slider values and all existing handlers unchanged; reflow must not
  trigger a second settings write solely because the window was resized.

### Settings

- Keep the current `Pivot` tabs and scrollable tab content.
- In wide/compact tiers, retain the current label/control rows while replacing
  hard widths that can force clipping with `MaxWidth`/stretch behavior.
- In narrow tier, move controls below their descriptive text for every setting
  row, including model/API fields, numeric generation fields, observation
  fields, and VTube Studio fields.
- Keep tab headers reachable horizontally if their total width exceeds the
  viewport; never hide a settings tab as a responsive shortcut.

## Implementation boundaries

Expected implementation files are:

- `gui-winui3/Services/ResponsiveLayoutPolicy.cs` and its focused tests.
- `gui-winui3/MainWindow.xaml` / `.xaml.cs` for shell visual states, tier
  application, sidebar restoration, tooltips, and status compaction.
- `gui-winui3/Views/ChatView.xaml`, `LogView.xaml`,
  `DiagnosticsView.xaml`, `VoiceView.xaml`, and `SettingsView.xaml` for
  view-level visual states and reflow.
- Minimal view code-behind changes only where a size-dependent calculation is
  already owned by that view (for example, Chat's input width).
- `gui-winui3.Tests` for policy and source/XAML contract coverage.

Do not mix unrelated diagnostics, logging, backend, or model changes into the
responsive UI change. Existing dirty files in the worktree remain outside this
scope.

## Performance and stability constraints

- The responsive handler must never call `ApiService`, touch the worker,
  restart a timer, reload a view, or recreate an `ItemsSource`.
- Tier transitions should be idempotent. Applying the same tier twice must not
  change data, focus, or scroll state.
- Do not add layout animations to a continuously resized window; they can cause
  visible flicker and unnecessary composition work.
- Keep the diagnostics list's existing batched update and scroll preservation
  behavior intact.
- A GUI test/build pass is evidence for code correctness only. Real window
  resizing and the final visual check remain a manual acceptance gate.

## Verification plan

1. Add policy tests for all breakpoint boundaries, sidebar clamping, saved-width
   restoration, and repeated same-tier application.
2. Add XAML/source contract tests that assert the shell and each affected view
   define the required responsive states, tooltips, and no-horizontal-scroll
   behavior.
3. Run the complete WinUI test project and the existing backend/Python suites
   to detect accidental cross-layer regressions.
4. Build/publish the WinUI Release configuration.
5. Perform manual acceptance at approximately:
   - 1440×900: wide shell and normal page density;
   - 1024×768: compact shell, filters still usable;
   - 800×600: narrow rail and stacked page controls;
   - 640×600: no clipped controls, no horizontal scrollbar, usable scrolling.
6. On each size, visit Chat, Voice & VTS, Activity Log, Technical Diagnostics,
   and all Settings tabs. Confirm that resizing does not alter request latency,
   audio playback, diagnostics polling, or scroll stability.

## Documentation follow-up

After implementation, update `docs/operations/runtime-behavior.md` with the
supported shell tiers and the distinction between automated layout tests and
manual visual acceptance. No existing document was found that describes a
responsive layout and can be retired at this stage.
