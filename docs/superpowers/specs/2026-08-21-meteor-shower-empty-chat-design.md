# Cinematic Meteor Shower Empty Chat

**Date:** 2026-08-21
**Status:** Implemented 2026-08-21
**Supersedes:** 2026-08-21-ascii-meteor-empty-chat-design.md
**Scope:** A choreographed, vector-native meteor shower for the empty Chat view

## Decision summary

The empty Chat state will use a short cinematic meteor-shower burst instead of
ASCII text or a single moving text mark. Five vector meteors enter in a fixed
sequence, with staggered timing, varied depth, and a shared diagonal direction.
The burst lasts roughly 2.5 seconds, then the reading area remains quiet for
10 seconds before the sequence repeats.

The visual is deliberately authored rather than randomly generated. Each meteor
has a crisp geometric head, a three-segment tapered tail, and a short
afterimage created by opacity steps. The depth variation creates motion and
personality without a generic particle-field look. The effect remains
presentation-only and disappears immediately when the first Chat message exists.

## Goals

- Make the empty Chat state feel cinematic and distinctive.
- Make the meteor shower motion unmistakable without turning it into a
  screensaver.
- Use existing Ru theme resources so Light and Dark remain coherent.
- Keep the central welcome heading, suggestions, and composer readable.
- Keep the implementation bounded, deterministic, disposable, and easy to tune.
- Remove the old ASCII/TextBlock visual language completely.

## Non-goals

- No ASCII meteor marks, emoji, Unicode decoration, or text-based animation.
- No random particle spawning, starfield, full-screen shader, or wallpaper.
- No bitmap, video, external SVG, third-party animation package, or sound.
- No glow-heavy neon treatment, rainbow palette, or decorative gradients.
- No new user setting for shower density or animation speed.
- No animation while the current conversation contains messages.
- No changes to Chat copy, send behavior, WebSocket/session logic, STT, voice,
  subtitles, VTube Studio, memory, or backend services.
- No automatic launching or control of RuApp or VTube Studio.

## Visual direction

### Meteor construction

Every meteor is a reusable vector group containing:

1. A small four-point head shape with the active Ru accent color.
2. A main tail segment using the accent color at medium opacity.
3. Two shorter trailing segments using lower opacity.
4. A group transform for position, rotation, and depth scale.

The head is sharp rather than a soft glowing dot. The tail segments are
separate lengths so the shower reads as designed motion blur without a blur
effect. Near meteors use larger geometry, a longer tail, and higher opacity.
Far meteors use smaller geometry, a shorter tail, and lower opacity.

### Fixed burst choreography

The sequence uses five fixed meteor specifications. Coordinates are normalized
to the field bounds so the composition adapts to window size:

| Meteor | Delay | Depth | Start position | End position | Travel | Scale | Opacity |
|---|---:|---|---|---|---:|---:|---:|
| 1 | 0 ms | Far | (-0.16, 0.12) | (0.42, 0.29) | 1.10 s | 0.72 | 0.14 |
| 2 | 110 ms | Near | (-0.12, 0.21) | (0.88, 0.46) | 0.86 s | 1.00 | 0.28 |
| 3 | 240 ms | Mid | (0.04, 0.07) | (0.94, 0.32) | 0.98 s | 0.86 | 0.22 |
| 4 | 390 ms | Far | (0.22, 0.25) | (1.06, 0.43) | 1.18 s | 0.68 | 0.12 |
| 5 | 580 ms | Near | (-0.04, 0.16) | (0.98, 0.39) | 0.82 s | 0.92 | 0.26 |

The coordinates keep the shower in the upper reading area and around the
central content rather than directly across the heading. The control clamps
the lane to its own bounds and rebuilds the positions on resize.

Each meteor uses an ease-in/ease-out travel curve with a short final
acceleration as it leaves the frame. The sequence is deterministic: no
per-burst random values are allowed. This makes the visual repeatable and
keeps future tuning in one data table.

### Color and theme

- The meteor head and main tail use the existing RuAccentBrush.
- Far tail segments use RuAccentSoftBrush or RuMutedBrush with bounded opacity.
- Light and Dark inherit their existing accent contrast.
- No new color resource is added.
- The effect never paints a background rectangle or a full-field overlay.

## Density and timing

- Maximum active meteor groups: 5.
- Burst choreography: 5 meteors, staggered from 0 to 580 ms.
- Visible burst window: approximately 2.5 seconds.
- Quiet rest between bursts: 10 seconds.
- Animation tick: approximately 16 ms while the empty Chat field is active.
- No timer runs when the field is collapsed, unloaded, disposed, or in a
  non-empty Chat state.
- The field is clipped to its own bounds and does not change layout measurement.

## Architecture and state flow

### MeteorShowerField

Create gui-winui3/Controls/MeteorShowerField.cs as a focused Canvas control
implementing IDisposable. It owns:

- Five reusable MeteorVisual groups, each containing only native WinUI shapes.
- One immutable MeteorSpec sequence containing the burst choreography.
- A small state machine with Idle, Burst, and Rest phases.
- A high-frequency DispatcherTimer active only while the empty Chat is visible.
- Elapsed-time progress, easing, position updates, and cleanup.
- Theme brush lookup, clipping, resize reset, unload cleanup, and dispose cleanup.

MeteorVisual and MeteorSpec remain private nested types unless a separate testable
domain need appears. This keeps the public surface to IsActive and Dispose and
prevents the control from spreading animation details into ChatView.

The control must not reference Messages, ChatView, ApiService, WebSocket,
microphone, voice, VTS, memory, or any backend/service type.

### ChatView

Replace the old controls:AsciiMeteorField element with:

~~~xml
<controls:MeteorShowerField x:Name="MeteorShowerField"
                            HorizontalAlignment="Stretch"
                            VerticalAlignment="Stretch"
                            IsHitTestVisible="False" />
~~~

Keep it immediately before WelcomeHeroPanel so the welcome content remains
above the effect in the Grid's child order.

Update the existing UpdateViewMode path:

~~~text
Messages.CollectionChanged
        -> UpdateViewMode()
        -> WelcomeHeroPanel / ChatScrollHost visibility
        -> MeteorShowerField.IsActive = !hasMessages
~~~

Dispose the new field from ChatView.Dispose. Do not change the existing
message collection, send handlers, WebSocket handlers, input handlers, or voice
handlers.

## Motion and lifecycle rules

- Start only after MeteorShowerField is loaded and Chat has no messages.
- Start each empty-state session at Idle with a one-second lead-in.
- Transition Idle -> Burst after the lead-in.
- Transition Burst -> Rest after all five specs complete.
- Transition Rest -> Burst after the fixed ten-second rest.
- On the first message, stop the timer before removing all visual groups and
  collapse the field immediately.
- On Unloaded, Dispose, or inactive state, stop the timer, remove every group,
  detach handlers where applicable, and reset phase/progress.
- On SizeChanged while active, update the clip, remove current groups, reset to
  Idle, and restart using the new bounds.
- An unavailable tick must fail visually only and must not alter Chat state or
  surface an error.
- No animation may capture pointer/keyboard input.

## Testing and acceptance

Automated source/XAML contracts must prove:

1. ChatView.xaml hosts MeteorShowerField behind WelcomeHeroPanel and keeps it
   non-interactive.
2. ChatView.xaml.cs drives MeteorShowerField from the existing message-count
   path and disposes it.
3. The control is MeteorShowerField : Canvas, IDisposable and contains one
   fixed five-entry MeteorSpec choreography.
4. Native vector shapes are used for the head/tail; no ASCII TextBlock,
   Random, particle field, gradient, or bitmap asset is introduced.
5. The control contains explicit Idle/Burst/Rest timing and max-five bounds.
6. Timer stop, group removal, Unloaded, resize, and Dispose cleanup exist.
7. No backend, WebSocket, STT, voice, subtitle, VTS, memory, or composer path
   changes are introduced.
8. Existing no-emoji, responsive, theme, Chat, and shell contracts remain
   green.
9. The full WinUI test project, Release build, UI detector, anti-slop scan, and
   git diff --check pass.

Manual acceptance remains user-run in Light and Dark at 1440×900, 1024×768,
and 800×600:

- The empty Chat shows a short, clearly recognizable meteor-shower burst.
- The five streaks feel layered and cinematic rather than like random dots.
- The burst stays behind and around the welcome content.
- Near/far differences are visible without neon or wallpaper treatment.
- The ten-second quiet interval prevents screensaver-like repetition.
- Typing does not start, stop, or distort the shower.
- The first sent message removes every meteor immediately.
- Returning to an empty Chat starts a clean sequence with no stale shapes.
- No clipping, layout shift, input interference, or visible timer artifact occurs.

## Explicit preserved boundaries

The shower is presentation-only. It does not alter:

- Messages contents, ordering, or session selection.
- WebSocket/session lifecycle or request cancellation.
- STT, voice playback, subtitle rendering, or VTube Studio control.
- Theme preference persistence or responsive breakpoints.
- Chat copy, internal marker privacy, or no-emoji policy.
