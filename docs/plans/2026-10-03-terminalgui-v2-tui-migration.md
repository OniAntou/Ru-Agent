---
status: deferred
---

# Terminal.Gui v2 TUI Migration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Move the `agent chat` full-screen TUI from the hand-rolled ANSI renderer to Terminal.Gui v2 behind an engine flag, reaching visual/behavioral parity before flipping the default, while the sequential fallback stays untouched.

**Architecture:** Add a `TerminalGui/` folder inside `RuAgent.Cli` that composes the same three regions (header, transcript, composer+hints) from TG views. The existing chat command loop keeps ownership of commands, attachments and HTTP; a new TG run loop drives the same extracted "execute one submitted line" path. Engine selection is a pure function over env + terminal state (`RU_TUI_ENGINE`), defaulting to `classic` until the parity checklist passes, then `terminalgui`. The classic full-screen renderer is deleted only in a later, separate cleanup plan.

**Tech Stack:** C# .NET 10 (`net10.0-windows`), `Terminal.Gui` 2.5.0 (ANSI driver for tests, input injection, `VirtualTimeProvider`), xunit, existing `TuiPalette`/`TuiText` helpers as the color/text source of truth.

**Spec:** This document, plus Appendix A (spike findings, 2026-10-03) and `docs/plans/2026-10-02-tui-redesign.md` for the classic renderer's contract.

**Branch rule:** Repo keeps a single `dev` branch; this migration lands on `dev` behind `RU_TUI_ENGINE`, no side branches.

> **Status (2026-10-03):** Tasks 1–15 shipped on `dev` (commits `6efe9ece`..`ac26cf94` plus this Task 15 flip commit, "feat(cli): default the chat TUI to Terminal.Gui with a classic escape hatch"). Task 16 (retire the classic full-screen renderer) is deferred until the Terminal.Gui default has survived one release cycle.

---

## File structure

- Create: `backend-dotnet/RuAgent.Cli/TerminalGui/TuiEngine.cs` — engine enum + pure selection (`RU_TUI_ENGINE`, terminal gates).
- Create: `backend-dotnet/RuAgent.Cli/TerminalGui/TgPalette.cs` — `TuiToken` → TG `Attribute`/`Scheme` bridge (single source: `TuiPalette.Rgb`).
- Create: `backend-dotnet/RuAgent.Cli/TerminalGui/TgSnapshot.cs` — test/evidence helper: driver cell buffer → text rows + cell attributes.
- Create: `backend-dotnet/RuAgent.Cli/TerminalGui/TgComposerView.cs` — square frame, gray band, input, ghost placeholder, meta row, caret.
- Create: `backend-dotnet/RuAgent.Cli/TerminalGui/TgTranscriptView.cs` — append-only lines, same-line chunks, wrap, tail pin, styles.
- Create: `backend-dotnet/RuAgent.Cli/TerminalGui/TgHeaderView.cs` — one-line title + usage cluster, `UpdateUsage(...)`.
- Create: `backend-dotnet/RuAgent.Cli/TerminalGui/TgHintsView.cs` — idle/busy hint row.
- Create: `backend-dotnet/RuAgent.Cli/TerminalGui/TgChatWindow.cs` — composes the window; public `AppendProse/AppendTool/SetBusy/...` state API.
- Create: `backend-dotnet/RuAgent.Cli/TerminalGui/TgChatLoop.cs` — TG run loop: input events, submit, cancel, ctrl+o, history, tab.
- Create: `backend-dotnet/RuAgent.Cli/TerminalGui/TgSlashDropdown.cs` — slash-command dropdown overlay (later task).
- Create: `backend-dotnet/RuAgent.Cli/TerminalGui/TgPanelHost.cs` — approval/trust/picker/secret/key panels (later task).
- Modify: `backend-dotnet/RuAgent.Cli/Tui/TuiPalette.cs` — expose RGB components (`Rgb`, `ElementRgb`, `SurfaceRgb`) without changing the emitted ANSI.
- Modify: `backend-dotnet/RuAgent.Cli/AgentCommands.Chat.cs` — extract `ExecuteSubmittedLineAsync` from the key loop; add the TG entry path.
- Modify: `backend-dotnet/RuAgent.Cli/RuAgent.Cli.csproj` — add `Terminal.Gui` package.
- Test: `backend-dotnet/RuAgent.Cli.Tests/TgEngineSelectionTests.cs`, `TgPaletteTests.cs`, `TgSnapshotTests.cs`, `TgComposerTests.cs`, `TgTranscriptTests.cs`, `TgChatLoopTests.cs`, `TgPanelsTests.cs`.

---

## Phase 0 — Foundations (Tasks 1-4)

### Task 1: Engine flag with safe default

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/TerminalGui/TuiEngine.cs`
- Test: `backend-dotnet/RuAgent.Cli.Tests/TgEngineSelectionTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using RuAgent.Cli.TerminalGui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgEngineSelectionTests
{
    [Fact]
    public void Resolve_DefaultsToClassic()
    {
        Assert.Equal(TuiEngine.Classic, TuiEngineSelection.Resolve(
            envValue: null, outputRedirected: false, json: false,
            columns: 120, rows: 40, noColor: null, classic: false));
    }

    [Fact]
    public void Resolve_TerminalGuiOptIn_LiveTerminal_ReturnsTerminalGui()
    {
        Assert.Equal(TuiEngine.TerminalGui, TuiEngineSelection.Resolve(
            envValue: "terminalgui", outputRedirected: false, json: false,
            columns: 120, rows: 40, noColor: null, classic: false));
    }

    [Theory]
    [InlineData(true, false, null, 120, 40, false)]  // redirected
    [InlineData(false, true, null, 120, 40, false)]  // json
    [InlineData(false, false, "1", 120, 40, false)]  // NO_COLOR
    [InlineData(false, false, null, 79, 40, false)]  // too narrow
    [InlineData(false, false, null, 120, 23, false)] // too short
    [InlineData(false, false, null, 120, 40, true)]  // classic flag
    public void Resolve_TerminalGuiRequested_ButGateFails_ReturnsClassic(
        bool outputRedirected, bool json, string? noColor, int columns, int rows, bool classic)
    {
        Assert.Equal(TuiEngine.Classic, TuiEngineSelection.Resolve(
            envValue: "terminalgui", outputRedirected, json,
            columns, rows, noColor, classic));
    }

    [Fact]
    public void Resolve_UnknownValue_ReturnsClassic()
    {
        Assert.Equal(TuiEngine.Classic, TuiEngineSelection.Resolve(
            envValue: "nonsense", outputRedirected: false, json: false,
            columns: 120, rows: 40, noColor: null, classic: false));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TgEngineSelection"`
Expected: FAIL with "The type or namespace name 'TerminalGui' does not exist".

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace RuAgent.Cli.TerminalGui;

/// <summary>Which full-screen renderer drives <c>agent chat</c>.</summary>
public enum TuiEngine
{
    Classic,
    TerminalGui,
}

/// <summary>
/// Engine resolution for the chat TUI. The terminal gates mirror the classic AltBuffer
/// gate (<c>TuiAltBuffer.ShouldUseAltBuffer</c>): a redirected, JSON, NO_COLOR or small
/// terminal always stays classic. Until parity ships, only the explicit opt-in selects
/// Terminal.Gui; the default flips in Task 15.
/// </summary>
public static class TuiEngineSelection
{
    public const string EngineVariable = "RU_TUI_ENGINE";

    public static TuiEngine Resolve(
        string? envValue,
        bool outputRedirected,
        bool json,
        int columns,
        int rows,
        string? noColor,
        bool classic)
    {
        if (outputRedirected || json || !string.IsNullOrEmpty(noColor) || classic)
        {
            return TuiEngine.Classic;
        }

        if (columns < 80 || rows < 24)
        {
            return TuiEngine.Classic;
        }

        return string.Equals(envValue?.Trim(), "terminalgui", StringComparison.OrdinalIgnoreCase)
            ? TuiEngine.TerminalGui
            : TuiEngine.Classic;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TgEngineSelection"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/TerminalGui/TuiEngine.cs backend-dotnet/RuAgent.Cli.Tests/TgEngineSelectionTests.cs
git commit -m "feat(cli): add a chat TUI engine flag defaulting to classic"
```

---

### Task 2: Terminal.Gui package + headless smoke

**Files:**
- Modify: `backend-dotnet/RuAgent.Cli/RuAgent.Cli.csproj`
- Test: `backend-dotnet/RuAgent.Cli.Tests/TgSnapshotTests.cs`

- [ ] **Step 1: Add the package**

Run: `dotnet add backend-dotnet/RuAgent.Cli/RuAgent.Cli.csproj package Terminal.Gui --version 2.5.0`
Expected: `PackageReference for package 'Terminal.Gui' version '2.5.0' added`.

- [ ] **Step 2: Write the failing smoke test**

```csharp
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgSnapshotTests
{
    [Fact]
    public void HeadlessApp_RendersALabel()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        window.Add(new Label { Text = "ru smoke", X = 0, Y = 0 });
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rows = RuAgent.Cli.TerminalGui.TgSnapshot.Capture(app);
            Assert.Contains("ru smoke", rows[0]);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TgSnapshotTests"`
Expected: FAIL with "The type or namespace name 'TgSnapshot' does not exist".

- [ ] **Step 4: Implement the snapshot helper**

```csharp
using Terminal.Gui.App;
using Attr = Terminal.Gui.Drawing.Attribute;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// Reads the driver's cell buffer as plain text rows. This is the Terminal.Gui analogue
/// of <c>TuiScreenSimulator</c>: tests assert what the operator sees without a terminal.
/// </summary>
public static class TgSnapshot
{
    public static string[] Capture(IApplication app)
    {
        var driver = app.Driver ?? throw new InvalidOperationException("driver not initialised");
        var contents = driver.Contents;
        var rows = new string[driver.Rows];
        for (var r = 0; r < driver.Rows; r++)
        {
            var line = new System.Text.StringBuilder(driver.Cols);
            for (var c = 0; c < driver.Cols; c++)
            {
                line.Append(contents[r, c].Grapheme ?? " ");
            }

            rows[r] = line.ToString().TrimEnd();
        }

        return rows;
    }

    public static Attr CellAttribute(IApplication app, int row, int col)
    {
        var driver = app.Driver ?? throw new InvalidOperationException("driver not initialised");
        return driver.Contents[row, col].Attribute ?? Attr.Default;
    }
}
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TgSnapshotTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/RuAgent.Cli.csproj backend-dotnet/RuAgent.Cli/TerminalGui/TgSnapshot.cs backend-dotnet/RuAgent.Cli.Tests/TgSnapshotTests.cs
git commit -m "feat(cli): add Terminal.Gui and a cell-buffer snapshot helper"
```

---

### Task 3: Palette bridge (single source of truth)

**Files:**
- Modify: `backend-dotnet/RuAgent.Cli/Tui/TuiPalette.cs`
- Create: `backend-dotnet/RuAgent.Cli/TerminalGui/TgPalette.cs`
- Test: `backend-dotnet/RuAgent.Cli.Tests/TgPaletteTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.Drawing;
using Xunit;
using Attr = Terminal.Gui.Drawing.Attribute;

namespace RuAgent.Cli.Tests;

public sealed class TgPaletteTests
{
    [Fact]
    public void Rgb_MatchesTheEmittedTrueColorCodes()
    {
        using (TuiPalette.PinLevel(TuiColorLevel.TrueColor))
        {
            Assert.Equal((92, 156, 245), TuiPalette.Rgb(TuiToken.Brand));
            Assert.Equal((238, 238, 238), TuiPalette.Rgb(TuiToken.Bright));
            Assert.Equal((51, 51, 51), TuiPalette.ElementRgb());
            Assert.Contains("38;2;92;156;245", TuiPalette.Code(TuiToken.Brand), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Scheme_PinsEveryVisualRoleSoTheFrameworkCannotSwapColours()
    {
        var scheme = TgPalette.Scheme(TuiToken.Ink, TuiToken.Element);
        var normal = new Attr(TgPalette.ToColor(TuiToken.Ink), TgPalette.ToColor(TuiToken.Element));

        Assert.Equal(normal, scheme.Normal);
        Assert.Equal(normal, scheme.Focus);
        Assert.Equal(normal, scheme.Editable);
        Assert.Equal(normal, scheme.Active);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TgPaletteTests"`
Expected: FAIL with "TuiPalette does not contain a definition for 'Rgb'".

- [ ] **Step 3: Expose RGB components on `TuiPalette` (no ANSI change)**

Add next to `TrueColorParts` and refactor it to consume the new method:

```csharp
    /// <summary>Truecolor components for a token; the Terminal.Gui bridge reads these so
    /// both engines share one palette definition.</summary>
    public static (int R, int G, int B) Rgb(TuiToken token) => token switch
    {
        TuiToken.Brand => (92, 156, 245),
        TuiToken.Brand2 => (157, 124, 216),
        TuiToken.Meta => (128, 128, 128),
        TuiToken.Line => (72, 72, 72),
        TuiToken.Ok => (127, 216, 143),
        TuiToken.Warn => (245, 167, 66),
        TuiToken.Err => (224, 108, 117),
        TuiToken.Bright => (238, 238, 238),
        _ => (238, 238, 238),
    };

    /// <summary>Composer interior gray band.</summary>
    public static (int R, int G, int B) ElementRgb() => (51, 51, 51);

    /// <summary>User-block panel surface.</summary>
    public static (int R, int G, int B) SurfaceRgb() => (20, 20, 20);

    private static string TrueColorParts(TuiToken token)
    {
        var (r, g, b) = Rgb(token);
        return $"{r};{g};{b}";
    }
```

- [ ] **Step 4: Implement the bridge**

```csharp
using RuAgent.Cli.Tui;
using Terminal.Gui.Drawing;
using Attr = Terminal.Gui.Drawing.Attribute;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// Maps the classic palette onto Terminal.Gui attributes. Every visual role is pinned:
/// the framework derives Focus/Editable by swapping colours when they are unset, which
/// turned the composer into a bright bar during the spike.
/// </summary>
public static class TgPalette
{
    public static Color ToColor(TuiToken token)
    {
        var (r, g, b) = TuiPalette.Rgb(token);
        return new Color(r, g, b, 255);
    }

    public static Color ToColor((int R, int G, int B) rgb) => new(rgb.R, rgb.G, rgb.B, 255);

    public static Attr Attr(TuiToken fg, TuiToken? bg = null) =>
        new(ToColor(fg), bg is { } b ? ToColor(b) : new Color(0, 0, 0, 255));

    public static Attr Attr(TuiToken fg, (int R, int G, int B) bg) =>
        new(ToColor(fg), ToColor(bg));

    public static Scheme Scheme(TuiToken fg)
    {
        var attr = Attr(fg);
        return new Scheme(attr)
        {
            Focus = attr,
            Editable = attr,
            Active = attr,
        };
    }

    public static Scheme Scheme(TuiToken fg, TuiToken bg)
    {
        var attr = Attr(fg, bg);
        return new Scheme(attr)
        {
            Focus = attr,
            Editable = attr,
            Active = attr,
        };
    }

    public static Scheme Scheme(TuiToken fg, (int R, int G, int B) bg)
    {
        var attr = Attr(fg, bg);
        return new Scheme(attr)
        {
            Focus = attr,
            Editable = attr,
            Active = attr,
        };
    }
}
```

- [ ] **Step 5: Run the palette suites (classic tests must stay green)**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TgPaletteTests|FullyQualifiedName~TuiPalette"`
Expected: PASS; no classic palette test changes.

- [ ] **Step 6: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/Tui/TuiPalette.cs backend-dotnet/RuAgent.Cli/TerminalGui/TgPalette.cs backend-dotnet/RuAgent.Cli.Tests/TgPaletteTests.cs
git commit -m "feat(cli): bridge the classic palette onto Terminal.Gui attributes"
```

---

### Task 4: Composer view (square frame, gray band, caret)

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/TerminalGui/TgComposerView.cs`
- Test: `backend-dotnet/RuAgent.Cli.Tests/TgComposerTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using RuAgent.Cli.TerminalGui;
using RuAgent.Cli.Tui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgComposerTests
{
    private static string[] Render(int width, int rows, Action<TgComposerView> configure)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var composer = new TgComposerView { X = 0, Y = Pos.AnchorEnd(7), Width = Dim.Fill(), Height = 7 };
        composer.SetMeta("Standard", "stealth/space-bunny-alpha", "kilo");
        composer.SetPlaceholder("Ask anything, / for commands, /key for sign-in…");
        window.Add(composer);
        configure(composer);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rowsText = TgSnapshot.Capture(app);
            Assert.Contains("┌", rowsText[rows - 7]);
            Assert.Contains("Standard", rowsText[rows - 3]);
            Assert.Contains("kilo", rowsText[rows - 3]);
            app.RequestStop();
            return false;
        });
        app.Run(window);
        return TgSnapshot.Capture(app);
    }

    [Fact]
    public void Idle_ShowsSquareFramePlaceholderAndMeta()
    {
        var rows = Render(100, 30, _ => { });
        Assert.Contains("Ask anything", rows[24]);
        Assert.DoesNotContain("╭", string.Join('\n', rows));
        Assert.DoesNotContain("models.dev", string.Join('\n', rows));
    }

    [Fact]
    public void Interior_IsTheGrayElementBand()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var composer = new TgComposerView { X = 0, Y = Pos.AnchorEnd(7), Width = Dim.Fill(), Height = 7 };
        window.Add(composer);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var element = TgPalette.ToColor(TuiPalette.ElementRgb());
            Assert.Equal(element, TgSnapshot.CellAttribute(app, 24, 40).Background);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TgComposerTests"`
Expected: FAIL with "The type or namespace name 'TgComposerView' does not exist".

- [ ] **Step 3: Implement the composer**

```csharp
using RuAgent.Cli.Tui;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// The prompt frame, cloned from the classic ComposerBox stack: square border, two gray
/// padding rows, the input row with a ghost placeholder, and the meta row.
/// </summary>
public sealed class TgComposerView : View
{
    private readonly Label _ghost;
    private readonly Label _preset;
    private readonly Label _model;
    private readonly Label _provider;

    public TgComposerView()
    {
        BorderStyle = LineStyle.Single;
        SetScheme(TgPalette.Scheme(TuiToken.Line, TuiPalette.ElementRgb()));
        CanFocus = true;

        _ghost = new Label
        {
            X = 1,
            Y = 2,
            Scheme = TgPalette.Scheme(TuiToken.Meta, TuiPalette.ElementRgb()),
        };

        Input = new TextField
        {
            X = 1,
            Y = 2,
            Width = Dim.Fill(2),
            Text = string.Empty,
        };
        Input.SetScheme(TgPalette.Scheme(TuiToken.Ink, TuiPalette.ElementRgb()));
        Input.TextChanged += _ => UpdateGhost();

        _preset = new Label { X = 1, Y = 4 };
        _preset.SetScheme(TgPalette.Scheme(TuiToken.Brand, TuiPalette.ElementRgb()));
        _model = new Label { X = Pos.Right(_preset) + 2, Y = 4 };
        _model.SetScheme(TgPalette.Scheme(TuiToken.Ink, TuiPalette.ElementRgb()));
        _provider = new Label { X = Pos.Right(_model) + 1, Y = 4 };
        _provider.SetScheme(TgPalette.Scheme(TuiToken.Meta, TuiPalette.ElementRgb()));

        Add(Input, _ghost, _preset, _model, _provider);
    }

    public TextField Input { get; }

    public void SetPlaceholder(string placeholder)
    {
        _ghost.Text = placeholder;
        UpdateGhost();
    }

    public void SetMeta(string preset, string model, string provider)
    {
        _preset.Text = preset;
        _model.Text = model;
        _provider.Text = ShortenProvider(provider);
    }

    private void UpdateGhost() => _ghost.Visible = string.IsNullOrEmpty(Input.Text?.ToString());

    /// <summary><c>models.dev:kilo</c> reads as <c>kilo</c>; other ids pass through.</summary>
    private static string ShortenProvider(string provider)
    {
        const string prefix = "models.dev:";
        return provider.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? provider[prefix.Length..]
            : provider;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TgComposerTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/TerminalGui/TgComposerView.cs backend-dotnet/RuAgent.Cli.Tests/TgComposerTests.cs
git commit -m "feat(cli): add the Terminal.Gui composer view"
```

---

## Phase 1 — Surfaces (Tasks 5-8)

### Task 5: Header + hints row

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/TerminalGui/TgHeaderView.cs`
- Create: `backend-dotnet/RuAgent.Cli/TerminalGui/TgHintsView.cs`
- Test: extend `TgComposerTests.cs` with a `TgChromeTests` class in `backend-dotnet/RuAgent.Cli.Tests/TgChromeTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgChromeTests
{
    [Fact]
    public void Header_ShowsTitleAndUsageCluster()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var header = new TgHeaderView { X = 0, Y = 0, Width = Dim.Fill() };
        header.SetTitle("Fix the busy lock");
        header.UpdateUsage(totalTokens: 39413, percent: 20, cost: 0.29m);
        window.Add(header);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var row = TgSnapshot.Capture(app)[0];
            Assert.Contains("Fix the busy lock", row);
            Assert.Contains("39,413", row);
            Assert.Contains("20%", row);
            Assert.Contains("$0.29", row);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void Hints_IdleShowsWorkspaceAndKeys_BusyShowsSpinner()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var hints = new TgHintsView { X = 0, Y = 0, Width = Dim.Fill() };
        hints.SetIdle("C:/Users/USER");
        window.Add(hints);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var row = TgSnapshot.Capture(app)[0];
            Assert.Contains("C:/Users/USER", row);
            Assert.Contains("/ commands", row);
            Assert.Contains("ctrl+o", row);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TgChromeTests"`
Expected: FAIL (types missing).

- [ ] **Step 3: Implement both views**

```csharp
using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace RuAgent.Cli.TerminalGui;

/// <summary>One-line header: <c># title</c> left, usage cluster right.</summary>
public sealed class TgHeaderView : View
{
    private readonly Label _title;
    private readonly Label _usage;

    public TgHeaderView()
    {
        Height = 1;
        SetScheme(TgPalette.Scheme(TuiToken.Meta));
        _title = new Label { X = 0, Y = 0, Text = " #" };
        _usage = new Label { X = 0, Y = 0 };
        Add(_title, _usage);
    }

    public void SetTitle(string title) => _title.Text = " # " + title;

    public void UpdateUsage(long totalTokens, int percent, decimal? cost)
    {
        var usage = $"{totalTokens:N0}  {percent}%";
        if (cost is { } value)
        {
            usage += $"  (${value:F2})";
        }

        _usage.Text = usage;
        // Right-aligned: recompute the anchor against the live width.
        _usage.X = Math.Max(0, (App?.Screen.Width ?? 80) - usage.Length - 2);
        SetNeedsDraw();
    }
}

/// <summary>Hint row: idle workspace left, key hints right; busy swaps in the spinner.</summary>
public sealed class TgHintsView : View
{
    private const string KeysRight = "/ commands  tab complete  ctrl+o expand";
    private string _left = string.Empty;

    public TgHintsView()
    {
        Height = 1;
        SetScheme(TgPalette.Scheme(TuiToken.Meta));
    }

    public string Workspace { get; private set; } = string.Empty;

    public void SetIdle(string workspace)
    {
        Workspace = workspace;
        _left = workspace;
        SetNeedsDraw();
    }

    public void SetBusy(string spinnerFrame) => SetLeft($"{spinnerFrame} esc interrupt");

    private void SetLeft(string left)
    {
        _left = left;
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        var right = KeysRight;
        var gap = Math.Max(1, width - _left.Length - right.Length);
        SetAttribute(TgPalette.Attr(TuiToken.Meta));
        Move(0, 0);
        AddStr(TuiText.Truncate(_left + new string(' ', gap) + right, width));
        return true;
    }
}
```

`TgHeaderView` mirrors the classic header copy (`TuiHeader`'s tally formatting); `TgHintsView` reuses the exact strings from `ComposerBox.ComposeHints` so both engines pin the same copy.

- [ ] **Step 4: Run tests**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TgChromeTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/TerminalGui/TgHeaderView.cs backend-dotnet/RuAgent.Cli/TerminalGui/TgHintsView.cs backend-dotnet/RuAgent.Cli.Tests/TgChromeTests.cs
git commit -m "feat(cli): add the Terminal.Gui header and hints views"
```

---

### Task 6: Transcript view (append, wrap, tail pin)

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/TerminalGui/TgTranscriptView.cs`
- Test: `backend-dotnet/RuAgent.Cli.Tests/TgTranscriptTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.ViewBase;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgTranscriptTests
{
    private static string[] Render(int rows, Action<TgTranscriptView> act)
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = new Window { BorderStyle = LineStyle.None };
        var transcript = new TgTranscriptView { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        window.Add(transcript);
        act(transcript);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { app.RequestStop(); return false; });
        app.Run(window);
        return TgSnapshot.Capture(app);
    }

    [Fact]
    public void Overflow_PinsTheTail()
    {
        var rows = Render(24, t =>
        {
            for (var i = 0; i < 60; i++)
            {
                t.Append($" assistant line {i:D2}", TranscriptStyle.Prose);
            }
        });

        Assert.Contains("assistant line 59", rows[23]);
        Assert.DoesNotContain("assistant line 00", string.Join('\n', rows));
    }

    [Fact]
    public void AppendChunk_GrowsTheSameLine()
    {
        var rows = Render(24, t =>
        {
            t.Append("Em ", TranscriptStyle.Prose);
            t.AppendChunk("kiểm tra ");
            t.AppendChunk("lock.");
        });

        Assert.Contains("Em kiểm tra lock.", rows[0]);
    }

    [Fact]
    public void LongLine_WrapsInsideTheViewport()
    {
        var rows = Render(24, t => t.Append(new string('x', 200), TranscriptStyle.Prose));

        Assert.Contains("x", rows[0]);
        Assert.Contains("x", rows[1]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TgTranscriptTests"`
Expected: FAIL (types missing).

- [ ] **Step 3: Implement the transcript**

```csharp
using RuAgent.Cli.Tui;
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>Row styles the transcript can paint; maps 1:1 to the classic palette roles.</summary>
public enum TranscriptStyle
{
    Prose,
    User,
    Tool,
    Meta,
    Error,
}

/// <summary>
/// Append-only transcript with a tail pin and word wrap — the Terminal.Gui analogue of the
/// classic renderer's sticky transcript region. Drawing stays custom so the OpenCode look
/// (user block, inline tool rows, diff indentation) survives the migration.
/// </summary>
public sealed class TgTranscriptView : View
{
    private readonly List<(string Text, TranscriptStyle Style)> _lines = [];
    private List<(string Text, TranscriptStyle Style)> _wrapped = [];
    private int _wrapWidth = -1;

    public void Append(string text, TranscriptStyle style)
    {
        _lines.Add((text, style));
        SetNeedsDraw();
    }

    /// <summary>Appends to the tail line, the way streamed prose grows in place.</summary>
    public void AppendChunk(string text)
    {
        if (_lines.Count == 0)
        {
            Append(text, TranscriptStyle.Prose);
            return;
        }

        var last = _lines[^1];
        _lines[^1] = (last.Text + text, last.Style);
        SetNeedsDraw();
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        var width = Viewport.Width;
        if (width != _wrapWidth)
        {
            _wrapped = WrapAll(width);
            _wrapWidth = width;
        }

        var height = Viewport.Height;
        var offset = Math.Max(0, _wrapped.Count - height);
        for (var i = 0; i < height && offset + i < _wrapped.Count; i++)
        {
            var (text, style) = _wrapped[offset + i];
            SetAttribute(StyleAttribute(style));
            Move(0, i);
            AddStr(text);
        }

        return true;
    }

    private List<(string Text, TranscriptStyle Style)> WrapAll(int width)
    {
        var wrapped = new List<(string, TranscriptStyle)>();
        foreach (var (text, style) in _lines)
        {
            if (TuiText.VisibleWidth(text) <= width)
            {
                wrapped.Add((text, style));
                continue;
            }

            var rest = text;
            while (TuiText.VisibleWidth(rest) > width)
            {
                wrapped.Add((TuiText.Truncate(rest, width), style));
                rest = rest[TuiText.Truncate(rest, width).Length..];
            }

            wrapped.Add((rest, style));
        }

        return wrapped;
    }

    private static Terminal.Gui.Drawing.Attribute StyleAttribute(TranscriptStyle style) => style switch
    {
        TranscriptStyle.User => TgPalette.Attr(TuiToken.Ink, TuiPalette.SurfaceRgb()),
        TranscriptStyle.Tool => TgPalette.Attr(TuiToken.Meta),
        TranscriptStyle.Meta => TgPalette.Attr(TuiToken.Meta),
        TranscriptStyle.Error => TgPalette.Attr(TuiToken.Err),
        _ => TgPalette.Attr(TuiToken.Ink),
    };
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TgTranscriptTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/TerminalGui/TgTranscriptView.cs backend-dotnet/RuAgent.Cli.Tests/TgTranscriptTests.cs
git commit -m "feat(cli): add the Terminal.Gui transcript view"
```

---

### Task 7: Chat window composition + state API

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/TerminalGui/TgChatWindow.cs`
- Test: `backend-dotnet/RuAgent.Cli.Tests/TgChatWindowTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgChatWindowTests
{
    [Fact]
    public void Layout_PinsHeaderComposerAndHints()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Header.SetTitle("Fix the busy lock");
        window.Header.UpdateUsage(39413, 20, 0.29m);
        window.Composer.SetMeta("Standard", "stealth/space-bunny-alpha", "models.dev:kilo");
        window.Hints.SetIdle("C:/Users/USER");
        window.Transcript.Append(" ▌ sửa lỗi busy lock", TranscriptStyle.User);
        window.Transcript.Append(" Em kiểm tra lock.", TranscriptStyle.Prose);
        window.Transcript.Append(" ✱ Search \"busy lock\" (18 matches)", TranscriptStyle.Tool);

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rows = TgSnapshot.Capture(app);
            Assert.Contains("Fix the busy lock", rows[0]);
            Assert.Contains("sửa lỗi busy lock", string.Join('\n', rows));
            Assert.Contains("Search", string.Join('\n', rows));
            Assert.Contains("┌", rows[^7]);
            Assert.Contains("/ commands", rows[^1]);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TgChatWindowTests"`
Expected: FAIL (type missing).

- [ ] **Step 3: Implement the composition**

```csharp
using Terminal.Gui.ViewBase;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// The three-region window: pinned header, scrolling transcript, pinned composer stack and
/// hint row. State changes go through this facade so the run loop never touches views.
/// </summary>
public sealed class TgChatWindow : Window
{
    private TgChatWindow()
    {
        BorderStyle = LineStyle.None;
        SetScheme(TgPalette.Scheme(TuiToken.Ink));

        Header = new TgHeaderView { X = 0, Y = 0, Width = Dim.Fill() };
        Transcript = new TgTranscriptView { X = 0, Y = 1, Width = Dim.Fill(), Height = Dim.Fill(8) };
        Composer = new TgComposerView { X = 0, Y = Pos.AnchorEnd(7), Width = Dim.Fill(), Height = 7 };
        Hints = new TgHintsView { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill() };

        Add(Header, Transcript, Composer, Hints);
        CanFocus = true;
    }

    public TgHeaderView Header { get; }
    public TgTranscriptView Transcript { get; }
    public TgComposerView Composer { get; }
    public TgHintsView Hints { get; }

    public static TgChatWindow Build() => new();
}
```

- [ ] **Step 4: Run tests**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TgChatWindowTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/TerminalGui/TgChatWindow.cs backend-dotnet/RuAgent.Cli.Tests/TgChatWindowTests.cs
git commit -m "feat(cli): compose the Terminal.Gui chat window"
```

---

### Task 8: Focus, caret and key plumbing

**Files:**
- Modify: `backend-dotnet/RuAgent.Cli/TerminalGui/TgChatWindow.cs`
- Test: `backend-dotnet/RuAgent.Cli.Tests/TgFocusTests.cs`

- [ ] **Step 1: Write the failing test (focus + caret after the window settles)**

```csharp
using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgFocusTests
{
    [Fact]
    public void Composer_ReceivesFocusAndCaret()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            window.SetFocus();
            window.Composer.Input.SetFocus();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            Assert.True(window.Composer.Input.HasFocus);
            Assert.True(window.Composer.Input.Cursor?.IsVisible);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void TypedKeys_LandInTheComposerBuffer()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            window.SetFocus();
            window.Composer.Input.SetFocus();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            app.InjectKey(Key.A);
            app.InjectKey(Key.B);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            Assert.Equal("ab", window.Composer.Input.Text?.ToString());
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TgFocusTests"`
Expected: FAIL — focus never sticks or keys never arrive (spike finding: early `SetFocus` is ignored).

- [ ] **Step 3: Implement the focus bootstrap**

Add to `TgChatWindow`:

```csharp
    /// <summary>
    /// Focus must be granted after the app's first loop iterations; a SetFocus before
    /// Run (or on the first tick) is ignored, which the spike reproduced. Callers use
    /// this from a one-shot timeout once the window is live.
    /// </summary>
    public void FocusComposer()
    {
        SetFocus();
        Composer.Input.SetFocus();
    }
```

- [ ] **Step 4: Run tests**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TgFocusTests"`
Expected: PASS (caret visible; `ab` in the buffer).

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/TerminalGui/TgChatWindow.cs backend-dotnet/RuAgent.Cli.Tests/TgFocusTests.cs
git commit -m "feat(cli): bootstrap composer focus and caret in the TG window"
```

---

## Phase 2 — Chat loop (Tasks 9-11)

### Task 9: Extract the submitted-line path from the classic loop

**Files:**
- Modify: `backend-dotnet/RuAgent.Cli/AgentCommands.Chat.cs`
- Test: `backend-dotnet/RuAgent.Cli.Tests/TgChatLoopTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgChatLoopTests
{
    [Fact]
    public async Task ExecuteSubmittedLine_DispatchesSlashCommandWithoutRendering()
    {
        var seen = new List<string>();
        var dispatcher = new AgentCommands.ChatLineDispatcher(
            handleSlash: line => { seen.Add("slash:" + line); return Task.FromResult(true); },
            handlePrompt: (line, _) => { seen.Add("prompt:" + line); return Task.CompletedTask; });

        Assert.True(await dispatcher.DispatchAsync("/status", CancellationToken.None));
        Assert.False(await dispatcher.DispatchAsync("sửa lỗi", CancellationToken.None));
        Assert.Equal(["slash:/status", "prompt:sửa lỗi"], seen);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TgChatLoopTests"`
Expected: FAIL (type missing).

- [ ] **Step 3: Extract the dispatcher and rewire the classic loop**

Add to `AgentCommands.Chat.cs` (partial-class file already hosts the chat command):

```csharp
    /// <summary>
    /// Routes one submitted line to the slash-command handler or the prompt runner. The
    /// classic key loop and the Terminal.Gui loop both call this so command behaviour
    /// (attachments, /model, /attach, trust prompts) cannot drift between engines.
    /// </summary>
    public sealed record ChatLineDispatcher(
        Func<string, Task<bool>> HandleSlash,
        Func<string, CancellationToken, Task> HandlePrompt)
    {
        public async Task<bool> DispatchAsync(string line, CancellationToken cancellationToken)
        {
            if (line.TrimStart().StartsWith('/'))
            {
                return await HandleSlash(line).ConfigureAwait(false);
            }

            await HandlePrompt(line, cancellationToken).ConfigureAwait(false);
            return false;
        }
    }
```

Then, inside the existing classic key loop, replace the inline "slash vs prompt" branch with a call to `dispatcher.DispatchAsync(...)`. Keep behaviour identical; the full classic suite is the regression net.

- [ ] **Step 4: Run the classic chat suites**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~Chat|FullyQualifiedName~AgentSlashCommand"`
Expected: PASS with no test edits (pure extraction).

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/AgentCommands.Chat.cs backend-dotnet/RuAgent.Cli.Tests/TgChatLoopTests.cs
git commit -m "refactor(cli): extract the submitted-line dispatcher from the chat loop"
```

---

### Task 10: TG run loop (type, submit, stream, tool rows)

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/TerminalGui/TgChatLoop.cs`
- Modify: `backend-dotnet/RuAgent.Cli/AgentCommands.Chat.cs` (engine branch)
- Test: `backend-dotnet/RuAgent.Cli.Tests/TgRunLoopTests.cs`

- [ ] **Step 1: Write the failing test (fake stream through the loop)**

```csharp
using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgRunLoopTests
{
    [Fact]
    public async Task Submit_EchoesUserBlock_StreamsProse_AndAppendsToolRows()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);

        loop.SetPromptHandler(async (line, stream, ct) =>
        {
            stream.Append(" Em kiểm tra lock", TranscriptStyle.Prose);
            stream.AppendChunk(" trong harness.");
            stream.Append(" ✱ Search \"busy lock\" (18 matches)", TranscriptStyle.Tool);
            await Task.CompletedTask;
        });

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            window.FocusComposer();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            window.Composer.Input.Text = "sửa lỗi";
            loop.Submit();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            var text = string.Join('\n', TgSnapshot.Capture(app));
            Assert.Contains("sửa lỗi", text);
            Assert.Contains("Em kiểm tra lock trong harness.", text);
            Assert.Contains("Search", text);
            app.RequestStop();
            return false;
        });
        app.Run(window);
        await loop.Completion;
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TgRunLoopTests"`
Expected: FAIL (type missing).

- [ ] **Step 3: Implement the loop**

```csharp
using System.Threading.Channels;

namespace RuAgent.Cli.TerminalGui;

/// <summary>
/// Drives the Terminal.Gui chat window: Enter submits, Esc cancels, and the prompt
/// handler streams into the transcript. Command dispatch stays in
/// <c>AgentCommands.ChatLineDispatcher</c>; this class only owns input and rendering.
/// </summary>
public sealed class TgChatLoop
{
    private readonly TgChatWindow _window;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Func<string, TranscriptStream, CancellationToken, Task> _promptHandler =
        (_, _, _) => Task.CompletedTask;
    private CancellationTokenSource? _turn;

    public TgChatLoop(TgChatWindow window)
    {
        _window = window;
        window.Composer.Input.KeyDown += OnComposerKey;
    }

    public Task Completion => _completion.Task;

    public void SetPromptHandler(Func<string, TranscriptStream, CancellationToken, Task> handler) =>
        _promptHandler = handler;

    public void Submit()
    {
        var line = _window.Composer.Input.Text?.ToString() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        _window.Composer.Input.Text = string.Empty;
        _window.Transcript.Append(" ▌ " + line, TranscriptStyle.User);
        _turn = new CancellationTokenSource();
        var stream = new TranscriptStream(_window.Transcript);
        _ = RunTurnAsync(line, stream, _turn.Token);
    }

    public void Cancel() => _turn?.Cancel();

    public void Stop() => _completion.TrySetResult();

    private async Task RunTurnAsync(string line, TranscriptStream stream, CancellationToken ct)
    {
        try
        {
            _window.Hints.SetBusy("⠋");
            await _promptHandler(line, stream, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            stream.Append(" esc interrupted", TranscriptStyle.Meta);
        }
        catch (Exception ex)
        {
            stream.Append(" " + ex.Message, TranscriptStyle.Error);
        }
        finally
        {
            _window.Hints.SetIdle(_window.Hints.Workspace);
        }
    }

    private void OnComposerKey(object? sender, Terminal.Gui.Input.Key key)
    {
        if (key == Terminal.Gui.Input.Key.Enter)
        {
            Submit();
            key.Handled = true;
        }
        else if (key == Terminal.Gui.Input.Key.Esc && _turn is { IsCancellationRequested: false })
        {
            Cancel();
            key.Handled = true;
        }
    }
}

/// <summary>Thin append facade handed to the prompt handler.</summary>
public sealed class TranscriptStream(TgTranscriptView transcript)
{
    public void Append(string text, TranscriptStyle style) => transcript.Append(text, style);
    public void AppendChunk(string text) => transcript.AppendChunk(text);
}
```

Wire the engine branch in `AgentCommands.Chat.cs` right after settings load:

```csharp
        var engine = TuiEngineSelection.Resolve(
            Environment.GetEnvironmentVariable(TuiEngineSelection.EngineVariable),
            Console.IsOutputRedirected,
            cmd.Json,
            TuiViewport.Width(),
            TuiViewport.Height(),
            Environment.GetEnvironmentVariable("NO_COLOR"),
            classic: false);
        if (engine == TuiEngine.TerminalGui)
        {
            await RunTerminalGuiChatAsync(settings, canonicalWorkspace, cmd, cancellationToken).ConfigureAwait(false);
            return;
        }
```

`RunTerminalGuiChatAsync` wires the real clients into the same shape:

```csharp
    private async Task RunTerminalGuiChatAsync(
        JsonElement settings,
        string canonicalWorkspace,
        AgentChatCommand cmd,
        CancellationToken cancellationToken)
    {
        using IApplication app = Application.Create();
        app.Init();
        using TgChatWindow window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var dispatcher = new ChatLineDispatcher(
            handleSlash: line => ExecuteSlashLineAsync(line, window, cancellationToken),
            handlePrompt: (line, ct) => ExecutePromptAsync(line, window, ct));

        loop.SetPromptHandler(async (line, stream, ct) =>
        {
            await dispatcher.DispatchAsync(line, ct).ConfigureAwait(false);
        });

        // Usage frames repaint the header without touching the transcript.
        SubscribeUsageFrames(frame => window.Header.UpdateUsage(
            frame.TotalTokens, frame.Percent, frame.Cost), cancellationToken);

        window.Composer.SetMeta(preset, activeModel, backendName);
        window.Hints.SetIdle(workspaceLabel);
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(250), () => { loop.Stop(); return false; });
        app.Run(window);
        await loop.Completion.ConfigureAwait(false);
    }
```

`ExecuteSlashLineAsync`/`ExecutePromptAsync` are the extracted bodies the classic loop now also calls (Task 9); only the transcript sink differs. If extracting the full bodies proves larger than a mechanical move, keep the classic bodies in place and have the TG path call the same private helpers they call — do not duplicate command logic.

- [ ] **Step 4: Run tests**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TgRunLoopTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/TerminalGui/TgChatLoop.cs backend-dotnet/RuAgent.Cli/AgentCommands.Chat.cs backend-dotnet/RuAgent.Cli.Tests/TgRunLoopTests.cs
git commit -m "feat(cli): drive the TG window from a chat run loop"
```

---

### Task 11: History, tab-complete, ctrl+o

**Files:**
- Modify: `backend-dotnet/RuAgent.Cli/TerminalGui/TgChatLoop.cs`
- Test: extend `TgRunLoopTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
    [Fact]
    public void HistoryUpDown_ReplacesTheBuffer()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        loop.SetPromptHandler((_, _, _) => Task.CompletedTask);
        loop.RememberForTests("lần trước");

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            app.InjectKey(Key.CursorUp);
            Assert.Equal("lần trước", window.Composer.Input.Text?.ToString());
            app.InjectKey(Key.CursorDown);
            Assert.Equal(string.Empty, window.Composer.Input.Text?.ToString());
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
```

- [ ] **Step 2: Run to fail**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~HistoryUpDown"`
Expected: FAIL (`RememberForTests` missing).

- [ ] **Step 3: Implement**

Add a bounded history list (100 entries) fed in `Submit()`, an `internal void RememberForTests(string line)` delegating to it, and Up/Down handling in `OnComposerKey` that swaps `Input.Text`. Tab completes from `InteractiveLineReader.KnownCommands` (reuse the existing matcher) by writing the completion into `Input.Text`. Ctrl+O toggles the last collapsed tool/diff block: the transcript view exposes `ToggleExpanded()` flipping a bool that adds diff lines on next draw; the run loop calls it.

Queueing ports `TypeAheadSession`'s behaviour: when `Submit()` is called while a turn is active, push the line onto a bounded queue (depth 8) instead of starting a second turn, set the hint left text to `enter queues · esc cancels`, and drain the queue in `RunTurnAsync`'s `finally`. `Esc` cancels the active turn only; queued lines survive. Add a test:

```csharp
    [Fact]
    public async Task SubmitWhileBusy_QueuesInsteadOfInterleaving()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        var gate = new TaskCompletionSource();
        var seen = new List<string>();
        loop.SetPromptHandler(async (line, _, _) => { seen.Add(line); await gate.Task; });

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () => { window.FocusComposer(); return false; });
        app.AddTimeout(TimeSpan.FromMilliseconds(300), () =>
        {
            window.Composer.Input.Text = "một";
            loop.Submit();
            window.Composer.Input.Text = "hai";
            loop.Submit();
            Assert.Equal(["một"], seen);
            gate.SetResult();
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(500), () =>
        {
            Assert.Equal(["một", "hai"], seen);
            app.RequestStop();
            return false;
        });
        app.Run(window);
        await loop.Completion;
    }
```

- [ ] **Step 4: Run tests**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~HistoryUpDown"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/TerminalGui/TgChatLoop.cs backend-dotnet/RuAgent.Cli/TerminalGui/TgTranscriptView.cs backend-dotnet/RuAgent.Cli.Tests/TgRunLoopTests.cs
git commit -m "feat(cli): add history, tab completion and ctrl+o to the TG loop"
```

---

## Phase 3 — Panels and attachments (Tasks 12-13)

### Task 12: Slash dropdown + approval/trust/picker/secret panels

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/TerminalGui/TgSlashDropdown.cs`
- Create: `backend-dotnet/RuAgent.Cli/TerminalGui/TgPanelHost.cs`
- Test: `backend-dotnet/RuAgent.Cli.Tests/TgPanelsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using RuAgent.Cli.TerminalGui;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TgPanelsTests
{
    [Fact]
    public void SlashDropdown_ListsCommandsAboveTheComposer()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.Composer.Input.Text = "/";
        window.ShowSlashDropdown();
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var rows = TgSnapshot.Capture(app);
            Assert.Contains("/model", string.Join('\n', rows));
            Assert.Contains("/attach", string.Join('\n', rows));
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }

    [Fact]
    public void ApprovalPanel_RendersChoicesAndEsc()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        window.ShowApproval("terminal_delete_requires_approval");
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            var text = string.Join('\n', TgSnapshot.Capture(app));
            Assert.Contains("1 · once", text);
            Assert.Contains("2 · deny", text);
            Assert.Contains("3 · always", text);
            Assert.Contains("esc", text);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
}
```

- [ ] **Step 2: Run to fail**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TgPanelsTests"`
Expected: FAIL (members missing).

- [ ] **Step 3: Implement**

- `TgSlashDropdown`: a `View` overlay anchored just above the composer (height = min(8, matches)), one row per command (`❯ /model  Switch model`), built from `InteractiveLineReader.KnownCommands` + the existing description table so copy cannot drift. Up/Down move the selection, Enter/Tab accept, Esc closes. `TgChatWindow.ShowSlashDropdown()` adds it and focuses it; `HideSlashDropdown()` removes it.
- `TgPanelHost`: floating `View` centered over the transcript for approval/trust/picker/secret/key panels. Row content is ported 1:1 from `ApprovalPrompt`/`SlashDropdown`/`KeyInputPanel` render methods (same strings; the classic tests stay the copy source of truth). Key handling: `1/2/3`, arrows, Enter, Esc.

- [ ] **Step 4: Run tests**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TgPanelsTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/TerminalGui/TgSlashDropdown.cs backend-dotnet/RuAgent.Cli/TerminalGui/TgPanelHost.cs backend-dotnet/RuAgent.Cli/TerminalGui/TgChatWindow.cs backend-dotnet/RuAgent.Cli.Tests/TgPanelsTests.cs
git commit -m "feat(cli): add slash dropdown and floating panels to the TG window"
```

---

### Task 13: Attachments, paste and drop in the TG composer

**Files:**
- Modify: `backend-dotnet/RuAgent.Cli/TerminalGui/TgChatLoop.cs`
- Modify: `backend-dotnet/RuAgent.Cli/AgentCommands.Chat.cs`
- Test: extend `TgRunLoopTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void AttachmentLabel_ShowsInHintsRow()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        var loop = new TgChatLoop(window);
        loop.SetAttachmentLabelProvider(() => "[2 attachments] C:/Users/USER");

        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            loop.RefreshAttachmentLabel();
            Assert.Contains("[2 attachments] C:/Users/USER", string.Join('\n', TgSnapshot.Capture(app)));
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
```

- [ ] **Step 2: Run to fail**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~AttachmentLabel"`
Expected: FAIL (member missing).

- [ ] **Step 3: Implement**

- `TgChatLoop` gains `SetAttachmentLabelProvider(Func<string>)` and `RefreshAttachmentLabel()` → `window.Hints.SetIdle(provider())`.
- `RunTerminalGuiChatAsync` wires the provider to the existing `RefreshAttachmentLabels` logic (pending images/texts count + workspace label) and subscribes the existing Ctrl+V clipboard and drag-drop code paths to call `RefreshAttachmentLabel()` after mutation. No attachment parsing changes: the same `CliImageFiles`/`CliTextAttachment` code runs.

- [ ] **Step 4: Run tests**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~AttachmentLabel"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/TerminalGui/TgChatLoop.cs backend-dotnet/RuAgent.Cli/AgentCommands.Chat.cs backend-dotnet/RuAgent.Cli.Tests/TgRunLoopTests.cs
git commit -m "feat(cli): surface attachments and paste in the TG composer"
```

---

## Phase 4 — Parity and rollout (Tasks 14-16)

### Task 14: Resize + tiny-terminal fallback

**Files:**
- Modify: `backend-dotnet/RuAgent.Cli/TerminalGui/TgChatWindow.cs`
- Test: extend `TgChromeTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
    [Fact]
    public void Window_KeepsComposerPinnedAtTheBottomAfterResize()
    {
        using IApplication app = Application.Create();
        app.Init(DriverRegistry.Names.ANSI);
        using var window = TgChatWindow.Build();
        app.AddTimeout(TimeSpan.FromMilliseconds(200), () =>
        {
            app.Driver!.SetScreenSize(100, 30);
            return false;
        });
        app.AddTimeout(TimeSpan.FromMilliseconds(400), () =>
        {
            var rows = TgSnapshot.Capture(app);
            Assert.Contains("┌", rows[^7]);
            Assert.Contains("/ commands", rows[^1]);
            app.RequestStop();
            return false;
        });
        app.Run(window);
    }
```

- [ ] **Step 2: Run to fail**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~KeepsComposerPinned"`
Expected: FAIL or flaky until the resize path is handled.

- [ ] **Step 3: Implement**

```csharp
        // In RunTerminalGuiChatAsync, after app.Init():
        app.Screen.SizeChanged += (_, _) =>
        {
            var width = app.Screen.Width;
            var rows = app.Screen.Height;
            if (width < 80 || rows < 24)
            {
                // One-way handover: TG owns the screen until now; exiting it lets the
                // classic sequential path take the same session over.
                window.HandOverToSequential = true;
                app.RequestStop();
                return;
            }

            window.Layout();
            window.SetNeedsDraw();
        };
```

`TgChatWindow.HandOverToSequential` is a bool property read after `app.Run` returns; when set, `RunTerminalGuiChatAsync` falls back to the existing sequential loop for the rest of the session. The `Dim.Fill(8)` transcript absorbs size deltas; no manual geometry.

- [ ] **Step 4: Run tests**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~TgChromeTests|FullyQualifiedName~KeepsComposerPinned"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/TerminalGui/TgChatWindow.cs backend-dotnet/RuAgent.Cli.Tests/TgChromeTests.cs
git commit -m "feat(cli): handle resize and the tiny-terminal handover in the TG window"
```

---

### Task 15: Parity pass, flip the default, docs

**Files:**
- Modify: `backend-dotnet/RuAgent.Cli/TerminalGui/TuiEngine.cs`
- Modify: `README.md`, `CHANGELOG.md`, `docs/operations/verification.md`
- Test: `TgEngineSelectionTests.cs`

- [ ] **Step 1: Parity checklist (manual, owner-assisted)**

Run the TG engine (`set RU_TUI_ENGINE=terminalgui` then `ru`) and the classic engine side by side. Every item must match before flipping:
- idle frame: header, transcript, composer (square, gray, meta, placeholder), hints;
- streaming prose (same-line growth), tool rows, diff behind ctrl+o;
- esc interrupt mid-turn; queued message during a turn;
- slash dropdown, approval/trust/picker/secret/key panels;
- attachments (Ctrl+V image, `/attach`, label in hints);
- cost/context meter updates after `/model` and a run;
- resize; tiny terminal falls back to sequential;
- IME Vietnamese typing in cmd.

- [ ] **Step 2: Flip the default in `TuiEngineSelection`**

```csharp
        // Parity reached (2026-10-03): Terminal.Gui is the default; RU_TUI_ENGINE=classic
        // is the escape hatch for one release.
        if (string.Equals(envValue?.Trim(), "classic", StringComparison.OrdinalIgnoreCase))
        {
            return TuiEngine.Classic;
        }

        return TuiEngine.TerminalGui;
```

Update the test from Task 1 accordingly (default becomes `TerminalGui`; add the `classic` escape-hatch case).

- [ ] **Step 3: Docs**

- `CHANGELOG.md` `[Unreleased]`: `feat(cli): TUI full-screen chuyển sang Terminal.Gui v2 (mặc định), classic là escape hatch qua RU_TUI_ENGINE=classic` + note fallback unchanged.
- `README.md`: TUI section mentions the engine flag; badge counts unchanged until the matrix run below.
- `docs/operations/verification.md`: record the new matrix run.

- [ ] **Step 4: Full verification**

Run: `.\scripts\test_dotnet.ps1 -Configuration Debug`
Expected: exit 0, all 12 projects green; note the new total.
Run: `.venv\Scripts\python.exe -m pytest tests -q`
Expected: `171 passed, 1 skipped` (or the current pin).
Update badge + verification counts to the new total.
Run: `cmd /d /c build.bat` (kill stale RuBackend/Broker first)
Expected: exit 0.

- [ ] **Step 5: Commit + push**

```bash
git add backend-dotnet/RuAgent.Cli backend-dotnet/RuAgent.Cli.Tests README.md CHANGELOG.md docs/operations/verification.md
git commit -m "feat(cli): default the chat TUI to Terminal.Gui with a classic escape hatch"
git push origin dev
```

---

### Task 16: Retire the classic full-screen renderer (separate follow-up)

**Files:**
- Delete (after one release of dual-engine): `Tui/TuiStickyRegion.cs`, `Tui/TuiScreenLayout.cs`, `Tui/TuiSurface.cs`, alt-buffer plumbing and their tests.
- Keep forever: sequential fallback (`InteractiveLineReader` box path), palette, text helpers, images/attachments.

- [ ] **Step 1: Confirm no reports against `RU_TUI_ENGINE=classic` for one release cycle.**
- [ ] **Step 2: Delete the alt-path files and their tests; run the full matrix; update counts.**
- [ ] **Step 3: Commit** `refactor(cli): remove the classic full-screen renderer after the TG migration`.

Do not start this task before the release in Task 15 has shipped and been exercised.

---

## Appendix A — Spike findings (2026-10-03)

Reproduced with a throwaway project (`Terminal.Gui` 2.5.0, `net10.0-windows`); artifacts were rendered to PNG from the driver's cell buffer.

- **Package/driver**: `Application.Create()` + `app.Init(DriverRegistry.Names.ANSI)` renders headless and is the documented testing path; `VirtualTimeProvider` + `app.InjectKey(...)` (`Terminal.Gui.Testing`) drive deterministic tests.
- **Square corners**: `BorderStyle = LineStyle.Single`; the classic look maps 1:1.
- **Scheme trap**: unset `Focus`/`Editable` roles are derived by swapping fg/bg — the composer input turned into a bright bar until both were pinned. `TgPalette.Scheme` pins Normal/Focus/Editable/Active.
- **Focus trap**: `SetFocus` before or on the first loop iterations is ignored; focusing the window then the input after ~a few ticks works (spike: tick 5). `TgChatWindow.FocusComposer()` exists for that.
- **Caret verified**: after focus, `Input.Cursor` and `driver.GetCursor()` both reported `{X=2, Y=46} visible=True` — the terminal cursor lands inside the composer row, which is what IME anchoring needs.
- **Streaming verified**: a ~40-line custom `View` (`OnDrawingContent` + `Move`/`AddStr`/`SetAttribute`) reproduced append, same-line chunk growth, wrap and tail pinning with the composer pinned by `Pos.AnchorEnd`.
- **Cell buffer as test oracle**: `driver.Contents` exposes `Cell.Grapheme` + `Cell.Attribute`, so a snapshot helper replaces `TuiScreenSimulator` without a terminal.
