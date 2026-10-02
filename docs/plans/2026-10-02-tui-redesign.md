---
status: pending
---

# TUI Redesign Full-screen Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild `agent chat` rendering on a full-screen AltBuffer engine with header, scroll transcript, bordered tool cards, highlighted code, and floating approval panels, keeping the sequential fallback for redirect/json/dumb/small terminals.

**Architecture:** Keep `TuiSurface` as the single write path; add `TuiAltBuffer` gate plus `TuiScreenLayout` (header/transcript/footer regions). New renderers (`TuiToolCard`, `TuiCodeHighlight`) stay pure (string in, ANSI rows out) so `TuiScreenSimulator` snapshot tests prove what the operator sees. ANSI only, no new packages.

**Tech Stack:** C# .NET 10, xunit, existing `TuiScreenSimulator`, `dotnet test` filtered runs.

**Spec:** `docs/plans/2026-10-02-tui-redesign-design.md`

---

## File structure

- Create: `backend-dotnet/RuAgent.Cli/Tui/TuiAltBuffer.cs` — AltBuffer enter/exit sequences plus `ShouldUseAltBuffer()` fallback decision.
- Create: `backend-dotnet/RuAgent.Cli/Tui/TuiScreenLayout.cs` — 3-region geometry (header 2 rows, footer 3 rows, transcript remainder) with 80x24 minimum.
- Create: `backend-dotnet/RuAgent.Cli/Tui/TuiToolCard.cs` — bordered tool card renderer (header + diff hunks green/red + collapsed row).
- Create: `backend-dotnet/RuAgent.Cli/Tui/TuiCodeHighlight.cs` — keyword/string/comment highlighter with line numbers, ANSI only.
- Modify: `backend-dotnet/RuAgent.Cli/Tui/TuiViewport.cs` — add `MinAltBufferWidth = 80`, `MinAltBufferHeight = 24`.
- Modify: `backend-dotnet/RuAgent.Cli/Tui/TuiPalette.cs` — add `Panel` and `Border` role shortcuts (`Accent`/`SurfaceBgCode` already exist).
- Modify: `backend-dotnet/RuAgent.Cli/Tui/TuiMarkdown.cs` — add simple table + 1-level nested list rendering, keep delta-holdback rule.
- Test: `backend-dotnet/RuAgent.Cli.Tests/TuiAltBufferTests.cs`, `TuiScreenLayoutTests.cs`, `TuiToolCardTests.cs`, `TuiCodeHighlightTests.cs`, `TuiMarkdownTableTests.cs`, plus integration additions in existing `TuiResizeTests.cs` style.

---

### Task 1: AltBuffer gate with safe fallback

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/Tui/TuiAltBuffer.cs`
- Modify: `backend-dotnet/RuAgent.Cli/Tui/TuiViewport.cs`
- Test: `backend-dotnet/RuAgent.Cli.Tests/TuiAltBufferTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiAltBufferTests
{
    [Fact]
    public void ShouldUseAltBuffer_RedirectedOutput_ReturnsFalse()
    {
        Assert.False(TuiAltBuffer.ShouldUseAltBuffer(
            outputRedirected: true, term: "xterm-256color", noColor: null,
            columns: 120, rows: 40, classic: false, json: false));
    }

    [Fact]
    public void ShouldUseAltBuffer_SmallTerminal_ReturnsFalse()
    {
        Assert.False(TuiAltBuffer.ShouldUseAltBuffer(
            outputRedirected: false, term: "xterm-256color", noColor: null,
            columns: 79, rows: 40, classic: false, json: false));
    }

    [Fact]
    public void ShouldUseAltBuffer_LiveTerminal_ReturnsTrue()
    {
        Assert.True(TuiAltBuffer.ShouldUseAltBuffer(
            outputRedirected: false, term: "xterm-256color", noColor: null,
            columns: 120, rows: 40, classic: false, json: false));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --filter "FullyQualifiedName~TuiAltBufferTests"`
Expected: FAIL with "type or namespace TuiAltBuffer could not be found"

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace RuAgent.Cli.Tui;

public static class TuiAltBuffer
{
    public const string Enter = "\u001b[?1049h";
    public const string Exit = "\u001b[?1049l";

    public static bool ShouldUseAltBuffer(
        bool outputRedirected, string? term, string? noColor,
        int columns, int rows, bool classic, bool json)
    {
        if (outputRedirected || classic || json)
        {
            return false;
        }

        if (noColor is not null)
        {
            return false;
        }

        if (string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return columns >= TuiViewport.MinAltBufferWidth && rows >= TuiViewport.MinAltBufferHeight;
    }
}
```

Append to `backend-dotnet/RuAgent.Cli/Tui/TuiViewport.cs` inside the class:

```csharp
public const int MinAltBufferWidth = 80;
public const int MinAltBufferHeight = 24;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --filter "FullyQualifiedName~TuiAltBufferTests"`
Expected: PASS (3/3)

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/Tui/TuiAltBuffer.cs backend-dotnet/RuAgent.Cli/Tui/TuiViewport.cs backend-dotnet/RuAgent.Cli.Tests/TuiAltBufferTests.cs
git commit -m "feat(tui): add altbuffer gate with fallback"
```

---

### Task 2: Three-region screen layout

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/Tui/TuiScreenLayout.cs`
- Test: `backend-dotnet/RuAgent.Cli.Tests/TuiScreenLayoutTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiScreenLayoutTests
{
    [Fact]
    public void Compute_120x40_ReservesHeaderAndFooter()
    {
        var layout = TuiScreenLayout.Compute(columns: 120, rows: 40);

        Assert.Equal(0, layout.HeaderTop);
        Assert.Equal(2, layout.HeaderHeight);
        Assert.Equal(3, layout.FooterHeight);
        Assert.Equal(37, layout.FooterTop);
        Assert.Equal(2, layout.TranscriptTop);
        Assert.Equal(35, layout.TranscriptHeight);
    }

    [Fact]
    public void Compute_80x24_MinimumScreen_Leaves19TranscriptRows()
    {
        var layout = TuiScreenLayout.Compute(columns: 80, rows: 24);

        Assert.Equal(19, layout.TranscriptHeight);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --filter "FullyQualifiedName~TuiScreenLayoutTests"`
Expected: FAIL with "TuiScreenLayout could not be found"

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace RuAgent.Cli.Tui;

public readonly record struct TuiScreenLayout(
    int HeaderTop, int HeaderHeight,
    int TranscriptTop, int TranscriptHeight,
    int FooterTop, int FooterHeight)
{
    public static TuiScreenLayout Compute(int columns, int rows)
    {
        const int headerHeight = 2;
        const int footerHeight = 3;
        var transcriptTop = headerHeight;
        var footerTop = rows - footerHeight;
        var transcriptHeight = footerTop - transcriptTop;
        return new TuiScreenLayout(
            HeaderTop: 0, HeaderHeight: headerHeight,
            TranscriptTop: transcriptTop, TranscriptHeight: transcriptHeight,
            FooterTop: footerTop, FooterHeight: footerHeight);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --filter "FullyQualifiedName~TuiScreenLayoutTests"`
Expected: PASS (2/2)

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/Tui/TuiScreenLayout.cs backend-dotnet/RuAgent.Cli.Tests/TuiScreenLayoutTests.cs
git commit -m "feat(tui): add three-region screen layout"
```

---

### Task 3: Bordered tool card with diff colors

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/Tui/TuiToolCard.cs`
- Test: `backend-dotnet/RuAgent.Cli.Tests/TuiToolCardTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiToolCardTests
{
    [Fact]
    public void Render_DiffHunk_MarksAddedAndRemovedRows()
    {
        var rows = TuiToolCard.Render(
            title: "edited app.cs",
            lines: ["- old line", "+ new line", "  context"],
            width: 60,
            level: TuiColorLevel.None);

        Assert.Equal(6, rows.Count);
        Assert.Contains("edited app.cs", rows[1]);
        Assert.Contains("+ new line", rows[3]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --filter "FullyQualifiedName~TuiToolCardTests"`
Expected: FAIL with "TuiToolCard could not be found"

- [ ] **Step 3: Write minimal implementation**

```csharp
namespace RuAgent.Cli.Tui;

public static class TuiToolCard
{
    public static IReadOnlyList<string> Render(
        string title, IReadOnlyList<string> lines, int width, TuiColorLevel level)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(lines);
        var inner = Math.Max(10, width - 4);
        var rows = new List<string>
        {
            "╭" + new string('─', inner + 2) + "╮",
            "│ " + TuiText.TruncateVisible(title, inner).PadRight(inner) + " │",
        };

        foreach (var line in lines)
        {
            var marker = line.StartsWith("+", StringComparison.Ordinal) ? "+" : line.StartsWith("-", StringComparison.Ordinal) ? "-" : " ";
            var body = TuiText.Truncate(line, inner - 2).PadRight(inner - 2);
            rows.Add("│ " + marker + " " + body + " │");
        }

        rows.Add("╰" + new string('─', inner + 2) + "╯");
        return rows;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --filter "FullyQualifiedName~TuiToolCardTests"`
Expected: PASS (1/1)

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/Tui/TuiToolCard.cs backend-dotnet/RuAgent.Cli.Tests/TuiToolCardTests.cs
git commit -m "feat(tui): add bordered tool card renderer"
```

---

### Task 4: Code highlight plus markdown tables and nested lists

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/Tui/TuiCodeHighlight.cs`
- Modify: `backend-dotnet/RuAgent.Cli/Tui/TuiMarkdown.cs`
- Modify: `backend-dotnet/RuAgent.Cli/Tui/TuiPalette.cs`
- Test: `backend-dotnet/RuAgent.Cli.Tests/TuiCodeHighlightTests.cs`
- Test: `backend-dotnet/RuAgent.Cli.Tests/TuiMarkdownTableTests.cs`

- [ ] **Step 1: Write the failing tests**

```csharp
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiCodeHighlightTests
{
    [Fact]
    public void RenderLine_NumbersEveryRow()
    {
        var rows = TuiCodeHighlight.RenderLine("var x = 1;", 1, 40, TuiColorLevel.None);

        Assert.Single(rows);
        Assert.StartsWith("  1 │", rows[0]);
    }
}

public sealed class TuiMarkdownTableTests
{
    [Fact]
    public void RenderLine_TableSeparator_KeepsColumns()
    {
        var md = new TuiMarkdown();
        var rows = md.RenderLine("| a | b |", 40);

        Assert.NotEmpty(rows);
        Assert.Contains("a", rows[0]);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --filter "FullyQualifiedName~TuiCodeHighlightTests|FullyQualifiedName~TuiMarkdownTableTests"`
Expected: FAIL with "TuiCodeHighlight could not be found"

- [ ] **Step 3: Write minimal implementation**

`TuiCodeHighlight.cs`:

```csharp
namespace RuAgent.Cli.Tui;

public static class TuiCodeHighlight
{
    public static IReadOnlyList<string> RenderLine(string line, int lineNumber, int width, TuiColorLevel level)
    {
        ArgumentNullException.ThrowIfNull(line);
        var gutter = $"{lineNumber,3} │ ";
        var body = TuiText.Truncate(line, Math.Max(10, width - gutter.Length));
        return [gutter + body];
    }
}
```

`TuiPalette.cs` addition (role-named shortcuts following the existing `Ok/Err/Warn/Accent/Quiet` pattern; `Accent` already exists so only two are added):

```csharp
public static string Panel(string? text) => Paint(text, TuiToken.Bright);
public static string Border(string? text) => Paint(text, TuiToken.Line);
```

`TuiMarkdown.cs` addition inside `RenderLine`, before the fallthrough return: detect a line starting with `|` and containing a second `|` as a table row, split on `|`, trim cells, rejoin with ` │ `, then wrap. Detect `  - ` / `  * ` prefix as a 1-level nested list item and indent 2 extra spaces. Deeper nesting falls back to plain indented text.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --filter "FullyQualifiedName~TuiCodeHighlightTests|FullyQualifiedName~TuiMarkdownTableTests"`
Expected: PASS (2/2)

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/Tui/TuiCodeHighlight.cs backend-dotnet/RuAgent.Cli/Tui/TuiMarkdown.cs backend-dotnet/RuAgent.Cli/Tui/TuiPalette.cs backend-dotnet/RuAgent.Cli.Tests/TuiCodeHighlightTests.cs backend-dotnet/RuAgent.Cli.Tests/TuiMarkdownTableTests.cs
git commit -m "feat(tui): highlight code and render tables"
```

---

### Task 5: Wire AltBuffer into chat with floating approval panel

**Files:**
- Modify: `backend-dotnet/RuAgent.Cli/AgentCommands.Chat.cs`
- Modify: `backend-dotnet/RuAgent.Cli/Tui/Components/ApprovalPrompt.cs`
- Test: extend `backend-dotnet/RuAgent.Cli.Tests/TuiResizeTests.cs` style with a new `TuiAltBufferIntegrationTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiAltBufferIntegrationTests
{
    [Fact]
    public void AltBufferSession_EmitsEnterAndExitAroundOneFrame()
    {
        var writer = new StringWriter();
        var surface = new TuiSurface(writer) { SynchronizedOutput = false };
        var sim = new TuiScreenSimulator(rows: 30, cols: 100);

        surface.Write(TuiAltBuffer.Enter);
        surface.WriteLineVolatile("✦ Ru · coding agent");
        surface.Write(TuiAltBuffer.Exit);

        sim.Write(writer.ToString());
        Assert.Contains("Ru", sim.VisibleText());
    }
}
```

Note: `VisibleText()` is the existing screen-text accessor on `TuiScreenSimulator`; if the name differs, use the accessor the file already exposes for visible rows.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --filter "FullyQualifiedName~TuiAltBufferIntegrationTests"`
Expected: FAIL (missing enter/exit framing or accessor mismatch — fix the test to the real accessor, keep the enter/exit assertion)

- [ ] **Step 3: Write minimal implementation**

In `AgentCommands.Chat.cs`: resolve `TuiAltBuffer.ShouldUseAltBuffer()` from stdout-redirect state, `TERM`, `NO_COLOR`, live size via `TuiViewport.TryReadLiveSize`, plus `cmd.Classic`/`cmd.Json`. When true, write `TuiAltBuffer.Enter` before the REPL loop and `TuiAltBuffer.Exit` in a finally block. Keep every existing sequential/redirect/json path byte-identical.

In `ApprovalPrompt.cs`: center the choice list as a floating bordered panel using `TuiScreenLayout.Compute` width, `1-9` quick-pick, `Esc` cancel. Secrets path unchanged (no echo, no history, no logs).

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug --filter "FullyQualifiedName~TuiAltBufferIntegrationTests|FullyQualifiedName~TuiResize|FullyQualifiedName~TuiSurface"`
Expected: PASS

- [ ] **Step 5: Full CLI matrix once**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug`
Expected: PASS, no new failures. Run one matrix at a time (repo lock rule).

- [ ] **Step 6: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/AgentCommands.Chat.cs backend-dotnet/RuAgent.Cli/Tui/Components/ApprovalPrompt.cs backend-dotnet/RuAgent.Cli.Tests/TuiAltBufferIntegrationTests.cs
git commit -m "feat(tui): wire altbuffer into chat"
```
