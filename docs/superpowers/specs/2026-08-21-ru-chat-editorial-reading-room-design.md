# Ru Chat Editorial Reading Room Design

**Date:** 2026-08-21
**Status:** Implemented; palette refined 2026-08-21
**Scope:** Phase 1 visual redesign of the WinUI 3 shell/sidebar and Chat view

## Decision summary

Ru will move from the current dark card-and-bubble presentation to a light,
editorial reading room. The first slice covers the shell/sidebar and Chat view
only. The other views keep their existing layout and behavior, but the shared
tokens created here must be reusable when they are redesigned later.

The redesign preserves the existing navigation, session selection, streamed
chat, Markdown rendering, STT microphone action, send behavior, responsive
tiers, backend contract, voice pipeline, VTube Studio integration, memory
behavior, and process lifecycle.

## Current-state audit

The current WinUI shell and Chat view are functional but visually repetitive:

- `App.xaml` defines a dark slate/obsidian palette, rounded cards, rounded
  controls, blue accent treatment, and a reusable card style.
- `MainWindow.xaml` uses a wide sidebar, repeated rounded navigation rows, a
  `Live2D Companion` branding badge, and a bottom status bar containing three
  service badges.
- `ChatView.xaml` uses a centered welcome card with an avatar emblem, pill-like
  suggestion buttons, user and assistant bubbles, an assistant avatar row, and
  a floating rounded composer capsule.
- Recent responsive work already establishes `Wide`, `Compact`, and `Narrow`
  layout tiers. This redesign keeps those tiers and changes their presentation,
  not their runtime responsibilities.

The checkout contains unrelated dirty changes across backend, runtime, GUI,
tests, and documentation. The implementation must edit only files explicitly
within this design slice and must not stage or commit unrelated work.

## Goals

- Make Chat feel like a calm writing workspace rather than an AI dashboard.
- Remove repeated card, bubble, pill, border, and badge decoration where it
  does not communicate hierarchy or state.
- Make the assistant response the primary reading surface.
- Keep the shell legible and useful at wide, compact, and narrow widths.
- Preserve all existing Chat and shell behavior, bindings, events, and data
  sources.
- Establish shared visual tokens that can later be applied to Voice/VTS, Log,
  Diagnostics, Settings, and Memory.
- Keep the UI free of Unicode emoji, ASCII emoticons, voice/VTS control
  metadata in chat, and invented technical status copy.

## Non-goals

- No ASP.NET Core route, WebSocket, Python worker, generation, memory, voice,
  VTube Studio, or process-lifecycle changes.
- No redesign of Voice/VTS, Log, Diagnostics, Settings, or Memory content in
  this phase.
- No navigation framework migration and no third-party UI package.
- No new persisted UI preference.
- No new backend endpoint or UI polling path.
- No automated launching or control of RuApp or VTube Studio.
- No content rewrite beyond the approved welcome intro and composer placeholder;
  conversational content, errors, and technical labels keep their current
  meaning.

## Visual direction

### Design read

This is a desktop companion app for one primary user, with a quiet editorial
language that should feel closer to a personal writing workspace than to a
generic AI product surface.

### Theme and tokens

Phase 1 uses one light theme. It is intentionally mineral and paper-like,
without a purple-blue gradient, glow, or glass treatment.

| Token | Value | Use |
| --- | --- | --- |
| `RuCanvasBrush` | `#E7E9E3` | Main content background |
| `RuSidebarBrush` | `#DCE1D9` | Sidebar background |
| `RuSurfaceBrush` | `#F0F2EC` | Composer and restrained elevated surfaces |
| `RuInkBrush` | `#20231F` | Primary text |
| `RuMutedBrush` | `#5F685F` | Secondary text and quiet metadata |
| `RuLineBrush` | `#C9CEC5` | Dividers and borders |
| `RuAccentBrush` | `#A84B38` | Active navigation, focus rule, primary action |
| `RuAccentSoftBrush` | `#EEDFD9` | Subtle selection and action background |
| `RuSuccessBrush` | `#2F6F51` | Positive service state |
| `RuWarningBrush` | `#9A6C1F` | Warning service state |
| `RuDangerBrush` | `#A73A3A` | Error state |

The shared palette was softened after visual review while keeping the same
editorial layout and one-light-theme decision. The refinement is recorded in
`2026-08-21-ru-mineral-paper-palette-refinement-design.md`.

Typography uses `Segoe UI Variable` for UI and message body. `Cambria` is
reserved for page titles and Markdown headings, where the editorial contrast
adds hierarchy while retaining Vietnamese glyph coverage. The base rhythm is
4/8/12/16/24/32 px. Controls use a 6 px corner radius. Main surfaces and
message blocks do not use decorative radius or drop shadows.

Semantic state colors remain distinct from the single visual accent. They are
used only for actual state and are never placed as decorative dots throughout
the UI.

## Shell and sidebar

### Wide layout

- Use a sidebar around 224 px wide.
- Keep `Ru` identity simple and remove the `Live2D Companion` badge from the
  branding row.
- Render `Đoạn chat mới` as a text action with a plus icon, not as a card.
- Render navigation as flat rows. The active item uses a 2 px accent rule and
  stronger text, not a rounded filled background.
- Render recent sessions as flat rows with a quiet hover tint. Keep the
  existing selection and delete handlers. The delete affordance remains
  visible on hover as it is today.
- Replace the bottom three service badges with one quiet status row showing the
  existing overall status text and one semantic dot. Its tooltip exposes the
  current backend, worker, and VTS details. Existing service state bindings
  stay intact without displaying three separate badge containers.

### Compact and narrow layouts

- Keep the existing policy boundaries: `Wide >= 1280`, `Compact 900-1279`,
  `Narrow < 900` device-independent pixels.
- Compact keeps labels and recent titles while reducing padding.
- Narrow uses the existing 72 px rail. Navigation labels and recent heading
  are hidden, but all icons retain tooltips and automation names.
- Narrow recent sessions remain selectable as compact chat items. The full
  title is available in a tooltip. Deletion remains available after returning
  to a wider tier.
- The status row collapses to a semantic dot with tooltip text in the narrow
  rail. It must not cause horizontal clipping.
- The existing lifetime-only sidebar width restoration and tier transition
  policy remain unchanged.

## Chat layout

The Chat view keeps its existing vertical-only scroll host and item source. The
main reading column is centered at approximately 760-820 px on wide layouts and
coalesces to the available content width on smaller layouts.

### Assistant messages

- Remove the avatar badge and message card.
- Render the assistant as a plain reading block with 16 px body text and
  generous line height.
- Render a small `Ru` label in the accent color above each assistant block. It
  is an inline content label, not an avatar or status badge.
- Preserve Markdown, error binding, text wrapping, and streaming updates.
- Markdown headings use the display face. Code blocks use the existing
  monospace family on a light neutral surface, without nested card decoration.

### User messages

- Remove the rounded user bubble.
- Render the user prompt as a compact right-aligned note with a maximum width
  around 560 px.
- Distinguish it through alignment, weight, and a quiet paper tint without an
  enclosing border or large filled bubble.
- Preserve message content, selection behavior, and the current no-emoji chat
  policy.

### Welcome state

Replace the centered hero card and avatar emblem with an intro block in the
reading column:

```text
Ru
Chào anh. Hôm nay mình bắt đầu từ đâu?

Hỏi về một việc đang làm          Nói cho Ru nghe
```

The two existing suggestion handlers remain. Suggestions become flat text
actions with small icons and an underline or accent change on hover. No new
technical metadata, version label, decorative strip, emoji, or marketing copy
is added.

### Composer

- Keep the composer at the bottom of the reading column rather than as a
  floating capsule spanning the full content area.
- Use a flat surface with one top divider and an accent focus rule along the
  lower edge.
- Keep the existing microphone and send buttons, click handlers, tooltips,
  hit targets, and keyboard behavior.
- Keep the TextBox transparent inside the composer. Use the shorter placeholder
  `Viết cho Ru...`.
- Keep Enter to send and Shift+Enter for a newline.

## Interaction and state behavior

- Navigation hover and active states use color, weight, and a small rule. No
  perpetual animation, glow, or magnetic interaction is introduced.
- Keep one short opacity and vertical-offset transition for content-view entry;
  do not animate each message. Resizing has no layout animation.
- The existing typing state becomes one restrained caret at the response
  position. Under reduced motion it remains a static caret.
- Empty, generating, success, and error states remain in the same reading
  column. Errors are inline and readable instead of a new alert card.
- Focus indicators remain visible in both normal and narrow layouts. Color is
  not the sole carrier of state.
- Voice playback, subtitles, VTS actions, and control metadata remain outside
  Chat text and Chat history.

## Technical boundaries and ownership

| Area | Planned responsibility |
| --- | --- |
| `gui-winui3/App.xaml` | Shared palette, typography, radius, divider, control, and action resources |
| `gui-winui3/MainWindow.xaml` | Shell composition, sidebar presentation, status row, and responsive visual states |
| `gui-winui3/MainWindow.xaml.cs` | Only name/reference adjustments required by the visual tree; preserve navigation, session, lifecycle, and status logic |
| `gui-winui3/Views/ChatView.xaml` | Message templates, welcome layout, suggestion actions, composer presentation, and responsive setters |
| `gui-winui3/Views/ChatView.xaml.cs` | No behavior change; only minimal visual hook changes if the new tree requires them |
| `gui-winui3/Services/ResponsiveLayoutPolicy.cs` | Existing breakpoint and sidebar policy remains the source of truth |
| `gui-winui3.Tests` | Extend existing source/XAML contracts and focused tests for the new visual structure |

The data path remains:

```text
existing MainWindow navigation
        -> existing ChatView
        -> existing ChatMessageItem templates
        -> existing WebSocket, session, STT, and send handlers
```

The redesign changes rendering only. It must not recreate an `ItemsSource`,
call `ApiService` during layout, restart timers, touch the worker, or change
request cancellation and ordering.

## Verification and acceptance

Automated verification will run sequentially on Windows:

1. Focused WinUI tests for source/XAML structure, responsive states, tooltip and
   automation names, no-horizontal-scroll behavior, and preserved Chat hooks.
2. The complete `gui-winui3.Tests` project.
3. WinUI Release build.
4. `git diff --check` for the scoped changes.

Manual visual acceptance remains separate from automated evidence. At
`1440x900`, `1024x768`, `800x600`, and `640x600`, verify:

- Sidebar navigation, active state, recent session selection, delete hover,
  and narrow tooltips.
- Empty Chat, an existing long conversation, Markdown, code blocks, streaming,
  inline error state, microphone, send, Enter, and Shift+Enter.
- No horizontal scrollbar, clipped text, or focus loss during resize.
- Existing service status updates still appear in the quiet status row.
- No change in request latency, audio playback, subtitle behavior, or VTS
  control. Live desktop and audio acceptance remains user-run.

## Documentation follow-up

After implementation, update `docs/operations/runtime-behavior.md` with the
supported shell tiers and the distinction between automated layout checks and
manual visual acceptance. Review the current documentation index for stale
responsive-layout references at that time. No current document is retired by
this design alone.
