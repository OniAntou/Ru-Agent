# TUI Sticky Footer + Type-Ahead — Design Spec

- Date: 2026-09-10
- Status: approved in chat (approaches + 3 design sections)
- Scope: `backend-dotnet/RuAgent.Cli` interactive TUI only. `--json` mode untouched.

## 1. Goal

Keep the prompt composer visible and usable at all times, chat-app style:

- During agent streaming, output scrolls in the upper region while the
  composer stays pinned at the viewport bottom.
- The user can type ahead while the agent is busy; Enter queues ONE
  pending prompt, sent automatically when the run finishes.
- No more "two identical boxes" confusion; no more "input bar disappears".

## 2. Locked decisions (from brainstorming Q&A)

| # | Question | Decision |
|---|----------|----------|
| Q1 | Footer content while agent runs | C: live composer (type-ahead, Enter queues) |
| Q2 | Queue depth | Single slot, NO replacement; extra Enter while occupied is ignored |
| Q3 | Ctrl+C while busy | A: cancel run (current behavior); queued prompt is kept and sent next |
| Q4 | Approval mid-stream | A: lock composer keys, existing inline 1/2 prompt, buffer preserved |
| Q5 | Unsupported terminals | C: sticky by default, `--classic` flag to revert, auto-degrade on redirect |
| Q6 | Enter on full slot | B: transient hint (`Đã có 1 prompt đang chờ…`, ~2s), typing continues |
| Q7 | Polish scope | B: bundle 3 small items (usage_recorded filter, dim echo, compact header) |

## 3. Approach: terminal scroll region (DECSTBM)

`ESC [ top ; bottom r` reserves footer rows; all existing streaming
`WriteLine` output then scrolls only above the footer. Rejected:

- Manual footer repaint per chunk: flicker + CPU, wider blast radius.
- Alternate-screen fullscreen app: biggest rewrite, loses native scrollback.

## 4. Layout & scroll-region lifecycle (Section 1, approved)

- Turn start (after submit, before streaming): draw 3-line footer box at the
  current cursor position; set scroll region bottom to the row above the
  footer top. All agent output scrolls above; footer stays pinned.
- Turn end (completed / failed / cancelled): reset region (`ESC [ r`); the
  live footer composer simply continues as the active composer in place
  (no redraw, no flicker).
- Dropdown popup is SUPPRESSED while busy (it opens upward into the scroll
  region and would drift); slash text, ghost text and Tab still work and a
  slash line can be queued like any prompt.
- Footer height is fixed at 3 rows while busy.
- Resize while sticky: reset region → recompute rows → repaint footer →
  set region again (extends the existing `Refresh` path).

## 5. Concurrency & input protocol (Section 2, approved)

- The key loop runs as a background Task during streaming. ALL console
  writes (spinner ~90ms loop, key loop, transient hint) go through ONE
  shared write gate (`lock`) — today the code is single-threaded and has none.
- Queue (1 slot): Enter while busy with empty slot stores the prompt and
  immediately prints a dimmed `❯ ...` echo under the output. Enter with a
  full slot shows the transient hint; further typing still goes to the buffer.
- Ctrl+C: cancels the run via the existing token; queued prompt (if any) is
  sent next; the half-typed buffer is preserved.
- Approval (`approval.requested`): lock composer keys; drain already-pressed
  keys into the buffer first (no lost chars); show the existing inline
  `1/2` prompt via `_in.ReadLineAsync`; on answer, unlock and resume.
- Multiline continuation (trailing `\`) works in the live composer; the
  whole block is queued on Enter.

## 6. Fallback, compat & polish (Section 3, approved)

- Classic sequential path is PRESERVED behind `--classic` (new parser flag)
  and auto-selected when output is redirected or `TERM=dumb`. No TUI changes
  apply in `--json` mode.
- Polish bundle:
  1. Filter `model.usage_recorded` from chat output (internal event leak in
     `RenderEnvelope` default case; extend the existing filter list).
  2. Submitted echo `❯ ...` rendered dim to distinguish from the open box.
  3. `GrokHeader`: remove one blank separator line to compact the card.
- Testing: xunit for pure parts (region-row math, queue slot transitions,
  hint timing), full `Cli.Tests` green, 0-warning build. Manual sticky /
  flicker validation on Windows Terminal together with the user (agent has
  no live screen).
- Open verification for plan phase: confirm `Program.cs` Ctrl+C wiring
  (`CancelKeyPress` → token) so cancel semantics stay identical.

## 7. Components (expected)

- NEW: scroll-region/footer manager (set/reset margins, footer geometry,
  fixed 3-row height) — pure math unit-testable, thin console-write shell.
- MODIFY: `InteractiveLineReader` (concurrent key-loop mode, key lock for
  approval, slot-full hint, dropdown suppression flag while busy).
- MODIFY: `AgentCommands` chat loop (pending-slot state, start/stop footer
  around streaming, lock around approval prompt, queue send-next, Ctrl+C
  keeps queue).
- MODIFY: `TuiSessionRenderer` minimally (route writes through shared gate).
- MODIFY: `AgentCliParser` (`--classic` flag).
- NO CHANGE: streaming event rendering logic, JSON/NDJSON paths, prompt
  receipt/usage pipelines.

## 8. Out of scope

- Fullscreen alternate-buffer UI; mouse support; transcript search/scroll;
  multi-prompt queue; editing the queued prompt; server-side changes.
