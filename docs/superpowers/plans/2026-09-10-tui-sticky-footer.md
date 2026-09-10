# TUI Sticky Footer + Type-Ahead Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Pin the composer at the viewport bottom during agent streaming (DECSTBM scroll region) with live type-ahead and a single pending slot.

**Architecture:** Terminal scroll-margins reserve 3 footer rows; all existing streaming output scrolls above untouched. The key loop gains an opt-in sticky mode (`StickySession`, default off) so the classic path is byte-identical. One shared write gate serializes the spinner task, key loop, and streaming writes.

**Tech Stack:** .NET 10, C# nullable + ImplicitUsings, xunit, ANSI (DECSTBM `ESC[P1;P2r`).

## Global Constraints

- Classic (`--classic`), JSON (`--json`), and redirected/dumb-terminal paths keep byte-identical behavior.
- TDD: failing test first for every unit-testable piece; full `Cli.Tests` green after every task.
- No `Console.ReadKey`/`KeyAvailable` access and no console writes outside `TuiWriteGate.Sync`, except code paths proven single-threaded classic-only.
- Conventional Commits with scope (`fix(cli):`, `feat(cli):`, `docs(cli):`).
- Ctrl+C semantics: cancel the active turn only, never kill the session; idle Ctrl+C quits (today's UX).
- Spec: `docs/superpowers/specs/2026-09-10-tui-sticky-footer-design.md`.

---

## File Structure

- Create: `backend-dotnet/RuAgent.Cli/Tui/StickyScrollRegion.cs` — margin escape builders, footer geometry, support probe, thin applier.
- Create: `backend-dotnet/RuAgent.Cli/Tui/PendingPromptSlot.cs` — 1-slot thread-safe queue.
- Create: `backend-dotnet/RuAgent.Cli/Tui/StickySession.cs` — `StickyKeyMode` enum + coordination state (busy, key mode, submit TCS, turn CTS).
- Create: `backend-dotnet/RuAgent.Cli.Tests/TuiStickyTests.cs` — region/slot/session tests.
- Modify: `backend-dotnet/RuAgent.Cli/Tui/InteractiveLineReader.cs` — sticky-mode key loop, legend live-read, region-aware resize.
- Modify: `backend-dotnet/RuAgent.Cli/AgentCommands.cs` — sticky turn loop, approval/trust lock sequence, send-next.
- Modify: `backend-dotnet/RuAgent.Cli/Tui/TuiSessionRenderer.cs` — write-gate routing.
- Modify: `backend-dotnet/RuAgent.Cli/Tui/TuiEventRenderer.cs` — filter `model.usage_recorded`.
- Modify: `backend-dotnet/RuAgent.Cli/Tui/GrokHeader.cs` — drop one blank separator line.
- Modify: `backend-dotnet/RuAgent.Cli/AgentCliParser.cs` — `--classic` flag + `ChatCommand.Classic`.
- Modify: `backend-dotnet/RuAgent.Cli/Program.cs` — CancelKeyPress no longer kills the session.
- Modify: `backend-dotnet/RuAgent.Cli/README.md`, `PrintUsage` chat line — document `--classic`.
- Modify: `backend-dotnet/RuAgent.Cli.Tests/TuiInteractiveTests.cs` — parser/usage/header/filter tests.

---

### Task 1: Land pending TUI WIP

**Files:**
- Verify: `backend-dotnet/RuAgent.Cli/Tui/InteractiveLineReader.cs` (ComputePushLines, BuildSubmitEcho, LeaveBox collapse)
- Verify: `backend-dotnet/RuAgent.Cli.Tests/TuiInteractiveTests.cs` (ComputePushLines theory, BuildSubmitEcho fact)

**Interfaces:**
- Consumes: nothing.
- Produces: clean tree for feature work.

- [ ] **Step 1: Run the suite**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj --nologo -v q`
Expected: PASS, 190/190 (179 baseline + 10 ComputePushLines + 1 BuildSubmitEcho).

- [ ] **Step 2: Commit**

```bash
git add backend-dotnet/RuAgent.Cli backend-dotnet/RuAgent.Cli.Tests
git commit -m "fix(cli): pad bottom only near full screen; collapse submit echo" -m "ComputePushLines caps bottom padding at MaxPushPadLines so an empty screen keeps the composer under the content; LeaveBox clears the 3-line box and prints a single dim echo so two identical input boxes never appear together."
```

---

### Task 2: Filter `model.usage_recorded` from chat output

**Files:**
- Modify: `backend-dotnet/RuAgent.Cli/Tui/TuiEventRenderer.cs:180`
- Test: `backend-dotnet/RuAgent.Cli.Tests/TuiInteractiveTests.cs` (append near `TuiEventRenderer_RendersRunCompleted_SuccessAndFailure`)

**Interfaces:**
- Consumes: `TuiEventRenderer.RenderEnvelope(HarnessSafeEventEnvelope, TextWriter)`.
- Produces: internal `model.*` bookkeeping events never reach the transcript.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void TuiEventRenderer_FiltersInternalUsageRecordedEvents()
{
    AnsiTheme.IsEnabled = false;
    using var sw = new StringWriter();
    var env = new HarnessSafeEventEnvelope(
        ProtocolVersion: 1,
        EventId: "evt_7",
        RunId: "run_1",
        SessionId: "sess_1",
        Sequence: 6,
        EventType: "model.usage_recorded",
        OccurredAt: DateTimeOffset.UtcNow.ToString("O"),
        Status: "reported",
        SafeSummary: "reported");

    TuiEventRenderer.RenderEnvelope(env, sw);
    Assert.Equal(string.Empty, sw.ToString());
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj --nologo -v q --filter "FullyQualifiedName~FiltersInternalUsageRecordedEvents"`
Expected: FAIL (output contains `model.usage_recorded: reported`).

- [ ] **Step 3: Extend the filter list**

```csharp
if (type is "run.state_changed" or "model.step_started" or "model.step_completed" or "prompt_receipt"
    or "model.usage_recorded")
    return;
```

- [ ] **Step 4: Run test to verify it passes**

Run: same filter as Step 2.
Expected: PASS.

- [ ] **Step 5: Run full suite**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj --nologo -v q`
Expected: all green.

- [ ] **Step 6: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/Tui/TuiEventRenderer.cs backend-dotnet/RuAgent.Cli.Tests/TuiInteractiveTests.cs
git commit -m "fix(cli): hide model.usage_recorded bookkeeping from chat transcript"
```

---

### Task 3: Compact the header card by one row

**Files:**
- Modify: `backend-dotnet/RuAgent.Cli/Tui/GrokHeader.cs:69-73`
- Test: `backend-dotnet/RuAgent.Cli.Tests/GrokHeaderTests.cs`

**Interfaces:**
- Consumes: `GrokHeader.Render(IAnsiConsole, string, string, string, string, string)`.
- Produces: tips block directly under the shortcuts block, zero blank lines between.

- [ ] **Step 1: Write the failing test**

```csharp
[Fact]
public void GrokHeader_TipsFollowShortcuts_WithoutBlankLine()
{
    AnsiTheme.IsEnabled = true;
    try
    {
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings { Out = new AnsiConsoleOutput(writer) });
        GrokHeader.Render(console, "sess_12345678", "D:\\repo", "Kilo Gateway", "grok-4.5", "standard");
        var lines = InteractiveLineReader.StripAnsi(writer.ToString()).Split('\n');
        var tipsIdx = Array.FindIndex(lines, l => l.Contains("Type /"));
        Assert.True(tipsIdx > 0, "tips block must exist");
        // Exactly one bordered-blank row (source blank line) sits between
        // shortcuts and tips. After the fix tips must sit directly under
        // shortcuts, so the row above tips must contain letters/digits.
        // (Note: IsNullOrWhiteSpace does NOT work here — blank rows still
        // carry the │ border glyphs.)
        var aboveTips = InteractiveLineReader.StripAnsi(lines[tipsIdx - 1]);
        Assert.Matches("[A-Za-z0-9]", aboveTips);
    }
    finally
    {
        AnsiTheme.IsEnabled = false;
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj --nologo -v q --filter "FullyQualifiedName~TipsFollowShortcuts"`
Expected: FAIL (a blank separator line sits between shortcuts and tips).

- [ ] **Step 3: Delete the blank separator line**

```csharp
var infoText =
    $"[bold cyan]✦ RuAgent v2.0[/] [grey]· Autonomous Coding Agent[/]\n" +
    $"[teal]{divider}[/]\n" +
    $"[grey]Session:[/]   [bold white]{Markup.Escape(ShortId(sessionId))}[/]\n" +
    $"[grey]Workspace:[/] [silver]{Markup.Escape(workspace)}[/]\n" +
    $"[grey]Engine:[/]    [bold yellow]{Markup.Escape(model)}[/] [grey]({Markup.Escape(backend)} · {Markup.Escape(preset)})[/]\n" +
    $"\n" +
    $"{shortcuts}\n" +
    $"[grey]Type [/][bold cyan]/[/][grey] to view & auto-complete available commands[/]\n" +
    $"[grey](↑/↓ to choose, Tab to apply)[/]";
```

- [ ] **Step 4: Run test to verify it passes**

Run: same filter as Step 2.
Expected: PASS (existing `GrokHeaderTests` still green — markers unchanged).

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/Tui/GrokHeader.cs backend-dotnet/RuAgent.Cli.Tests/GrokHeaderTests.cs
git commit -m "fix(cli): compact header card by one row"
```

---

### Task 4: `--classic` flag (parser, record, docs)

**Files:**
- Modify: `backend-dotnet/RuAgent.Cli/AgentCliParser.cs:120-124` (record), `:934-935` (locals), `:996-1003` (token block), `:1011` (Success)
- Modify: `backend-dotnet/RuAgent.Cli/Program.cs:125` (usage line)
- Modify: `backend-dotnet/RuAgent.Cli/README.md:8` (chat line)
- Test: `backend-dotnet/RuAgent.Cli.Tests/TuiInteractiveTests.cs` (append near `Parser_ParsesChatAndReplCommands`)

**Interfaces:**
- Consumes: existing `--json` switch pattern (duplicate-guard, unknown-switch fallthrough).
- Produces: `ChatCommand.Classic: bool` (default `false`, so existing positional constructions still compile); `agent chat --classic` parses.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public void Parser_ParsesClassicFlag()
{
    var res = AgentCliParser.Parse(["chat", "--classic"]);
    Assert.True(res.Success);
    var cmd = Assert.IsType<ChatCommand>(res.Command);
    Assert.True(cmd.Classic);
    Assert.False(cmd.Json);

    var res2 = AgentCliParser.Parse(["chat"]);
    Assert.True(res2.Success);
    Assert.False(Assert.IsType<ChatCommand>(res2.Command).Classic);

    var res3 = AgentCliParser.Parse(["chat", "--classic", "--classic"]);
    Assert.False(res3.Success);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj --nologo -v q --filter "FullyQualifiedName~ParsesClassicFlag"`
Expected: FAIL with "does not contain a definition for 'Classic'".

- [ ] **Step 3: Extend the record (default keeps old call sites compiling)**

```csharp
public sealed record ChatCommand(
    string WorkspacePath,
    string? SessionId,
    string Preset,
    bool Json,
    bool Classic = false) : AgentCommand(CliCommandKind.Chat, Json);
```

- [ ] **Step 4: Parse the switch (mirrors `--json`)**

Locals, after `bool jsonSeen = false;`:

```csharp
bool classic = false;
bool classicSeen = false;
```

Token block, after the `--json` block:

```csharp
if (token == "--classic")
{
    if (classicSeen)
        return Fail("duplicate_switch", "Duplicate --classic.");
    classic = true;
    classicSeen = true;
    continue;
}
```

Success line:

```csharp
return Success(new ChatCommand(workspace ?? Environment.CurrentDirectory, sessionId, preset, json, classic));
```

Usage (`Program.cs:125`) and `README.md:8` chat lines gain ` [--classic]`:

```
agent chat [--preset standard|full_access] [--workspace PATH] [--session <id>] [--json] [--classic]
```

- [ ] **Step 5: Run tests to verify they pass**

Run: same filter as Step 2, then full suite `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj --nologo -v q`.
Expected: PASS, all green (existing 4-arg `new ChatCommand(...)` sites still compile via the default).

- [ ] **Step 6: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/AgentCliParser.cs backend-dotnet/RuAgent.Cli/Program.cs backend-dotnet/RuAgent.Cli/README.md backend-dotnet/RuAgent.Cli.Tests/TuiInteractiveTests.cs
git commit -m "feat(cli): add --classic flag to keep sequential chat layout"
```

---

### Task 5: Scroll-region geometry + support probe (TDD)

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/Tui/StickyScrollRegion.cs`
- Test: `backend-dotnet/RuAgent.Cli.Tests/TuiStickyTests.cs`

**Interfaces:**
- Consumes: nothing (pure math + thin console shell).
- Produces: `StickyScrollRegion.BuildSetMargins(int, int): string`, `BuildResetMargins(): string`, `ComputeBottomMargin(int): int`, `IsSupported(bool, string?): bool`, `SetRegion(TextWriter, int, int): void`, `ResetRegion(TextWriter): void`; `FooterRows = 3`.

- [ ] **Step 1: Write the failing tests**

```csharp
using RuAgent.Cli.Tui;
using Xunit;

namespace RuAgent.Cli.Tests;

public sealed class TuiStickyTests
{
    [Theory]
    [InlineData(1, 21, "\x1b[1;21r")]
    [InlineData(1, 10, "\x1b[1;10r")]
    public void StickyScrollRegion_BuildsSetMarginsSequence(int top, int bottom, string expected)
    {
        Assert.Equal(expected, StickyScrollRegion.BuildSetMargins(top, bottom));
    }

    [Fact]
    public void StickyScrollRegion_BuildsResetSequence()
    {
        Assert.Equal("\x1b[r", StickyScrollRegion.BuildResetMargins());
    }

    [Theory]
    // Box top at 0-based row 11 pins 1-based rows 1..11 as scrollable; footer starts at 1-based row 12.
    [InlineData(11, 11)]
    [InlineData(4, 4)]
    [InlineData(0, 0)]
    public void StickyScrollRegion_ComputesBottomMargin(int boxTopRowZeroBased, int expected)
    {
        Assert.Equal(expected, StickyScrollRegion.ComputeBottomMargin(boxTopRowZeroBased));
    }

    [Theory]
    [InlineData(false, null, true)]
    [InlineData(false, "xterm-256color", true)]
    [InlineData(false, "dumb", false)]
    [InlineData(false, "DUMB", false)]
    [InlineData(true, null, false)]
    [InlineData(true, "xterm-256color", false)]
    public void StickyScrollRegion_DetectsSupport(bool redirected, string? term, bool expected)
    {
        Assert.Equal(expected, StickyScrollRegion.IsSupported(redirected, term));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj --nologo -v q --filter "FullyQualifiedName~TuiStickyTests"`
Expected: FAIL (type not defined).

- [ ] **Step 3: Implement**

```csharp
namespace RuAgent.Cli.Tui;

/// <summary>
/// DECSTBM scroll-region helpers that pin the 3-row composer footer while
/// agent output scrolls above it. Pure math is unit-tested; the thin applier
/// swallows IOException like the rest of the TUI (best-effort rendering).
/// </summary>
public static class StickyScrollRegion
{
    public const int FooterRows = 3;

    public static string BuildSetMargins(int topRow1Based, int bottomRow1Based) =>
        $"\x1b[{topRow1Based};{bottomRow1Based}r";

    public static string BuildResetMargins() => "\x1b[r";

    /// <summary>
    /// DECSTBM margins are 1-based inclusive. A footer whose top border sits at
    /// 0-based row T means 1-based rows 1..T stay scrollable, so the bottom
    /// margin parameter equals T.
    /// </summary>
    public static int ComputeBottomMargin(int boxTopRowZeroBased) => boxTopRowZeroBased;

    public static bool IsSupported(bool outputRedirected, string? term) =>
        !outputRedirected && !string.Equals(term, "dumb", StringComparison.OrdinalIgnoreCase);

    public static void SetRegion(TextWriter writer, int topRow1Based, int bottomRow1Based)
    {
        try
        {
            writer.Write(BuildSetMargins(topRow1Based, bottomRow1Based));
            writer.Flush();
        }
        catch (IOException)
        {
            // Margins are best-effort; output stays usable without them.
        }
    }

    public static void ResetRegion(TextWriter writer)
    {
        try
        {
            writer.Write(BuildResetMargins());
            writer.Flush();
        }
        catch (IOException)
        {
            // Best-effort only.
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: same filter as Step 2.
Expected: PASS (10/10).

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/Tui/StickyScrollRegion.cs backend-dotnet/RuAgent.Cli.Tests/TuiStickyTests.cs
git commit -m "feat(cli): add sticky scroll-region geometry and support probe"
```

---

### Task 6: Single pending slot (TDD)

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/Tui/PendingPromptSlot.cs`
- Test: `backend-dotnet/RuAgent.Cli.Tests/TuiStickyTests.cs` (append class `PendingPromptSlotTests`)

**Interfaces:**
- Consumes: nothing.
- Produces: `PendingPromptSlot.HasPending: bool`, `TryQueue(string): bool`, `TryTake(out string?): bool`, `Clear(): void` — all thread-safe.

- [ ] **Step 1: Write the failing tests**

```csharp
public sealed class PendingPromptSlotTests
{
    [Fact]
    public void PendingPromptSlot_QueuesAndTakesOnce()
    {
        var slot = new PendingPromptSlot();
        Assert.False(slot.HasPending);
        Assert.True(slot.TryQueue("hello"));
        Assert.True(slot.HasPending);
        Assert.True(slot.TryTake(out var taken));
        Assert.Equal("hello", taken);
        Assert.False(slot.HasPending);
        Assert.False(slot.TryTake(out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void PendingPromptSlot_RejectsBlankPrompts(string prompt)
    {
        Assert.False(new PendingPromptSlot().TryQueue(prompt));
    }

    [Fact]
    public void PendingPromptSlot_RejectsSecondQueue_WithoutReplacing()
    {
        var slot = new PendingPromptSlot();
        Assert.True(slot.TryQueue("first"));
        Assert.False(slot.TryQueue("second"));
        Assert.True(slot.TryTake(out var taken));
        Assert.Equal("first", taken);
    }

    [Fact]
    public void PendingPromptSlot_Clear_EmptiesSlot()
    {
        var slot = new PendingPromptSlot();
        slot.TryQueue("x");
        slot.Clear();
        Assert.False(slot.HasPending);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj --nologo -v q --filter "FullyQualifiedName~PendingPromptSlotTests"`
Expected: FAIL (type not defined).

- [ ] **Step 3: Implement**

```csharp
namespace RuAgent.Cli.Tui;

/// <summary>
/// Holds at most one queued prompt typed while the agent is busy. Extra
/// submits are rejected (never replace), blanks are rejected. Thread-safe:
/// the composer key task queues while the chat loop takes.
/// </summary>
public sealed class PendingPromptSlot
{
    private readonly object _sync = new();
    private string? _pending;

    public bool HasPending
    {
        get { lock (_sync) return _pending is not null; }
    }

    public bool TryQueue(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt)) return false;
        lock (_sync)
        {
            if (_pending is not null) return false;
            _pending = prompt;
            return true;
        }
    }

    public bool TryTake(out string? prompt)
    {
        lock (_sync)
        {
            prompt = _pending;
            _pending = null;
            return prompt is not null;
        }
    }

    public void Clear()
    {
        lock (_sync) _pending = null;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: same filter as Step 2.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/Tui/PendingPromptSlot.cs backend-dotnet/RuAgent.Cli.Tests/TuiStickyTests.cs
git commit -m "feat(cli): add single-slot pending prompt queue"
```

---

### Task 7: Sticky coordination state (TDD)

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/Tui/StickySession.cs`
- Test: `backend-dotnet/RuAgent.Cli.Tests/TuiStickyTests.cs` (append class `StickySessionTests`)

**Interfaces:**
- Consumes: `PendingPromptSlot` (Task 6).
- Produces: `StickyKeyMode` enum (`Normal`, `Drain`, `Parked`); `StickySession` with `Queue`, `IsBusy`, `Mode`, `FooterTopRowZeroBased`, `ActiveTurnCts`, `RequestTurnCancel()`, `WaitSubmitAsync()`, `CompleteSubmit(string)`, `ResetSubmit()`.

- [ ] **Step 1: Write the failing tests**

```csharp
public sealed class StickySessionTests
{
    [Fact]
    public async Task StickySession_SubmitSignal_CompletesExactlyOnce()
    {
        using var session = new StickySession();
        var waiter = session.WaitSubmitAsync();
        Assert.False(waiter.IsCompleted);
        session.CompleteSubmit("hi");
        Assert.Equal("hi", await waiter);
        session.ResetSubmit();
        Assert.False(session.WaitSubmitAsync().IsCompleted);
    }

    [Fact]
    public void StickySession_BusyAndMode_ToggleUnderLock()
    {
        using var session = new StickySession();
        Assert.False(session.IsBusy);
        session.IsBusy = true;
        Assert.True(session.IsBusy);
        Assert.Equal(StickyKeyMode.Normal, session.Mode);
        session.Mode = StickyKeyMode.Drain;
        Assert.Equal(StickyKeyMode.Drain, session.Mode);
        session.Mode = StickyKeyMode.Parked;
        session.ResetSubmit();
        session.IsBusy = false;
        Assert.False(session.IsBusy);
    }

    [Fact]
    public void StickySession_RequestTurnCancel_CancelsActiveTurn_AndNoopsWithoutOne()
    {
        using var session = new StickySession();
        session.RequestTurnCancel(); // must not throw with no active turn
        using var cts = new CancellationTokenSource();
        session.ActiveTurnCts = cts;
        session.RequestTurnCancel();
        Assert.True(cts.IsCancellationRequested);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj --nologo -v q --filter "FullyQualifiedName~StickySessionTests"`
Expected: FAIL (type not defined).

- [ ] **Step 3: Implement**

```csharp
namespace RuAgent.Cli.Tui;

public enum StickyKeyMode
{
    Normal,
    Drain,
    Parked,
}

/// <summary>
/// Coordination state between the background composer key task and the chat
/// loop during sticky turns. All members are thread-safe. Disposable: cancels
/// and disposes the active turn source on chat exit.
/// </summary>
public sealed class StickySession : IDisposable
{
    private readonly object _sync = new();
    private bool _busy;
    private StickyKeyMode _mode = StickyKeyMode.Normal;
    private TaskCompletionSource<string> _submitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _turnCts;
    private bool _disposed;

    public int FooterTopRowZeroBased { get; set; }

    public PendingPromptSlot Queue { get; } = new();

    public bool IsBusy
    {
        get { lock (_sync) return _busy; }
        set { lock (_sync) _busy = value; }
    }

    public StickyKeyMode Mode
    {
        get { lock (_sync) return _mode; }
        set { lock (_sync) _mode = value; }
    }

    public CancellationTokenSource? ActiveTurnCts
    {
        get { lock (_sync) return _turnCts; }
        set { lock (_sync) _turnCts = value; }
    }

    public Task<string> WaitSubmitAsync()
    {
        lock (_sync) return _submitTcs.Task;
    }

    public void CompleteSubmit(string prompt)
    {
        TaskCompletionSource<string> tcs;
        lock (_sync) tcs = _submitTcs;
        tcs.TrySetResult(prompt);
    }

    public void ResetSubmit()
    {
        lock (_sync) _submitTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public void RequestTurnCancel()
    {
        CancellationTokenSource? cts;
        lock (_sync) cts = _turnCts;
        try { cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            cts = _turnCts;
            _turnCts = null;
        }
        try { cts?.Cancel(); } catch (ObjectDisposedException) { }
        cts?.Dispose();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: same filter as Step 2.
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/Tui/StickySession.cs backend-dotnet/RuAgent.Cli.Tests/TuiStickyTests.cs
git commit -m "feat(cli): add sticky session coordination state"
```

---

### Task 8: Shared write gate + route concurrent writers

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/Tui/TuiWriteGate.cs`
- Modify: `backend-dotnet/RuAgent.Cli/Tui/TuiSessionRenderer.cs` (spinner frame write, spinner clear, summary write)
- Modify: `backend-dotnet/RuAgent.Cli/Tui/InteractiveLineReader.cs` (all `outWriter.Write*` inside `Refresh`, `LeaveBox`, `RenderDropdown`, `ClearDropdown`, `RedrawBoxClean`, box-open/mid writes, resize clear)

**Interfaces:**
- Consumes: nothing.
- Produces: `TuiWriteGate.Sync: object`; every console write in the three writer sites takes the gate. No new unit test is meaningful for a lock — verification is the full suite staying green plus the Task 13 manual checklist.

- [ ] **Step 1: Create the gate**

```csharp
namespace RuAgent.Cli.Tui;

/// <summary>
/// Single global lock serializing all console writes once the composer key
/// task runs concurrently with streaming output (sticky mode). Classic and
/// JSON paths are unaffected (lock is uncontended there).
/// </summary>
internal static class TuiWriteGate
{
    public static readonly object Sync = new();
}
```

- [ ] **Step 2: Route the spinner task through the gate**

In `TuiSessionRenderer.StartThinkingSpinner`, wrap the per-frame write:

```csharp
lock (TuiWriteGate.Sync)
{
    _writer.Write($"\r  {AnsiTheme.White(frameChar)} {AnsiTheme.White(verb)}… {AnsiTheme.Silver($"({elapsed.TotalSeconds:F1}s)")}  ");
    _writer.Flush();
}
```

and the spinner-clear write in `StopThinkingSpinner`:

```csharp
lock (TuiWriteGate.Sync)
{
    _writer.Write("\r                                        \r");
    _writer.Flush();
}
```

- [ ] **Step 3: Route key-loop box writes through the gate**

Wrap each `outWriter.Write*` sequence in `Refresh`, `LeaveBox`, `RenderDropdown`, `ClearDropdown`, `RedrawBoxClean`, the box-open block, and the resize clear in `lock (TuiWriteGate.Sync) { ... }`. Same file, same namespace — no using needed. Do NOT hold the gate across `Console.ReadKey` or `Task.Delay`.

- [ ] **Step 4: Run full suite + build**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj --nologo -v q`
Expected: all green (no behavior change — lock is uncontended on these paths today).
Run: `dotnet build backend-dotnet/RuAgent.Cli/RuAgent.Cli.csproj --nologo -v q --no-restore`
Expected: 0 warnings, 0 errors.

- [ ] **Step 5: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/Tui/TuiWriteGate.cs backend-dotnet/RuAgent.Cli/Tui/TuiSessionRenderer.cs backend-dotnet/RuAgent.Cli/Tui/InteractiveLineReader.cs
git commit -m "refactor(cli): serialize console writes behind a shared gate"
```

---

### Task 9: Sticky mode in the composer key loop

**Files:**
- Modify: `backend-dotnet/RuAgent.Cli/Tui/InteractiveLineReader.cs` (`ReadInteractiveKeyLoopAsync` + its `ReadSingleLineAsync`/`ReadLineWithContinuationAsync` call chain)

**Interfaces:**
- Consumes: `StickySession` + `StickyKeyMode` (Task 7), `PendingPromptSlot` (Task 6), `TuiWriteGate.Sync` (Task 8).
- Produces: sticky-aware key loop. `sticky: null` (default) keeps the classic path byte-identical. New local behaviors: queue-on-Enter-while-busy, turn-cancel on Ctrl+C-while-busy, dropdown suppression while busy, key-mode drain/park handshake, live `Legend` read, slot-full legend hint with 2s auto-restore.

- [ ] **Step 1: Thread an optional session through the read chain**

```csharp
public async Task<string?> ReadLineWithContinuationAsync(
    TextReader inReader,
    TextWriter outWriter,
    string primaryPrompt,
    string continuationPrompt,
    CancellationToken cancellationToken = default,
    StickySession? sticky = null)
```

Pass `sticky` down through `ReadSingleLineAsync` (new optional trailing parameter) into `ReadInteractiveKeyLoopAsync` (new optional trailing parameter). Default `null` everywhere: existing callers and tests compile and behave identically. In sticky mode the loop never `LeaveBox`/returns per submit (Task 10 drives turns via the session); it returns only `null` on cancellation or `string.Empty` on idle Ctrl+C (today's quit signal).

- [ ] **Step 2: Read `Legend` live instead of capturing it once**

Delete `string defaultLegend = Legend;` and replace every `defaultLegend` use inside the loop (`ComputeAutocomplete` return, `BuildBoxOpen`/`BuildBottom` legend args, `LeaveBox`) with a direct `Legend` property read, so `UpdateLegendAsync` refreshes take effect across long sticky sessions.

- [ ] **Step 3: Queue on Enter while busy; signal submit while idle**

Replace the Enter branch (currently `LeaveBox(); return buffer.ToString();`) with:

```csharp
if (sticky is not null && sticky.IsBusy)
{
    var queued = buffer.ToString();
    if (sticky.Queue.TryQueue(queued))
    {
        EchoQueuedInRegion(outWriter, queued);
        buffer.Clear();
        cursor = 0;
        selectedIndex = 0;
        Refresh();
    }
    else
    {
        ShowSlotFullHint();
    }
    continue;
}

if (sticky is not null)
{
    var submitted = buffer.ToString();
    AddToHistory(submitted);
    EchoQueuedInRegion(outWriter, submitted);
    buffer.Clear();
    cursor = 0;
    selectedIndex = 0;
    historyIndex = _history.Count;
    tempInput = string.Empty;
    sticky.CompleteSubmit(submitted);
    Refresh();
    continue;
}

LeaveBox();
return buffer.ToString();
```

New local funcs (cursor sits on the mid line; footer top is `sticky.FooterTopRowZeroBased`, maintained by Task 10):

```csharp
void EchoQueuedInRegion(TextWriter w, string text)
{
    // Step above the footer, print the dim echo into the scroll region (it
    // scrolls with output), step back onto the mid line, redraw the box.
    lock (TuiWriteGate.Sync)
    {
        w.Write("\r\x1b[2A");
        w.WriteLine(BuildSubmitEcho(text));
        w.Write("\x1b[1B");
        w.Flush();
    }
}
```

Slot-full hint: add locals `string? legendOverride` + `DateTime legendOverrideUntil`; `Refresh`/`RedrawBoxClean`/`ComputeAutocomplete` prefer `legendOverride` while `DateTime.UtcNow < legendOverrideUntil`, else `Legend`. `ShowSlotFullHint()` sets override to `"đã có 1 prompt đang chờ…"` with a 2s deadline and fires a `Task.Delay(2000)` continuation that clears the override and calls `Refresh()` under the gate (re-arming on repeat presses).

- [ ] **Step 4: Turn-cancel on Ctrl+C while busy; drain/park handshake**

Replace the Ctrl+C branch head with:

```csharp
if (sticky is not null && sticky.IsBusy)
{
    sticky.RequestTurnCancel();
    continue;
}
```

In the `while (!Console.KeyAvailable)` poll section, before blocking in `ReadKey`, add the sticky pre-check (sticky only; classic flow untouched):

```csharp
if (sticky is not null && sticky.Mode != StickyKeyMode.Normal)
{
    if (sticky.Mode == StickyKeyMode.Drain)
    {
        // Consume pressed-but-unprocessed keys into the buffer (no render),
        // then park so the approval prompt owns stdin.
        while (Console.KeyAvailable)
        {
            var pending = Console.ReadKey(intercept: true);
            if (!char.IsControl(pending.KeyChar))
            {
                buffer.Insert(cursor, pending.KeyChar);
                cursor++;
            }
        }
        sticky.Mode = StickyKeyMode.Parked;
    }
    await Task.Delay(KeyPollMs, cancellationToken).ConfigureAwait(false);
    continue;
}
```

- [ ] **Step 5: Suppress the dropdown popup while busy**

At the top of `RenderDropdown`, add:

```csharp
if (sticky is not null && sticky.IsBusy)
{
    ClearDropdown();
    return;
}
```

Ghost text stays inline (safe); slash lines remain typeable and queueable.

- [ ] **Step 6: Run full suite + build**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj --nologo -v q`
Expected: all green (`sticky: null` everywhere in tests → classic path).
Run: `dotnet build backend-dotnet/RuAgent.Cli/RuAgent.Cli.csproj --nologo -v q --no-restore`
Expected: 0 warnings, 0 errors.

- [ ] **Step 7: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/Tui/InteractiveLineReader.cs
git commit -m "feat(cli): sticky-mode composer loop with queue, cancel and key lock"
```

---

### Task 10: Sticky turn wiring in `ChatAsync`

**Files:**
- Modify: `backend-dotnet/RuAgent.Cli/AgentCommands.cs` (`ChatAsync`: read loop, trust prompt, approval prompt, SSE stream, status bar)

**Interfaces:**
- Consumes: `StickySession`/`StickyKeyMode` (Task 7), `StickyScrollRegion` (Task 5), `TuiWriteGate.Sync` (Task 8), `ChatCommand.Classic` (Task 4), key-loop `sticky` parameter (Task 9).
- Produces: sticky chat loop. Classic branch (`cmd.Classic || cmd.Json || redirected/dumb`) runs today's code verbatim. Sticky branch: region enter/exit, background composer task, submit/queue/send-next, approval+trust lock sequence, per-turn CTS, idle-Ctrl+C quit.

- [ ] **Step 1: Branch sticky vs classic after the header render**

After prompt setup (`turn`/`lastTotalTokens` locals), compute once:

```csharp
bool stickyEnabled = !cmd.Classic && !cmd.Json
    && _in == Console.In && _out == Console.Out
    && !Console.IsOutputRedirected && !Console.IsInputRedirected
    && StickyScrollRegion.IsSupported(Console.IsOutputRedirected, Environment.GetEnvironmentVariable("TERM"));
if (!stickyEnabled)
{
    ... today's while loop verbatim (classic path, unchanged) ...
}
```

Keep the classic loop body exactly as-is (it is the `--classic` fallback).

- [ ] **Step 2: Start one sticky composer task for the whole chat**

```csharp
using var sticky = new StickySession();
using var uiCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
var composerTask = lineReader.ReadLineWithContinuationAsync(
    _in, _out, prompt, contPrompt, uiCts.Token, sticky);
```

(The composer task never returns per turn in sticky mode: idle submits complete `sticky`'s TCS (Task 9 Step 3) and keep looping; it ends only when `uiCts` is cancelled at chat exit.)

- [ ] **Step 3: Sticky turn loop (submit → run → stream → send-next)**

```csharp
while (!cancellationToken.IsCancellationRequested)
{
    string line = await sticky.WaitSubmitAsync().ConfigureAwait(false);
    sticky.ResetSubmit();
    line = line.Trim().Trim('"', '\'');
    if (string.IsNullOrEmpty(line)) continue;
    ... existing slash-command block verbatim (/exit /help /history /clear TryHandleAsync ...) ...
    // ... then the existing run body (CreateRun, trust, SSE, status),
    // with the sticky adaptations in Steps 4-6 ...
    if (sticky.Queue.TryTake(out var queued) && !string.IsNullOrWhiteSpace(queued))
    {
        ... process `queued` as the next line WITHOUT awaiting a new submit
        (loop back to slash-block/run body; wrap the post-submit body so both
        fresh submits and queued takes flow through it) ...
    }
}
```

Concretely: extract the post-`ReadLine` body (slash handling through status bar) into a local func `ProcessChatLineAsync(string rawLine)` returning `bool quit` (`/exit` or idle-Ctrl+C empty). Both the classic loop and the sticky loop call it — classic behavior unchanged (suite proves it).

- [ ] **Step 4: Enter/exit the scroll region around each run**

Before `CreateRunAsync`, on the main thread:

```csharp
int footerTop;
lock (TuiWriteGate.Sync)
{
    footerTop = Console.CursorTop - 1; // cursor sits on the composer mid line
    sticky.FooterTopRowZeroBased = footerTop;
    sticky.IsBusy = true;
    sticky.ActiveTurnCts = turnCts; // per-turn source, see Step 5
    StickyScrollRegion.SetRegion(_out, 1, StickyScrollRegion.ComputeBottomMargin(footerTop));
}
```

After streaming ends (all exits: completed/failed/cancelled — `finally`):

```csharp
lock (TuiWriteGate.Sync)
{
    StickyScrollRegion.ResetRegion(_out);
    sticky.IsBusy = false;
    sticky.ActiveTurnCts = null;
}
```

`Console.CursorTop` reads take the gate (a resize mid-read is handled by Task 11).

- [ ] **Step 5: Per-turn CTS (Ctrl+C cancels the run, never the session)**

```csharp
using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
```

Pass `turnCts.Token` (not the app token) to `CreateRunAsync`, the SSE `ReadEventsAsync` enumeration, and the approval/trust awaits of THIS run. The key loop's Ctrl+C-while-busy cancels exactly this source (Task 9 Step 4). After a cancel, the existing `OperationCanceledException` path prints "Run interrupted by user." and the loop continues to send-next/await-submit.

- [ ] **Step 6: Lock keys around the inline trust + approval prompts**

Trust prompt and approval prompt both use `_in.ReadLineAsync` mid-stream. Wrap each:

```csharp
sticky.Mode = StickyKeyMode.Drain;
var parkedDeadline = DateTime.UtcNow.AddSeconds(2);
while (sticky.Mode != StickyKeyMode.Parked && DateTime.UtcNow < parkedDeadline)
    await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
await _out.WriteAsync(... existing prompt ...).ConfigureAwait(false);
await _out.FlushAsync().ConfigureAwait(false);
var answer = await _in.ReadLineAsync(turnCts.Token).ConfigureAwait(false);
sticky.Mode = StickyKeyMode.Normal;
```

(All under no gate except the prompt write itself, which takes `TuiWriteGate.Sync`. The 2s fallback proceeds even if the key task is momentarily descheduled; worst case matches today's behavior.)

- [ ] **Step 7: Stream writes through the gate**

Wrap the `sessionRenderer.RenderFrame(frame)` call and the inline trust/approval/status writes in `lock (TuiWriteGate.Sync) { ... }`. (Async bodies can't `lock` across awaits — lock only the synchronous write call itself, never across `await`.)

- [ ] **Step 8: Run full suite + build**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj --nologo -v q`
Expected: all green (tests use classic/JSON paths; sticky branch needs a live console).
Run: `dotnet build backend-dotnet/RuAgent.Cli/RuAgent.Cli.csproj --nologo -v q --no-restore`
Expected: 0 warnings, 0 errors.

- [ ] **Step 9: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/AgentCommands.cs
git commit -m "feat(cli): sticky turn loop with type-ahead queue and per-turn cancel"
```

---

### Task 11: Region-aware resize + no-push under sticky

**Files:**
- Modify: `backend-dotnet/RuAgent.Cli/Tui/InteractiveLineReader.cs` (`ApplyPendingResizeAsync`, initial `PushToBottomAsync` call, `Refresh`)
- Modify: `backend-dotnet/RuAgent.Cli/Tui/StickySession.cs` (add `RegionActive: bool`, locked get/set)

**Interfaces:**
- Consumes: `StickySession.IsBusy`, `StickyScrollRegion`, `TuiWriteGate.Sync`.
- Produces: resize under an active region resets margins, rebuilds the footer in place, restores margins; no blank-line pushing while sticky.

- [ ] **Step 1: Skip bottom-push while a sticky region is active**

Guard both `PushToBottomAsync` calls (initial open + `ApplyPendingResizeAsync`) with `if (sticky is null || !sticky.RegionActive)`. Set `RegionActive = true` right after `SetRegion` and `false` right before `ResetRegion` (Task 10 Step 4 sites — wire the two assignments here if Task 10 did not).

- [ ] **Step 2: Region-aware resize rebuild**

In `ApplyPendingResizeAsync`, when `sticky?.RegionActive == true`:

```csharp
StickyScrollRegion.ResetRegion(outWriter);   // under TuiWriteGate.Sync
... existing clear (upLines + \x1b[J) and box rebuild, unchanged ...
sticky.FooterTopRowZeroBased = Console.CursorTop - 1; // mid line after rebuild
StickyScrollRegion.SetRegion(outWriter, 1, StickyScrollRegion.ComputeBottomMargin(sticky.FooterTopRowZeroBased));
```

(All console writes in this path already take the gate from Task 8; keep them gated. `Console.CursorTop` read takes the gate.)

- [ ] **Step 3: Run full suite + build**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj --nologo -v q`
Expected: all green.
Run: `dotnet build backend-dotnet/RuAgent.Cli/RuAgent.Cli.csproj --nologo -v q --no-restore`
Expected: 0 warnings, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/Tui/InteractiveLineReader.cs backend-dotnet/RuAgent.Cli/Tui/StickySession.cs
git commit -m "fix(cli): region-aware resize and no bottom-push under sticky"
```

---

### Task 12: Program Ctrl+C routing + idle quit

**Files:**
- Modify: `backend-dotnet/RuAgent.Cli/Program.cs:56-61`
- Modify: `backend-dotnet/RuAgent.Cli/AgentCommands.cs` (sticky idle-empty branch)

**Interfaces:**
- Consumes: key-loop Ctrl+C protocol (Task 9: busy → turn cancel; idle → `LeaveBox` + `"^C"` + `string.Empty`).
- Produces: Ctrl+C never kills the session; idle Ctrl+C quits with exit 0 (today's UX).

- [ ] **Step 1: Stop killing the app token on Ctrl+C**

```csharp
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (sender, e) =>
{
    // Never terminate the session here: the composer key loop observes the
    // Ctrl+C keystroke itself (cancel turn while busy, quit while idle).
    // Only stop the runtime from killing the process.
    e.Cancel = true;
};
```

- [ ] **Step 2: Idle Ctrl+C quits the sticky loop**

In the sticky turn loop (Task 10), the composer task returning `null` (uiCts cancelled) ends the chat; a submitted `string.Empty` (only produced by the idle Ctrl+C path — empty Enter never leaves the reader) returns `0` immediately, matching today's quit UX. Wire both, mirroring the classic `if (line is null) break;` / empty handling.

- [ ] **Step 3: Run full suite + build**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj --nologo -v q`
Expected: all green.
Run: `dotnet build backend-dotnet/RuAgent.Cli/RuAgent.Cli.csproj --nologo -v q --no-restore`
Expected: 0 warnings, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add backend-dotnet/RuAgent.Cli/Program.cs backend-dotnet/RuAgent.Cli/AgentCommands.cs
git commit -m "fix(cli): route Ctrl+C per turn, quit only while idle"
```

---

### Task 13: Final verification + publish recipe + manual checklist

**Files:**
- None (verification only; no commit unless fixes are needed).

- [ ] **Step 1: Full suite**

Run: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj --nologo -v q`
Expected: all green (target: 190 + Task 2/3/4/5/6/7 new tests).

- [ ] **Step 2: Release build + publish**

```bash
dotnet build backend-dotnet/RuAgent.Cli/RuAgent.Cli.csproj -c Release --nologo -v q
dotnet publish backend-dotnet/RuAgent.Cli/RuAgent.Cli.csproj -c Release -r win-x64 --self-contained true --nologo -v q
```

Expected: 0 warnings, 0 errors; fresh `bin/Release/net10.0/win-x64/publish/RuAgent.Cli.exe`.

- [ ] **Step 3: Manual checklist (with the user, live Windows Terminal)**

1. Fresh `chat`: composer sits under the header (no giant gap).
2. Long run: output scrolls above; composer pinned at bottom, no flicker.
3. Type-ahead mid-stream + Enter: dim echo appears; sent automatically after the run.
4. Second Enter while occupied: hint shows ~2s, prompt NOT replaced.
5. Ctrl+C mid-run: run aborts, queued prompt sends next, buffer preserved.
6. Approval mid-stream: keys lock, 1/2 prompt works, half-typed buffer intact after.
7. Resize mid-stream: footer rebuilt in place, no torn box.
8. `chat --classic`: identical to today's layout/behavior.
9. Redirected output: auto-degrades (no ANSI margins in pipe).
10. `--json` chat: unchanged NDJSON.
11. No `model.usage_recorded` line in transcript; header one row shorter.

## Self-Review

**1. Spec coverage:** Q1 live composer → Tasks 9–10; Q2 single slot no-replace → Task 6 tests (`RejectsSecondQueue_WithoutReplacing`); Q3 cancel-keeps-queue → Tasks 9–10 Step 5 + Task 12; Q4 approval lock → Tasks 9–10 Step 6; Q5 classic/redirect → Tasks 4, 10 Step 1, 11; Q6 slot hint → Task 9 Step 3; Q7 polish trio → Tasks 2–3 (+dim echo landed in Task 1 WIP); approach DECSTBM → Task 5; 3 design sections → Tasks 9 (S1 lifecycle/S2 protocol), 11 (S1 resize), Task 13 S3 checklist. Open Program.cs item from spec section 6: resolved — `CancelKeyPress` → `cts.Cancel()` confirmed at `Program.cs:57-61`, addressed by Task 12.

**2. Placeholder scan:** no TBD/TODO; every code step shows exact code; edge behavior (drain race, lock discipline, `lock`-across-`await` ban) specified inline.

**3. Type consistency:** `StickyScrollRegion`/`PendingPromptSlot`/`StickySession`/`StickyKeyMode`/`TuiWriteGate` member names identical across Tasks 5–11; `ChatCommand.Classic` default `false` keeps existing 4-arg constructions compiling; `sticky: null` defaults keep classic call chains unchanged.
