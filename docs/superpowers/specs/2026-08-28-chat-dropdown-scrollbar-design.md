# Chat Dropdown and Full-Width Scrollbar Design

**Date:** 2026-08-28  
**Status:** Approved for implementation  
**Scope:** `gui-winui3/Views/ChatView.xaml`, its focused source-contract tests, and current UI documentation

## Problem

The Chat composer already exposes the response-mode control inside the input
surface, but its mode flyout still inherits the generic WinUI `Flyout` and
`RadioButton` presentation. That makes the small control feel visually
disconnected from Ru's editorial surface palette and gives the flyout more
weight than the two choices require. Agent settings remain a separate action
and must not return to this mode flyout.

When the transcript grows, `ChatScrollHost` is currently inside a centered
820 px `ReadingColumn`. The native vertical scrollbar therefore appears at the
right edge of that centered column instead of at the right edge of the whole
Chat viewport. The messages must remain centered while the scroll viewport and
its scrollbar use the full available width.

## Decision

### Mode flyout

Keep the existing `Flyout` and `RadioButton` semantics, names, click handlers,
and automation IDs. Scope a `FlyoutPresenter` style to `AgentModeFlyout` using
the existing `RuSurfaceBrush`, `RuLineBrush`, `RuInkBrush`, `RuMutedBrush`,
and `RuAccentSoftBrush` tokens. The presenter is a compact surface with a
single-pixel line, the existing control corner radius, and restrained padding.

The content contains only the `Trò chuyện` and `Coding Agent` radio options.
Each option stretches to the flyout width, uses the UI font and a 13 px label,
has a 38 px minimum target, and receives a muted accent-soft hover/pressed
surface through scoped `RadioButton` theme resources. The selected radio glyph
continues to use Ru's accent color. No title, workspace field, permission
preset, or Agent activity status is added to this flyout.

### Full-width Chat viewport

Change `ReadingColumn` to stretch across the Chat view. `ChatScrollHost` keeps
vertical-only native scrolling and fills that row, so its scrollbar is laid out
at the far right edge of the available Chat viewport. `ChatContentGrid` and
`ChatListView` retain the existing calculated reading width and centered
alignment. `WelcomeHeroPanel`, `InputContainer`, and `InputBorder` retain that
same calculated width, so the empty state and composer do not visually widen.

Scope a light native scrollbar resource override to `ChatScrollHost`: a
transparent track, an 8 px thumb using the Ru line token, and the Ru accent
token on pointer-over/pressed states. This removes the heavy default strip
without replacing the accessible native scrollbar or changing keyboard/wheel
behavior. Horizontal scrolling remains disabled.

## Boundaries and invariants

- No Chat WebSocket, Agent API/SSE, message collection, session, voice, or
  transport behavior changes.
- Existing automation IDs and event handlers remain unchanged.
- Agent settings stay in `AgentSettingsFlyout`; the mode flyout remains limited
  to the two response modes.
- The transcript column remains at most 820 px and centered at wide widths.
- The scrollbar is at the right edge of the Chat viewport at wide, compact, and
  narrow widths; no horizontal scrollbar is introduced.
- Selecting Agent while idle remains layout-neutral: the Agent inline panel is
  still collapsed until real activity exists.

## Verification and acceptance

Add source/XAML contracts for the scoped presenter and radio resources, the
stretching `ReadingColumn`, the centered fixed-width content grid, and the
absence of a `ReadingColumn.Width` assignment in `ApplyInputWidth`. Preserve
the existing mode/settings separation assertions and add a contract that the
outer Chat scroll host is vertical-only.

Run the focused contract test, the complete WinUI test project, Debug builds
for the app and harness, the documentation test suite, and `git diff --check`.
Publish the Release self-contained app only after confirming no `RuApp.exe` is
running. Human acceptance remains separate: open the existing shortcut, open
the mode flyout, verify its two compact choices, then grow a transcript and
confirm the scrollbar sits against the far right Chat edge while messages stay
centered.

## Non-goals

- No custom scroll implementation, overlay canvas scrollbar, or third-party UI
  dependency.
- No global theme rewrite or visual redesign of other pages.
- No automatic opening or control of the user's desktop application.
