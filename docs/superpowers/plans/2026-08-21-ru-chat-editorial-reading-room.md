
# Ru Chat Editorial Reading Room Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax `- [ ]` for tracking.

**Goal:** Redesign the Ru WinUI 3 shell/sidebar and Chat view into a light editorial reading room while preserving all existing navigation, session, streaming, STT, voice, VTS, backend, and process behavior.

**Architecture:** Keep the existing MainWindow navigation, ChatView data templates, ChatMessageItem model, WebSocket client, session collection, and responsive policy. Replace only the visual resources and presentation trees, using a canonical editorial token set in App.xaml; keep legacy resource keys mapped to the new palette so untouched views do not fall back to the old dark theme. Add source/XAML contract tests beside the existing WinUI tests because live visual acceptance cannot be automated safely in this workspace.

**Tech Stack:** WinUI 3/XAML, C#/.NET 10, xUnit, existing ResponsiveLayoutPolicy, existing MarkdownBlock, PowerShell, and the current gui-winui3.Tests project. No new UI dependency.

## Global Constraints

- Phase 1 covers Chat + shell/sidebar; Voice/VTS, Log, Diagnostics, Settings, and Memory keep their existing layout and behavior.
- Use one light theme with RuCanvasBrush #F3F4F0, RuSidebarBrush #E9ECE6, RuSurfaceBrush #FBFCF8, RuInkBrush #20231F, RuMutedBrush #687067, RuLineBrush #D9DDD5, and RuAccentBrush #A84B38.
- Use Segoe UI Variable for UI and message body, Cambria for page titles and Markdown headings, and Consolas for code.
- Use 6 px control radius, no decorative radius or shadow on the reading surface, no gradient, glow, glass, purple-blue accent, avatar badge, chat bubble, pill suggestion, or repeated status badge.
- Keep responsive boundaries exact: Wide >= 1280, Compact 900-1279, Narrow < 900 device-independent pixels; narrow rail remains 72 px.
- Preserve the existing ResponsiveLayoutPolicy, sidebar width restoration, vertical-only chat scrolling, ItemsSource, session selection, send/stream/cancellation ordering, mic handler, Enter/Shift+Enter behavior, and status polling.
- Do not change ASP.NET Core routes, WebSocket contracts, Python worker behavior, generation, memory, voice playback, subtitles, VTube Studio, or process lifecycle.
- Keep Ru chat free of Unicode emoji and ASCII emoticons. Keep voice/VTS control metadata private from Chat text and Chat history.
- Do not add a package, navigation framework, persisted UI preference, backend endpoint, polling path, or resize animation.
- The worktree contains unrelated dirty files. Before editing an already-dirty file, inspect and preserve its current diff. Stage only the redesign hunks; never commit a whole dirty file merely because the file is in scope.
- Run heavyweight verification sequentially on Windows. Automated checks prove source/layout contracts only; actual window resizing, audio, and VTS acceptance remain user-run.

---

## File map and responsibilities

Create:

- gui-winui3.Tests/EditorialReadingRoomContractTests.cs for token, shell, Chat, and Markdown source contracts that do not need a live desktop window.

Modify:

- gui-winui3/App.xaml for canonical editorial colors, fonts, radius, and legacy brush mappings.
- gui-winui3/MainWindow.xaml for the flat sidebar, relocated status row, title identity, and responsive visual setters.
- gui-winui3/MainWindow.xaml.cs for active-nav rule styling and dynamic status tooltip composition only.
- gui-winui3/Views/ChatView.xaml for document-stream templates, welcome intro, flat suggestions, reading-column width, and composer presentation.
- gui-winui3/Views/ChatView.xaml.cs for tag-based suggestion extraction and the 820 px reading-column width calculation only.
- gui-winui3/Controls/MarkdownBlock.cs for explicit body/display/monospace typography in the Chat renderer.
- docs/operations/runtime-behavior.md for the phase-one shell/Chat visual boundary and automated versus manual acceptance distinction.

Do not modify gui-winui3/Services/ResponsiveLayoutPolicy.cs; its current policy is the source of truth for the approved breakpoints. Do not modify unrelated dirty files. Existing responsive edits in MainWindow.xaml, MainWindow.xaml.cs, and ChatView.xaml are a baseline to preserve, not a reason to overwrite the files.

### Safe staging rule for dirty GUI files

For MainWindow.xaml, MainWindow.xaml.cs, ChatView.xaml, ChatView.xaml.cs, and MarkdownBlock.cs:

1. Run git diff -- <file> before editing and record the existing hunk boundaries in the task notes.
2. Apply the redesign without reverting or reformatting unrelated lines.
3. Run git diff -- <file> after editing and separate the pre-existing hunk from the new redesign hunk.
4. Use patch-based staging or git add -p to stage only the redesign hunk. If a safe split is not possible, leave that file unstaged and report the checkpoint rather than committing unrelated work.

---

### Task 1: Establish the editorial token contract

**Files:**

- Create: gui-winui3.Tests/EditorialReadingRoomContractTests.cs
- Modify: gui-winui3/App.xaml

**Interfaces:**

- Produces the shared XAML resources RuCanvasBrush, RuSidebarBrush, RuSurfaceBrush, RuInkBrush, RuMutedBrush, RuLineBrush, RuAccentBrush, RuAccentSoftBrush, RuSuccessBrush, RuWarningBrush, RuDangerBrush, RuUiFontFamily, RuDisplayFontFamily, RuMonoFontFamily, and RuControlCornerRadius.
- Keeps the existing AppSurfaceBrush, AppSidebarBackgroundBrush, AppCardBackgroundBrush, AppCardBackgroundHoverBrush, AppCardBorderBrush, AppTextPrimaryBrush, AppTextSecondaryBrush, AppTextTertiaryBrush, AppAccentBrush, AppAccentHoverBrush, and status brush keys available to untouched views, mapped to the new palette.

- [ ] Step 1: Write the failing token and legacy-palette tests

Create the contract test with this concrete shape:

~~~csharp
using Xunit;

namespace RuApp.Tests;

public sealed class EditorialReadingRoomContractTests
{
    [Theory]
    [InlineData("RuCanvasBrush")]
    [InlineData("RuSidebarBrush")]
    [InlineData("RuSurfaceBrush")]
    [InlineData("RuInkBrush")]
    [InlineData("RuMutedBrush")]
    [InlineData("RuLineBrush")]
    [InlineData("RuAccentBrush")]
    [InlineData("RuAccentSoftBrush")]
    [InlineData("RuUiFontFamily")]
    [InlineData("RuDisplayFontFamily")]
    [InlineData("RuMonoFontFamily")]
    [InlineData("RuControlCornerRadius")]
    public void App_defines_each_editorial_resource(string resourceKey)
    {
        var source = File.ReadAllText(FindSource("App.xaml"));
        Assert.Contains($"x:Key=\"{resourceKey}\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void App_no_longer_contains_the_old_dark_default_palette()
    {
        var source = File.ReadAllText(FindSource("App.xaml"));

        foreach (var staleColor in new[] { "#0D0E12", "#12141B", "#171923", "#4F6BFF", "#6881FF", "#8FA2FF" })
        {
            Assert.DoesNotContain(staleColor, source, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Legacy_brush_keys_remain_available_for_untouched_views()
    {
        var source = File.ReadAllText(FindSource("App.xaml"));

        foreach (var legacyKey in new[]
        {
            "AppSurfaceBrush", "AppSidebarBackgroundBrush", "AppCardBackgroundBrush",
            "AppCardBackgroundHoverBrush", "AppCardBorderBrush", "AppTextPrimaryBrush",
            "AppTextSecondaryBrush", "AppTextTertiaryBrush", "AppAccentBrush",
            "AppAccentHoverBrush", "AppSuccessBrush", "AppWarningBrush", "AppDangerBrush"
        })
        {
            Assert.Contains($"x:Key=\"{legacyKey}\"", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void App_uses_the_single_six_pixel_control_radius()
    {
        var source = File.ReadAllText(FindSource("App.xaml"));

        Assert.Contains("<CornerRadius x:Key=\"AppCardCornerRadius\">6</CornerRadius>", source, StringComparison.Ordinal);
        Assert.Contains("<CornerRadius x:Key=\"AppControlCornerRadius\">6</CornerRadius>", source, StringComparison.Ordinal);
        Assert.Contains("<CornerRadius x:Key=\"AppPillCornerRadius\">6</CornerRadius>", source, StringComparison.Ordinal);
    }

    private static string FindSource(string fileName)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "gui-winui3", fileName);
            if (File.Exists(path)) return path;
        }

        throw new FileNotFoundException(fileName);
    }
}
~~~

- [ ] Step 2: Run the focused tests and verify the expected failure

Run:

~~~powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo --filter FullyQualifiedName~EditorialReadingRoomContractTests
~~~

Expected result: FAIL because the Ru resources are not defined and the old dark color literals remain in App.xaml.

- [ ] Step 3: Add canonical colors, brushes, fonts, and the legacy mapping

Add these concrete resources near the top of Application.Resources:

~~~xml
<Color x:Key="RuCanvasColor">#F3F4F0</Color>
<Color x:Key="RuSidebarColor">#E9ECE6</Color>
<Color x:Key="RuSurfaceColor">#FBFCF8</Color>
<Color x:Key="RuInkColor">#20231F</Color>
<Color x:Key="RuMutedColor">#687067</Color>
<Color x:Key="RuLineColor">#D9DDD5</Color>
<Color x:Key="RuAccentColor">#A84B38</Color>
<Color x:Key="RuAccentSoftColor">#F3E5E0</Color>
<Color x:Key="RuSuccessColor">#2F6F51</Color>
<Color x:Key="RuWarningColor">#9A6C1F</Color>
<Color x:Key="RuDangerColor">#A73A3A</Color>

<SolidColorBrush x:Key="RuCanvasBrush" Color="{StaticResource RuCanvasColor}" />
<SolidColorBrush x:Key="RuSidebarBrush" Color="{StaticResource RuSidebarColor}" />
<SolidColorBrush x:Key="RuSurfaceBrush" Color="{StaticResource RuSurfaceColor}" />
<SolidColorBrush x:Key="RuInkBrush" Color="{StaticResource RuInkColor}" />
<SolidColorBrush x:Key="RuMutedBrush" Color="{StaticResource RuMutedColor}" />
<SolidColorBrush x:Key="RuLineBrush" Color="{StaticResource RuLineColor}" />
<SolidColorBrush x:Key="RuAccentBrush" Color="{StaticResource RuAccentColor}" />
<SolidColorBrush x:Key="RuAccentSoftBrush" Color="{StaticResource RuAccentSoftColor}" />
<SolidColorBrush x:Key="RuSuccessBrush" Color="{StaticResource RuSuccessColor}" />
<SolidColorBrush x:Key="RuWarningBrush" Color="{StaticResource RuWarningColor}" />
<SolidColorBrush x:Key="RuDangerBrush" Color="{StaticResource RuDangerColor}" />

<FontFamily x:Key="RuUiFontFamily">Segoe UI Variable</FontFamily>
<FontFamily x:Key="RuDisplayFontFamily">Cambria</FontFamily>
<FontFamily x:Key="RuMonoFontFamily">Consolas</FontFamily>
<CornerRadius x:Key="RuControlCornerRadius">6</CornerRadius>
~~~

Map the existing App*Brush values to these same color resources. Keep the old keys but remove the stale dark literals so all views share one light theme while only the shell and Chat layout are redesigned. Map AppCardCornerRadius, AppControlCornerRadius, and AppPillCornerRadius to 6 as well. Replace the old palette comments so they no longer describe a dark slate or neon-free blue theme. Keep the existing semantic success, warning, and danger keys usable by the status code.

- [ ] Step 4: Run the focused tests and inspect the scoped diff

Run the focused command again. Expected result: PASS. Then run:

~~~powershell
git diff --check -- gui-winui3/App.xaml gui-winui3.Tests/EditorialReadingRoomContractTests.cs
git diff --stat -- gui-winui3/App.xaml gui-winui3.Tests/EditorialReadingRoomContractTests.cs
~~~

- [ ] Step 5: Commit only the clean token files

App.xaml is clean in the starting worktree and the contract test is new, so stage only these paths:

~~~powershell
git add -- gui-winui3/App.xaml gui-winui3.Tests/EditorialReadingRoomContractTests.cs
git diff --cached --check
git commit --only -m "style: add Ru editorial palette tokens" -- gui-winui3/App.xaml gui-winui3.Tests/EditorialReadingRoomContractTests.cs
~~~

### Task 2: Redesign the shell and sidebar without changing navigation

**Files:**

- Modify: gui-winui3/MainWindow.xaml
- Modify: gui-winui3/MainWindow.xaml.cs
- Modify: gui-winui3.Tests/EditorialReadingRoomContractTests.cs

**Interfaces:**

- Consumes the existing NavChat, NavVoice, NavLog, NavDiagnostics, NavSettings, SidebarSessionList, Sessions, StatusText, StatusDot, DotBackend, DotWorker, DotVts, TextBackend, TextWorker, and TextVts fields.
- Produces a ShellStatusRow named element, flat navigation states, and a status tooltip composed from the existing service text fields.

- [ ] Step 1: Add failing shell contract assertions

Append these tests to EditorialReadingRoomContractTests:

~~~csharp
[Theory]
[InlineData("MainWindow.xaml", "x:Name=\"ShellStatusRow\"")]
[InlineData("MainWindow.xaml", "Background=\"{StaticResource RuSidebarBrush}\"")]
[InlineData("MainWindow.xaml", "ToolTipService.ToolTip=\"Đoạn chat mới\"")]
[InlineData("MainWindow.xaml", "AutomationProperties.Name=\"Trò chuyện\"")]
[InlineData("MainWindow.xaml.cs", "RuAccentBrush")]
[InlineData("MainWindow.xaml.cs", "activeBtn.BorderThickness = new Thickness(2, 0, 0, 0)")]
[InlineData("MainWindow.xaml.cs", "UpdateShellStatusTooltip")]
public void Shell_exposes_the_editorial_navigation_contract(string fileName, string marker)
{
    Assert.Contains(marker, File.ReadAllText(FindSource(fileName)), StringComparison.Ordinal);
}

[Fact]
public void Shell_does_not_render_the_old_status_badge_containers()
{
    var source = File.ReadAllText(FindSource("MainWindow.xaml"));

    Assert.DoesNotContain("x:Name=\"StatusBackendBadge\"", source, StringComparison.Ordinal);
    Assert.DoesNotContain("x:Name=\"StatusWorkerBadge\"", source, StringComparison.Ordinal);
    Assert.DoesNotContain("x:Name=\"StatusVtsBadge\"", source, StringComparison.Ordinal);
}
~~~

Keep the existing FindSource helper from Task 1 and add a second path candidate for MainWindow.xaml.cs under gui-winui3.

- [ ] Step 2: Run the shell contract and verify it fails against the current card shell

Run:

~~~powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo --filter FullyQualifiedName~EditorialReadingRoomContractTests
~~~

Expected result: FAIL because the current status containers, active background styling, and old shell resource references are still present.

- [ ] Step 3: Replace the root status bar with the sidebar status row

Keep the title row and ContentFrame. Change the root content rows from Auto,*,Auto to Auto,*, remove the bottom status Border, and place this row at the bottom of the sidebar above the settings action:

~~~xml
<StackPanel x:Name="ShellStatusRow"
            Orientation="Horizontal"
            Spacing="8"
            Padding="10,8,10,12"
            VerticalAlignment="Center"
            ToolTipService.ToolTip="Đang kiểm tra trạng thái dịch vụ">
    <Ellipse x:Name="StatusDot"
             Width="7"
             Height="7"
             Fill="{StaticResource RuSuccessBrush}"
             VerticalAlignment="Center" />
    <TextBlock x:Name="StatusText"
               Text="Hệ thống đã sẵn sàng"
               FontFamily="{StaticResource RuUiFontFamily}"
               FontSize="12"
               Foreground="{StaticResource RuMutedBrush}"
               TextTrimming="CharacterEllipsis" />
    <StackPanel x:Name="StatusServiceDetails" Visibility="Collapsed">
        <TextBlock x:Name="TextBackend" Text=".NET Backend" />
        <TextBlock x:Name="TextWorker" Text="AI Worker" />
        <TextBlock x:Name="TextVts" Text="VTS Live2D" />
        <Ellipse x:Name="DotBackend" />
        <Ellipse x:Name="DotWorker" />
        <Ellipse x:Name="DotVts" />
    </StackPanel>
</StackPanel>
~~~

The collapsed service details preserve the names consumed by UpdateHealthStatusAsync and OnBackendExited; the visible row shows only the overall status, and its tooltip will carry the details. Keep the status fields in the visual tree so existing code-behind compiles without moving service ownership.

- [ ] Step 4: Flatten navigation, branding, and session rows

Apply the following presentation rules to the existing named controls:

~~~xml
<Button x:Name="NavChat"
        Background="Transparent"
        BorderBrush="Transparent"
        BorderThickness="0"
        CornerRadius="0"
        Foreground="{StaticResource RuInkBrush}"
        Padding="10,8"
        HorizontalContentAlignment="Left" />
~~~

Use the same flat defaults for the other navigation buttons and NavNewChat. Keep the existing icons, tags, click handlers, tooltips, and automation names. Remove the BrandModeBadge element. Set the session ListViewItem corner radius to zero, use RuSidebarBrush/RuAccentSoftBrush only for hover or selected-state presentation, and keep PointerEntered, PointerExited, OnDeleteSessionClick, and SessionInfo bindings unchanged. Remove all StatusBackendBadge, StatusWorkerBadge, and StatusVtsBadge containers.

- [ ] Step 5: Make the active navigation rule explicit in code-behind

Replace the existing background-based SetActiveNav body with this behavior:

~~~csharp
private void SetActiveNav(Button activeBtn)
{
    var transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    var muted = (SolidColorBrush)Application.Current.Resources["RuMutedBrush"];
    var ink = (SolidColorBrush)Application.Current.Resources["RuInkBrush"];
    var accent = (SolidColorBrush)Application.Current.Resources["RuAccentBrush"];

    foreach (var button in new[] { NavChat, NavVoice, NavLog, NavDiagnostics, NavSettings })
    {
        button.Background = transparent;
        button.Foreground = muted;
        button.BorderBrush = transparent;
        button.BorderThickness = new Thickness(0);
    }

    activeBtn.Background = transparent;
    activeBtn.Foreground = ink;
    activeBtn.BorderBrush = accent;
    activeBtn.BorderThickness = new Thickness(2, 0, 0, 0);
}
~~~

Do not alter the tag switch, ContentFrame.Content, NavigateToDiagnostics, session selection, or new-session handlers.

- [ ] Step 6: Compose the status tooltip at existing status update seams

Add this method to MainWindow.xaml.cs:

~~~csharp
private void UpdateShellStatusTooltip()
{
    if (ShellStatusRow == null) return;

    var details = string.Join(
        Environment.NewLine,
        new[] { TextBackend.Text, TextWorker.Text, TextVts.Text }
            .Where(value => !string.IsNullOrWhiteSpace(value)));

    ToolTipService.SetToolTip(
        ShellStatusRow,
        string.IsNullOrWhiteSpace(details) ? StatusText.Text : details);
}
~~~

Call it after all three service text fields are assigned in UpdateHealthStatusAsync and OnBackendExited. Do not add a new timer, API call, worker request, or polling path.

- [ ] Step 7: Preserve narrow visual states and run the focused test

Update the existing Narrow setters to hide StatusText and StatusServiceDetails, collapse BrandModeBadge references that no longer exist, keep the 72 px rail, and preserve all tooltip/automation names. Remove setters targeting the deleted badge containers. Run:

~~~powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo --filter FullyQualifiedName~EditorialReadingRoomContractTests
~~~

Expected result: PASS. Inspect the XAML and C# diff to confirm the current responsive and diagnostic changes remain present.

- [ ] Step 8: Create a safe shell checkpoint

Run:

~~~powershell
git diff --check -- gui-winui3/MainWindow.xaml gui-winui3/MainWindow.xaml.cs gui-winui3.Tests/EditorialReadingRoomContractTests.cs
git diff -- gui-winui3/MainWindow.xaml gui-winui3/MainWindow.xaml.cs
~~~

Stage only the new shell hunks. If the pre-existing dirty hunks cannot be separated safely, leave the shell files unstaged and record that the task is verified but not committed; do not commit the whole files.

### Task 3: Convert Chat to the document stream and reading-column composer

**Files:**

- Modify: gui-winui3/Views/ChatView.xaml
- Modify: gui-winui3/Views/ChatView.xaml.cs
- Modify: gui-winui3.Tests/EditorialReadingRoomContractTests.cs

**Interfaces:**

- Consumes the existing ChatMessageItem properties Role, Content, IsError, ShowTypingDots, ShowContent, and the existing Messages collection.
- Produces the existing UserTemplate, AssistantTemplate, WelcomeHeroPanel, SuggestionPanel, ChatContentGrid, InputBorder, MicButton, and SendButton with the approved editorial presentation and unchanged handlers.

- [ ] Step 1: Add failing Chat presentation contracts

Extend FindSource with a gui-winui3/Views candidate, then append these tests:

~~~csharp
[Theory]
[InlineData("ChatView.xaml", "x:Name=\"ReadingColumn\"")]
[InlineData("ChatView.xaml", "x:Name=\"WelcomeIntro\"")]
[InlineData("ChatView.xaml", "Background=\"{StaticResource RuCanvasBrush}\"")]
[InlineData("ChatView.xaml", "Text=\"Viết cho Ru...\"")]
[InlineData("ChatView.xaml.cs", "var promptText = button.Tag as string")]
[InlineData("ChatView.xaml.cs", "ReadingColumnMaxWidth = 820")]
public void Chat_exposes_the_reading_room_contract(string fileName, string marker)
{
    Assert.Contains(marker, File.ReadAllText(FindSource(fileName)), StringComparison.Ordinal);
}

[Fact]
public void Chat_removes_the_old_bubble_and_capsule_markers()
{
    var source = File.ReadAllText(FindSource("ChatView.xaml"));

    Assert.DoesNotContain("CornerRadius=\"26\"", source, StringComparison.Ordinal);
    Assert.DoesNotContain("CornerRadius=\"18\"", source, StringComparison.Ordinal);
    Assert.DoesNotContain("Text=\"Ru đang ở đây cùng anh nè ~\"", source, StringComparison.Ordinal);
    Assert.DoesNotContain("Dot1Storyboard", source, StringComparison.Ordinal);
    Assert.DoesNotContain("Dot2Storyboard", source, StringComparison.Ordinal);
    Assert.DoesNotContain("Dot3Storyboard", source, StringComparison.Ordinal);
}
~~~

Keep ShowTypingDots in the model and use it to control the new caret visibility through a different XAML marker.

- [ ] Step 2: Run the focused test and verify the expected failure

Run the focused EditorialReadingRoomContractTests filter. Expected result: FAIL because the current Chat still uses the old centered card, 26 px composer capsule, 18 px welcome emblem, and three-dot storyboard.

- [ ] Step 3: Replace the message templates with flat document blocks

Keep the existing DataTemplate keys and ChatMessageTemplateSelector, but use this structure:

~~~xml
<DataTemplate x:Key="UserMessageTemplate" x:DataType="models:ChatMessageItem">
    <Grid Margin="0,16" HorizontalAlignment="Stretch">
        <Border MaxWidth="560"
                HorizontalAlignment="Right"
                Background="{StaticResource RuSurfaceBrush}"
                Padding="14,10">
            <controls:MarkdownBlock MarkdownText="{x:Bind Content, Mode=OneWay}"
                                    FontSize="16"
                                    Foreground="{StaticResource RuInkBrush}"
                                    FontFamily="{StaticResource RuUiFontFamily}" />
        </Border>
    </Grid>
</DataTemplate>

<DataTemplate x:Key="AssistantMessageTemplate" x:DataType="models:ChatMessageItem">
    <Grid Margin="0,18" HorizontalAlignment="Stretch">
        <StackPanel MaxWidth="700" HorizontalAlignment="Left" Spacing="6">
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
</DataTemplate>
~~~

Wrap the welcome panel and ChatScrollHost in a Grid named ReadingColumn at Grid.Row 0. Give RootGrid a RuCanvasBrush background. ReadingColumn is the width-controlled reading surface; InputContainer remains its sibling at Grid.Row 1. Use this nesting so the width helper controls both empty and populated states:

~~~xml
<Grid x:Name="ReadingColumn"
      Grid.Row="0"
      HorizontalAlignment="Center"
      Background="Transparent">
    <StackPanel x:Name="WelcomeHeroPanel" />
    <ScrollViewer x:Name="ChatScrollHost" />
</Grid>
~~~

The user block uses a paper tint without an enclosing border or rounded bubble. The assistant has no avatar. The caret is static, so reduced-motion settings are honored without a new animation loop. Preserve the existing ShowTypingDots, ShowContent, and IsError bindings through the new visual structure; only the visible template changes.

- [ ] Step 4: Replace the welcome card and suggestions without breaking the current handler

Keep the WelcomeHeroPanel name so UpdateViewMode remains unchanged. Replace its Border with a StackPanel named WelcomeIntro, aligned to the reading column and using the approved copy:

~~~xml
<StackPanel x:Name="WelcomeHeroPanel"
            Grid.Row="0"
            MaxWidth="820"
            HorizontalAlignment="Center"
            VerticalAlignment="Center"
            Spacing="18"
            Margin="24,16">
    <TextBlock Text="Ru"
               FontFamily="{StaticResource RuDisplayFontFamily}"
               FontSize="14"
               Foreground="{StaticResource RuAccentBrush}" />
    <TextBlock x:Name="WelcomeIntro"
               Text="Chào anh. Hôm nay mình bắt đầu từ đâu?"
               FontFamily="{StaticResource RuDisplayFontFamily}"
               FontSize="28"
               Foreground="{StaticResource RuInkBrush}"
               TextWrapping="Wrap" />
    <StackPanel x:Name="SuggestionPanel" Orientation="Horizontal" Spacing="24">
        <Button Tag="Hỏi về một việc đang làm"
                Click="OnSuggestionClick"
                Content="Hỏi về một việc đang làm"
                Background="Transparent"
                BorderThickness="0"
                Foreground="{StaticResource RuMutedBrush}"
                Padding="0,8"
                ToolTipService.ToolTip="Hỏi về một việc đang làm" />
        <Button Tag="Nói cho Ru nghe"
                Click="OnSuggestionClick"
                Content="Nói cho Ru nghe"
                Background="Transparent"
                BorderThickness="0"
                Foreground="{StaticResource RuMutedBrush}"
                Padding="0,8"
                ToolTipService.ToolTip="Nói cho Ru nghe" />
    </StackPanel>
</StackPanel>
~~~

Update OnSuggestionClick to read the explicit tag instead of assuming a StackPanel child at a fixed index:

~~~csharp
private async void OnSuggestionClick(object sender, RoutedEventArgs e)
{
    if (sender is not Button button)
    {
        return;
    }

    var promptText = button.Tag as string;
    if (string.IsNullOrWhiteSpace(promptText))
    {
        return;
    }

    ChatInputTextBox.Text = promptText;
    await SendCurrentMessageAsync();
}
~~~

This changes only how the existing suggestion text is read. It keeps the same send path and event handler.

- [ ] Step 5: Make the composer flat and preserve input behavior

Keep InputContainer, InputBorder, MicButton, ChatInputTextBox, SendButton, their click handlers, tooltips, and keyboard event. Replace the capsule styling with:

~~~xml
<Grid x:Name="InputContainer"
      Grid.Row="1"
      Width="820"
      MaxWidth="820"
      HorizontalAlignment="Center"
      Padding="0,8,0,24">
    <Border x:Name="InputBorder"
            Background="{StaticResource RuSurfaceBrush}"
            BorderBrush="{StaticResource RuLineBrush}"
            BorderThickness="0,1,0,0"
            CornerRadius="0"
            Padding="4,4">
        <Grid ColumnDefinitions="Auto,*,Auto">
            <Button x:Name="MicButton"
                    Grid.Column="0"
                    Click="OnMicClick"
                    Width="36"
                    Height="36"
                    Background="Transparent"
                    BorderThickness="0"
                    ToolTipService.ToolTip="Nói cho Ru nghe (STT)" />
            <TextBox x:Name="ChatInputTextBox"
                     Grid.Column="1"
                     PlaceholderText="Viết cho Ru..."
                     PreviewKeyDown="OnChatInputPreviewKeyDown"
                     AcceptsReturn="True"
                     MaxHeight="120"
                     Background="Transparent"
                     BorderThickness="0"
                     FontFamily="{StaticResource RuUiFontFamily}"
                     FontSize="16"
                     Foreground="{StaticResource RuInkBrush}" />
            <Button x:Name="SendButton"
                    Grid.Column="2"
                    Click="OnSendClick"
                    Width="36"
                    Height="36"
                    Background="{StaticResource RuAccentBrush}"
                    BorderThickness="0"
                    ToolTipService.ToolTip="Gửi tin nhắn" />
        </Grid>
    </Border>
</Grid>
~~~

Use the existing MicIcon and send FontIcon children inside the buttons; do not change their glyph handlers or enabled-state code.

- [ ] Step 6: Update the width calculation to the 820 px reading column

Change the current constant and helper to:

~~~csharp
private const double ReadingColumnMaxWidth = 820;

private void ApplyInputWidth()
{
    if (RootGrid == null || RootGrid.ActualWidth <= 0) return;

    double horizontalInset = RootGrid.ActualWidth < 900
        ? 24
        : RootGrid.ActualWidth < 1280
            ? 48
            : 96;
    double availableWidth = Math.Max(160, RootGrid.ActualWidth - horizontalInset);
    double targetWidth = Math.Min(ReadingColumnMaxWidth, availableWidth);

    if (ReadingColumn != null) ReadingColumn.Width = targetWidth;
    if (WelcomeHeroPanel != null) WelcomeHeroPanel.Width = targetWidth;
    if (InputContainer != null) InputContainer.Width = targetWidth;
    if (InputBorder != null) InputBorder.Width = targetWidth;
    if (ChatContentGrid != null) ChatContentGrid.Width = targetWidth;
    if (ChatListView != null) ChatListView.Width = targetWidth;
}
~~~

Keep RootGrid.SizeChanged, Loaded, vertical-only scroll settings, and ScrollChatToBottom unchanged. The InputContainer width is updated together with the message list so the composer and reading column stay aligned.

- [ ] Step 7: Preserve the approved responsive setters and run focused tests

Keep Wide, Compact, and Narrow visual states. In Narrow, set SuggestionPanel.Orientation to Vertical, set WelcomeHeroPanel and InputContainer to the approved 12/16 px content insets, and keep ChatScrollHost horizontal scrolling disabled. Run:

~~~powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo --filter FullyQualifiedName~EditorialReadingRoomContractTests
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo --filter FullyQualifiedName~ChatViewNoEmojiContractTests
~~~

Expected result: PASS. Inspect ChatView.xaml.cs to confirm SendCurrentMessageAsync, WebSocket callbacks, STT, voice playback, and session loading were not changed except for the suggestion-text extraction and width helper.

- [ ] Step 8: Create a safe Chat checkpoint

Run git diff --check and inspect the Chat diff. Stage only the new Chat presentation and suggestion/width hunks. If the current responsive Chat changes cannot be separated safely, leave the file unstaged and record the verified checkpoint instead of committing unrelated changes.

### Task 4: Apply explicit editorial typography to Markdown output

**Files:**

- Modify: gui-winui3/Controls/MarkdownBlock.cs
- Modify: gui-winui3.Tests/EditorialReadingRoomContractTests.cs

**Interfaces:**

- Consumes the existing MarkdownText, FontSize, and Foreground properties.
- Produces body runs in Segoe UI Variable, headings in Cambria, inline/code runs in Consolas, and the same Markdown content/selection behavior as before.

- [ ] Step 1: Add failing Markdown typography contracts

Append:

~~~csharp
[Theory]
[InlineData("MarkdownBlock.cs", "new FontFamily(\"Segoe UI Variable\")")]
[InlineData("MarkdownBlock.cs", "new FontFamily(\"Cambria\")")]
[InlineData("MarkdownBlock.cs", "new FontFamily(\"Consolas\")")]
[InlineData("MarkdownBlock.cs", "RegisterPropertyChangedCallback")]
public void Markdown_renderer_has_explicit_editorial_typography(string fileName, string marker)
{
    Assert.Contains(marker, File.ReadAllText(FindSource(fileName)), StringComparison.Ordinal);
}
~~~

Add a Controls candidate to FindSource so it resolves MarkdownBlock.cs from gui-winui3/Controls.

- [ ] Step 2: Run the focused contract and verify the expected failure

Run the EditorialReadingRoomContractTests filter. Expected result: FAIL because the current renderer only hard-codes Consolas for code and does not register a foreground update seam.

- [ ] Step 3: Add explicit font families and inherited foreground synchronization

In MarkdownBlock, define these fields:

~~~csharp
private static readonly FontFamily UiFont = new FontFamily("Segoe UI Variable");
private static readonly FontFamily DisplayFont = new FontFamily("Cambria");
private static readonly FontFamily MonoFont = new FontFamily("Consolas");
~~~

In the constructor, set _richTextBlock.FontFamily = UiFont and register a Control.ForegroundProperty callback that assigns the current Foreground brush to _richTextBlock.Foreground. Pass DisplayFont for #, ##, and ### heading runs, UiFont for body/list runs, and MonoFont for fenced and inline code. Keep the existing parsing rules, text selection, and MarkdownText property callback.

Use the current FontSize value as the body size when it is positive; derive heading sizes by adding 2, 4, and 6 px to that body size. This makes Chat's FontSize="16" effective without changing the parser's visible content.

- [ ] Step 4: Run the focused test and check the dirty-file diff

Run the focused contract filter and git diff --check -- gui-winui3/Controls/MarkdownBlock.cs. Confirm the existing dirty changes in this file are still present and that only the font/foreground seam was added for this task.

### Task 5: Update the current runtime documentation and run the full UI gates

**Files:**

- Modify: docs/operations/runtime-behavior.md

**Interfaces:**

- Documents the already-implemented responsive shell tiers and the new phase-one visual scope without exposing private voice/VTS metadata or changing runtime contracts.

- [ ] Step 1: Update the existing WinUI responsive-shell section

After the existing responsive-tier paragraph, add this exact content:

~~~markdown
The phase-one Chat redesign uses the same tiers. The shell and Chat use the
Editorial reading room presentation: a light mineral canvas, a flat sidebar,
and a centered document-style reading column. Voice/VTS, Log, Diagnostics,
Settings, and Memory retain their existing layout in this phase.

The redesign is presentation-only. It does not recreate message or session
sources, call the backend during layout, alter WebSocket ordering, change STT,
voice playback, subtitles, VTube Studio control, or persist a new UI setting.
Automated source/XAML contracts and the WinUI build cover structural
correctness. Visual resizing and live audio/VTS behavior remain manual
acceptance checks.
~~~

Do not remove the existing historical or operations documents. Review the documentation index and current files after the edit; retire nothing unless it is clearly superseded and explicitly within this task.

- [ ] Step 2: Run the focused UI contract suite sequentially

Run:

~~~powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo --filter FullyQualifiedName~EditorialReadingRoomContractTests
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo --filter FullyQualifiedName~ChatViewNoEmojiContractTests
~~~

Expected result: all new token, shell, Chat, Markdown, and no-emoji contracts pass.

- [ ] Step 3: Run the complete WinUI test project

Run:

~~~powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo
~~~

Expected result: PASS. If a failure comes from an unrelated dirty change or an environment/import/build issue, report it separately from redesign failures and do not modify unrelated code to make the gate green.

- [ ] Step 4: Build the WinUI Release configuration

Run the repository's existing build command sequentially:

~~~powershell
cmd /c D:\LLM\build.bat
~~~

Expected result: Release build succeeds with no XAML compiler errors. Do not launch the resulting app automatically.

- [ ] Step 5: Run the scoped final checks

Run:

~~~powershell
git diff --check
git status --short --branch
git diff --stat -- gui-winui3/App.xaml gui-winui3/MainWindow.xaml gui-winui3/MainWindow.xaml.cs gui-winui3/Views/ChatView.xaml gui-winui3/Views/ChatView.xaml.cs gui-winui3/Controls/MarkdownBlock.cs gui-winui3.Tests/EditorialReadingRoomContractTests.cs docs/operations/runtime-behavior.md
~~~

Confirm that no unrelated file was staged, deleted, or overwritten. Preserve all pre-existing dirty changes.

- [ ] Step 6: Hand off the manual visual acceptance matrix

Leave RuApp and VTube Studio closed unless the user starts them. Ask the user to check these sizes and flows manually:

| Size | Required checks |
| --- | --- |
| 1440x900 | Flat sidebar, reading column width, empty intro, long Markdown response, composer alignment |
| 1024x768 | Compact labels, recent sessions, active rule, streaming response, no clipping |
| 800x600 | 72 px rail, tooltip names, stacked suggestions, full-width composer, vertical scrolling |
| 640x600 | No horizontal scrollbar, focus order, send/mic hit targets, no clipped prompt or response |

Across all sizes, verify new chat, session selection, session deletion, Enter, Shift+Enter, mic, streaming, inline error, and status tooltip. Confirm that voice playback, subtitles, VTS state, request latency, and session history behave as before. These are user-run acceptance checks, not claims proven by the automated suite.

---

## Plan self-review checklist

- Spec coverage: token palette and legacy mapping are covered by Task 1; shell/sidebar/status and responsive preservation by Task 2; document transcript, welcome state, composer, and width behavior by Task 3; Markdown typography and reduced-motion-safe loading presentation by Task 4; documentation, build, and manual acceptance by Task 5.
- Scope check: every code path is limited to App resources, MainWindow presentation/status styling, Chat rendering/width/suggestion extraction, Markdown rendering, tests, and the current runtime documentation. No backend or runtime contract task is present.
- Type consistency: XAML uses the exact resource names from Task 1; shell code uses ShellStatusRow, StatusText, TextBackend, TextWorker, and TextVts; Chat code uses ReadingColumnMaxWidth, InputContainer, InputBorder, and button.Tag; Markdown code uses the three declared FontFamily fields.
- Dirty-worktree safety: the plan explicitly forbids committing whole dirty GUI files and provides a patch-based staging checkpoint.
- Verification evidence: focused tests, full WinUI tests, Release build, diff check, status inspection, and user-run visual/audio/VTS acceptance are all separate gates.
