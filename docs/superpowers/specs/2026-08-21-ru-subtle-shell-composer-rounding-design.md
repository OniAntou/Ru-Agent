# Ru Subtle Shell and Composer Rounding

**Date:** 2026-08-21
**Status:** Draft for user review
**Scope:** Add a barely perceptible corner radius to the sidebar and Chat
composer

## Decision summary

Reuse the existing 6 px `RuControlCornerRadius` token. The sidebar receives
only right-side rounding (`0,6,6,0`) because its left edge sits against the
window edge. The Chat input frame receives a uniform 6 px radius. This adds a
small amount of softness without changing the approved flat editorial visual
language.

## Scope boundaries

- Modify only the sidebar `Border` in `gui-winui3/MainWindow.xaml` and the
  `InputBorder` in `gui-winui3/Views/ChatView.xaml`.
- Add source/XAML contract coverage in
  `gui-winui3.Tests/EditorialReadingRoomContractTests.cs`.
- Reuse `RuControlCornerRadius`; do not introduce a new radius token.
- Keep the existing widths, heights, padding, colors, divider thickness,
  responsive states, navigation handlers, input handlers, and send/microphone
  behavior unchanged.
- Do not round individual navigation rows, session rows, buttons, or message
  blocks.

## Exact visual contract

```xml
<!-- MainWindow.xaml -->
<Border Grid.Column="0"
        Background="{StaticResource RuSidebarBrush}"
        BorderThickness="0"
        CornerRadius="0,6,6,0">

<!-- ChatView.xaml -->
<Border x:Name="InputBorder"
        Background="{StaticResource RuSurfaceBrush}"
        BorderBrush="{StaticResource RuLineBrush}"
        BorderThickness="0,1,0,0"
        CornerRadius="{StaticResource RuControlCornerRadius}">
```

## Acceptance criteria

Automated checks must prove that:

1. The sidebar has only the two right corners rounded at 6 px.
2. `InputBorder` uses the shared 6 px control radius.
3. Existing editorial, responsive, no-emoji, and Settings contracts remain
   intact.
4. The full WinUI test project and Release build pass.

Manual acceptance remains user-run after restarting RuApp at 1440x900 and
1024x768: the two surfaces should feel slightly softer, while navigation rows,
message blocks, controls, spacing, and the narrow rail remain unchanged.
