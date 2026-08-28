# Chat Dropdown and Full-Width Scrollbar Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Chat mode flyout visually belong to Ru's current surface language and move the transcript scrollbar to the far right edge of the whole Chat viewport while keeping the reading column centered.

**Architecture:** Keep the existing `Flyout`/`RadioButton` semantics and event handlers, but scope Ru surface, typography, spacing, hover, and selected-state resources to the mode flyout. Stretch only the outer `ReadingColumn`/`ChatScrollHost`; keep `ChatContentGrid`, `ChatListView`, the welcome panel, and the composer at the existing calculated width. Use the native vertical scrollbar with a narrow Ru-token thumb so keyboard and pointer behavior remain accessible.

**Tech Stack:** WinUI 3 XAML/C#, xUnit source/XAML contract tests, PowerShell/.NET 10, repository documentation checks.

---

## Files and responsibilities

- Create: `docs/superpowers/specs/2026-08-28-chat-dropdown-scrollbar-design.md` — approved visual/layout decision.
- Modify: `gui-winui3/Views/ChatView.xaml` — scoped flyout/radio resources, full-width scroll host, and native scrollbar tokens.
- Modify: `gui-winui3/Views/ChatView.xaml.cs` — stop assigning the calculated reading width to the outer `ReadingColumn`.
- Modify: `gui-winui3.Tests/AgentSessionUiSourceContractTests.cs` — lock the two-option flyout, scoped styling, and full-width viewport contract.
- Modify: `docs/operations/runtime-behavior.md` — document the centered transcript/full-width scrollbar relationship if the current UI paragraph mentions the old placement.
- Modify: `docs/operations/verification.md` — add the manual check for the mode flyout and right-edge scrollbar.
- Modify: `tests/test_current_documentation.py` — update the current-documentation contract only if the two operation paragraphs are asserted there.

### Task 1: Add the regression contract before changing UI code

**Files:**
- Modify: `gui-winui3.Tests/AgentSessionUiSourceContractTests.cs`

- [ ] **Step 1: Add a failing source/XAML contract for the layout and styling invariants.**

Append a focused fact that reads both `ChatView.xaml` and `ChatView.xaml.cs`, then asserts the following exact markers:

```csharp
[Fact]
public void Chat_mode_flyout_and_transcript_viewport_follow_ru_layout_contract()
{
    string xaml = File.ReadAllText(FindSource("Views", "ChatView.xaml"));
    string codeBehind = File.ReadAllText(FindSource("Views", "ChatView.xaml.cs"));

    Assert.Contains("x:Name=\"AgentModeFlyout\"", xaml, StringComparison.Ordinal);
    Assert.Contains("TargetType=\"FlyoutPresenter\"", xaml, StringComparison.Ordinal);
    Assert.Contains("RuSurfaceBrush", xaml, StringComparison.Ordinal);
    Assert.Contains("RuAccentSoftBrush", xaml, StringComparison.Ordinal);
    Assert.Contains("RadioButtonBackgroundPointerOver", xaml, StringComparison.Ordinal);
    Assert.Contains("RadioButtonBackgroundPressed", xaml, StringComparison.Ordinal);
    Assert.Contains("x:Name=\"AgentModeChatOption\"", xaml, StringComparison.Ordinal);
    Assert.Contains("x:Name=\"AgentModeAgentOption\"", xaml, StringComparison.Ordinal);

    int settingsStart = xaml.IndexOf("x:Name=\"AgentSettingsFlyout\"", StringComparison.Ordinal);
    int modeStart = xaml.IndexOf("x:Name=\"AgentModeFlyout\"", StringComparison.Ordinal);
    Assert.True(modeStart >= 0 && settingsStart > modeStart);
    string modeBlock = xaml[modeStart..settingsStart];
    Assert.DoesNotContain("AgentWorkspaceBox", modeBlock, StringComparison.Ordinal);
    Assert.DoesNotContain("AgentPresetBox", modeBlock, StringComparison.Ordinal);

    Assert.Contains("x:Name=\"ReadingColumn\"", xaml, StringComparison.Ordinal);
    Assert.Contains("HorizontalAlignment=\"Stretch\"", xaml, StringComparison.Ordinal);
    Assert.Contains("x:Name=\"ChatScrollHost\"", xaml, StringComparison.Ordinal);
    Assert.Contains("VerticalScrollBarVisibility=\"Auto\"", xaml, StringComparison.Ordinal);
    Assert.Contains("HorizontalScrollBarVisibility=\"Disabled\"", xaml, StringComparison.Ordinal);
    Assert.Contains("x:Name=\"ChatContentGrid\"", xaml, StringComparison.Ordinal);
    Assert.Contains("HorizontalAlignment=\"Center\"", xaml, StringComparison.Ordinal);

    int applyWidthStart = codeBehind.IndexOf("private void ApplyInputWidth", StringComparison.Ordinal);
    Assert.True(applyWidthStart >= 0);
    int nextMethod = codeBehind.IndexOf("\n        private", applyWidthStart + 1, StringComparison.Ordinal);
    string applyWidthBody = nextMethod > applyWidthStart
        ? codeBehind[applyWidthStart..nextMethod]
        : codeBehind[applyWidthStart..];
    Assert.DoesNotContain("ReadingColumn.Width", applyWidthBody, StringComparison.Ordinal);
    Assert.Contains("ChatContentGrid.Width = targetWidth", applyWidthBody, StringComparison.Ordinal);
    Assert.Contains("ChatListView.Width = targetWidth", applyWidthBody, StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run the new focused test and confirm it fails before the UI change.**

Run:

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --no-restore --nologo --filter FullyQualifiedName~AgentSessionUiSourceContractTests.Chat_mode_flyout_and_transcript_viewport_follow_ru_layout_contract
```

Expected result before the fix: `FAIL`, because the current flyout has no scoped `FlyoutPresenter`/radio styling and `ApplyInputWidth` still assigns `ReadingColumn.Width`.

### Task 2: Style the mode flyout without changing its behavior

**Files:**
- Modify: `gui-winui3/Views/ChatView.xaml:372-392`

- [ ] **Step 1: Add a scoped FlyoutPresenter surface.**

Inside `AgentModeFlyout`, add this presenter style before the content panel:

```xml
<Flyout.FlyoutPresenterStyle>
    <Style TargetType="FlyoutPresenter">
        <Setter Property="Background" Value="{StaticResource RuSurfaceBrush}" />
        <Setter Property="BorderBrush" Value="{StaticResource RuLineBrush}" />
        <Setter Property="BorderThickness" Value="1" />
        <Setter Property="CornerRadius" Value="{StaticResource RuControlCornerRadius}" />
        <Setter Property="Padding" Value="6" />
        <Setter Property="HorizontalContentAlignment" Value="Stretch" />
    </Style>
</Flyout.FlyoutPresenterStyle>
```

- [ ] **Step 2: Add compact radio option resources and keep only the two options.**

Replace the mode panel's `Width="220" Spacing="8" Padding="4"` with `Width="208" Spacing="2" Padding="0"`, and add these resources to that panel:

```xml
<StackPanel.Resources>
    <SolidColorBrush x:Key="RadioButtonForeground" Color="{ThemeResource RuInkColor}" />
    <SolidColorBrush x:Key="RadioButtonForegroundPointerOver" Color="{ThemeResource RuInkColor}" />
    <SolidColorBrush x:Key="RadioButtonForegroundPressed" Color="{ThemeResource RuInkColor}" />
    <SolidColorBrush x:Key="RadioButtonBackground" Color="Transparent" />
    <StaticResource x:Key="RadioButtonBackgroundPointerOver" ResourceKey="RuAccentSoftBrush" />
    <StaticResource x:Key="RadioButtonBackgroundPressed" ResourceKey="RuAccentSoftBrush" />
    <StaticResource x:Key="RadioButtonOuterEllipseStroke" ResourceKey="RuMutedBrush" />
    <StaticResource x:Key="RadioButtonOuterEllipseCheckedStroke" ResourceKey="RuAccentBrush" />
    <StaticResource x:Key="RadioButtonOuterEllipseCheckedFill" ResourceKey="RuAccentBrush" />
</StackPanel.Resources>
```

Set both existing radio options to `Padding="10,8"`, `MinHeight="38"`, `Margin="0"`, `HorizontalContentAlignment="Stretch"`, `FontFamily="{StaticResource RuUiFontFamily}"`, `FontSize="13"`, and `Foreground="{StaticResource RuInkBrush}"`. Do not add a title, workspace field, preset, or Agent activity status to the mode flyout; retain the existing names, tags, group, click handlers, and automation properties.

- [ ] **Step 3: Build the XAML after the style change.**

Run:

```powershell
dotnet build D:\LLM\gui-winui3\RuApp.csproj -c Debug -p:Platform=x64 --no-restore --nologo
```

Expected result: build succeeds. If a resource type is rejected, remove only the rejected resource and use an equivalent typed `SolidColorBrush`; do not replace the native RadioButton template.

### Task 3: Put the native scrollbar at the full Chat edge

**Files:**
- Modify: `gui-winui3/Views/ChatView.xaml:105-209`
- Modify: `gui-winui3/Views/ChatView.xaml.cs:905-946`

- [ ] **Step 1: Stretch the outer reading viewport but preserve the centered content width.**

Change `ReadingColumn` to `HorizontalAlignment="Stretch"`. Keep `ChatScrollHost` stretch-aligned and add `Padding="0"` if it is not already implicit. Keep `ChatContentGrid` centered; it still receives `targetWidth` in code-behind. Do not move the welcome panel or composer out of their current rows.

- [ ] **Step 2: Scope a narrow Ru-token scrollbar without replacing the native control.**

Add `ScrollViewer.Resources` inside `ChatScrollHost`:

```xml
<ScrollViewer.Resources>
    <SolidColorBrush x:Key="ScrollBarBackground" Color="Transparent" />
    <SolidColorBrush x:Key="ScrollBarForeground" Color="Transparent" />
    <SolidColorBrush x:Key="ScrollBarThumbFill" Color="{ThemeResource RuLineColor}" />
    <SolidColorBrush x:Key="ScrollBarThumbFillPointerOver" Color="{ThemeResource RuAccentColor}" />
    <SolidColorBrush x:Key="ScrollBarThumbFillPressed" Color="{ThemeResource RuAccentColor}" />
    <SolidColorBrush x:Key="ScrollBarThumbFillDisabled" Color="Transparent" />
    <SolidColorBrush x:Key="ScrollBarTrackFill" Color="Transparent" />
    <SolidColorBrush x:Key="ScrollBarTrackFillPointerOver" Color="Transparent" />
    <SolidColorBrush x:Key="ScrollBarTrackFillDisabled" Color="Transparent" />
    <x:Double x:Key="ScrollBarSize">8</x:Double>
</ScrollViewer.Resources>
```

Keep `VerticalScrollBarVisibility="Auto"` and `HorizontalScrollBarVisibility="Disabled"`. The scrollbar must stay accessible to keyboard, wheel, and automation input; do not draw a custom overlay.

- [ ] **Step 3: Stop constraining `ReadingColumn` in `ApplyInputWidth`.**

Delete only this assignment from `ApplyInputWidth`:

```csharp
if (ReadingColumn != null)
{
    ReadingColumn.Width = targetWidth;
}
```

Keep the existing `targetWidth` calculation and assignments to `WelcomeHeroPanel`, `InputContainer`, `InputBorder`, `ChatContentGrid`, and `ChatListView`. This leaves messages, welcome content, and composer aligned while the outer scroll host fills the Chat viewport.

- [ ] **Step 4: Run the focused layout/style contract.**

Run the Task 1 command again. Expected result: `PASS`.

### Task 4: Update current documentation and complete verification

**Files:**
- Modify: `docs/operations/runtime-behavior.md`
- Modify: `docs/operations/verification.md`
- Modify: `tests/test_current_documentation.py` only when its asserted wording requires it

- [ ] **Step 1: Document the stable UI relationship.**

In the current Chat UI paragraph, state that the transcript reading column remains centered and capped at 820 px while its vertical scrollbar belongs to the full Chat viewport and sits at the far right; horizontal scrolling remains disabled. Keep the existing Agent idle/activity and privacy wording unchanged.

- [ ] **Step 2: Add the manual acceptance steps.**

In the GUI verification instructions, add:

```text
Open the mode control in the composer: the flyout is a compact Ru surface containing only Trò chuyện and Coding Agent; Agent settings remain behind the separate settings button. Grow a transcript until scrolling is needed: messages stay centered in the reading column and the native vertical scrollbar sits at the far right edge of the Chat viewport. Horizontal scrolling remains disabled.
```

- [ ] **Step 3: Run all focused and repository checks.**

Run sequentially:

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --no-restore --nologo
dotnet build D:\LLM\gui-winui3\RuApp.csproj -c Debug -p:Platform=x64 --no-restore --nologo
dotnet build D:\LLM\gui-winui3.Harness\RuApp.Harness.csproj -c Debug --no-restore --nologo
.venv\Scripts\python.exe -m pytest D:\LLM\tests\test_current_documentation.py -q
git diff --check
```

Expected result: all GUI tests and documentation tests pass, both builds succeed, and `git diff --check` exits 0. Line-ending normalization warnings are not source failures.

- [ ] **Step 4: Publish the official Release artifact after a process guard.**

Run:

```powershell
Get-CimInstance Win32_Process -Filter "Name = 'RuApp.exe'" | Select-Object ProcessId,ExecutablePath,CommandLine
dotnet publish D:\LLM\gui-winui3\RuApp.csproj -c Release -r win-x64 --self-contained true --no-restore --nologo
Get-Item D:\LLM\gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe | Select-Object FullName,Length,LastWriteTime
```

Only publish when the first command returns no running `RuApp.exe`. Do not launch the app automatically.

- [ ] **Step 5: Preserve unrelated dirty work and hand off visual acceptance.**

Inspect `git status --short`; do not reset, delete, or stage unrelated files. Tell the user to open the existing shortcut and perform the flyout/right-edge scrollbar checks. Automated tests and builds do not replace this human visual check.
