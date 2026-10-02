---
status: pending
---

# TUI Redesign — Full-screen Engine Design

Date: 2026-10-02
Status: approved (all 4 sections confirmed by owner)
Scope: `backend-dotnet/RuAgent.Cli/Tui/*`, `AgentCommands.Chat.cs`, `AgentSlashCommands.cs`. No new external dependencies. No commit for this spec (owner request).

## 1. Context

Current TUI (`agent chat`) is a sequential log plus a pinned sticky footer (`TuiSurface` single gate + cursor tracking + `RepaintSingleLine`, history 2000 lines, resize rebuild). Palette has 9 tokens with prose left at terminal default. Markdown deliberately skips tables/nested lists. Code blocks use only a `│` gutter with no syntax highlight (GUI-only `CodeHighlighter`). Tool rows render as sentences (`reading app.cs` → `✓ read app.cs`), diffs capped at 12 lines. Owner decision: redesign lớn, visual là chính, full màn hình, direction B (full-screen engine).

References: `backend-dotnet/RuAgent.Cli/Tui/TuiSurface.cs`, `TuiPalette.cs`, `TuiStatusLine.cs`, `TuiMarkdown.cs`, `TuiTranscript.cs` (1192 lines), `docs/operations/tui-known-limitations.md`.

## 2. Architecture

- Keep `TuiSurface` as the single write path. Add AltBuffer (`CSI 1049h` / `1049l`) for interactive runs only.
- Auto-fallback to the current sequential layout when: stdout redirected, `--json`, `TERM=dumb`, `NO_COLOR` with level None where repaints are suppressed, terminal smaller than 80x24, or `--classic`.
- Layout, when AltBuffer is active:
  - Header (2 rows): brand + session id / preset / backend+model / workspace leaf.
  - Transcript (scroll region in the middle, rebuilt from bounded history).
  - Footer (3 rows): status line (spinner + activity + elapsed + turn/token/tool counters), live tool-card row, composer row.
- Keep synchronized output (`CSI ?2026h/l`) for mutable repaints. Keep 180ms resize debounce and full rebuild from history. Keep `RU_TUI_DEBUG` layout trace.

## 3. Components

- Tool card: rounded border panel. Header line shows past-tense verb + target path + elapsed. Body shows diff hunks in green/red instead of the 12-line cap. Collapsed state keeps one summary row expandable with `Ctrl+O`.
- Code block: keyword/string/comment highlight plus line numbers, keeping the existing gutter style. No new rendering dependency; ANSI only.
- Markdown: keep bold/italic/inline-code/link behavior and delta-holdback rule. Add simple tables and 1-level nested lists. Anything deeper falls back to plain indented text.
- Approval / picker / key panels: centered floating panel with `1-9` quick-pick, `↑↓` + `Enter` confirm, `Esc` cancel. Secrets never echoed, never logged, never stored in history.
- Theme: extend the 9-token palette to an aurora theme with panel background, border, and accent roles. Truecolor → 256 → 16 → none degradation stays unchanged.

## 4. Data flow and error handling

- Durable event stream stays the truth. Transient frames only paint live detail. Keep the existing tail-reconcile against the durable `assistanttext` payload and the `assistant_text_truncated N/M` notice for unrepairable head loss.
- Paint through the `TuiSurface` queue. Spinner at 10fps. Tool-card updates repaint in place; no new transcript line per tick.
- Failures render through the existing `TuiFailureCatalog` / `AgentFailureCopy` 3-line card (title + code, cause, fix). `payload_ref` stays opaque; fetch only via explicit `agent output`.
- Trust challenge stays inline with default cancel. Approval expiry stays 30 minutes (`approval_expired`). Cancel stays best-effort `POST cancel` with the manual `ru agent cancel <run-id>` fallback. Parked runs (`waiting_user`, `reconciliation_required`) surface `/continue` and are never auto-cancelled.

## 5. Testing and rollout

- Snapshot tests on `RepaintPreview` at widths 80/120/160 for header, tool card, code block, table, approval panel.
- Redirect test: no ANSI escapes in output, replay path unchanged (newest 20 text messages, 2000 chars each, no tool/thinking rows).
- Resize test: rebuild keeps the assistant tail; missing head reports truncation instead of interleaving.
- Rollout behind existing `--classic` flag as the AltBuffer kill-switch. No new flags. No new packages. One matrix at a time per repo test rules.
