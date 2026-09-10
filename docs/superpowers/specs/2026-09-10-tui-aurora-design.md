# TUI Aurora — Design Spec

- Date: 2026-09-10
- Status: approved in chat (visual direction, copy language, scope P0–P3)
- Scope: `backend-dotnet/RuAgent.Cli` interactive TUI only. `--json` mode untouched.
- Supersedes nothing: the sticky-footer spec (`2026-09-10-tui-sticky-footer-design.md`) stays
  authoritative for scroll-region/queue semantics and is executed here as phase P3.

## 1. Goal

Make the interactive TUI look intentional and behave completely without giving up the
native scrollback: one visual system shared with the GUI, one writer per surface, no
duplicated information, and no dependence on the terminal's colour depth.

## 2. Locked decisions

| Decision | Choice |
|---|---|
| Visual direction | Aurora: GUI cyan `#38BDF8` → violet `#818CF8`, with GUI dark semantics |
| Copy language | English only (hints, errors, help, slash descriptions) |
| Scope now | P0 primitives, P1 transcript/header/status, P2 composer, P3 sticky footer + queue |
| Terminal paradigm | In-line, scrollback preserved (no alternate screen) |
| Colour depth | truecolor → 256 → 16 → none, `NO_COLOR` honoured |

## 3. Design system

### 3.1 Tokens

| Token | Truecolor hex | 256 | 16 | Role |
|---|---|---|---|---|
| `brand` | `#38BDF8` | 39 | 96 | identity mark, active composer border, hunk header, streaming |
| `brand2` | `#818CF8` | 105 | 94 | tool verbs, section labels |
| `ink` | terminal default | — | — | assistant prose, user echo |
| `meta` | `#94A3B8` | 246 | 90 | paths, durations, counts, hints |
| `line` | `#334155` | 238 | 90 | idle borders, rules |
| `ok` | `#34D399` | 79 | 92 | success glyph, diff addition |
| `warn` | `#FBBF24` | 221 | 93 | approval, degrade notices |
| `err` | `#F87171` | 210 | 91 | failure glyph, diff deletion |

Level detection, in order: `RU_TUI_COLOR` override (`truecolor|256|16|none`) →
`NO_COLOR` set → output redirected → `TERM=dumb` → `none`; `COLORTERM` or `WT_SESSION`
or `TERM` containing `direct` → `truecolor`; otherwise `256`.

### 3.2 Glyphs (BMP only, width 1, no emoji)

`✦` brand · `◆` turn · `✓` ok · `✗` err · `▲` warn · `▸` running · `❯` prompt ·
`⠋⠙⠹⠸⠼⠴⠦⠧⠇⠏` spinner · `│` gutter · `·` separator · `╭ ╮ ╰ ╯ ─` frames.
`⚠` (U+26A0, East-Asian ambiguous) is retired: it breaks alignment in wide-ambiguous modes.

### 3.3 Width rules

Every structural line is measured with an ANSI-aware east-asian-width table
(`TuiText.VisibleWidth`). Zero-width (combining marks, ZWJ), wide (CJK, emoji) and
ambiguous (arrows, box drawing in CJK-ambiguous mode) are classified. Any line handed to
the terminal is truncated to the viewport with a single-cell `…`. Vietnamese diacritics
are width 1 and must fit exactly.

### 3.4 Copy rules

English, sentence case, no exclamation marks. Errors state what broke and the next action.
Raw `error_code` values stay verbatim (they are searchable in docs/logs).

## 4. Layout grammar

```
  ◆ turn 3 · 12:04                         turn header (meta, rule to the right)
  │ assistant prose, verbatim, default ink
  ✓ read    Tui/TuiEventRenderer.cs:386-488                            12ms
  ✓ edit    AgentCommands.cs                                      +12 -4
  │    @@ -1313,6 +1321,10 @@
  │    + catch (AgentApiException ex) when (…)
  │    · 38 more lines · ctrl+o
  ✓ bash    dotnet test Cli.Tests · exit 0                      203 passed
  · context compacted · 12 items · 1.4s
  ✗ turn 3 · 6.1s · provider_rate_limited
  │    provider returned HTTP 429 · retry in ~10s or switch model with /model
```

- Tool results are **one line each**, emitted when the tool settles. Glyph column 2,
  display verb column 5, target, then metadata right-aligned when it fits.
- Diffs render under the tool line, bounded to 12 lines, then `· N more lines · ctrl+o`.
- Tool stdout is never dumped: `└ 42 lines · ctrl+o` (expand reprints the retained block).
- Notices (compaction, retry, frames dropped) are one muted line prefixed `·`.
- Turn footer merges the local stopwatch with `agent.summary`:
  `✓ turn 3 · 18.4s · 4 tools · 3 files changed · verified`.

### 4.1 Live row (single owner)

Exactly one mutable row exists above the composer. It shows the in-flight activity and the
counters, and it is the only component allowed to repaint in place:

```
  ⠹ Edit src/app.cs:12-40 · 1.1s              turn 3 · ⇣12.4k · 4 tools
  ✓ idle · turn 3 · 18.4s · 3 files changed                    ⇣12.4k · /help
```

Right-hand counters are dropped below 60 columns. Nothing else may write a mutable row:
this retires the spinner's hard-coded 40-space clear and the three-way write race.

### 4.2 Composer states

Frame colour encodes state; the top border carries the state label, the bottom border
carries the model legend on the left and the contextual hint on the right.

| State | Border | Top label | Bottom hint |
|---|---|---|---|
| idle | `line` | — | `type / for commands · tab complete` |
| slash | `brand` | — | `↑↓ select · tab apply · esc close` |
| busy | `brand` | `⠹ thinking · 2.1s` (or the tool) | `esc cancel · ctrl+o expand` |
| queued | `brand` | `⠹ thinking · 2.1s · 1 queued` | `esc cancel · enter replaces queue` |
| approval | `warn` | `▲ approval needed` | `↑↓ select · enter confirm` |
| error | `err` | `✗ last turn failed` | `enter to retry · /help` |

Submit echo becomes a single dim `❯ text` line instead of a three-row filled bar.

Approval choices are rendered as a list under the composer (same component as the slash
dropdown) and are selectable with `↑↓`/`1-3`/`enter`; `allow_session_directory` appears only
for `external_directory_required`.

## 5. Data mapping (authoritative)

The durable event stream carries **codes**, not descriptions: a tool request is
`read_only`/`side_effects_possible` and a completion carries the receipt code. The rich
detail (display name, file, line range, symbol, counts, duration) exists only in the
transient `agent.activity` frame (`AgentActivityModel`).

Therefore:

- **Outcome truth = durable event.** A tool line is written when the durable completion
  arrives, so a dropped transient frame can never lose the record.
- **Detail = transient activity.** A `Started` activity sets the live row; the matching
  `Completed`/`Failed` activity supplies the verb, target, counts and duration that the
  finalised line renders.
- **Degrade path.** If no activity detail was captured, the line renders the safe code:
  `✓ tool · <safe-code>`.

`agent.summary` (transient, already published by the host) feeds the turn footer's
files-changed and verification fields; the CLI does not parse it today.

## 6. Architecture

```
Tui/
  TuiPalette.cs     semantic tokens, colour-level detection, styled spans
  TuiText.cs        VisibleWidth / Truncate / Wrap / Pad / Clip (ANSI + east-asian aware)
  TuiSurface.cs     TuiWriteGate (one lock) + synchronized-output batches + live row + sticky region
  TuiTranscript.cs  event grammar -> finalised lines (turn, tool, diff, notice, footer)
  TuiStatusLine.cs  the live row: state, activity, elapsed, counters
  TuiHeader.cs      replaces GrokHeader (no Spectre)
  Components/
    ComposerBox.cs    three-row frame, state colours, legend, ghost text
    SlashDropdown.cs  suggestion list (also used for approvals and pickers)
```

- `InteractiveLineReader` shrinks to the input state machine (history, cursor, keys,
  continuation) and delegates all painting to `Components/`. Target: under 500 lines.
- `TuiEventRenderer`/`TuiSessionRenderer` become thin: parse frames, hand them to
  `TuiTranscript`, drop the ad-hoc spinner and the manual clear strings.
- **One writer per surface.** Transcript appends, live-row repaints, composer repaints and
  dropdown paints all pass through `TuiWriteGate`. Mutable repaints are wrapped in
  synchronized output (`CSI ?2026h` … `CSI ?2026l`); terminals that do not implement the
  mode ignore it, so the wrapper is emitted unconditionally.
- Sticky region (P3): reserve the bottom four rows (live row + composer) with DECSTBM
  `CSI 1;{rows-4} r` while a turn runs; transcript output scrolls above. Reset on turn end,
  resize, and exit. `--classic`, redirected output and `TERM=dumb` keep the current
  sequential behaviour. The region grows while an approval block is open.
- Spectre.Console was removed with the retired header/status helpers; the CLI renders ANSI
  directly, so the package reference is gone.

## 6b. Information surfaces (P4)

One frame serves every information surface, and one list serves every selection.

- `TuiPanel` renders a framed panel: title border, optional section headers, label/detail
  rows, right-aligned footer hint. Every line is exactly the requested width, and the detail
  column is dropped (not squeezed) when the label column leaves no room.
- `TuiSurfaces` builds the panels from data rather than hand-written lines:
  - `/help` is generated from `InteractiveLineReader.KnownCommands`, grouped into
    session/model/terminal. A command can therefore never be listed without its description,
    and a command added to the reader shows up in `/help` even before it is filed into a group.
  - `/status`, `/stats` and `/tokens` share one session card: session id, preset, backend,
    model, API-key state (only when known), turns, tools, tokens and a leaf-trimmed workspace.
  - `/model` and `/backend` without an argument list their candidates as a panel when there is
    no interactive console.
- `ChoiceList` is the single selection behaviour (`↑↓`, digits, `Enter`, `Esc`) and renders
  through `SlashDropdown`. `ApprovalPrompt` delegates to it, and `InteractiveLineReader`
  gained a picker mode (`ReadChoiceAsync`) that draws the list under the composer using the
  same rows the slash dropdown uses. `/model` and `/backend` pick through it between turns;
  `Esc` cancels silently, and a non-interactive console falls back to the panel.

## 6c. Resize hardening

Dragging a terminal edge used to shred the layout. Five separate causes were fixed:

1. **Never claim a size the terminal does not have.** `TuiViewport` no longer substitutes the
   80×30 fallback while a live console exists: the fallback is only for redirected output.
   `UsableWidth(columns)` leaves the one-cell margin when there is room and otherwise returns
   the real width, so a single write can never wrap — wrapping is what misplaces every
   absolute cursor move that follows.
2. **Debounce the rebuild.** `TuiResizeWatcher` reports a size only after it has held steady
   for 180 ms (the first observation applies immediately), so a drag produces one rebuild
   instead of dozens. Non-positive sizes are ignored.
3. **Stop painting a stale layout.** While the live height differs from the height the region
   was built for, `TuiStickyRegion.Paint` writes nothing. A window that grew keeps its content
   in place, so painting the old bottom rows strands a live row and a border in the middle of
   the transcript.
4. **Rebuild the whole screen once the window settles.** The terminal has already reflowed the
   rows this UI addresses absolutely, so patching the frame cannot be reliable. On a settled
   resize the CLI resets the scrolling region, clears the screen, draws a fresh header, reprints
   the tail of the transcript from a bounded line history (`TuiSurface.SnapshotTail`, 2000
   lines), and rebuilds the pinned footer. Scrollback above the viewport is untouched, and the
   visible transcript survives the resize.
5. **Clamp every write.** `TuiSurface.WriteBlock` and `RepaintSingleLine` bound each line to the
   live width, so a cached width can never outlive the window it was measured in.

`RU_TUI_DEBUG=<path>` turns on `TuiDebug`, which records the sizes the app actually observed and
every layout decision. It is allocation-free when unset and never throws.

## 7. Testing

- Pure builders are unit-tested (existing pattern): palette level mapping, width table
  (combining, CJK, emoji, Vietnamese), truncation, live-row composition, composer state
  colours, dropdown windowing, sticky region math, queue slot transitions.
- Golden lines for transcript events: turn header, tool settle (with and without activity
  detail), diff bounding, notice, failure footer with remedy.
- A full-turn snapshot test renders a scripted frame sequence and asserts the transcript.
- Manual validation on Windows Terminal (truecolor), conhost (256) and redirected output.

## 8. Out of scope

Mouse support, alternate-screen layouts, TUI-side clipboard, transcript search/back-scroll,
image rendering, and any change to `--json` output.

## 9. Implementation notes (deltas found while building)

- **Assistant prose carries no gutter.** The design sketch drew `│` on prose lines, but the
  terminal soft-wraps prose without a gutter, so only the first row would be aligned. Prose is
  printed with a two-space indent and the `✦` marker instead; structural blocks keep gutters.
- **Paths are basenames.** `AgentActivityWireValidator.ValidateFileName` rejects `/` and `\`,
  so activity frames only ever carry a leaf name (`app.cs`, never `src/app.cs`). Tool rows show
  the leaf plus line range, which matches what the GUI renders.
- **Diffs and output on the wire.** Durable `safe_summary` values are bounded safe codes
  (`read_only`, `side_effects_possible`, receipt codes) — the rich detail lives in transient
  activity frames, which can be dropped. The transcript therefore settles one row per tool from
  the durable stream and enriches it from the activity captured for that tool; a dropped frame
  degrades to a code-only row. Diff lines are still rendered when a summary contains them.
- **`ctrl+o` scope.** Expansion reprints the most recent bounded block retained in memory
  (200 lines). Fetching the full tool payload via `agent output` stays an explicit user action.
- **Verb vocabulary is the wire's, not the tool id's.** Activity frames carry a bounded
  `tool_display` (`View`, `Edit`, `Terminal`, `Search`, `Explore`, …) and `read`+`op=read`
  projects to kind `analyze`, so a settled row reads `✓ view app.cs` rather than `✓ read`.
  Matching the wire keeps the TUI and the desktop surface telling the same story.
- **A settled tool with no captured detail shows its safe code.** When a transient activity
  frame is dropped the durable event still carries a bounded code, so the row degrades to
  `✓ tool read_only` instead of an unexplained `✓ tool`.
- **Sticky footer state.** The pinned footer runs a real type-ahead loop while a turn streams:
  typed text is queued on Enter and sent as the next prompt, Escape clears the draft then
  cancels the turn, and Ctrl+O expands the last collapsed block. A draft that was never
  Entered is discarded when the turn ends, so nothing is sent by surprise. The hardware cursor
  is parked inside the composer at `ComposerBox.CursorColumn` so IME candidate windows open
  next to the text instead of wherever the transcript left the cursor; while an approval owns
  the keyboard the caret goes back to the transcript row.
- **Approvals own the keyboard.** While an approval is pending the footer grows a choice list
  (arrow keys, digits, Enter, Escape denies) and the queue is suspended; the answer is posted
  by the streaming callback, which waits on the choice instead of reading stdin. Cancelling
  the run releases that wait. The sequential layout keeps the numbered stdin prompt.
- **Spectre.Console** was removed once the retired Grok chrome stopped needing it; the CLI no
  longer references the package.

