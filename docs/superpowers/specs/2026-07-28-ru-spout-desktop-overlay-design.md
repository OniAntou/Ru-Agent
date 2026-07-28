# Ru Spout2 desktop overlay

## Goal

Show Ru's existing VTube Studio avatar above ordinary Windows desktop apps with
real alpha transparency. The overlay is local-only, keeps VTube Studio as the
single Live2D renderer, and does not change the current `VoiceVolume`,
`LLM_Sway`, or hotkey integration.

## Decision

Build a small project-local Windows receiver named `RuDesktopOverlay`. It
receives VTube Studio's `VTubeStudioSpout` sender through the supported
SpoutLibrary API, then presents its alpha pixels in a borderless Win32 layered
window. The receiver, not VTube Studio's normal window, is made topmost and
optionally mouse click-through.

This replaces the rejected direct-window color-key approach. VTube Studio's
ordinary GPU-rendered window cannot be made transparent through an external
Win32 color key; its documented transparent outputs are capture paths such as
Spout2.

## Architecture

`VTube Studio` produces the existing transparent Spout2 stream. The local
receiver opens the sender named `VTubeStudioSpout`, receives a BGRA frame with
alpha through SpoutLibrary, and presents it with `UpdateLayeredWindow` in a
transparent, always-on-top Win32 window.

```text
VTube Studio model + Ru controls
        |
        | Spout2 texture with alpha: VTubeStudioSpout
        v
RuDesktopOverlay.exe
        |
        +-- transparent, topmost desktop window
        +-- optional click-through mode
```

The receiver has no VTube Studio API token, model data, microphone data, or
LLM access. Closing it leaves VTube Studio and Ru unchanged.

## User-facing behavior

- On launch, the overlay connects to `VTubeStudioSpout` and starts non
  click-through so the user can move/resize it.
- A visible status/control surface provides: sender connected/disconnected,
  topmost toggle, click-through toggle, and close.
- When click-through is enabled, the overlay does not intercept pointer input;
  keyboard focus stays with the active desktop app.
- If the sender is absent, the overlay shows a clear local status instead of a
  blank/green model. It retries at a bounded interval and reconnects when VTS
  starts sending.
- The overlay uses the sender's alpha directly: no green screen or chroma key.

## Technical boundaries

- Target: Windows desktop use over normal, windowed applications. Exclusive
  fullscreen games are explicitly out of scope for this first version.
- Use the upstream Spout2 SDK's native SpoutLibrary receiver path rather than
  an outdated managed wrapper. It provides a supported CPU-frame fallback that
  is appropriate for the initial ordinary-desktop scope, without a separate
  overlay product or virtual-camera driver.
- VTube Studio must have its Spout2 sender enabled and its Color Picker
  background configured for transparency in capture. The receiver validates
  the sender name rather than attempting to manipulate VTube Studio's window.
- Preserve alpha through the receive and layered-window path; do not color-key
  pixels. This avoids removing green details from an avatar.

## Failure handling and cleanup

- Startup validates that a compatible Spout2 sender is present; connection
  errors remain visible and recoverable without terminating VTube Studio.
- Sender resolution changes recreate receiver textures safely.
- Closing the receiver destroys only its own window and graphics resources.
- Click-through is opt-in and is cleared on restart, so an inaccessible window
  cannot be left behind.

## Verification

1. Unit tests cover sender-name selection, command-line/control state, and
   click-through Win32 style calculation.
2. A build check compiles the receiver against the selected Spout2 SDK.
3. Live validation with VTube Studio confirms that `VTubeStudioSpout` connects,
   the model appears with no green background over a normal desktop app, and
   Ru voice lip-sync/hotkeys/sway remain visible.
4. Toggle click-through only after the visual result is approved, then confirm
   that pointer input reaches the app behind the overlay.
5. Close/restart the receiver and confirm VTube Studio and Ru keep running.

## Out of scope

- OBS scenes, recording, streaming, NDI, virtual-webcam output, game-overlay
  anti-cheat compatibility, and changing VTube Studio model mappings.
