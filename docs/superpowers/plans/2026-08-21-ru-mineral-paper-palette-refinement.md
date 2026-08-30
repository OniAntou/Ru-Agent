# Ru Mineral Paper Palette Refinement Implementation Plan

> **Status:** Historical implementation record. The palette baseline shipped;
> current presentation boundaries live in `docs/operations/runtime-behavior.md`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task with verification checkpoints.

**Goal:** Reduce the perceived glare of Ru's shared light palette while preserving the approved editorial layout, contrast, controls, and runtime behavior.

**Architecture:** Keep the existing application-level token architecture in `App.xaml`. Lock the proposed token values with source-level contract tests so every legacy `App*Brush` alias continues to inherit the same shared palette. No view-specific colors, layout changes, or runtime behavior changes are needed.

**Tech Stack:** WinUI 3, XAML resource dictionaries, .NET 10, xUnit, PowerShell, `build.bat`.

---

### Task 1: Lock the muted palette with a failing contract test

**Files:**
- Modify: `D:\LLM\gui-winui3.Tests\EditorialReadingRoomContractTests.cs`
- Test: `App_uses_the_muted_mineral_paper_palette`

- [ ] **Step 1: Add the parameterized palette contract**

Add this test after `App_no_longer_contains_the_old_dark_default_palette`:

```csharp
[Theory]
[InlineData("RuCanvasColor", "#E7E9E3")]
[InlineData("RuSidebarColor", "#DCE1D9")]
[InlineData("RuSurfaceColor", "#F0F2EC")]
[InlineData("RuInkColor", "#20231F")]
[InlineData("RuMutedColor", "#5F685F")]
[InlineData("RuLineColor", "#C9CEC5")]
[InlineData("RuAccentColor", "#A84B38")]
[InlineData("RuAccentSoftColor", "#EEDFD9")]
public void App_uses_the_muted_mineral_paper_palette(string resourceKey, string expectedColor)
{
    var source = File.ReadAllText(FindSource("App.xaml"));

    Assert.Contains($"<Color x:Key=\"{resourceKey}\">{expectedColor}</Color>", source, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run the focused test and verify the expected failure**

Run:

```powershell
dotnet test D:\LLM\gui-winui3.Tests --filter FullyQualifiedName~EditorialReadingRoomContractTests --nologo
```

Expected: the new test fails because the current `RuCanvasColor` is still
`#F3F4F0`; the existing editorial contract tests continue to pass.

### Task 2: Apply the shared mineral-paper token values

**Files:**
- Modify: `D:\LLM\gui-winui3\App.xaml:18-25`

- [ ] **Step 1: Replace only the approved shared color values**

Update the color resources to:

```xml
<Color x:Key="RuCanvasColor">#E7E9E3</Color>
<Color x:Key="RuSidebarColor">#DCE1D9</Color>
<Color x:Key="RuSurfaceColor">#F0F2EC</Color>
<Color x:Key="RuInkColor">#20231F</Color>
<Color x:Key="RuMutedColor">#5F685F</Color>
<Color x:Key="RuLineColor">#C9CEC5</Color>
<Color x:Key="RuAccentColor">#A84B38</Color>
<Color x:Key="RuAccentSoftColor">#EEDFD9</Color>
```

Leave `RuSuccessColor`, `RuWarningColor`, `RuDangerColor`,
`RequestedTheme="Light"`, all brush aliases, typography resources, and
radius resources unchanged.

- [ ] **Step 2: Run the focused contract test and verify green**

Run:

```powershell
dotnet test D:\LLM\gui-winui3.Tests --filter FullyQualifiedName~EditorialReadingRoomContractTests --nologo
```

Expected: all editorial contract tests pass, including the new palette test.

### Task 3: Review documentation and run the full verification gates

**Files:**
- Review: `D:\LLM\docs\operations\runtime-behavior.md`
- Review: `D:\LLM\docs\superpowers\specs\2026-08-21-ru-chat-editorial-reading-room-design.md`
- Review: `D:\LLM\docs\superpowers\specs\2026-08-21-ru-mineral-paper-palette-refinement-design.md`

- [ ] **Step 1: Confirm documentation remains accurate**

Keep the existing runtime note if “light mineral canvas” still describes the
muted palette. Do not retire the original reading-room spec; the palette
refinement is intentionally compatible with it.

- [ ] **Step 2: Run all WinUI tests sequentially**

Run:

```powershell
dotnet test D:\LLM\gui-winui3.Tests --nologo
```

Expected: zero failures and the full test count reported by the runner.

- [ ] **Step 3: Build the Release desktop output**

Run:

```powershell
cmd /c D:\LLM\build.bat
```

Expected: exit code 0 and the final `Build complete` message.

- [ ] **Step 4: Check the scoped diff**

Run:

```powershell
git diff --check
git status --short --branch
```

Expected: no whitespace errors and only the approved test/App.xaml changes
before commit.

### Task 4: Commit, push, and hand off manual visual acceptance

**Files:**
- Commit: `D:\LLM\gui-winui3\App.xaml`
- Commit: `D:\LLM\gui-winui3.Tests\EditorialReadingRoomContractTests.cs`

- [ ] **Step 1: Commit the scoped implementation**

Run:

```powershell
git add D:\LLM\gui-winui3\App.xaml D:\LLM\gui-winui3.Tests\EditorialReadingRoomContractTests.cs
git commit -m "style: soften Ru mineral paper palette"
```

- [ ] **Step 2: Push the approved main branch**

Run:

```powershell
git push origin main
```

- [ ] **Step 3: Verify synchronization**

Run:

```powershell
$head = git rev-parse HEAD
$remote = git rev-parse origin/main
if ($head -ne $remote) { exit 1 }
git status --short --branch
```

Expected: `HEAD` equals `origin/main` and the working tree is clean.

Manual acceptance remains user-run: restart RuApp, inspect Settings and Chat at
1440x900 and 1024x768, and confirm the background feels muted mineral rather
than white while labels, controls, focus states, and the red-earth accent stay
readable. No automated desktop control is part of this task.
