# Ru Dark Theme Toggle

**Date:** 2026-08-21  
**Status:** Implemented 2026-08-21  
**Scope:** Persistent light/dark theme switching for the Ru WinUI 3 shell

## Decision summary

Ru will keep the existing muted mineral light palette and add a dark
charcoal-mineral counterpart. A single appearance action in the sidebar
footer will switch between Light and Dark without changing Chat, backend,
voice, VTube Studio, memory, or process behavior.

The selected appearance is a local UI preference. It is stored under the
canonical `%LOCALAPPDATA%\\Ru` directory and is deliberately not added to the
ASP.NET settings contract, model configuration, or backend persistence.

## Goals

- Provide a readable dark theme that feels like Ru's existing mineral/editorial
  language rather than a generic black dashboard.
- Make the switch available from the persistent shell footer on every view.
- Apply the theme immediately to custom Ru surfaces, text, borders, semantic
  states, retained views, and the custom title-bar buttons.
- Restore the selected theme after restarting Ru.
- Preserve the current responsive tiers, navigation, Chat behavior, voice/VTS
  behavior, backend contract, and process lifecycle.
- Keep the UI free of Unicode emoji and ASCII emoticons.

## Non-goals

- No backend `/settings` field or route change.
- No model, generation, memory, history, transcript, voice, subtitle, or VTS
  behavior change.
- No automatic launching or control of RuApp or VTube Studio.
- No system-wide Windows theme synchronization in this slice.
- No redesign of individual retained pages beyond applying shared theme tokens.

## Visual direction

The dark theme is a low-luminance charcoal mineral surface with warm red-earth
actions and soft green semantic states. It must avoid pure black, neon accents,
gradients, glow, glass treatment, and decorative shadows.

| Token | Light | Dark | Use |
| --- | --- | --- | --- |
| `RuCanvasColor` | `#E7E9E3` | `#191C19` | Main content background |
| `RuSidebarColor` | `#DCE1D9` | `#121612` | Sidebar background |
| `RuSurfaceColor` | `#F0F2EC` | `#232923` | Composer and restrained surfaces |
| `RuInkColor` | `#20231F` | `#EEF2EB` | Primary text |
| `RuMutedColor` | `#5F685F` | `#A9B3A7` | Secondary text and metadata |
| `RuLineColor` | `#C9CEC5` | `#39443A` | Dividers and borders |
| `RuAccentColor` | `#A84B38` | `#C76A52` | Active navigation and primary action |
| `RuAccentSoftColor` | `#EEDFD9` | `#3A2924` | Quiet selection/action background |
| `RuSuccessColor` | `#2F6F51` | `#75B28D` | Positive service state |
| `RuWarningColor` | `#9A6C1F` | `#D7A759` | Warning service state |
| `RuDangerColor` | `#A73A3A` | `#E38181` | Error state |

`App.xaml` will retain `RequestedTheme="Light"` as the application fallback.
The shared Ru colors will move into `ResourceDictionary.ThemeDictionaries` for
`Light` and `Dark`. Global brush resources will reference those colors through
`ThemeResource`, so existing views that use `StaticResource Ru*Brush` or legacy
`App*Brush` aliases receive the active palette without a view-by-view rewrite.

## Theme control

The toggle will sit in the sidebar footer between the divider and
`Cài đặt hệ thống`. Its action wording is explicit:

- Light active: `Chuyển sang tối`
- Dark active: `Chuyển sang sáng`

The button will expose the same action through its tooltip and automation name.
It will use a brightness glyph in Light and a clear-night glyph in Dark. The
existing narrow visual state will collapse the text label, center the icon,
and preserve the tooltip and automation name.

The shell root will own the active `ElementTheme`. Switching will update the
root theme, the toggle presentation, and the custom title-bar button colors
as one synchronous UI operation. The existing title-bar drag area remains
unchanged.

## Preference boundary and failure behavior

`RuApp.Services.UiThemePreferenceStore` will own only the local UI preference.
Its JSON file is:

```json
{
  "theme": "Dark"
}
```

The store will accept an explicit file path for tests and default to
`%LOCALAPPDATA%\\Ru\\ui-preferences.json` in production. A missing file,
invalid JSON, unknown theme value, or read failure resolves to Light. Writes
will create the canonical directory and replace the preference atomically via
a temporary file. A write failure is non-fatal: the current session still
switches successfully, while the next restart falls back to the last readable
preference.

## Technical ownership

| Area | Responsibility |
| --- | --- |
| `gui-winui3/App.xaml` | Light/Dark token dictionaries and dynamic brush aliases |
| `gui-winui3/MainWindow.xaml` | Theme toggle placement and narrow responsive setters |
| `gui-winui3/MainWindow.xaml.cs` | Load/apply/toggle/save theme and title-bar colors |
| `gui-winui3/Services/UiThemePreferenceStore.cs` | Local preference format, fallback, and atomic persistence |
| `gui-winui3.Tests/EditorialReadingRoomContractTests.cs` | XAML/source contracts for tokens and toggle |
| `gui-winui3.Tests/UiThemePreferenceStoreTests.cs` | Preference default, round-trip, invalid-input, and failure behavior |
| `docs/operations/runtime-behavior.md` | Current theme behavior and acceptance boundary |
| Existing reading-room and palette specs | Historical baseline updated with the dark-theme extension |

The runtime data path remains unchanged:

```text
MainWindow -> existing ContentFrame/views -> existing API, WebSocket, voice, and VTS paths
          -> local UiThemePreferenceStore only for appearance preference
```

No backend call, session source recreation, polling restart, or audio/VTS
operation is allowed when the theme changes.

## Testing and acceptance

The implementation will use a red-green TDD loop:

1. Add failing preference-store tests for the Light default, Dark round-trip,
   invalid/missing input fallback, and non-fatal save behavior.
2. Add failing source/XAML contracts for Light/Dark dictionaries, dynamic brush
   references, toggle placement/labels, and narrow icon-only behavior.
3. Implement the store and shell integration minimally, then run the focused
   tests after each green step.
4. Run the complete `gui-winui3.Tests` project sequentially.
5. Run the Release WinUI build and `git diff --check`.

Manual visual acceptance remains user-run. At 1440x900, 1024x768, 800x600,
and 640x600, inspect both themes for readable text, visible focus states,
sidebar and composer rounding, no clipping, correct title-bar contrast, and
correct narrow tooltips. Confirm that switching pages, sending Chat messages,
voice playback, subtitles, VTS control, and backend status remain unchanged.

## Documentation follow-up

The runtime behavior document will no longer describe Ru as light-only. The
reading-room and mineral-palette documents will retain their historical light
baseline while explicitly recording that the shared tokens now have a Dark
dictionary and the appearance preference is local UI state. No historical
implementation plan will be deleted solely because this extension supersedes
its one-theme assumption.
