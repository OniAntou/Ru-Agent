# TUI Aurora Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rebuild the RuAgent.Cli interactive TUI on a single aurora design system with one
writer per surface, rich tool lines, a live status row, stateful composer, and a sticky
footer with a one-slot prompt queue.

**Architecture:** New `Tui/` modules own the palette, text measurement, write gate, live row
and transcript grammar. `InteractiveLineReader` keeps only the input state machine and
delegates painting to `Tui/Components/`. Durable events decide outcomes; transient
`agent.activity` frames supply detail. Sticky mode reserves the bottom rows with DECSTBM.

**Tech Stack:** .NET 10, C#, xunit, ANSI/VT (no new packages; Spectre.Console becomes unused
and is removed at the end).

**Spec:** `docs/superpowers/specs/2026-09-10-tui-aurora-design.md`
**Sticky semantics:** `docs/superpowers/specs/2026-09-10-tui-sticky-footer-design.md`

## Global Constraints

- Copy in the TUI is English only; errors keep raw `error_code` values verbatim.
- Glyphs stay BMP/width-1: `✦ ◆ ✓ ✗ ▲ ▸ ❯ ⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏ │ · ╭ ╮ ╰ ╯ ─`. No emoji, no `⚠`.
- Palette tokens and hex values are exactly those in spec §3.1; no new colours.
- Colour levels: `truecolor → 256 → 16 → none`; `NO_COLOR` disables colour; `RU_TUI_COLOR`
  overrides; redirected output and `TERM=dumb` disable colour.
- Every rendered line must be `<= width` by `TuiText.VisibleWidth`; truncate with `…`.
- Mutable rows are painted only by `TuiStatusLine` (live row) and `Components/ComposerBox`
  (composer). Transcript appends are the only other writes.
- All writes go through `TuiWriteGate`; mutable repaints are wrapped in `CSI ?2026h/l`.
- `--json` behaviour and `SafeEventRenderer` output are unchanged.
- Existing test projects must stay green; `dotnet build` must stay at 0 warnings.
- Verify with: `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Release`

---

### Task 1: TuiText — display width and truncation

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/Tui/TuiText.cs`
- Test: `backend-dotnet/RuAgent.Cli.Tests/TuiTextTests.cs`

**Interfaces:**
- Produces: `static int TuiText.VisibleWidth(string text)`,
  `static string TuiText.Truncate(string text, int width)`,
  `static string TuiText.Pad(string text, int width)`,
  `static IReadOnlyList<string> TuiText.Wrap(string text, int width)`,
  `static string TuiText.Strip(string text)`,
  `static int TuiText.RuneWidth(int codePoint)`

- [ ] **Step 1: Write failing tests** — combining marks are width 0; CJK `漢` is 2;
  emoji `🙂` is 2; `❯` and `─` are 1; Vietnamese `ế` is 1; `VisibleWidth` ignores ANSI;
  `Truncate` never splits an escape sequence and appends `…`; `Truncate` on a wide rune at
  the boundary drops it rather than half-rendering; `Pad` pads to exact width; `Wrap`
  splits on spaces and never exceeds width.

- [ ] **Step 2: Run to fail** — `dotnet test ... --filter TuiTextTests` → compile error.

- [ ] **Step 3: Implement** — code-point walk with zero-width (Mn/Me/Cf, U+200B–200D,
  FE00–FE0F), wide (U+1100–115F, U+2E80–A4CF, U+AC00–D7A3, U+F900–FAFF, U+FE30–FE6F,
  U+FF00–FF60, U+FFE0–FFE6, U+1F300–1FAFF, U+20000+), ambiguous (U+2190–21FF, U+2500–257F,
  U+25A0–25FF, U+2600–27BF) treated as width 1.

- [ ] **Step 4: Verify** — tests pass, `dotnet build` 0 warnings.

---

### Task 2: TuiPalette — aurora tokens and colour levels

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/Tui/TuiPalette.cs`
- Modify: `backend-dotnet/RuAgent.Cli/Tui/AnsiTheme.cs` (delegate to TuiPalette; keep the
  existing method names until Task 14 so call sites compile during the migration)
- Test: `backend-dotnet/RuAgent.Cli.Tests/TuiPaletteTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `enum TuiColorLevel { None, Basic16, Palette256, TrueColor }`,
  `static TuiColorLevel TuiPalette.Level { get; set; }` (settable for tests),
  `static string TuiPalette.Paint(string text, TuiToken token)`,
  `static string TuiPalette.Code(TuiToken token)`,
  `enum TuiToken { Brand, Brand2, Ink, Meta, Line, Ok, Warn, Err }`

- [ ] **Step 1: Write failing tests** — `truecolor` level emits `38;2;56;189;248` for
  `Brand`; `Palette256` emits `38;5;39`; `Basic16` emits `96`; `None` emits plain text with
  no ESC; `NO_COLOR` env forces `None`; `RU_TUI_COLOR=256` forces `Palette256`; `Ink` never
  emits a foreground code.

- [ ] **Step 2: Run to fail.**

- [ ] **Step 3: Implement** the level table from spec §3.1 and the detection order in
  spec §3.1 (env → NO_COLOR → redirected → dumb → COLORTERM/WT_SESSION → 256).

- [ ] **Step 4: Verify** — tests pass; `GrokFaithfulTests`/`AnsiTheme` tests still pass
  (they assert 256 codes for the old tokens, so `AnsiTheme` keeps its own constants this
  task).

---

### Task 3: TuiSurface — write gate, synchronized batches, live row

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/Tui/TuiSurface.cs`
- Test: `backend-dotnet/RuAgent.Cli.Tests/TuiSurfaceTests.cs`

**Interfaces:**
- Consumes: `TuiText`, `TuiPalette`.
- Produces:
  `sealed class TuiSurface(TextWriter writer)` with
  `void Write(string text)`, `void WriteLine(string text)`, `void Line()`,
  `void Repaint(Action<TextWriter> paint)` (wraps in `CSI ?2026h`/`l` and `\r`),
  `void RepaintSingleLine(string content, int width)` (`\r` + `CSI 2K` + content + clear),
  `void ClearSingleLine(int width)`, `bool SynchronizedOutput { get; set; }`.

- [ ] **Step 1: Write failing tests** — `Repaint` output starts with `\x1b[?2026h` and ends
  with `\x1b[?2026l`; `SynchronizedOutput = false` emits neither; `RepaintSingleLine`
  truncates to width and never splits ANSI; concurrent `Write`/`Repaint` from two threads
  never interleave within one call (assert no torn line by counting newlines).

- [ ] **Step 2: Run to fail.**

- [ ] **Step 3: Implement** with a single `lock` object shared across instances created for
  the same writer, plus `Interlocked`-free simplicity: the gate is a `static readonly object`
  because one process has one console.

- [ ] **Step 4: Verify** — tests pass.

---

### Task 4: TuiStatusLine — the live row

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/Tui/TuiStatusLine.cs`
- Test: `backend-dotnet/RuAgent.Cli.Tests/TuiStatusLineTests.cs`

**Interfaces:**
- Consumes: `TuiSurface`, `TuiText`, `TuiPalette`.
- Produces:
  `sealed class TuiStatusLine(TuiSurface surface)` with
  `void SetActivity(string label)`, `void SetState(TuiRunState state)`,
  `void SetCounters(int turn, long? tokens, int tools)`,
  `void Tick()` (advances the spinner frame and repaints when busy),
  `void Render()`, `string Compose(int width)` (pure, for tests),
  `enum TuiRunState { Idle, Thinking, Tool, WaitingApproval, Failed }`.

- [ ] **Step 1: Write failing tests** — `Compose(80)` shows `⠋ Thinking · 0.4s` left and
  `turn 2 · ⇣1.2k · 3 tools` right; below 60 columns the right side is dropped; the line is
  exactly `width` cells (pad/truncate) for widths 20/60/80/200; idle state renders
  `✓ idle · turn 3 · 18.4s · 3 files changed`; elapsed formatting is `0.4s` / `18.4s` / `1m02s`.

- [ ] **Step 2: Run to fail.**

- [ ] **Step 3: Implement** `Compose` as a pure function; `Render`/`Tick` paint it through
  `surface.RepaintSingleLine`.

- [ ] **Step 4: Verify** — tests pass.

---

### Task 5: TuiHeader — compact aurora header

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/Tui/TuiHeader.cs`
- Modify: `backend-dotnet/RuAgent.Cli/AgentCommands.cs` (replace `GrokHeader.Render` calls)
- Test: `backend-dotnet/RuAgent.Cli.Tests/TuiHeaderTests.cs`
- Delete later (Task 14): `Tui/GrokHeader.cs`

**Interfaces:**
- Consumes: `TuiSurface`, `TuiPalette`, `TuiText`.
- Produces: `static IReadOnlyList<string> TuiHeader.Compose(int width, string workspace,
  string backend, string model, string preset, string sessionId, bool empty)` and
  `static void Render(TuiSurface surface, int width, …)`.

- [ ] **Step 1: Write failing tests** — three content lines plus a rule when `empty=false`
  (brand line, context line, hint line); the empty variant returns the welcome block from
  spec §4; workspace is basename-shortened; model/backend/preset appear on the context line;
  every line is `<= width` at 20/40/80/200; no `Spectre` types in the signature.

- [ ] **Step 2: Run to fail.**

- [ ] **Step 3: Implement** the exact layout:

```
 ✦ Ru · coding agent                                          <session7>
 <workspace> · <preset>                            <backend>/<model>
 ─────────────────────────────────────────────────────────────────────
 / commands · ↑↓ history · tab complete · ctrl+o expand · ctrl+c stop
```

- [ ] **Step 4: Verify** — tests pass; `/clear` path re-renders the header.

---

### Task 6: TuiTranscript — event grammar

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/Tui/TuiTranscript.cs`
- Modify: `backend-dotnet/RuAgent.Cli/Tui/TuiEventRenderer.cs` (delegate to the transcript;
  keep `TuiSessionRenderer.RenderFrame(envelope)` as the entry point used by
  `AgentCommands.OnFrameAsync`)
- Test: `backend-dotnet/RuAgent.Cli.Tests/TuiTranscriptTests.cs`

**Interfaces:**
- Consumes: `TuiSurface`, `TuiStatusLine`, `AgentActivityModel`, `HarnessSafeEventEnvelope`,
  `AgentRunSummaryWireCodec`.
- Produces:
  `sealed class TuiTranscript(TuiSurface surface, TuiStatusLine status)` with
  `void BeginTurn(int turn)`, `void ApplyActivity(AgentActivityModel activity)`,
  `void ApplyDurable(HarnessSafeEventEnvelope envelope)`,
  `void ApplySummary(AgentRunSummaryWire summary)`,
  `void AppendAssistant(string text)`, `void EndTurn(TuiTurnOutcome outcome, string? code)`,
  `void ToggleExpand()`, `IReadOnlyList<string> ComposeToolLine(...)` (pure),
  `IReadOnlyList<string> ComposeFailure(...)` (pure).

- [ ] **Step 1: Write failing tests** — tool line composes as
  `✓ edit  src/app.cs:12-40   +12 -4   340ms` columns; a tool with no captured activity
  degrades to `✓ tool · read_only`; diff blocks render at most 12 lines then
  `· N more lines · ctrl+o`; `ctrl+o` reprints the retained block; assistant deltas append
  verbatim and never re-emit the `…`-glued marker text; `agent.summary` produces
  `✓ turn 3 · 18.4s · 4 tools · 3 files changed · verified`; failure renders the code and a
  remedy line for `provider_rate_limited`/`auth`/`timeout`.

- [ ] **Step 2: Run to fail.**

- [ ] **Step 3: Implement** — hook `ToolStarted` activity to `status.SetActivity`, and
  finalise on durable `tool.execution_completed` (using the captured activity detail);
  finalise on activity `Completed`/`Failed` only when no durable completion follows within
  the turn (track by tool call id).

- [ ] **Step 4: Verify** — tests pass; the old `GrokRendererTests`/`TuiInteractiveTests`
  expectations that assert duplicated tool lines are updated to the single-line grammar.

---

### Task 7: Components/ComposerBox — stateful frame

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/Tui/Components/ComposerBox.cs`
- Modify: `backend-dotnet/RuAgent.Cli/Tui/InteractiveLineReader.cs` (Build* methods delegate)
- Test: `backend-dotnet/RuAgent.Cli.Tests/ComposerBoxTests.cs`

**Interfaces:**
- Produces: `enum ComposerState { Idle, Slash, Busy, Queued, Approval, Error }`,
  `static IReadOnlyList<string> ComposerBox.Compose(int width, ComposerState state,
  string buffer, string ghost, string legend, string hint, string? topLabel)`,
  `static int ComposerBox.Height => 3`.

- [ ] **Step 1: Write failing tests** — border token per state from spec §4.2; top label
  appears only when non-null; bottom border carries `legend` left and `hint` right; ghost
  text renders after the buffer; every line is exactly `width` for 20/40/80/200; the
  `─` fill never emits a broken ANSI run.

- [ ] **Step 2: Run to fail.**

- [ ] **Step 3: Implement**; `InteractiveLineReader.BuildTop/BuildMid/BuildBottom/BuildBoxOpen`
  forward to it so existing callers and tests keep working during migration.

- [ ] **Step 4: Verify** — tests pass; `GrokFaithfulTests.ComposerBox_*` still pass.

---

### Task 8: Components/SlashDropdown — suggestions, approvals, pickers

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/Tui/Components/SlashDropdown.cs`
- Modify: `backend-dotnet/RuAgent.Cli/Tui/InteractiveLineReader.cs` (dropdown builders delegate)
- Test: `backend-dotnet/RuAgent.Cli.Tests/SlashDropdownTests.cs`

**Interfaces:**
- Produces: `sealed record DropdownItem(string Label, string Description, bool Enabled = true)`,
  `static IReadOnlyList<string> SlashDropdown.Compose(int width, string title,
  IReadOnlyList<DropdownItem> items, int selected, int maxVisible, string footer)`.

- [ ] **Step 1: Write failing tests** — window scrolls around the selection, `❯` marks the
  selected row, footer shows `n/total`, disabled rows render muted with a reason, all lines
  fit width, English descriptions.

- [ ] **Step 2: Run to fail.**

- [ ] **Step 3: Implement**; slash command descriptions in `InteractiveLineReader.KnownCommands`
  become English, matching the existing `/help` copy.

- [ ] **Step 4: Verify** — tests pass; approval rendering uses the same component with
  `1 allow once / 2 deny / 3 allow this directory` (row 3 only for
  `external_directory_required`).

---

### Task 9: Submit echo and expand

**Files:**
- Modify: `backend-dotnet/RuAgent.Cli/Tui/InteractiveLineReader.cs` (`BuildSubmitEcho`, `LeaveBox`)
- Modify: `backend-dotnet/RuAgent.Cli/AgentCommands.cs` (Ctrl+O key handling, echo + 1 blank line)
- Test: `backend-dotnet/RuAgent.Cli.Tests/TuiInteractiveTests.cs`

- [ ] **Step 1: Write failing tests** — submit echo is one dim line `❯ text`, flattened for
  multiline input, no background fill; `Ctrl+O` calls `TuiTranscript.ToggleExpand`.

- [ ] **Step 2: Run to fail.**

- [ ] **Step 3: Implement** — replace the three-row filled bar; wire `ConsoleKey.O` with
  `Control` in the key loop to the transcript's expand toggle.

- [ ] **Step 4: Verify** — tests pass.

---

### Task 10: Sticky region

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/Tui/TuiStickyRegion.cs`
- Modify: `backend-dotnet/RuAgent.Cli/AgentCommands.cs` (enter on run start, reset on end)
- Test: `backend-dotnet/RuAgent.Cli.Tests/TuiStickyRegionTests.cs`

**Interfaces:**
- Produces: `sealed class TuiStickyRegion(TextWriter writer)` with
  `static (int Top, int Bottom) Compute(int rows, int footerHeight)` (pure),
  `void Enter(int rows, int footerHeight)`, `void Exit()`, `void Repaint(TextWriter paint)`,
  `bool Active { get; }`, `void Reset()`.

- [ ] **Step 1: Write failing tests** — `Compute(30, 4) == (1, 26)`; `Compute(5, 4)`
  refuses (returns `(0,0)`) because no transcript rows remain; `Enter` emits
  `\x1b[1;26r`; `Exit` emits `\x1b[r`; `Repaint` moves to `rows - footer + 1`, repaints each
  row, and restores the cursor; nested `Enter` calls are idempotent.

- [ ] **Step 2: Run to fail.**

- [ ] **Step 3: Implement** with `Console.WindowHeight` guarded by try/catch (no live
  console in tests/redirect).

- [ ] **Step 4: Verify** — tests pass.

---

### Task 11: Pending prompt slot and sticky turn loop

**Files:**
- Create: `backend-dotnet/RuAgent.Cli/PendingPromptSlot.cs`
- Modify: `backend-dotnet/RuAgent.Cli/AgentCommands.cs` (key loop during streaming)
- Test: `backend-dotnet/RuAgent.Cli.Tests/PendingPromptSlotTests.cs`

**Interfaces:**
- Produces: `sealed class PendingPromptSlot` with `bool TryQueue(string prompt)`,
  `string? Take()`, `bool HasValue { get; }`, `void Clear()`.

- [ ] **Step 1: Write failing tests** — first `TryQueue` returns true, second returns false
  (single slot, no replacement); `Take` clears; `Take` on empty returns null.

- [ ] **Step 2: Run to fail.**

- [ ] **Step 3: Implement** — per the sticky-footer spec §5: Enter queues while busy,
  a full slot shows a transient hint, Ctrl+C cancels the run and keeps the queued prompt,
  approval locks the composer and drains pressed keys first.

- [ ] **Step 4: Verify** — tests pass.

---

### Task 12: Resize, cancel and `--classic`

**Files:**
- Modify: `backend-dotnet/RuAgent.Cli/AgentCliParser.cs` (`--classic` on `chat`),
  `AgentCommands.cs`, `InteractiveLineReader.cs`
- Test: `backend-dotnet/RuAgent.Cli.Tests/TuiInteractiveTests.cs`, `AgentCliParserTests`

- [ ] **Step 1: Write failing tests** — `--classic` parses and disables sticky; resize
  (width or height change) re-enters the region, recomputes rows and repaints the footer;
  `TERM=dumb` and redirected output select classic automatically.

- [ ] **Step 2: Run to fail.**

- [ ] **Step 3: Implement** — check `Console.WindowWidth/Height` on spinner ticks and key
  events; `ChatCommand` gains `bool Classic`.

- [ ] **Step 4: Verify** — tests pass.

---

### Task 13: Full-turn snapshot

**Files:**
- Test: `backend-dotnet/RuAgent.Cli.Tests/TuiTranscriptSnapshotTests.cs`

- [ ] **Step 1: Write the test** — feed a scripted sequence (turn start, thinking, two tool
  activities + durable completions, a diff, an assistant reply, `agent.summary`, turn end)
  through `TuiSessionRenderer` into a `StringWriter` and assert the full transcript, colour
  disabled for stability.
- [ ] **Step 2: Run to fail, then to pass** once the grammar is in place.
- [ ] **Step 3: Commit** the snapshot so later edits show up as diffs.

---

### Task 14: Cleanup, docs, verification

**Files:**
- Modify: `docs/operations/runtime-behavior.md`, `CHANGELOG.md`, `README.md` (TUI section)
- Delete: `Tui/GrokHeader.cs`, `Tui/GrokStatusBar.cs`, `AnsiTheme.cs`, the Spectre package reference
- Rename/retire: `GrokHeaderTests.cs`, `GrokStatusBarTests.cs`, `GrokFaithfulTests.cs`,
  `GrokRendererTests.cs` → aurora equivalents

- [ ] **Step 1:** Remove the dead files and the `Spectre.Console` `PackageReference`.
- [ ] **Step 2:** `dotnet build -c Release --nologo` → 0 warnings; grep proves no `Spectre.`
  usage remains in `RuAgent.Cli`.
- [ ] **Step 3:** `dotnet test backend-dotnet/RuAgent.Cli.Tests -c Release` → all green.
- [ ] **Step 4:** Update `CHANGELOG.md` (Fixed/Changed) and the README TUI feature list.
- [ ] **Step 5:** Commit.

---

## Self-review

- **Spec coverage:** §3.1→Task 2, §3.2/§3.3→Task 1, §3.4→Tasks 5/8, §4→Tasks 4/6,
  §4.1→Task 4, §4.2→Tasks 7/8, §5→Task 6, §6→Tasks 3/10/11/12/14, §7→Tasks 1–13.
- **Placeholders:** none; every task names its files, interfaces and verification command.
- **Type consistency:** `TuiSurface`/`TuiStatusLine`/`TuiTranscript`/`ComposerBox`/
  `SlashDropdown`/`TuiStickyRegion`/`PendingPromptSlot` signatures are repeated verbatim
  where a later task consumes them.

---

## Status (as built)

**Landed:** every task, plus the P4 information surfaces and a resize fix found on a live
terminal.

`TuiText`, `TuiPalette`, `TuiSurface`, `TuiStatusLine`, `TuiHeader`, `TuiTranscript`,
`TuiViewport`, `TuiResizeWatcher`, `TuiPanel`, `TuiSurfaces`, `TuiChoiceList`,
`ComposerBox`, `SlashDropdown`, `ApprovalPrompt`, `TypeAheadSession`, `PendingPromptSlot`,
`TuiStickyRegion`, the rewired `TuiEventRenderer`/`TuiSessionRenderer`, the `--classic`
switch, and the Grok chrome/`AnsiTheme`/Spectre retirement. `AnsiTheme` was replaced by
role-named `TuiPalette` helpers (`Ok`/`Err`/`Warn`/`Accent`/`Quiet`/`BoldPaint`/`DimPaint`)
and deleted together with the `Spectre.Console` package reference.

**P4 surfaces:** `/help` renders a framed panel generated from the reader's command table;
`/status`/`/stats`/`/tokens` share one session card; `/model` and `/backend` open a picker
(`ChoiceList`, the same selection behaviour as approvals) and fall back to a panel when there
is no interactive console.

**Resize fix:** the layout was shredded by dragging a window edge. Reproduced on a real
Windows Terminal by scripting 40 resize steps: the residue was a status row and a border
stranded mid-screen. `TuiViewport` no longer substitutes the 80×30 fallback on a live console
(that is what wrapped rows and misaligned every later cursor move), `TuiResizeWatcher`
debounces to one rebuild per settled size, `TuiStickyRegion` paints nothing while the layout is
stale, a settled resize rebuilds the entire screen (region reset, clear, fresh header,
transcript tail reprinted from `TuiSurface.SnapshotTail`) instead of patching the frame, and
`TuiSurface` clamps every write to the live width. `RU_TUI_DEBUG=<path>` traces the sizes the
app actually observes, which is how the remaining cases were found.

**Two behaviours were added on top of the original plan, both from wire facts found while
building:**

- A settled tool with no captured activity now prints its bounded safe code instead of a bare
  `✓ tool`, so a dropped transient frame still leaves something auditable on screen.
- The verb comes from the wire `tool_display` (`View`, `Edit`, `Terminal`, …) rather than the
  tool id, because `read`+`op=read` projects to kind `analyze` — the TUI now matches the
  desktop surface instead of inventing its own vocabulary.

**Verification:** `dotnet build -c Release --nologo` → 0 warnings for `RuAgent.Cli` (and no
`Spectre.` usage anywhere); `RuAgent.Cli.Tests` → 491 passed, including the scripted full-turn
snapshot, the panel/picker surfaces, the resize watcher and the layout-staleness rule. The
non-interactive surfaces were exercised end-to-end through the published binary, and the
resize path was exercised on a real terminal with scripted window resizing before and after
the fix (screenshots compared). The pickers and the IME caret still deserve a human pass.

