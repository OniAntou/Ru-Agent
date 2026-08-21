# ASCII Meteor Empty Chat Background

**Date:** 2026-08-21
**Status:** Superseded 2026-08-21
**Scope:** A restrained animated ASCII meteor field for the empty Chat view

This record documents the rejected ASCII meteor implementation. The current
approved direction is the vector-based cinematic shower in
2026-08-21-meteor-shower-empty-chat-design.md.

## Decision summary

The empty Chat state will gain one coherent, warm ASCII meteor streak that
supports Ru's quiet signature without turning the shell into a visual-effects
demo. The streak appears only while the current Chat view has no messages. The
first sent message changes the view to the normal conversation state, stops the
animation, removes the streak, and leaves no background activity running.

The effect is text-only and uses a small `Canvas`-based control. It adds no
bitmap asset, gradient, third-party package, backend call, polling path, or
conversation content.

## Goals

- Give the empty Chat state one recognizable Ru-specific visual detail.
- Keep the effect sparse enough that the welcome heading and composer remain
  the primary focus.
- Use ASCII characters only for the meteor marks: `.`, `-`, and `*`.
- Preserve the existing Light/Dark theme system and responsive layout.
- Stop and clean up the effect as soon as the first message exists.
- Keep all Chat, WebSocket, STT, voice, subtitle, VTS, memory, and lifecycle
  behavior unchanged.

## Non-goals

- No starfield, particle field, glow, bloom, gradient, blur, or decorative
  background image.
- No emoji or Unicode decorative glyphs.
- No new settings or user preference for animation density.
- No animation while a conversation contains messages.
- No changes to the welcome copy, suggestions, composer behavior, or message
  templates.
- No automatic launching or control of RuApp or VTube Studio.

## Visual direction

### Meteor mark

The meteor is rendered as one short, contiguous ASCII text mark:

~~~text
..---*
~~~

The final `*` is the head and the preceding dots/dashes are the tail. The whole
mark moves as one object diagonally from the upper-left toward the lower-right,
so the eye reads a single travelling streak rather than scattered text. It has
no glow and uses the existing Ru warm accent brush at restrained opacity. The
field is behind the welcome content and never captures input.

### Density and timing

- Exactly one meteor may exist at a time; there is no random multi-meteor
  spawning.
- A meteor crosses its short path in approximately 1.5–2 seconds, making the
  direction clearly visible without becoming a constant distraction.
- After each pass, the field rests for approximately 7–10 seconds before the
  next pass.
- The field uses a UI-thread timer appropriate for one text element; it must
  not run a full-screen per-pixel render loop.
- The field is clipped to its own bounds and does not participate in layout
  measurement for the welcome content.

### Theme and responsive behavior

- The mark uses the existing `RuAccentBrush`; no new color family is created.
- Light and Dark inherit their existing accent contrast from the shared theme
  resources.
- The field fills only the Chat reading area, never the sidebar or composer.
- Narrow layouts shorten or reposition the fixed travel lane when needed; they
  do not move, resize, or obscure the welcome heading and prompt.

## Architecture and state flow

### `AsciiMeteorField`

Keep a focused control under `gui-winui3/Controls/` that derives from `Canvas`.
The control owns:

- One `TextBlock` meteor element at most.
- A fixed travel lane, progress updates, rest timing, and path cleanup.
- Its active/inactive state.
- Timer start/stop and unload/dispose cleanup.

The control must expose a minimal state surface, for example an active boolean
or `SetActive(bool)`. It must not know about `Messages`, backend services, or
Chat commands.

### `ChatView`

Place the control in `ChatView.xaml` behind `WelcomeHeroPanel` and inside the
existing reading-column bounds. Extend the existing `UpdateViewMode()` path:

~~~text
Messages.CollectionChanged
        -> UpdateViewMode()
        -> WelcomeHeroPanel / ChatScrollHost visibility
        -> AsciiMeteorField active state
~~~

The empty state (`Messages.Count == 0`) activates the field. Any non-empty state
deactivates it, stops its timer, removes the meteor child, and collapses its
surface. Clearing a conversation returns to the empty state and starts a fresh
field with progress reset to the beginning of the lane.

The control must remain `IsHitTestVisible="False"`. No send, typing, STT, or
WebSocket handler may be changed to support the effect.

## Motion and lifecycle rules

- Start only after the control is loaded and the Chat state is empty.
- Stop on `Unloaded`, `Dispose`, or transition to a non-empty Chat state.
- Stop before clearing children so no timer tick can update a removed element.
- Do not keep a timer alive when the field is collapsed.
- Resize handling must keep the fixed lane inside the field bounds without
  changing the reading column's measured size.
- A failed or unavailable animation tick must fail visually only; it must not
  affect Chat state or surface an error message.

## Testing and acceptance

Automated contracts must prove:

1. `ChatView.xaml` places the field behind the welcome content and keeps it
   non-interactive.
2. `ChatView.xaml.cs` drives the field from the existing message-count state
   path rather than from typing or backend events.
3. The field uses one contiguous ASCII meteor mark and never creates more than
   one active text element.
4. Start/stop cleanup exists for empty-to-chat, unload, and dispose paths.
5. No backend, WebSocket, STT, voice, subtitle, VTS, memory, or composer
   behavior is introduced.
6. Existing no-emoji, responsive, theme, Chat, and shell contracts remain
   green.
7. The full WinUI test project, Release build, and `git diff --check` pass.

Manual acceptance remains user-run in Light and Dark at 1440×900, 1024×768,
and 800×600:

- Empty Chat shows an occasional contiguous `..---*` streak whose direction is
  clearly visible but whose opacity remains restrained.
- The animation stays behind the welcome content and does not compete with the
  heading, suggestions, or composer.
- Sending the first message removes the field immediately and permanently for
  that conversation state.
- Returning to a genuinely empty Chat starts a fresh, clean field.
- No clipping, layout shift, input interference, visible timer artifacts,
  random scatter, or generic particle/starfield aesthetic appears.

## Explicit preserved boundaries

The effect is presentation-only. It does not alter:

- `Messages` contents or message ordering.
- WebSocket/session lifecycle or request cancellation.
- STT, voice playback, subtitle rendering, or VTube Studio control.
- Theme preference persistence or responsive breakpoints.
- Chat copy, internal marker privacy, or no-emoji policy.
