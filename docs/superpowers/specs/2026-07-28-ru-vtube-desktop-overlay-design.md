# Ru transparent VTube Studio desktop overlay

> **Status (2026-07-28):** Blocked before setup. The official Sprout Gumroad
> page currently reports that the product is not for sale, so no executable was
> downloaded or run. A replacement approach requires a new approval.

## Goal

Show Ru's current VTube Studio model as a transparent, always-on-top, desktop
overlay on Windows while preserving the existing VTube API, `VoiceVolume`
lip-sync, hidden action catalog, and `LLM_Sway` behavior.

## Chosen approach

Use VTube Studio's Windows Spout2 output as the transparent source and Sprout
as the local desktop-overlay receiver. Sprout is a single executable: it does
not require a driver, service, OBS, or a change to the Ru GUI. It will receive
the VTube Studio sender normally named `VTubeStudioSpout` and render it on the
selected monitor.

## Setup flow

1. Download the Sprout executable from its official site into the project-local
   tools area; do not install a system-wide service or driver.
2. In VTube Studio, select a Color Picker background, enable transparent output
   and start Spout2.
3. Start Sprout, choose the VTube Studio sender, select the desired monitor,
   then position and scale the avatar.
4. Enable click-through after positioning so normal desktop input reaches the
   application below the avatar.
5. Leave VTube Studio and Ru running. Ru continues to drive the model through
   its existing API connection; Sprout only displays the resulting image.

## Safety and failure behavior

- If VTube Studio or Spout2 is not running, Sprout shows no avatar and makes
  no change to Ru or the model.
- Closing Sprout removes only the overlay. It does not stop VTube Studio,
  change the model, or modify the VTube Studio API configuration.
- The overlay is local-only; it does not capture the desktop, publish a stream,
  or require OBS.

## Acceptance criteria

1. Ru appears without a rectangular background above normal desktop windows.
2. The overlay can be moved, scaled, assigned to a monitor, and made
   click-through.
3. Ru's lip-sync, hotkeys, and sway remain visible in the overlay.
4. Closing Sprout immediately returns the desktop to its prior behavior.
