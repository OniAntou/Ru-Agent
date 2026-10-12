# GUI and TUI review — 2026-10-12

Reviewed `dev` at `650e128c` including the uncommitted TUI changes. The initial
review below was read-only; the follow-up describes subsequent fixes. Areas covered
include session ownership, streaming, activity/output projection, transcript
retention and scrolling, composer layout, model selection, and terminal lifecycle.

## Findings

These findings describe the initial snapshot, before the follow-up fixes below.

1. **P2 — streamed Markdown depends on delta boundaries.**
   `backend-dotnet/RuAgent.Cli/Tui/TuiTranscript.cs:418-457` renders unfinished
   fragments immediately, but passes only the fragment before the next newline
   to the block parser. The complete raw line is unavailable when deciding fences,
   headings, and lists. A real renderer probe of the same fenced C# reply produced
   a numbered code line with one chunk, but a literal fence fragment and a code
   gutter on the following prose with six chunks. Resize preserves the incorrect
   rows. The completion recovery compares raw text, so it does not repair this
   when all raw characters arrived. Keep raw lines and parse stable completed
   lines; render the current tail as a replaceable projection. Test identical
   Markdown across chunk partitions, including fence delimiters and newline splits.

2. **P2 — old shell callbacks can stop the new shell.**
   `gui-winui3/Controls/InteractiveTerminalControl.xaml.cs:160-171` enqueues output
   and exit callbacks without capturing the originating session. If shell A exits
   and its callback is queued while a directory switch stops A and starts B, A's
   callback calls `StopSession()` on the current B. This disposes B and its process
   job; old output can also appear in B's terminal. Unsubscribing events does not
   remove already queued work. Bind callbacks to their session/generation and
   discard stale callbacks; invalidate pending startup on shutdown. Source-confirmed
   race; native WinUI scheduling was not driven in this review.

3. **P2 — Tasks responses can overwrite another session's panel.**
   `gui-winui3/Views/ChatView.Tasks.cs:76-89` applies an asynchronous `/tasks`
   response, failure, or exception without rechecking the session or run. Switching
   from A to B while A's refresh is pending allows A's rows or error to replace B's
   Tasks tab. Concurrent refreshes for the same run can likewise publish older
   results last. Stop then uses the current run with a stale execution ID. Bind
   refreshes to session generation, run ID, and request version; guard every UI
   completion and cancel superseded refreshes. Source-confirmed race; the
   controller's command dispatcher forwards the request without UI ownership checks.

4. **P2 — long fenced code loses its tail from the visible transcript.**
   `backend-dotnet/RuAgent.Cli/Tui/TuiCodeHighlight.cs:32-34` truncates code to the
   current width before it enters transcript history. The probe at width 40 lost
   `TAIL_SENTINEL`; resizing to 200 did not recover it and retained expansion was
   empty. This is distinct from the documented history caps and affects short
   responses with long source lines. Durable/raw text is not lost, but the TUI
   presentation cannot expose the complete line. Retain complete code text and
   wrap or provide horizontal navigation at display time. Verify resize and
   expansion preserve the tail.

5. **P2 — rapid model selections can settle out of order.**
   `gui-winui3/Views/ChatView.Models.cs:288-300` unconditionally installs a model
   after its settings POST finishes. The selection handler starts saves without
   awaiting them, the picker remains available, and `ApiService.Settings.cs`
   contains no ordering gate. Choose A, then B while A is pending: a late A
   completion replaces B's label and local selection. The settings requests can
   also reach the server out of order. Serialize/coalesce selection persistence
   so the last user choice wins in both settings and UI; a UI-only generation
   guard is insufficient for server ordering. Test delayed and reversed responses.
   Source-confirmed race; no real provider was called.

## Verification and limits

- `scripts/test_dotnet.ps1 -Configuration Debug -ProjectFilter RuAgent.Cli.Tests`:
  **1,284 passed, 0 failed, 0 skipped**.
- `scripts/test_dotnet.ps1 -Configuration Debug -ProjectFilter gui-winui3.Tests`:
  **1,385 passed, 0 failed, 0 skipped**. Suites ran sequentially.
- `dotnet run --project artifacts/gui-tui-review-probe/probe.csproj`: real
  `TuiTranscript`/`TuiSurface` rendering probe, exit 0. Reproduction output in
  `artifacts/gui-tui-review-probe/results.json`. Test logs are
  `artifacts/gui-tui-review-cli.log` and `artifacts/gui-tui-review-gui.log`.
- Existing tests pass but do not cover the reproductions and ownership races above.
  No full matrix, real-model run, native GUI automation, or physical-terminal
  rendering acceptance was performed. Production files were not changed.

Reviewed CLI README, architecture, and verification guidance. No architecture
contract changed and no changelog behavior entry is needed for this review.
Historical verification sections remain explicitly historical.

## Follow-up fixes

All five findings were addressed in the working tree:

- Markdown retains the complete raw line, advances block state only at commit,
  and uses replaceable Terminal.Gui tail previews. Tests compare every two-chunk
  partition plus character-by-character delivery of code, emphasis, headings,
  lists, tool markers and horizontal rules. Headless bridge tests verify live
  preview, final rows, long code and retry removal.
- Terminal callback lifetime versions invalidate queued output/exit work after
  shell replacement or shutdown. Shutdown also prevents pending WebView startup
  from creating a shell. The shared callback guard is covered behaviorally;
  native WebView/ConPTY interaction remains unrun.
- Tasks completions check session generation, controller, run ID and latest
  request before applying successful responses, errors or exceptions. Superseded
  refreshes are cancelled, and outstanding refreshes are cancelled on disposal.
  Actual WinUI session switching remains a manual acceptance check.
- Code highlighting retains the full source line before screen wrapping. The
  original probe now recovers `TAIL_SENTINEL` after resizing from 40 to 200.
- Model settings saves are serialized; superseded queued choices are skipped and
  only the newest completion publishes UI state. Deferred-save tests cover three
  choices, stale failure, latest failure, recovery and shutdown invalidation.

The initial CLI test run after changing Markdown commitment failed three older
expectations: immediate plain-output fragments, keeping retry's provisional text,
and displaying raw emphasis delimiters across chunks. Those expectations were
updated to the intended complete-line/replaceable-preview behavior; bridge tests
retain the requirement that interactive streaming appears before line completion.
The initial run was **1,286 passed / 3 failed**, logged in
`artifacts/gui-tui-fix-cli.log`. Subsequent results are recorded in verification.
After preview throttling, one bridge recovery test failed because it flushed the
downstream writer instead of the raw-line renderer. It now calls the same
`TuiTranscript.Flush()` boundary as production suffix recovery, retaining its
full-text assertion. That run was **1,291 passed / 1 failed** in
`artifacts/gui-tui-fix-cli-verified.log`; the final CLI rerun passed **1,292/1,292**
in `artifacts/gui-tui-fix-cli-verified2.log`.
The subsequent GUI rerun passed **1,389/1,389** in
`artifacts/gui-tui-fix-gui-verified.log`. Both suites built their affected projects.
No full matrix, release publish or native desktop interaction was performed.

The full rendering pipeline probe (4,000 fragments with 1,000 history rows,
including final reflow) measured **7.8 ms / 1,427,032 allocated bytes** after
preview coalescing. This is synthetic CPU evidence, not terminal FPS; see
`artifacts/gui-tui-stream-perf/result.txt`. Updated reproduction output is
`artifacts/gui-tui-review-probe/fixed-results.json`.

## Release publication

On the subsequent release request, the full Debug matrix passed **5,987 tests
across 12 projects**, with no failures or skips. Python passed **214 tests**,
with one POSIX process-group test skipped on Windows. `build.bat` completed all
11 stages and refreshed the self-contained flat GUI/backend/CLI package.

The flat package contains every file from the CLI and backend publish trees,
the required compiled XAML, bundled terminal assets, and Windows App Runtime.
A published-backend smoke using isolated data and a synthetic token passed
authenticated `/status`, actual `bin/ru.cmd` session listing and default startup
with redirected `/exit`, then authenticated host shutdown. This establishes
packaging and launcher startup, not physical TUI rendering or WinUI interaction.

Logs: `artifacts/gui-tui-release-matrix.log`, `artifacts/gui-tui-release-python.log`,
`artifacts/gui-tui-release-build.log`, `artifacts/gui-tui-release-inventory.log`,
`artifacts/gui-tui-release-smoke.log`. No user application was launched or stopped.
