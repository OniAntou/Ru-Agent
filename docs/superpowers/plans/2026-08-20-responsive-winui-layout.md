# Responsive WinUI Layout Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the Ru WinUI 3 shell and its content pages resize cleanly from a wide desktop layout to compact and narrow layouts without clipping controls or adding runtime latency.

**Architecture:** Use a pure `ResponsiveLayoutPolicy` to centralize the three shell breakpoints and sidebar bounds. Use WinUI `VisualStateManager`/`AdaptiveTrigger` groups in the shell and each affected view for presentation-only changes; keep the existing API, timers, data sources, audio path, and logging behavior untouched. The current sidebar pointer-resizer remains imperative C# because it owns a live `GridLength`, while page reflow is declarative XAML.

**Tech Stack:** WinUI 3/XAML, C#/.NET 10, xUnit, PowerShell, existing `gui-winui3.Tests` project.

## Global Constraints

- Breakpoints are exact: `Wide >= 1280`, `Compact 900–1279`, `Narrow < 900` device-independent pixels.
- Wide sidebar bounds are `180–400` px with a `250` px default; compact bounds are `180–320` px; narrow rail is exactly `72` px.
- No third-party UI dependency and no `NavigationView` migration.
- No changes to ASP.NET Core routes, Python worker behavior, API contracts, logging timers, voice playback, STT/VTS behavior, or memory/business logic.
- Responsive transitions must not call `ApiService`, touch the worker, restart timers, reload views, recreate `ItemsSource`, or add layout animations.
- Ru chat remains free of Unicode emoji and ASCII emoticons; responsive work must not add user-visible technical metadata.
- Work stays on the existing `main` checkout and must preserve unrelated dirty files. Stage only files named by the current task.
- Heavy verification runs sequentially on Windows to avoid the known pagefile/OOM failure mode.
- Automated checks prove source/layout contracts; actual window resizing and visual acceptance remain user-run checks.

---

## File map and responsibilities

Create:

- `gui-winui3/Services/ResponsiveLayoutPolicy.cs` — breakpoint enum, immutable metrics, and sidebar clamping helpers.
- `gui-winui3.Tests/ResponsiveLayoutPolicyTests.cs` — boundary and clamping tests.
- `gui-winui3.Tests/ResponsiveLayoutContractTests.cs` — XAML/source contracts for shell and affected views.

Modify:

- `gui-winui3/MainWindow.xaml` / `.xaml.cs` — root size observation, shell visual states, sidebar tier bounds, title/status compaction, and lifetime-only width restoration.
- `gui-winui3/Views/ChatView.xaml` / `.xaml.cs` — responsive welcome actions and input/message insets.
- `gui-winui3/Views/LogView.xaml` / `.xaml.cs` — responsive header actions and stable vertical timeline cards.
- `gui-winui3/Views/DiagnosticsView.xaml` / `.xaml.cs` — responsive filters/status metrics and stable diagnostic cards.
- `gui-winui3/Views/VoiceView.xaml` — responsive form rows, action buttons, sliders, and test-voice row.
- `gui-winui3/Views/SettingsView.xaml` — responsive setting rows, model/API fields, observation fields, and VTS fields.
- `docs/operations/runtime-behavior.md` — supported responsive tiers and automated/manual acceptance boundary.

Do not modify the existing unrelated dirty files listed by `git status`, and do not add a new runtime service or backend endpoint.

---

### Task 1: Add the responsive policy with tests first

**Files:**

- Create: `gui-winui3/Services/ResponsiveLayoutPolicy.cs`
- Test: `gui-winui3.Tests/ResponsiveLayoutPolicyTests.cs`

**Interfaces:**

- Produces `RuApp.Services.ResponsiveLayoutTier` with values `Wide`, `Compact`, and `Narrow`.
- Produces `RuApp.Services.ResponsiveLayoutMetrics` with properties `Tier`, `DefaultSidebarWidth`, `MinSidebarWidth`, `MaxSidebarWidth`, `NarrowRailWidth`, and `PageHorizontalPadding`.
- Produces `ResponsiveLayoutPolicy.GetForWidth(double width)` and `ResponsiveLayoutPolicy.ClampSidebarWidth(double width, ResponsiveLayoutMetrics metrics)`.

- [ ] **Step 1: Write the failing policy tests**

Create the test class with these exact assertions:

```csharp
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class ResponsiveLayoutPolicyTests
{
    [Theory]
    [InlineData(0, ResponsiveLayoutTier.Narrow)]
    [InlineData(899.99, ResponsiveLayoutTier.Narrow)]
    [InlineData(900, ResponsiveLayoutTier.Compact)]
    [InlineData(1279.99, ResponsiveLayoutTier.Compact)]
    [InlineData(1280, ResponsiveLayoutTier.Wide)]
    [InlineData(1920, ResponsiveLayoutTier.Wide)]
    public void Selects_the_expected_tier_at_each_boundary(double width, ResponsiveLayoutTier expected)
    {
        Assert.Equal(expected, ResponsiveLayoutPolicy.GetForWidth(width).Tier);
    }

    [Fact]
    public void Returns_the_wide_metrics()
    {
        var metrics = ResponsiveLayoutPolicy.GetForWidth(1440);

        Assert.Equal(ResponsiveLayoutTier.Wide, metrics.Tier);
        Assert.Equal(250, metrics.DefaultSidebarWidth);
        Assert.Equal(180, metrics.MinSidebarWidth);
        Assert.Equal(400, metrics.MaxSidebarWidth);
        Assert.Equal(72, metrics.NarrowRailWidth);
        Assert.Equal(36, metrics.PageHorizontalPadding);
    }

    [Fact]
    public void Clamps_a_saved_sidebar_width_to_the_current_tier()
    {
        var compact = ResponsiveLayoutPolicy.GetForWidth(1024);

        Assert.Equal(180, ResponsiveLayoutPolicy.ClampSidebarWidth(100, compact));
        Assert.Equal(250, ResponsiveLayoutPolicy.ClampSidebarWidth(250, compact));
        Assert.Equal(320, ResponsiveLayoutPolicy.ClampSidebarWidth(400, compact));
    }

    [Fact]
    public void Narrow_metrics_force_the_icon_rail_width()
    {
        var narrow = ResponsiveLayoutPolicy.GetForWidth(640);

        Assert.Equal(72, narrow.DefaultSidebarWidth);
        Assert.Equal(72, narrow.MinSidebarWidth);
        Assert.Equal(72, narrow.MaxSidebarWidth);
        Assert.Equal(12, narrow.PageHorizontalPadding);
        Assert.Equal(72, ResponsiveLayoutPolicy.ClampSidebarWidth(400, narrow));
    }
}
```

- [ ] **Step 2: Run the focused test and verify it fails**

Run:

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo --filter FullyQualifiedName~ResponsiveLayoutPolicyTests
```

Expected: FAIL because `ResponsiveLayoutTier`, `ResponsiveLayoutMetrics`, and `ResponsiveLayoutPolicy` do not exist yet.

- [ ] **Step 3: Implement the minimal policy**

Implement the following shape in `ResponsiveLayoutPolicy.cs`:

```csharp
namespace RuApp.Services;

public enum ResponsiveLayoutTier
{
    Wide,
    Compact,
    Narrow
}

public readonly record struct ResponsiveLayoutMetrics(
    ResponsiveLayoutTier Tier,
    double DefaultSidebarWidth,
    double MinSidebarWidth,
    double MaxSidebarWidth,
    double NarrowRailWidth,
    double PageHorizontalPadding);

public static class ResponsiveLayoutPolicy
{
    public static ResponsiveLayoutMetrics GetForWidth(double width)
    {
        if (double.IsNaN(width))
        {
            width = 0;
        }

        if (width < 900)
        {
            return new(ResponsiveLayoutTier.Narrow, 72, 72, 72, 72, 12);
        }

        if (width < 1280)
        {
            return new(ResponsiveLayoutTier.Compact, 210, 180, 320, 72, 24);
        }

        return new(ResponsiveLayoutTier.Wide, 250, 180, 400, 72, 36);
    }

    public static double ClampSidebarWidth(double width, ResponsiveLayoutMetrics metrics)
        => Math.Clamp(width, metrics.MinSidebarWidth, metrics.MaxSidebarWidth);
}
```

Keep the policy pure: it must not reference XAML controls, `Window`, `ApiService`, timers, or filesystem state.

- [ ] **Step 4: Run the focused test and verify it passes**

Run the same focused `dotnet test` command. Expected: all policy tests PASS.

- [ ] **Step 5: Commit only the policy slice**

```powershell
git add -- gui-winui3/Services/ResponsiveLayoutPolicy.cs gui-winui3.Tests/ResponsiveLayoutPolicyTests.cs
git diff --cached --name-only
git commit -m "feat: add responsive layout policy"
```

The staged-name check must list exactly the two task files.

---

### Task 2: Make the main shell tier-aware without touching services

**Files:**

- Modify: `gui-winui3/MainWindow.xaml`
- Modify: `gui-winui3/MainWindow.xaml.cs`
- Modify: `gui-winui3.Tests/ResponsiveLayoutContractTests.cs`

**Interfaces:**

- Consumes `ResponsiveLayoutPolicy.GetForWidth` and `ResponsiveLayoutPolicy.ClampSidebarWidth` from Task 1.
- Produces `RootLayoutGrid_SizeChanged`, `ApplyResponsiveShell(double width)`, and a lifetime-only `_lastExpandedSidebarWidth` state in `MainWindow`.

- [ ] **Step 1: Add failing shell contract tests**

Create a source contract test that reads the repository files and asserts:

```csharp
using Xunit;

namespace RuApp.Tests;

public sealed class ResponsiveLayoutContractTests
{
    [Theory]
    [InlineData("MainWindow.xaml", "x:Name=\"RootLayoutGrid\"")]
    [InlineData("MainWindow.xaml", "SizeChanged=\"RootLayoutGrid_SizeChanged\"")]
    [InlineData("MainWindow.xaml", "VisualState x:Name=\"Wide\"")]
    [InlineData("MainWindow.xaml", "VisualState x:Name=\"Compact\"")]
    [InlineData("MainWindow.xaml", "VisualState x:Name=\"Narrow\"")]
    [InlineData("MainWindow.xaml", "AdaptiveTrigger MinWindowWidth=\"900\"")]
    [InlineData("MainWindow.xaml", "AdaptiveTrigger MinWindowWidth=\"1280\"")]
    [InlineData("MainWindow.xaml.cs", "ResponsiveLayoutPolicy.GetForWidth")]
    [InlineData("MainWindow.xaml.cs", "_lastExpandedSidebarWidth")]
    public void Main_shell_contains_the_responsive_boundary(string fileName, string expectedText)
    {
        Assert.Contains(expectedText, File.ReadAllText(FindSource(fileName)), StringComparison.Ordinal);
    }

    [Fact]
    public void Responsive_shell_does_not_add_a_horizontal_scroll_viewer()
    {
        var source = File.ReadAllText(FindSource("MainWindow.xaml"));
        Assert.DoesNotContain("HorizontalScrollBarVisibility=\"Auto\"", source, StringComparison.Ordinal);
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
```

- [ ] **Step 2: Run the focused contract test and verify it fails**

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo --filter FullyQualifiedName~ResponsiveLayoutContractTests
```

Expected: FAIL on the missing root/state/policy markers.

- [ ] **Step 3: Add the shell visual states**

In `MainWindow.xaml`:

1. Name the main row-1 grid `RootLayoutGrid` and attach `SizeChanged="RootLayoutGrid_SizeChanged"`.
2. Add a `VisualStateGroup x:Name="ResponsiveStates"` to `RootLayoutGrid` with `Wide`, `Compact`, and `Narrow` states. Use `AdaptiveTrigger MinWindowWidth="0"`, `900`, and `1280` respectively.
3. In `Compact`, reduce sidebar button padding/gaps, retain labels, hide no navigation item, and keep service badge text visible.
4. In `Narrow`, set the sidebar navigation labels and recent heading to `Collapsed`, set navigation button content alignment to `Center`, hide the title-bar `Live2D Companion` badge, reduce the status bar to dots/tooltips, and set the resizer hit target to `Collapsed`.
5. Add `ToolTipService.ToolTip` and `AutomationProperties.Name` to every icon-only navigation control.
6. Add `TextTrimming="CharacterEllipsis"` and existing title tooltips to session items so the icon rail never exposes an unbounded text block.

Keep all current navigation `Click`, session selection, delete, and status-binding handlers unchanged.

- [ ] **Step 4: Add the tier-aware sidebar size handler**

In `MainWindow.xaml.cs`, add:

```csharp
private ResponsiveLayoutTier? _responsiveTier;
private double _lastExpandedSidebarWidth = 250;

private void RootLayoutGrid_SizeChanged(object sender, SizeChangedEventArgs e)
{
    if (e.NewSize.Width <= 0)
    {
        return;
    }

    ApplyResponsiveShell(e.NewSize.Width);
}

private void ApplyResponsiveShell(double width)
{
    var metrics = ResponsiveLayoutPolicy.GetForWidth(width);
    if (_responsiveTier == metrics.Tier)
    {
        return;
    }

    if (metrics.Tier == ResponsiveLayoutTier.Narrow)
    {
        if (_responsiveTier is not null && SidebarColumn.ActualWidth > 0)
        {
            _lastExpandedSidebarWidth = SidebarColumn.ActualWidth;
        }

        SidebarColumn.MinWidth = metrics.MinSidebarWidth;
        SidebarColumn.MaxWidth = metrics.MaxSidebarWidth;
        SidebarColumn.Width = new GridLength(metrics.NarrowRailWidth);
    }
    else
    {
        SidebarColumn.MinWidth = metrics.MinSidebarWidth;
        SidebarColumn.MaxWidth = metrics.MaxSidebarWidth;
        var restoredWidth = _responsiveTier == ResponsiveLayoutTier.Narrow
            ? _lastExpandedSidebarWidth
            : SidebarColumn.ActualWidth > 0 ? SidebarColumn.ActualWidth : metrics.DefaultSidebarWidth;
        SidebarColumn.Width = new GridLength(
            ResponsiveLayoutPolicy.ClampSidebarWidth(restoredWidth, metrics));
    }

    _responsiveTier = metrics.Tier;
}
```

Do not call `ApplyResponsiveShell` from polling, navigation, health updates, or backend startup. The `SizeChanged` path may update only shell properties and must not call `_apiService`, `_wsClient`, `_voicePlayback`, or any timer method. Keep the existing pointer-resizer bounds check; it will use the current tier's `MinWidth` and `MaxWidth`.

- [ ] **Step 5: Run shell contracts and a GUI build**

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo --filter FullyQualifiedName~ResponsiveLayoutContractTests
dotnet build D:\LLM\gui-winui3\RuApp.csproj --nologo
```

Expected: focused contracts PASS and the WinUI project builds without warnings/errors introduced by the shell changes.

- [ ] **Step 6: Commit only the shell slice**

```powershell
git add -- gui-winui3/MainWindow.xaml gui-winui3/MainWindow.xaml.cs gui-winui3.Tests/ResponsiveLayoutContractTests.cs
git diff --cached --name-only
git commit -m "feat: add responsive shell states"
```

---

### Task 3: Make Chat use the available width safely

**Files:**

- Modify: `gui-winui3/Views/ChatView.xaml`
- Modify: `gui-winui3/Views/ChatView.xaml.cs`
- Modify: `gui-winui3.Tests/ResponsiveLayoutContractTests.cs`

**Interfaces:**

- Consumes the existing `ChatView.RootGrid_SizeChanged`/`ApplyInputWidth` path and `ResponsiveLayoutPolicy` thresholds through XAML `AdaptiveTrigger` states.
- Produces no new chat/API protocol and no new user-visible metadata.

- [ ] **Step 1: Add failing Chat layout contracts**

Extend `ResponsiveLayoutContractTests` with:

```csharp
[Theory]
[InlineData("ChatView.xaml", "x:Name=\"SuggestionPanel\"")]
[InlineData("ChatView.xaml", "HorizontalScrollBarVisibility=\"Disabled\"")]
[InlineData("ChatView.xaml", "VisualState x:Name=\"Narrow\"")]
[InlineData("ChatView.xaml.cs", "ApplyInputWidth")]
[InlineData("ChatView.xaml.cs", "RootGrid.ActualWidth")]
public void Chat_preserves_vertical_flow_and_has_responsive_markers(string fileName, string marker)
{
    Assert.Contains(marker, File.ReadAllText(FindSource(fileName)), StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run the focused contract and verify it fails**

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo --filter FullyQualifiedName~ResponsiveLayoutContractTests
```

Expected: FAIL because `SuggestionPanel` and the Chat visual states do not exist.

- [ ] **Step 3: Add Chat visual states and safe insets**

In `ChatView.xaml`:

1. Name the quick-topic horizontal `StackPanel` `SuggestionPanel`.
2. Add `ResponsiveStates` to `RootGrid` with `Narrow`, `Compact`, and `Wide` adaptive triggers.
3. In `Compact`, reduce `InputContainer` bottom/side padding and set the welcome panel margin to `16,12`.
4. In `Narrow`, change `SuggestionPanel.Orientation` to `Vertical`, set it to stretch within the welcome card, reduce `InputContainer` padding to `0,4,0,12`, and set the input capsule padding values to remain inside the viewport.
5. Reduce the fixed message side margin from `80` to `48` so narrow message cards keep a readable content column while retaining the current bubble alignment.
6. Keep both scroll viewers vertical-only; do not add an `Auto` horizontal scrollbar.

In `ChatView.xaml.cs`, retain the existing `RootGrid.SizeChanged` subscription but make `ApplyInputWidth` use the current available width:

```csharp
double horizontalInset = RootGrid.ActualWidth < 900 ? 24 : 64;
double availableWidth = Math.Max(160, RootGrid.ActualWidth - horizontalInset);
double targetWidth = Math.Min(InputMaxWidth, availableWidth);
```

Apply the same `targetWidth` to `InputBorder`, `ChatContentGrid`, and `ChatListView`. Do not modify message sending, WebSocket handling, TTS/STT, session loading, or collection updates.

- [ ] **Step 4: Run Chat contracts and build**

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo --filter FullyQualifiedName~ResponsiveLayoutContractTests
dotnet build D:\LLM\gui-winui3\RuApp.csproj --nologo
```

Expected: PASS and no new compiler warnings/errors.

- [ ] **Step 5: Commit only the Chat slice**

```powershell
git add -- gui-winui3/Views/ChatView.xaml gui-winui3/Views/ChatView.xaml.cs gui-winui3.Tests/ResponsiveLayoutContractTests.cs
git diff --cached --name-only
git commit -m "feat: make chat layout responsive"
```

---

### Task 4: Reflow Activity Log and Technical Diagnostics

**Files:**

- Modify: `gui-winui3/Views/LogView.xaml` / `.xaml.cs`
- Modify: `gui-winui3/Views/DiagnosticsView.xaml` / `.xaml.cs`
- Modify: `gui-winui3.Tests/ResponsiveLayoutContractTests.cs`

**Interfaces:**

- Consumes existing `Rows`, `ItemsSource`, filters, diagnostics polling, and batched collection behavior without changing any of them.
- Produces visual-only `AdaptiveTrigger` states and a `ReflowStatusMetrics(ResponsiveLayoutTier tier)` helper in `DiagnosticsView.xaml.cs`.

- [ ] **Step 1: Add failing Log/Diagnostics contracts**

Add:

```csharp
[Theory]
[InlineData("LogView.xaml", "VisualState x:Name=\"Narrow\"")]
[InlineData("LogView.xaml", "HorizontalScrollBarVisibility=\"Disabled\"")]
[InlineData("DiagnosticsView.xaml", "VisualState x:Name=\"Narrow\"")]
[InlineData("DiagnosticsView.xaml", "x:Name=\"StatusMetricsGrid\"")]
[InlineData("DiagnosticsView.xaml.cs", "ReflowStatusMetrics")]
[InlineData("DiagnosticsView.xaml.cs", "Rows.ReplaceAll")]
public void Log_and_diagnostics_keep_stable_vertical_data_surfaces(string fileName, string marker)
{
    Assert.Contains(marker, File.ReadAllText(FindSource(fileName)), StringComparison.Ordinal);
}

[Theory]
[InlineData("LogView.xaml")]
[InlineData("DiagnosticsView.xaml")]
public void Log_and_diagnostics_never_enable_horizontal_scrolling(string fileName)
{
    Assert.DoesNotContain(
        "HorizontalScrollBarVisibility=\"Auto\"",
        File.ReadAllText(FindSource(fileName)),
        StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run the focused contracts and verify they fail**

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo --filter FullyQualifiedName~ResponsiveLayoutContractTests
```

Expected: FAIL on the missing responsive markers.

- [ ] **Step 3: Rebuild each header around a title block and action block**

In both XAML files:

1. Add a two-row header grid with named title and action/filter panels.
2. Keep the current one-row title/action arrangement in `Wide` through attached `Grid.Row`, `Grid.Column`, and `Grid.ColumnSpan` setters.
3. In `Compact`, move the action/filter panel to row 1 while keeping its controls horizontal.
4. In `Narrow`, keep the title in row 0 and switch the action/filter panel to a vertical `StackPanel` in row 1. Set filter controls to stretch with explicit minimum widths of 100–120 px; remove the existing fixed header widths that force overflow.
5. Keep every current filter event, toggle event, pause/export/clear event, and binding.

Use the existing filter controls; do not create a second polling path or duplicate an `ItemsSource`.

- [ ] **Step 4: Add the diagnostics status metric reflow**

Name the diagnostics four-metric grid `StatusMetricsGrid` and each metric panel (`QueueMetricPanel`, `DroppedMetricPanel`, `FailuresMetricPanel`, `FilesMetricPanel`). Give the grid four rows and four columns from the start, attach `SizeChanged="ResponsiveRoot_SizeChanged"` to the diagnostics root, and add a nullable `_statusMetricsTier` field. The size handler must call the policy only when the root width is positive and invoke `ReflowStatusMetrics` only when the returned tier changes:

```csharp
private ResponsiveLayoutTier? _statusMetricsTier;

private void ResponsiveRoot_SizeChanged(object sender, SizeChangedEventArgs e)
{
    if (e.NewSize.Width <= 0)
    {
        return;
    }

    var tier = ResponsiveLayoutPolicy.GetForWidth(e.NewSize.Width).Tier;
    if (_statusMetricsTier == tier)
    {
        return;
    }

    _statusMetricsTier = tier;
    ReflowStatusMetrics(tier);
}
```

Then implement:

```csharp
private void ReflowStatusMetrics(ResponsiveLayoutTier tier)
{
    var panels = new[]
    {
        QueueMetricPanel,
        DroppedMetricPanel,
        FailuresMetricPanel,
        FilesMetricPanel
    };

    var columns = tier == ResponsiveLayoutTier.Wide ? 4
        : tier == ResponsiveLayoutTier.Compact ? 2
        : 1;

    for (var index = 0; index < panels.Length; index++)
    {
        Grid.SetColumn(panels[index], index % columns);
        Grid.SetRow(panels[index], index / columns);
    }
}
```

The method may update row/column definitions or child positions, but it must not touch `Rows`, the poll timer, or the API client. Add the same three `AdaptiveTrigger` states to the diagnostics root as the shell; the XAML states handle card/header presentation while the size handler handles the four metric positions.

- [ ] **Step 5: Make timeline/diagnostic cards wrap without changing data**

In both item templates:

1. Keep the outer list vertical-only and retain the existing `BatchObservableCollection`/scroll preservation code.
2. Set the summary/detail column to `MinWidth="0"` and `TextWrapping="Wrap"`.
3. Add a narrow metadata layout inside each card, containing time, level/type, and component badges above the summary; show it only in `Narrow` and keep the existing horizontal metadata layout in `Wide`/`Compact`.
4. Keep the existing detail expansion binding and click handler; reflow must not change selection or expanded state.

- [ ] **Step 6: Run focused tests and the GUI build**

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo --filter FullyQualifiedName~ResponsiveLayoutContractTests
dotnet build D:\LLM\gui-winui3\RuApp.csproj --nologo
```

Expected: PASS with no build errors, no horizontal-scroll contract failures, and no changes to diagnostics/log collection behavior.

- [ ] **Step 7: Commit only the log/diagnostics slice**

```powershell
git add -- gui-winui3/Views/LogView.xaml gui-winui3/Views/LogView.xaml.cs gui-winui3/Views/DiagnosticsView.xaml gui-winui3/Views/DiagnosticsView.xaml.cs gui-winui3.Tests/ResponsiveLayoutContractTests.cs
git diff --cached --name-only
git commit -m "feat: reflow log and diagnostics views"
```

---

### Task 5: Make Voice and Settings form rows responsive

**Files:**

- Modify: `gui-winui3/Views/VoiceView.xaml`
- Modify: `gui-winui3/Views/SettingsView.xaml`
- Modify: `gui-winui3.Tests/ResponsiveLayoutContractTests.cs`

**Interfaces:**

- Consumes existing control names, selection/toggle/slider handlers, and scrollable `VoiceView`/`SettingsView` roots.
- Produces responsive visual states only; no new settings DTO, persistence call, or VTS command.

- [ ] **Step 1: Add failing form-layout contracts**

Add:

```csharp
[Theory]
[InlineData("VoiceView.xaml", "AdaptiveTrigger MinWindowWidth=\"900\"")]
[InlineData("VoiceView.xaml", "AdaptiveTrigger MinWindowWidth=\"1280\"")]
[InlineData("SettingsView.xaml", "AdaptiveTrigger MinWindowWidth=\"900\"")]
[InlineData("SettingsView.xaml", "AdaptiveTrigger MinWindowWidth=\"1280\"")]
[InlineData("VoiceView.xaml", "x:Name=\"VoiceProfileComboBox\"")]
[InlineData("SettingsView.xaml", "x:Name=\"BackendComboBox\"")]
[InlineData("SettingsView.xaml", "x:Name=\"GeminiVisionKeyBox\"")]
public void Form_views_define_all_responsive_tiers(string fileName, string marker)
{
    Assert.Contains(marker, File.ReadAllText(FindSource(fileName)), StringComparison.Ordinal);
}
```

- [ ] **Step 2: Run the focused contracts and verify they fail**

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo --filter FullyQualifiedName~ResponsiveLayoutContractTests
```

Expected: FAIL because the form view roots do not yet define all three adaptive states.

- [ ] **Step 3: Add shared form-row structure in Voice and Settings XAML**

For every descriptive `Grid ColumnDefinitions="*,Auto"` row in `VoiceView.xaml` and `SettingsView.xaml`:

1. Add `RowDefinitions="Auto,Auto"` while keeping the existing `*,Auto` columns.
2. Give any unnamed editor a stable `x:Name` (do not rename existing names used by code-behind).
3. Keep the description panel at row 0/column 0.
4. In `Narrow`, move the editor to row 1/column 0 using `Setter Target="EditorName.(Grid.Row)"` and `Setter Target="EditorName.(Grid.Column)"`.
5. In `Wide` and `Compact`, keep the editor at row 0/column 1.
6. Apply `MaxWidth="300"`, `HorizontalAlignment="Stretch"`, and a safe `MinWidth` to fixed editors instead of allowing them to force the parent wider.
7. Set slider widths to stretch within the available width while retaining their existing preferred maximum; preserve values and `ValueChanged` handlers.

Cover these existing controls without changing their event wiring: `VoiceProfileComboBox`, `SpeedSlider`, `SubtitleSizeCombo`, `SubtitleColorCombo`, `SubtitleDurationSlider`, `TestTextBox`, `BackendComboBox`, `ModelSearchBox`, `ModelComboBox`, `OpenCodeApiKeyBox`, `ApiVariantComboBox`, `KiloApiKeyBox`, `CustomEndpointUrlBox`, `CustomApiKeyBox`, `TemperatureNumberBox`, `TopKNumberBox`, `MaxTokensNumberBox`, `GeminiVisionKeyBox`, `SwaySlider`, `HeadSlider`, `EyeSlider`, `MicroSlider`, `PoseSlider`, and `MouseSensitivitySlider`.

- [ ] **Step 4: Reflow action groups and VTS controls**

In `VoiceView.xaml`:

1. Add `VisualStateGroup` states to the root content.
2. Keep warmup/release buttons horizontal in `Wide` and `Compact`; stack them in `Narrow`.
3. Keep VTS manual-action buttons wrapped or vertical in `Narrow`.
4. Make the test text box and `Phát thử` button a vertical pair in `Narrow`.

In `SettingsView.xaml`:

1. Keep all `PivotItem` content in its existing `ScrollViewer`.
2. Preserve all tabs at every tier; never collapse a tab as a responsive shortcut.
3. Make the VTS connect action and observation/API-key fields move below their descriptions in `Narrow`.

- [ ] **Step 5: Run focused tests and GUI build**

```powershell
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo --filter FullyQualifiedName~ResponsiveLayoutContractTests
dotnet build D:\LLM\gui-winui3\RuApp.csproj --nologo
```

Expected: PASS; resizing must not emit a settings write or VTS action because no event handler is attached to the responsive states.

- [ ] **Step 6: Commit only the form-view slice**

```powershell
git add -- gui-winui3/Views/VoiceView.xaml gui-winui3/Views/SettingsView.xaml gui-winui3.Tests/ResponsiveLayoutContractTests.cs
git diff --cached --name-only
git commit -m "feat: make voice and settings layouts responsive"
```

---

### Task 6: Add documentation and complete verification

**Files:**

- Modify: `docs/operations/runtime-behavior.md`
- Modify: `gui-winui3.Tests/ResponsiveLayoutContractTests.cs` only if a final stable contract is missing.

**Interfaces:**

- Consumes the final breakpoint values and acceptance boundary from Tasks 1–5.
- Produces the documented distinction between automated layout checks and manual visual acceptance.

- [ ] **Step 1: Add the runtime-behavior documentation section**

Add a `## WinUI responsive shell` section after the desktop/runtime behavior material:

```markdown
The WinUI shell has three presentation tiers based on client width: Wide at
1280 px and above, Compact from 900 through 1279 px, and Narrow below 900 px.
Wide keeps the resizable 180–400 px sidebar; Compact keeps labels with tighter
spacing and 180–320 px bounds; Narrow uses a 72 px icon rail and stacks page
controls. These transitions are presentation-only and do not call the backend,
restart polling, recreate list sources, or alter audio/VTS behavior.

Automated tests cover policy boundaries, XAML state markers, vertical-only list
surfaces, and build correctness. Visual resizing at representative window sizes
remains a user-run acceptance check.
```

- [ ] **Step 2: Run the documentation test**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest D:\LLM\tests\test_current_documentation.py -q
```

Expected: PASS.

- [ ] **Step 3: Run all automated gates sequentially**

Use the repository's documented order:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --no-restore --nologo
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --no-restore --nologo
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
cmd /c D:\LLM\build.bat
```

Expected: all existing Python/backend/WinUI tests pass, route validation reports the repository's current contract (41 HTTP routes and 2 WebSocket routes at this checkpoint), and the build produces the existing self-contained `RuBackend.exe` and `RuApp.exe` artifacts. Report environment/import failures separately from code failures.

- [ ] **Step 4: Run static cleanliness checks**

```powershell
git diff --check
rg -n 'HorizontalScrollBarVisibility="Auto"' gui-winui3/MainWindow.xaml gui-winui3/Views/ChatView.xaml gui-winui3/Views/LogView.xaml gui-winui3/Views/DiagnosticsView.xaml
git status --short --branch
```

Expected: `git diff --check` exits 0; the horizontal-scroll search returns no affected-view matches; unrelated dirty files remain present and unstaged.

- [ ] **Step 5: Perform manual visual acceptance without desktop automation**

The user runs RuApp and manually resizes the window to approximately 1440×900, 1024×768, 800×600, and 640×600. At each size, inspect Chat, Voice & VTS, Activity Log, Technical Diagnostics, and every Settings tab. Confirm:

- no clipped or overlapping controls;
- no horizontal scrollbar on the shell or affected list surfaces;
- icon-only navigation has tooltips and remains keyboard reachable;
- filters, status metrics, expanded diagnostic details, and settings editors remain usable;
- diagnostics/log list scroll positions remain stable during resize;
- resizing does not change request latency, audio playback, diagnostics polling, or VTS behavior.

- [ ] **Step 6: Commit documentation and final test additions only**

```powershell
git add -- docs/operations/runtime-behavior.md gui-winui3.Tests/ResponsiveLayoutContractTests.cs
git diff --cached --name-only
git commit -m "docs: document responsive WinUI acceptance"
```

The staged-name check must contain only the documentation and final contract-test files.

---

## Self-review checklist

- [x] Spec coverage: breakpoint policy, shell/sidebar, Chat, Log, Diagnostics, Voice, Settings, performance constraints, documentation, and manual acceptance each have a task.
- [x] No task changes backend, worker, audio, VTS, memory, business logic, or logging behavior.
- [x] Every new policy type and method name used by later tasks is defined in Task 1.
- [x] Every verification command uses the repository's Windows paths and runs heavyweight suites sequentially.
- [x] The plan keeps unrelated dirty worktree files unstaged.
- [x] The only manual gate is visual desktop acceptance; no automated desktop control is introduced.


