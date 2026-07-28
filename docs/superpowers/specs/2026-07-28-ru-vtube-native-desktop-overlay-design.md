# Ru native VTube Studio desktop overlay (rejected after live validation)

> **Status (2026-07-28):** This direct-window design was tested against the
> active VTube Studio window and rejected. The VTube Studio window accepted
> the Win32 layered/color-key state and its background pixels were confirmed as
> `#00FF00`, but the pixels still rendered opaque. A control window using the
> same Win32 calls became transparent, so this is a VTube Studio rendering-path
> limitation rather than a color-picker or Windows configuration error. The
> VTube Studio window was restored to its original style. Do not use this
> design for a desktop overlay.

## Original goal

Show the existing VTube Studio window as a local transparent, always-on-top,
click-through desktop overlay without OBS, Spout2, a driver, or a third-party
overlay executable.

## Rejected approach

Add one project-local PowerShell utility that invokes documented Win32 window
APIs for the already-running `VTube Studio` process. On enable, it finds the
main window, applies `WS_EX_LAYERED` and `WS_EX_TRANSPARENT`, makes the window
topmost without activating it, and makes the selected Color Picker background
color transparent. On disable, it removes the added extended-style flags and
returns the window to the non-topmost group.

The utility does not capture or duplicate the avatar. VTube Studio remains the
only renderer, so Ru's existing voice lip-sync, catalog-driven hotkeys, and
`LLM_Sway` continue to affect the same displayed model.

## Original user setup

Before enabling the overlay, the user selects VTube Studio's **Color Picker
Background** and chooses a chroma color that does not appear in the avatar
(default: pure green `#00FF00`). The script uses that same color as its Win32
color key. Any model pixel with that exact color becomes transparent, so the
color can be changed with a script argument if the avatar contains green.

The script initially enables the overlay without click-through so the user can
check position and scale. A second explicit invocation enables click-through.
The disable command is always available and does not stop VTube Studio or Ru.

## Original script interface

`scripts\set_vtube_desktop_overlay.ps1` provides:

```powershell
# Display on top with a green background made transparent.
.\scripts\set_vtube_desktop_overlay.ps1 -Enable -ColorKey 00FF00

# Pass mouse input to the desktop beneath the avatar.
.\scripts\set_vtube_desktop_overlay.ps1 -Enable -ClickThrough -ColorKey 00FF00

# Return the VTube Studio window to ordinary behavior.
.\scripts\set_vtube_desktop_overlay.ps1 -Disable
```

It refuses to act when there is not exactly one VTube Studio main window. It
reports the selected process ID, window handle, original/current style values,
and any Win32 error. It stores the original style values in a small local state
file under `%LOCALAPPDATA%\Ru\` so disable can restore the same window after a
separate PowerShell invocation. No token, API credential, or model data is
stored.

## Why it cannot meet the goal

- VTube Studio supports transparency in capture outputs (Spout2, virtual
  webcam, NDI, and compatible capture paths), not as alpha in its ordinary
  desktop window. An external overlay receiver must consume one of those
  outputs.
- Directly applying a Win32 color key to the VTube Studio window does not make
  its GPU-rendered background transparent, even though the API reports success.
- The VTube Studio window must remain open and must not be minimized; closing
  it removes the overlay naturally.
- Fullscreen-exclusive games may draw above ordinary desktop overlays; use
  borderless/windowed mode when an overlay is required over a game.
- `-Disable` restores the normal non-topmost, mouse-interactive window state.

## Failed acceptance criteria

1. With VTube Studio open and a green Color Picker background, `-Enable` shows
   only the avatar over ordinary desktop windows.
2. `-ClickThrough` sends mouse input to the application behind the avatar.
3. Ru's voice, actions, and sway remain visually active in the overlay.
4. `-Disable` removes color-key transparency, topmost behavior, and
   click-through without closing VTube Studio or Ru.
