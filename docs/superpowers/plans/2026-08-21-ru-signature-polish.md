# Ru Signature Polish Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Ru distinctive through a restrained wordmark, editorial welcome state, signature message rhythm, and purposeful micro-interactions while preserving runtime behavior.

**Architecture:** Extend the existing theme-token and XAML presentation system only. Add a stronger title-bar lockup, editorial welcome eyebrow, message edge rules, and focus-state presentation; keep Chat handlers, data templates, theme persistence, responsive policy, and backend paths intact. Do not add a rectangular ambient wash: visual review showed that it reads as an unintended block in Dark mode.

**Post-implementation correction (2026-08-21):** The ambient wash token and
welcome rectangle described in the original plan were removed. The contract
tests now assert their absence while retaining the typography and accent-rule
markers.

**Tech Stack:** WinUI 3 XAML, C#, existing Ru resources, xUnit source/XAML contracts, sequential dotnet test and Release build.

---

### Task 1: Add failing anti-slop and signature contracts

**Files:**
- Modify: gui-winui3.Tests/EditorialReadingRoomContractTests.cs

- [ ] **Step 1: Add the failing signature contract tests**

Add these focused tests:

~~~csharp
[Fact]
public void App_does_not_define_a_hard_edged_signature_wash()
{
    var source = File.ReadAllText(FindSource("App.xaml"));

    Assert.DoesNotContain("x:Key=\"RuAccentWashBrush\"", source, StringComparison.Ordinal);
}

[Fact]
public void Shell_has_a_quiet_Ru_brand_lockup_without_dashboard_badges()
{
    var source = File.ReadAllText(FindSource("MainWindow.xaml"));

    Assert.Contains("x:Name=\"BrandLockup\"", source, StringComparison.Ordinal);
    Assert.Contains("x:Name=\"BrandName\"", source, StringComparison.Ordinal);
    Assert.Contains("x:Name=\"BrandRule\"", source, StringComparison.Ordinal);
    Assert.Contains("x:Name=\"BrandDescriptor\"", source, StringComparison.Ordinal);
    Assert.Contains("Text=\"conversational runtime\"", source, StringComparison.Ordinal);
    Assert.DoesNotContain("Live2D Companion", source, StringComparison.Ordinal);
}

[Fact]
public void Chat_has_authored_welcome_and_reading_rhythm_markers()
{
    var source = File.ReadAllText(FindSource("ChatView.xaml"));

    Assert.Contains("x:Name=\"WelcomeEyebrow\"", source, StringComparison.Ordinal);
    Assert.Contains("Text=\"RU / CONVERSATIONAL RUNTIME\"", source, StringComparison.Ordinal);
    Assert.DoesNotContain("x:Name=\"WelcomeAccentWash\"", source, StringComparison.Ordinal);
    Assert.DoesNotContain("RuAccentWashBrush", source, StringComparison.Ordinal);
    Assert.Contains("x:Name=\"AssistantAccentRule\"", source, StringComparison.Ordinal);
    Assert.Contains("BorderThickness=\"0,0,2,0\"", source, StringComparison.Ordinal);
}

[Fact]
public void Composer_exposes_a_theme_aware_focus_rule_without_new_behavior()
{
    var source = File.ReadAllText(FindSource("ChatView.xaml"));

    Assert.Contains("x:Name=\"ComposerFocusRule\"", source, StringComparison.Ordinal);
    Assert.Contains("GotFocus=\"OnChatInputGotFocus\"", source, StringComparison.Ordinal);
    Assert.Contains("LostFocus=\"OnChatInputLostFocus\"", source, StringComparison.Ordinal);
    Assert.Contains("Grid.ColumnSpan=\"3\"", source, StringComparison.Ordinal);
}

[Fact]
public void Signature_polish_does_not_introduce_AI_dashboard_decoration()
{
    var shell = File.ReadAllText(FindSource("MainWindow.xaml"));
    var chat = File.ReadAllText(FindSource("ChatView.xaml"));

    foreach (var staleMarker in new[]
    {
        "LinearGradientBrush",
        "RadialGradientBrush",
        "BlurEffect",
        "CornerRadius=\"18\"",
        "CornerRadius=\"26\"",
        "Text=\"AI companion\"",
        "Text=\"Live2D Companion\""
    })
    {
        Assert.DoesNotContain(staleMarker, shell, StringComparison.Ordinal);
        Assert.DoesNotContain(staleMarker, chat, StringComparison.Ordinal);
    }
}
~~~

- [ ] **Step 2: Run the focused contracts and verify red**

Run:

~~~powershell
dotnet test gui-winui3.Tests --nologo --filter FullyQualifiedName~EditorialReadingRoomContractTests
~~~

Expected: existing contracts pass and the five new signature contracts fail because the new markers do not exist.

- [ ] **Step 3: Commit the test-only red slice**

~~~powershell
git add gui-winui3.Tests/EditorialReadingRoomContractTests.cs
git commit -m "test: define Ru signature polish contracts"
~~~

### Task 2: Add signature resources and the title-bar lockup

**Files:**
- Modify: gui-winui3/App.xaml
- Modify: gui-winui3/MainWindow.xaml
- Test: gui-winui3.Tests/EditorialReadingRoomContractTests.cs

- [x] **Step 1: Keep the signature resource set free of an ambient wash**

Visual review showed that a low-opacity rectangular wash becomes a visible
block in Dark mode, so no `RuAccentWashBrush` is added to `App.xaml`.

- [ ] **Step 2: Replace the title-bar text stack with the brand lockup**

Inside AppTitleBar, use:

~~~xml
<Grid x:Name="BrandLockup"
      ColumnDefinitions="Auto,Auto,Auto"
      ColumnSpacing="9"
      VerticalAlignment="Center">
    <TextBlock x:Name="BrandName"
               Text="Ru"
               FontFamily="{StaticResource RuDisplayFontFamily}"
               FontSize="21"
               FontWeight="SemiBold"
               Foreground="{StaticResource RuInkBrush}"
               VerticalAlignment="Center" />
    <Rectangle x:Name="BrandRule"
               Grid.Column="1"
               Width="16"
               Height="1"
               Fill="{StaticResource RuAccentBrush}"
               VerticalAlignment="Center"
               Opacity="0.82" />
    <TextBlock x:Name="BrandDescriptor"
               Grid.Column="2"
               Text="conversational runtime"
               FontFamily="{StaticResource RuUiFontFamily}"
               FontSize="11"
               Foreground="{StaticResource RuMutedBrush}"
               VerticalAlignment="Center" />
</Grid>
~~~

Keep AppTitleBar as the existing drag region and add no clickable element there.

- [ ] **Step 3: Run focused contracts and verify the Task 2 markers**

Run the EditorialReadingRoomContractTests filter. Expected: token, brand lockup, and anti-dashboard assertions pass; welcome/message/composer assertions remain red until later tasks.

- [ ] **Step 4: Commit the token and shell slice**

~~~powershell
git add gui-winui3/App.xaml gui-winui3/MainWindow.xaml gui-winui3.Tests/EditorialReadingRoomContractTests.cs
git commit -m "style: add Ru signature brand lockup"
~~~

### Task 3: Add the editorial welcome state and message edge rules

**Files:**
- Modify: gui-winui3/Views/ChatView.xaml
- Test: gui-winui3.Tests/EditorialReadingRoomContractTests.cs

- [x] **Step 1: Add the welcome eyebrow without an ambient rectangle**

At the top of WelcomeHeroPanel, add:

~~~xml
<TextBlock x:Name="WelcomeEyebrow"
           Text="RU / CONVERSATIONAL RUNTIME"
           FontFamily="{StaticResource RuUiFontFamily}"
           FontSize="10"
           FontWeight="SemiBold"
           CharacterSpacing="120"
           Foreground="{StaticResource RuMutedBrush}" />
~~~

Keep the existing heading and OnSuggestionClick handlers unchanged.

- [ ] **Step 2: Add assistant and user signature rules**

Add BorderBrush="{StaticResource RuAccentBrush}" and
BorderThickness="0,0,2,0" to the user message Border.

Replace the assistant message StackPanel wrapper with:

~~~xml
<Grid MaxWidth="700"
      HorizontalAlignment="Left"
      ColumnDefinitions="2,*">
    <Rectangle x:Name="AssistantAccentRule"
               Grid.Column="0"
               Width="2"
               Fill="{StaticResource RuAccentBrush}"
               Opacity="0.72"
               VerticalAlignment="Stretch" />
    <StackPanel Grid.Column="1"
                Spacing="6"
                Margin="12,0,0,0">
        <TextBlock Text="Ru"
                   FontFamily="{StaticResource RuDisplayFontFamily}"
                   FontSize="12"
                   Foreground="{StaticResource RuAccentBrush}" />
        <Rectangle x:Name="TypingCaret"
                   Width="1"
                   Height="20"
                   Fill="{StaticResource RuAccentBrush}"
                   Visibility="{x:Bind ShowTypingDots, Mode=OneWay, Converter={StaticResource BoolToVisibilityConverter}}" />
        <controls:MarkdownBlock MarkdownText="{x:Bind Content, Mode=OneWay}"
                                FontSize="16"
                                Foreground="{x:Bind IsError, Converter={StaticResource ErrorColorConverter}}"
                                FontFamily="{StaticResource RuUiFontFamily}"
                                Visibility="{x:Bind ShowContent, Mode=OneWay, Converter={StaticResource BoolToVisibilityConverter}}" />
    </StackPanel>
</Grid>
~~~

Do not change bindings, selectors, Markdown properties, or message content.

- [ ] **Step 3: Run focused tests and verify green**

Run the EditorialReadingRoomContractTests filter. Expected: all signature contracts and existing Chat contracts pass.

- [ ] **Step 4: Commit the welcome/message slice**

~~~powershell
git add gui-winui3/Views/ChatView.xaml gui-winui3.Tests/EditorialReadingRoomContractTests.cs
git commit -m "style: add Ru editorial message rhythm"
~~~

### Task 4: Add composer focus polish without changing input behavior

**Files:**
- Modify: gui-winui3/Views/ChatView.xaml
- Modify: gui-winui3/Views/ChatView.xaml.cs
- Test: gui-winui3.Tests/EditorialReadingRoomContractTests.cs

- [ ] **Step 1: Add a focus rule to the composer grid**

Add this Rectangle inside the InputBorder child Grid:

~~~xml
<Rectangle x:Name="ComposerFocusRule"
           Grid.ColumnSpan="3"
           Height="2"
           VerticalAlignment="Bottom"
           Fill="{StaticResource RuAccentBrush}"
           Opacity="0"
           IsHitTestVisible="False" />
~~~

Add GotFocus="OnChatInputGotFocus" and LostFocus="OnChatInputLostFocus" to ChatInputTextBox. Keep all existing handlers and keyboard behavior unchanged.

- [ ] **Step 2: Add visual-only focus handlers**

Add to ChatView.xaml.cs:

~~~csharp
private void OnChatInputGotFocus(object sender, RoutedEventArgs e)
{
    ComposerFocusRule.Opacity = 1;
}

private void OnChatInputLostFocus(object sender, RoutedEventArgs e)
{
    ComposerFocusRule.Opacity = 0;
}
~~~

The handlers must only change ComposerFocusRule opacity.

- [ ] **Step 3: Add the exact focus contract and run focused tests**

Add:

~~~csharp
[Fact]
public void Chat_focus_polish_handlers_are_visual_only()
{
    var source = File.ReadAllText(FindSource("ChatView.xaml.cs"));
    var gotFocusStart = source.IndexOf("private void OnChatInputGotFocus", StringComparison.Ordinal);
    var lostFocusStart = source.IndexOf("private void OnChatInputLostFocus", StringComparison.Ordinal);
    var nextMethodStart = source.IndexOf("private ", lostFocusStart + 1, StringComparison.Ordinal);

    Assert.True(gotFocusStart >= 0);
    Assert.True(lostFocusStart > gotFocusStart);
    Assert.True(nextMethodStart > lostFocusStart);

    var handlers = source[gotFocusStart..nextMethodStart];
    Assert.DoesNotContain("SendCurrentMessageAsync", handlers, StringComparison.Ordinal);
    Assert.DoesNotContain("ApiService", handlers, StringComparison.Ordinal);
    Assert.Contains("ComposerFocusRule.Opacity = 1", handlers, StringComparison.Ordinal);
    Assert.Contains("ComposerFocusRule.Opacity = 0", handlers, StringComparison.Ordinal);
}
~~~

Run:

~~~powershell
dotnet test gui-winui3.Tests --nologo --filter FullyQualifiedName~EditorialReadingRoomContractTests
~~~

Expected: all focused contracts pass.

- [ ] **Step 4: Build the GUI Release project**

~~~powershell
dotnet build gui-winui3/RuApp.csproj -c Release -r win-x64 --self-contained true --no-restore
~~~

Expected: exit 0 with zero warnings and zero errors.

- [ ] **Step 5: Commit the composer slice**

~~~powershell
git add gui-winui3/Views/ChatView.xaml gui-winui3/Views/ChatView.xaml.cs gui-winui3.Tests/EditorialReadingRoomContractTests.cs
git commit -m "style: polish Ru composer focus state"
~~~

### Task 5: Reconcile documentation and complete verification

**Files:**
- Modify: docs/operations/runtime-behavior.md
- Modify: docs/superpowers/specs/2026-08-21-ru-signature-polish-design.md

- [ ] **Step 1: Update current runtime documentation**

Add that Ru's visual signature is expressed by the wordmark, editorial welcome
eyebrow, message edge rules, and composer focus rule. State that the removed
ambient rectangle must not return and that these remain presentation-only,
adding no backend calls, VTS metadata, or Chat content.

- [ ] **Step 2: Mark the signature design implemented**

Change the signature spec status to Implemented after code and verification are
complete. Keep the anti-AI-slop guardrails intact.

- [ ] **Step 3: Run the complete test project**

~~~powershell
dotnet test gui-winui3.Tests --nologo
~~~

Expected: all tests pass with zero failures and zero errors.

- [ ] **Step 4: Run anti-slop and diff checks**

~~~powershell
rg -n -i "Live2D Companion|AI companion|LinearGradientBrush|RadialGradientBrush|BlurEffect|CornerRadius=\"18\"|CornerRadius=\"26\"" gui-winui3 docs --glob '*.xaml' --glob '*.cs' --glob '*.md'
git diff --check
~~~

Expected: no new anti-slop markers in changed presentation files; any historical
plan references are explicitly historical and not current runtime code. Diff
check is clean.

- [ ] **Step 5: Verify scope and push main**

Run sequentially:

~~~powershell
git status --short --branch
git fetch origin main
git rev-list --left-right --count HEAD...origin/main
git push origin main
git diff --check
git status --short --branch
git rev-list --left-right --count HEAD...origin/main
~~~

Expected: no unintended dirty files, 0 0 after push, and a clean synchronized
main branch. Manual visual acceptance remains user-run at 1440x900, 1024x768,
800x600, and 640x600 in both themes.
