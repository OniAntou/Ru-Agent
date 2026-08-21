# Ru Mineral Paper Palette Refinement

**Date:** 2026-08-21  
**Status:** Implemented
**Scope:** Tone down the shared light palette after visual feedback that the
current shell and Settings surface feels too bright

## Decision summary

Keep the current editorial reading-room layout, typography, navigation, and
light WinUI theme. Recalibrate only the shared mineral palette so the canvas,
sidebar, cards, dividers, and subtle action fills have less luminance and less
white-on-white contrast. Primary ink, semantic status colors, behavior, and
responsive layout remain unchanged.

## Design read

This is a redesign-preserve pass for a personal desktop companion. The target
language is calm editorial utility: mineral paper rather than a white sheet,
with quiet hierarchy and one restrained red-earth accent.

Design dials: variance 5, motion 3, density 2. No new decoration or motion is
introduced.

## Token changes

The values below replace the current shared values in `gui-winui3/App.xaml`:

| Token | Current | Proposed | Purpose |
| --- | --- | --- | --- |
| `RuCanvasColor` | `#F3F4F0` | `#E7E9E3` | Main content canvas |
| `RuSidebarColor` | `#E9ECE6` | `#DCE1D9` | Sidebar separation |
| `RuSurfaceColor` | `#FBFCF8` | `#F0F2EC` | Cards and composer |
| `RuInkColor` | `#20231F` | `#20231F` | Preserve primary text contrast |
| `RuMutedColor` | `#687067` | `#5F685F` | Slightly stronger secondary text |
| `RuLineColor` | `#D9DDD5` | `#C9CEC5` | Dividers and borders |
| `RuAccentColor` | `#A84B38` | `#A84B38` | Preserve Ru accent |
| `RuAccentSoftColor` | `#F3E5E0` | `#EEDFD9` | Less bright action/selection fill |

`RuSuccessColor`, `RuWarningColor`, and `RuDangerColor` remain unchanged.
Legacy `App*Brush` aliases continue to resolve through the shared Ru tokens.

## Scope boundaries

- Change only shared palette values and the matching palette contract tests.
- Keep `RequestedTheme="Light"`; default WinUI controls must remain readable.
- Do not modify layout, spacing, typography, content, navigation, settings
  behavior, backend calls, voice/VTS behavior, or persisted settings.
- Do not add a theme selector or a new user preference.
- Do not launch or control RuApp or VTube Studio automatically.

## Acceptance criteria

Automated checks must prove that:

1. The proposed token values are present in `App.xaml`.
2. The Light application theme remains explicit.
3. Existing editorial, no-emoji, responsive, and Settings source contracts
   remain intact.
4. The complete WinUI test project passes.
5. The Release build completes successfully.

Manual visual acceptance remains user-run. At 1440x900 and 1024x768, the
canvas and cards should read as a muted mineral gray-green rather than white;
labels, controls, dividers, focus states, and accent actions must remain easy
to distinguish. No horizontal clipping or behavior change is expected.

## Documentation

After implementation, update the runtime behavior note if its palette
description no longer matches the shipped values. Review nearby design specs
for stale token tables; do not retire the original reading-room spec because
this is a refinement of its shared palette, not a change to its layout
decision.
