# Ru Dark Theme Toggle Implementation Plan

> **Status:** Historical implementation record. The theme toggle shipped;
> current shell behavior lives in `docs/operations/runtime-behavior.md`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a persistent Light/Dark appearance toggle to the Ru WinUI 3 shell without changing Chat, backend, voice, VTS, memory, or process behavior.

**Architecture:** Keep RequestedTheme="Light" as the application fallback, move shared Ru color tokens into Light and Dark theme dictionaries, and make existing global brushes resolve colors through ThemeResource. Add a small file-backed UiThemePreferenceStore under %LOCALAPPDATA%\\Ru; MainWindow owns the active ElementTheme, toggle presentation, persistence, and custom title-bar colors.

**Tech Stack:** WinUI 3, XAML ResourceDictionary.ThemeDictionaries, .NET 10, System.Text.Json, xUnit source/XAML contracts, sequential dotnet test and Release build.

---

### Task 1: Add failing preference-store and resource/toggle contract tests

**Files:**
- Create: gui-winui3.Tests/UiThemePreferenceStoreTests.cs
- Modify: gui-winui3.Tests/EditorialReadingRoomContractTests.cs

- [ ] **Step 1: Write the failing preference tests**

Create UiThemePreferenceStoreTests with an isolated temporary directory. It must test:

~~~csharp
using RuApp.Services;
using Xunit;

namespace RuApp.Tests;

public sealed class UiThemePreferenceStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "RuAppTests",
        $"theme-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Load_returns_light_when_preference_file_is_missing()
    {
        var store = new UiThemePreferenceStore(Path.Combine(_root, "ui-preferences.json"));

        Assert.Equal(UiTheme.Light, store.Load());
    }

    [Fact]
    public void TrySave_and_load_round_trip_dark_theme()
    {
        var path = Path.Combine(_root, "ui-preferences.json");
        var store = new UiThemePreferenceStore(path);

        Assert.True(store.TrySave(UiTheme.Dark));
        Assert.Equal(UiTheme.Dark, store.Load());
        Assert.Contains("\"theme\":\"Dark\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"theme\":\"Sepia\"}")]
    [InlineData("{\"other\":true}")]
    public void Load_returns_light_for_invalid_or_unknown_preference(string content)
    {
        var path = Path.Combine(_root, "ui-preferences.json");
        Directory.CreateDirectory(_root);
        File.WriteAllText(path, content);

        Assert.Equal(UiTheme.Light, new UiThemePreferenceStore(path).Load());
    }

    [Fact]
    public void TrySave_returns_false_when_target_path_is_a_directory()
    {
        var path = Path.Combine(_root, "ui-preferences.json");
        Directory.CreateDirectory(path);

        Assert.False(new UiThemePreferenceStore(path).TrySave(UiTheme.Dark));
    }
}
~~~

- [ ] **Step 2: Run the focused tests and record the red result**

Run:

~~~powershell
dotnet test gui-winui3.Tests --nologo --filter FullyQualifiedName~UiThemePreferenceStoreTests
~~~

Expected red result: the new production API is missing. Once the minimal type is added in Task 2, assertions must remain red until persistence behavior is implemented.

- [ ] **Step 3: Add failing source/XAML contracts**

Add tests to EditorialReadingRoomContractTests asserting:

~~~csharp
[Fact]
public void App_defines_light_and_dark_theme_dictionaries_with_dynamic_brushes()
{
    var source = File.ReadAllText(FindSource("App.xaml"));

    Assert.Contains("<ResourceDictionary x:Key=\"Light\">", source, StringComparison.Ordinal);
    Assert.Contains("<ResourceDictionary x:Key=\"Dark\">", source, StringComparison.Ordinal);
    Assert.Contains("Color=\"{ThemeResource RuCanvasColor}\"", source, StringComparison.Ordinal);
    Assert.Contains("<Color x:Key=\"RuCanvasColor\">#191C19</Color>", source, StringComparison.Ordinal);
    Assert.Contains("<Color x:Key=\"RuAccentColor\">#C76A52</Color>", source, StringComparison.Ordinal);
}

[Fact]
public void Shell_exposes_a_persistent_theme_toggle_before_settings()
{
    var source = File.ReadAllText(FindSource("MainWindow.xaml"));

    Assert.Contains("x:Name=\"ThemeToggleButton\"", source, StringComparison.Ordinal);
    Assert.Contains("Click=\"OnThemeToggleClick\"", source, StringComparison.Ordinal);
    Assert.Contains("x:Name=\"LabelThemeToggle\"", source, StringComparison.Ordinal);
    Assert.Contains("x:Name=\"ThemeToggleIcon\"", source, StringComparison.Ordinal);
    Assert.True(source.IndexOf("x:Name=\"ThemeToggleButton\"", StringComparison.Ordinal)
        < source.IndexOf("x:Name=\"NavSettings\"", StringComparison.Ordinal));
    Assert.Contains("Target=\"LabelThemeToggle.Visibility\" Value=\"Collapsed\"", source, StringComparison.Ordinal);
}

[Theory]
[InlineData("UiThemePreferenceStore")]
[InlineData("ApplyTheme")]
[InlineData("ThemeToggleButton")]
[InlineData("ButtonHoverBackgroundColor")]
public void MainWindow_owns_theme_application_and_title_bar_state(string marker)
{
    Assert.Contains(marker, File.ReadAllText(FindSource("MainWindow.xaml.cs")), StringComparison.Ordinal);
}
~~~

- [ ] **Step 4: Run the editorial filter and verify the new contracts are red**

Run:

~~~powershell
dotnet test gui-winui3.Tests --nologo --filter FullyQualifiedName~EditorialReadingRoomContractTests
~~~

Expected red result: the new assertions fail because the Dark dictionary and toggle do not exist.

### Task 2: Implement and verify the local UI preference store

**Files:**
- Create: gui-winui3/Services/UiThemePreferenceStore.cs
- Test: gui-winui3.Tests/UiThemePreferenceStoreTests.cs

- [ ] **Step 1: Add the store API and behavior**

Create:

~~~csharp
using System.Text.Json;

namespace RuApp.Services;

public enum UiTheme
{
    Light,
    Dark
}

public sealed class UiThemePreferenceStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _filePath;

    public UiThemePreferenceStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ru",
            "ui-preferences.json");
    }

    public UiTheme Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return UiTheme.Light;
            }

            var preferences = JsonSerializer.Deserialize<UiThemePreferences>(
                File.ReadAllText(_filePath),
                JsonOptions);

            return Enum.TryParse(preferences?.Theme, ignoreCase: true, out UiTheme theme)
                ? theme
                : UiTheme.Light;
        }
        catch (Exception ex) when (
            ex is IOException ||
            ex is UnauthorizedAccessException ||
            ex is JsonException ||
            ex is NotSupportedException ||
            ex is ArgumentException)
        {
            return UiTheme.Light;
        }
    }

    public bool TrySave(UiTheme theme)
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            Directory.CreateDirectory(directory);
            var json = JsonSerializer.Serialize(new UiThemePreferences(theme.ToString()), JsonOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _filePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (
            ex is IOException ||
            ex is UnauthorizedAccessException ||
            ex is ArgumentException ||
            ex is NotSupportedException)
        {
            return false;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    private sealed record UiThemePreferences(string? Theme);
}
~~~

- [ ] **Step 2: Run the preference tests and verify green**

Run the focused preference filter again. Expected: all tests pass, including invalid input fallback and the directory-target failure case, with no temporary-file leak.

- [ ] **Step 3: Commit the isolated store slice**

~~~powershell
git add gui-winui3/Services/UiThemePreferenceStore.cs gui-winui3.Tests/UiThemePreferenceStoreTests.cs
git commit -m "feat: add local Ru theme preference store"
~~~

### Task 3: Add Light/Dark resource dictionaries

**Files:**
- Modify: gui-winui3/App.xaml
- Modify: gui-winui3/Views/ChatView.xaml
- Test: gui-winui3.Tests/EditorialReadingRoomContractTests.cs

- [ ] **Step 1: Replace flat colors with Light and Dark dictionaries**

Keep RequestedTheme="Light" as fallback. Inside the existing application ResourceDictionary, add ResourceDictionary.ThemeDictionaries with the exact approved colors:

~~~xml
<ResourceDictionary.ThemeDictionaries>
    <ResourceDictionary x:Key="Light">
        <Color x:Key="RuCanvasColor">#E7E9E3</Color>
        <Color x:Key="RuSidebarColor">#DCE1D9</Color>
        <Color x:Key="RuSurfaceColor">#F0F2EC</Color>
        <Color x:Key="RuInkColor">#20231F</Color>
        <Color x:Key="RuMutedColor">#5F685F</Color>
        <Color x:Key="RuLineColor">#C9CEC5</Color>
        <Color x:Key="RuAccentColor">#A84B38</Color>
        <Color x:Key="RuAccentSoftColor">#EEDFD9</Color>
        <Color x:Key="RuSuccessColor">#2F6F51</Color>
        <Color x:Key="RuWarningColor">#9A6C1F</Color>
        <Color x:Key="RuDangerColor">#A73A3A</Color>
    </ResourceDictionary>
    <ResourceDictionary x:Key="Dark">
        <Color x:Key="RuCanvasColor">#191C19</Color>
        <Color x:Key="RuSidebarColor">#121612</Color>
        <Color x:Key="RuSurfaceColor">#232923</Color>
        <Color x:Key="RuInkColor">#EEF2EB</Color>
        <Color x:Key="RuMutedColor">#A9B3A7</Color>
        <Color x:Key="RuLineColor">#39443A</Color>
        <Color x:Key="RuAccentColor">#C76A52</Color>
        <Color x:Key="RuAccentSoftColor">#3A2924</Color>
        <Color x:Key="RuSuccessColor">#75B28D</Color>
        <Color x:Key="RuWarningColor">#D7A759</Color>
        <Color x:Key="RuDangerColor">#E38181</Color>
    </ResourceDictionary>
</ResourceDictionary.ThemeDictionaries>
~~~

Change every global brush color reference from StaticResource Ru...Color to ThemeResource Ru...Color while preserving all existing brush keys and style consumers.

- [ ] **Step 2: Make the Chat placeholder theme-aware**

In gui-winui3/Views/ChatView.xaml, change the local resource to:

~~~xml
<Color x:Key="TextControlPlaceholderForeground" Color="{ThemeResource RuMutedColor}" />
~~~

- [ ] **Step 3: Run the editorial tests and verify green**

Run the EditorialReadingRoomContractTests filter. Expected: Light baseline, Dark token, dynamic-brush, legacy-alias, reading-room, responsive, and rounding contracts all pass.

- [ ] **Step 4: Commit the resource slice**

~~~powershell
git add gui-winui3/App.xaml gui-winui3/Views/ChatView.xaml gui-winui3.Tests/EditorialReadingRoomContractTests.cs
git commit -m "style: add Ru light and dark theme tokens"
~~~

### Task 4: Add the sidebar toggle and apply the active theme

**Files:**
- Modify: gui-winui3/MainWindow.xaml
- Modify: gui-winui3/MainWindow.xaml.cs
- Test: gui-winui3.Tests/EditorialReadingRoomContractTests.cs

- [ ] **Step 1: Add the toggle before Settings**

Insert this button after the existing footer divider and before NavSettings:

~~~xml
<Button x:Name="ThemeToggleButton"
        Click="OnThemeToggleClick"
        HorizontalAlignment="Stretch"
        Background="Transparent"
        Foreground="{StaticResource RuMutedBrush}"
        Padding="10,8"
        CornerRadius="0"
        BorderBrush="Transparent"
        BorderThickness="0"
        HorizontalContentAlignment="Left"
        ToolTipService.ToolTip="Chuyển sang tối"
        AutomationProperties.Name="Chuyển sang tối">
    <Grid ColumnDefinitions="Auto,*">
        <FontIcon x:Name="ThemeToggleIcon"
                  Grid.Column="0"
                  Glyph="&#xE706;"
                  FontSize="15"
                  Margin="0,0,12,0" />
        <TextBlock x:Name="LabelThemeToggle"
                   Grid.Column="1"
                   Text="Chuyển sang tối"
                   FontFamily="{StaticResource RuUiFontFamily}"
                   FontSize="13.5"
                   FontWeight="Medium"
                   VerticalAlignment="Center" />
    </Grid>
</Button>
~~~

Add Compact padding and Narrow setters: collapse LabelThemeToggle, center ThemeToggleButton, and set its padding to 8.

- [ ] **Step 2: Add theme state and store the AppWindow**

Add:

~~~csharp
private readonly UiThemePreferenceStore _themePreferenceStore = new();
private Microsoft.UI.Windowing.AppWindow? _appWindow;
private UiTheme _currentTheme = UiTheme.Light;
~~~

Replace the constructor's local appWindow with _appWindow; call ApplyTheme(_themePreferenceStore.Load()) after InitializeComponent() and before backend initialization. Keep title-bar setup guarded as it is now.

- [ ] **Step 3: Implement apply/toggle/title-bar methods**

Add methods with these behaviors:

~~~csharp
private void OnThemeToggleClick(object sender, RoutedEventArgs e)
{
    var nextTheme = _currentTheme == UiTheme.Dark ? UiTheme.Light : UiTheme.Dark;
    ApplyTheme(nextTheme);
    _themePreferenceStore.TrySave(nextTheme);
}

private void ApplyTheme(UiTheme theme)
{
    _currentTheme = theme;
    ShellRoot.RequestedTheme = theme == UiTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;

    bool isDark = theme == UiTheme.Dark;
    var label = isDark ? "Chuyển sang sáng" : "Chuyển sang tối";
    LabelThemeToggle.Text = label;
    ThemeToggleIcon.Glyph = isDark ? "\uE793" : "\uE706";
    ToolTipService.SetToolTip(ThemeToggleButton, label);
    AutomationProperties.SetName(ThemeToggleButton, label);
}
~~~

ApplyTitleBarTheme(bool isDark) must update _appWindow.TitleBar with the approved light/dark foreground, muted, hover, pressed, and transparent background colors. It must return when AppWindow/title bar is unavailable. OnThemeToggleClick and ApplyTheme must not call the backend, recreate views, restart timers, or alter Chat/voice/VTS state.

- [ ] **Step 4: Run focused tests and build sequentially**

~~~powershell
dotnet test gui-winui3.Tests --nologo --filter FullyQualifiedName~EditorialReadingRoomContractTests
dotnet build gui-winui3/RuApp.csproj -c Release -r win-x64 --self-contained true --no-restore
~~~

Expected: focused tests pass and the GUI Release build exits 0.

- [ ] **Step 5: Commit the shell integration slice**

~~~powershell
git add gui-winui3/MainWindow.xaml gui-winui3/MainWindow.xaml.cs gui-winui3.Tests/EditorialReadingRoomContractTests.cs
git commit -m "feat: add persistent Ru theme toggle"
~~~

### Task 5: Update current documentation and stale theme notes

**Files:**
- Modify: docs/operations/runtime-behavior.md
- Modify: docs/superpowers/specs/2026-08-21-ru-chat-editorial-reading-room-design.md
- Modify: docs/superpowers/specs/2026-08-21-ru-mineral-paper-palette-refinement-design.md

- [ ] **Step 1: Update runtime behavior**

Replace the light-only paragraph with the current behavior: Ru ships Light and Dark mineral themes; the sidebar footer toggles them; the preference is stored at %LOCALAPPDATA%\\Ru\\ui-preferences.json; appearance changes are presentation-only and have no backend, Chat, voice, subtitle, or VTS side effects.

- [ ] **Step 2: Mark historical design assumptions**

In the reading-room design, say that the Light palette remains the baseline and the Dark dictionary was added by 2026-08-21-ru-dark-theme-toggle-design.md. In the palette refinement design, retain the Light token table as historical baseline and replace the old “do not add a theme selector or new preference” statement with a note that the later extension owns the selector and local preference.

- [ ] **Step 3: Scan docs and verify diff hygiene**

~~~powershell
rg -n -i "light-only|one light theme|one-light-theme|do not add a theme selector|RequestedTheme=.?Light" docs --glob '*.md'
git diff --check
~~~

Expected: remaining light-only matches are explicitly historical plan/spec language, not the current runtime behavior; git diff --check is clean.

- [ ] **Step 4: Commit docs**

~~~powershell
git add docs/operations/runtime-behavior.md docs/superpowers/specs/2026-08-21-ru-chat-editorial-reading-room-design.md docs/superpowers/specs/2026-08-21-ru-mineral-paper-palette-refinement-design.md
git commit -m "docs: document Ru light and dark themes"
~~~

### Task 6: Complete verification and push main

- [ ] **Step 1: Run the complete WinUI tests**

~~~powershell
dotnet test gui-winui3.Tests --nologo
~~~

Expected: zero failures and zero errors.

- [ ] **Step 2: Run the Release build**

~~~powershell
dotnet build gui-winui3/RuApp.csproj -c Release -r win-x64 --self-contained true --no-restore
~~~

Expected: exit 0. The earlier backend process-lock issue concerns build.bat's backend publish step, not this direct GUI build, and must be reported separately if encountered.

- [ ] **Step 3: Verify scope and synchronization**

~~~powershell
git diff --check
git status --short --branch
git log -5 --oneline --decorate
git fetch origin main
git rev-list --left-right --count HEAD...origin/main
~~~

Expected: no whitespace errors, no unintended dirty files, and 0 0 synchronization before pushing.

- [ ] **Step 4: Push the approved implementation**

~~~powershell
git push origin main
~~~

Then rerun status and synchronization checks. Report exact commits and fresh test/build output. Manual visual and live audio/VTS acceptance remains user-run and explicitly pending.
