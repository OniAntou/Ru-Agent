# Ru Signature Polish

**Date:** 2026-08-21
**Status:** Implemented 2026-08-21
**Scope:** A visual polish pass for the existing Light/Dark Ru WinUI 3 shell and Chat view

**Visual follow-up:** The initially implemented welcome ambient wash was removed
after visual inspection because its hard-edged rectangle read as an unintended
block in Dark mode. The signature now relies on typography, spacing, accent
rules, and focus state without a welcome wash layer.

## Decision summary

Ru will become more distinctive through composition, typography, hierarchy, and
small intentional interactions. The polish keeps the approved mineral palette,
subtle six-pixel rounding, Light/Dark preference, responsive shell, and existing
runtime behavior. It does not introduce a generic AI dashboard aesthetic or a
new visual-effects layer.

The visual identity is called “Ru signature”: a stronger wordmark, an editorial
welcome composition, restrained red-earth accents, quieter status treatment,
and motion that communicates state rather than decoration.

## Anti-AI-slop guardrails

The implementation must not add:

- Card grids, dashboard tiles, repeated floating panels, or decorative badges.
- Pill-shaped controls, oversized rounded containers, or nested card-on-card
  surfaces.
- Purple-blue gradients, neon accents, glassmorphism, fake depth, drop-shadow
  stacks, or a full-screen hero treatment.
- Decorative typing dots, random pulsing status lights, fake activity counts,
  marketing copy, or invented technical metadata.
- Unicode emoji, ASCII emoticons, voice/VTS metadata, or internal control codes
  in Chat content.
- Per-message entrance animations, parallax, cursor gimmicks, or motion that
  delays reading or sending.

“Wow” must come from editorial contrast, asymmetric spacing, a single warm
accent, clear state changes, and a recognizable Ru mark. Do not add a welcome
ambient wash: a flat low-opacity rectangle becomes visual noise, especially in
Dark mode.

## Goals

- Make Ru recognizable at a glance without adding a mascot, illustration, or
  new bitmap asset.
- Make the empty Chat state feel authored rather than template-generated.
- Give assistant and user messages a signature reading rhythm.
- Make the composer feel like the primary interaction surface.
- Add short, purposeful feedback for navigation, streaming, theme switching,
  focus, and reduced-motion users.
- Preserve all current Light/Dark behavior and responsive tiers.
- Preserve Chat, backend, WebSocket, STT, voice, subtitle, VTS, memory, and
  process behavior.

## Non-goals

- No backend route, settings DTO, model, prompt, history, memory, voice, or VTS
  changes.
- No new user preference beyond the existing local Light/Dark preference.
- No new image generation, bitmap, font package, or third-party UI library.
- No redesign of retained page information architecture.
- No automatic launching or control of RuApp or VTube Studio.

## Visual direction

### Ru identity

The title bar becomes a quiet signature:

- Ru uses the existing Cambria display face at a slightly stronger scale.
- A one-pixel red-earth rule or compact vector mark anchors the wordmark.
- “conversational runtime” remains secondary and small.
- No badge, capsule, “AI companion” slogan, or product-status marketing line.

The mark is built from existing XAML text/shapes so it remains crisp in both
themes and does not introduce an asset pipeline.

### Welcome state

The welcome composition becomes an editorial starting point:

- A small eyebrow reads RU / CONVERSATIONAL RUNTIME.
- The existing Vietnamese welcome line remains the primary heading.
- Suggestion actions remain flat text actions with icons and a short underline or
  accent response on hover.
- No rectangular ambient layer sits behind the welcome state.
- The composition stays left-anchored within the existing reading column and
  retains the existing suggestion handlers.

### Reading rhythm

- Assistant blocks keep the plain document surface and gain a small Ru label
  plus a restrained left accent rule.
- User prompts keep right alignment and gain a quiet right-side rule instead of
  a filled bubble.
- Streaming uses one restrained caret/active rule at the response position.
- Markdown, code blocks, errors, selection, wrapping, and existing content remain
  unchanged.
- The no-emoji Chat contract remains unchanged.

### Composer

- Keep the existing 6 px shared rounding and reading-column placement.
- Strengthen the composer as the visual endpoint with a slightly deeper
  theme-aware surface, a clear focus rule, and a more deliberate send-button
  contrast.
- Keep microphone/send buttons, tooltips, keyboard behavior, Enter, and
  Shift+Enter unchanged.
- Do not turn the composer into a capsule or floating glass panel.

### Navigation and status

- Active navigation uses ink weight, a red-earth rule, and a brief 120–160 ms
  transition; no permanent glow or scale bounce.
- Hover changes color and rule length only.
- The existing overall status text and semantic dot remain the source of truth.
  No decorative status lights or additional service badges are introduced.
- Theme toggle keeps its explicit action label, icon, tooltip, automation name,
  local persistence, and narrow icon-only behavior.

## Interaction and motion

- Navigation entry and welcome entry use the existing content transition with a
  small opacity/vertical offset adjustment only.
- The active rule may animate once when navigation changes.
- Assistant stream entry may reveal the label/rule before content, but must not
  animate each token or delay response rendering.
- Theme icon changes with a short crossfade or glyph transition that does not
  block the click.
- Focus states remain visible through both color and shape/rule changes.
- Under reduced motion, all optional transitions become immediate/static.
- No animation may call the backend, recreate an ItemsSource, restart polling,
  or affect voice/subtitle/VTS timing.

## Technical ownership

| Area | Responsibility |
| --- | --- |
| gui-winui3/App.xaml | Add only the theme-aware signature/focus tokens required by the polish |
| gui-winui3/MainWindow.xaml | Wordmark, navigation/status presentation, and responsive setters |
| gui-winui3/MainWindow.xaml.cs | Minimal visual state updates; preserve navigation, theme persistence, and lifecycle |
| gui-winui3/Views/ChatView.xaml | Welcome composition, message markers, composer emphasis, and restrained transitions |
| gui-winui3/Views/ChatView.xaml.cs | No behavior changes; retain existing suggestion/send/STT/stream hooks |
| gui-winui3.Tests/EditorialReadingRoomContractTests.cs | Source/XAML contracts for signature structure and anti-slop boundaries |
| gui-winui3.Tests/ChatViewNoEmojiContractTests.cs | Existing no-emoji and content privacy contracts remain green |
| docs/operations/runtime-behavior.md | Record the visual-only polish boundary and manual acceptance |
| docs/superpowers/specs/2026-08-21-ru-signature-polish-design.md | This approved design and its anti-slop constraints |

The data path remains unchanged:

~~~text
existing MainWindow navigation
        -> existing ChatView
        -> existing ChatMessageItem templates
        -> existing WebSocket, session, STT, voice, subtitle, and VTS paths

local UiThemePreferenceStore
        -> Light/Dark appearance only
~~~

## Testing and acceptance

Automated tests must prove:

1. The new wordmark/eyebrow/signature markers exist in the intended XAML
   locations.
2. The welcome and composer retain existing handlers and reading-column bounds.
3. No new emoji, pill radius, large-card radius, glow, fake status copy, or
   voice/VTS metadata is introduced.
4. Responsive Narrow setters preserve labels/tooltips/automation names.
5. Existing theme preference, no-emoji, responsive, Chat, and shell contracts
   remain intact.
6. The full WinUI test project and Release build pass sequentially.
7. git diff --check is clean.

Manual acceptance remains user-run at 1440x900, 1024x768, 800x600, and 640x600
in both Light and Dark:

- The Ru wordmark is recognizable but quiet.
- The welcome state feels authored without looking like a landing page.
- The single warm accent guides attention without dominating the canvas.
- Message hierarchy and composer focus are immediately legible.
- Hover/focus/theme transitions feel deliberate and short.
- No clipping, horizontal scroll, excessive rounded surfaces, visual noise, or
  generic AI-dashboard patterns appear.
- Chat, sending, STT, voice playback, subtitles, VTS, and backend status behave
  exactly as before.

## Documentation follow-up

Update the runtime behavior note after implementation if it still describes the
current visual layer as only a static reading room. Keep the existing Light/Dark
design spec as the palette and preference source of truth, and keep this
document as the signature-polish boundary. Historical implementation plans are
not deleted solely because this visual pass extends them.
