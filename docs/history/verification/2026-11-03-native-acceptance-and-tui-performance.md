# Native acceptance, TUI latency and real-provider tasks — 2026-11-03

Snapshot: `dev`, HEAD `922335d4`, with the existing pending changes. The user
requested native GUI/TUI verification, three long real-provider tasks and TUI
smoothness improvements. They selected Ru's current provider/model and a maximum
of 1,200 seconds per task. This record separates native presentation, controlled
performance measurements, deterministic regression tests and live task quality.

## Native GUI checks

The actual Release WinUI executable was launched with a fresh Host data/log root,
seeded only with credential-free current settings and the Models.dev cache. No
user conversation database was copied. Windows UIAutomation, native mouse input
and `PrintWindow` captured only the owned app. The helper checks window/process
ownership before input. Existing device-local UI preferences were backed up for
restoration after the owned GUI closes.

- First Customize activation without a workspace showed user skills. A native
  screenshot confirms populated content without closing/reopening the page.
- Clicking the model picker showed provider/model rows on the native surface.
  UIAutomation Invoke does not open the attached flyout in this harness; native
  mouse click does. Its content is not in the ordinary window-descendant dump,
  so that dump alone cannot establish an empty picker.
- Ten Customize/New Chat cycles passed 20 data/selection checks. Unicode,
  Vietnamese text, emoji, multiline input and clearing the prior draft on New
  Chat passed the additional case. The initial fixture compared raw LF with the
  native TextBox's CR line endings; only newline normalization was corrected.
- At 1200×800 and 900×650, the composer stayed visible. Captured native images
  were inspected; no overlapping controls were observed in these empty-chat
  states. This does not establish streaming or all-content resize acceptance.
- Opening the live TypeScript run reproduced an actual presentation mismatch:
  the header and Stop button were active, while the compact activity line used
  its settled past-tense summary. Replay left the matched assistant row with
  `IsStreaming=false`, and adoption did not seed the text accumulator. The fix
  reattaches that row by `SourceRunId`, marks it live and seeds its durable text
  before accepting further deltas. Three regression cases cover row identity,
  activity updates and prefix/delta/reset behavior.
- On the unmodified TypeScript adoption path, genuine terminal completion did
  remove the running header and restore **Send message**. The reproduced defect
  was the prematurely settled transcript.

## TUI measurements and change

The standalone probe uses production `TgChatWindow`, `TgChatLoop` and the session
host with real ConPTY bytes. It pins and validates the logical driver viewport,
records input/paint/frame timestamps, and requires the expected text and a
completed editor paint before acknowledging each key. It checks menu geometry,
footer visibility, empty final input and zero prompt submissions. Each case
types four `/model`/Backspace cycles plus ordinary text: 58 keys. A 60-iteration
override isolates cadence from the default 25-iteration baseline. These are
instrumented driver-frame measurements, not physical display latency.

| Hosted 180×48 case | Before, 25 iterations: median / P95 | Repaint fix, 25 iterations | Repaint fix, 60 iterations |
| --- | --- | --- | --- |
| Welcome | 82.381 / 98.245 ms | 77.702 / 105.542 ms | 51.882 / 72.380 ms |
| Existing chat | 85.249 / 118.955 ms | 77.857 / 96.687 ms | 48.548 / 91.248 ms |

Managed allocations over the same instrumented input interval fell from 1,285
MiB to 306 MiB for chat. Welcome fell from 2,536 MiB to 1,171 MiB when combining
the repaint fix with 60 iterations. These totals include the framework/driver
and probe; they are allocations, not retained memory or a memory-leak measure.
At 100×30, the 60-iteration fixed welcome case had median/P95 47.236/68.740 ms.
Short idle CPU samples varied and did not demonstrate an idle-CPU improvement;
raising the dispatch rate can increase idle polling cost.

The implementation makes closed-menu hiding a no-op, retains geometry delegates,
preserves identical match selection and confines stable-height updates to the
dropdown. Parent redraws still erase old cells on open/close/height changes.
Native startup now sets 60 iterations; the welcome clock remains 50 ms with its
existing overlay/hidden pauses. No theme, graphics or execution-policy change is
part of this optimization.

New tests first reproduced redundant landing paints and selection reset. Fixture
corrections include the current five-row card chrome and using the ANSI driver's
observed dimensions: forcing a competing size caused periodic native resize
redraws unrelated to input. Resize restarts the fixture scenario; the no-repaint
assertions remain strict. Both new tests passed after the product fix. Earlier
probe compile/IPC/viewport failures are preserved, not counted as product bugs.

The full CLI project passed 2,068 tests. Native ConPTY input passed all six cases
(control, BMP, emoji, fragmented input, navigation and typing). Release packaging
passed all eleven build stages and authenticated published-Host smoke checks:
53 user skills, native session listing and retired interop routes returning 405.
The first GUI Escape suite run passed 2,096 tests and failed one new source guard:
it searched beyond the Escape switch into Enter handling. Narrowing that check
to its own `default`/`break` branch produced 2,097 passing tests. Adoption then
passed 2,100 tests. The final suite passed 2,105 tests after the popup routing
and hover fixes. Two pre-existing xUnit analyzer warnings remain in unrelated
tests. The final Release build and authenticated package smoke passed.
After the final history reload change, the first suite run failed one
controller ownership assertion. The unchanged binaries passed all 2,105 tests
on rerun. Both logs are retained.

## Real-provider task protocol

The current selection was read from the existing settings table in read-only
mode: `models.dev:kilo`, `inclusionai/ling-3.1-flash-1019-free`. Credentials stay
in Windows Credential Manager and are not copied to fixtures. The native GUI
also displayed this selection and its stored high reasoning setting.

Three private copies of the SWE fixtures use 1,200-second deadlines:
`cs-lease-renewal`, `py-migration-rollback` and `ts-session-replay`. Only their
deadline metadata changed. Offline oracle preflight passed: starters fail the
required groups and reference implementations pass. Each live attempt will use
a fresh candidate workspace and the ordinary Standard/trust/RPC path. Approval
or user waits remain intervention outcomes. Actual provider/model/protocol,
completion, external grader result, available usage and evidence completeness
will be recorded separately. These compact repositories do not establish
large-repository quality; compaction stress is established only if observed.

The first C# attempt ran 659.8 seconds, then was cancelled by the native GUI
observer pressing Escape to close the context popup. This uncovered a GUI
keyboard-routing bug; the interrupted candidate is not an unattended model
outcome. Its grader passed six of seven groups, which is intermediate evidence
only. The last durable tool receipts were complete, followed by model step 155
starting and no matching completion before cancellation. Completed step 147
still requested two file writes; all fifteen completed replies had tool calls.
There was no accepted final model reply or completed run before Escape.

This explains a completed-looking screen without active terminal processes:
the Host was awaiting another model response, while live file/task panels and
earlier completed tool rows remained visible. A file-change card is not the
terminal run summary. The generic **Working** label does not explain that wait.

The RPC benchmark's usage counters were null, so durable completed-step payloads
were audited separately through the authenticated API of the isolated Host.
Fifteen usage records reported 661,294 input and 34,929 output tokens (696,223
total; 586,752 cached input). These sum repeated request usage, not context-window
occupancy. They omit any unreported in-flight request; billing cost is unknown.
No compaction or approval was observed. A model id ending in `free` is not
billing evidence.

Python completed in 546.152 seconds with 28 model steps and 30 tool receipts,
without approval or compaction. The original external grader reported 5/6:
the future-version case catches only `ValueError`, while the issue prescribes
no exception class and the candidate raises a descriptive `MigrationError`
before any writes. A separate source review confirmed this grader constraint
is stricter than the published contract. The original 5/6 result is retained.
The corrected grader accepts any descriptive ordinary exception, still asserts
the exact unchanged schema/version snapshot, and still rejects silent success.
Seven regression variants first reproduced two false failures, then passed;
mutation, empty-error and silent-success cases remain rejected. A private copy
with only this grader correction passed all six groups for the exact original
candidate hash, with no new model call or source repair. Its starter/reference
oracle passed too. The C#
expiry failure is different: resetting reacquisition version to 1 permits stale
version reuse and fails the versioned-ownership contract.

The Python model also attributed a command failure to Host executable
resolution. Durable arguments and receipts contradict that claim: it requested
`executable: python` with `arguments: [python, check_behavior.py]`, so Python
correctly tried to open a file named `python`. The smoke command repeated the
same extra argument. These are model argument mistakes, not a reproduced Broker
launch defect. Later equivalent PowerShell checks did not retire those exact
failed check identities; the model received bounded verification recovery notes.
Reported success, run completion and verified completion remain distinct.

Python's SSE usage record reports the final request (127,374 input / 499 output),
not the sum of all requests. The authenticated durable audit sees 24 usage records
totalling 1,475,011 input / 13,813 output, including the final request; four
recovery-only completed steps are intentionally inaccessible to clients and are
excluded. These are observed lower bounds for usage, not complete billing.
TypeScript completed in 460.799 seconds with 16 model steps and 22 tool receipts,
without approval or compaction. The independent grader passed 6/7 groups; a
delta missing its required text was accepted rather than rejected without
mutation. The model's final claim that every requirement was exercised therefore
does not establish correctness. Its final request usage was 72,974 input / 1,524
output. Thirteen client-readable usage records total 520,623 input / 17,650
output; three recovery-only steps are excluded. Cost remains unknown.

The interrupted C# retry used the remaining 540 seconds and timed out at
540.027 seconds. Together with the interrupted attempt, adapter elapsed time
was 1,199.827 seconds. The fresh retry left `scratch-verify/Program.cs` outside
the editable source boundary, so integrity admission rejected it before grading;
there is no valid group score for that retry. The report's last sampled status
is running, but the durable database confirms cancellation after timeout. All
four live attempt records are terminal: two completed and two cancelled. This
retry is not matched to the others' full uninterrupted 1,200-second budget.
Fifteen accessible usage records report at least 660,955 input / 20,009 output;
one recovery-only completed step and the in-flight request are excluded.

| Task | Run outcome | Independent correctness |
| --- | --- | --- |
| Python migration rollback | Completed, 546.152 s | Original 5/6; 6/6 with the documented grader correction and identical candidate |
| TypeScript session replay | Completed, 460.799 s | 6/7; malformed delta without text accepted |
| C# lease renewal | First interrupted by GUI observer; constrained retry timed out | First interrupted candidate 6/7 only; retry not admitted because of scratch source outside the boundary |

No aggregate pass rate is claimed for this interrupted, adjudicated small sample.
Durable event intervals put Python's completed model steps at 535.175 s and tool
execution at 3.057 s of a 544.116-second Host run. TypeScript was 453.462 s for
model steps and 1.460 s for tool execution of 458.345 s. C#'s longest completed
model step was 196.831 s. These model-step spans include prompt preparation,
transport and generation; they are not isolated provider/network latency. They
explain long periods with no active terminal command independently of UI repaint.

A scripted model with a bounded local terminal wait was used to verify the
Escape guard without spending another provider request. The first guard kept
the run active when Escape was pressed with the context popup open, but left
the popup visible. Clicking the popup closed and then pressing Escape in the
composer cancelled only this dummy run. The refinement explicitly hides the
three chat-owned flyouts before consuming Escape. Native retries found that
popup focus takes a separate route. A handler on the popup content root received
Escape and hid the flyout, but hover immediately reopened it while the pointer
remained on the meter. Suppression now lasts until the pointer leaves the meter.
One test fixture also
used a title longer than the sidebar's title bound and was corrected to a short,
unique label; that failure is preserved. Scripted acceptance is separate from
real-provider quality evidence.

The final native run kept its live activity summary after the first Escape,
completed normally, showed the final reply, and restored Send. An earlier
attempt restored Send but kept the replay seed even though the final reply
was persisted. History reload now uses the persisted reply for the same
terminal run when no live text arrived. A separate round confirmed that
Escape from the composer after popup dismissal cancelled the run. The ten
backed-up device-local preference files were restored to their original state.

The actual published CLI reopened the completed Python session in a 100×30
ConPTY, typed unsent slash filters and Vietnamese/emoji text, opened/cancelled
the local model picker and exited with code 0. Nine cumulative VT stages were
recorded without truncation and without a model prompt. The first observer
attempt inherited Codex's `TERM=dumb`/`NO_COLOR=1` and timed out looking for the
native composer because those flags intentionally select classic mode. The
successful run explicitly supplied a VT-capable terminal environment. Offline
replay in xterm verified the Vietnamese text and emoji in the logical cells
and confirmed Escape preserved the selected model.

The native inline terminal check failed independently of run settlement. The
expanded card for the second command showed “No streamed output captured.”
The in-process tool gateway accepts a live output callback but never calls it.
The Broker captures output in its spool and background task registry, and the
gateway returns a durable receipt. Forwarding live output is a separate issue.

## Artifacts and limits

Artifacts and scripts are under
`C:\Users\USER\.codex\visualizations\2026\10\31\01a1916b-98e4-7d21-be03-7426d5468f75\native-acceptance-perf`.
GUI screenshots/dumps, navigation results, probe source and raw measurements,
oracle results and eventual live trajectories are retained there. Focused .NET
logs named `native-tui-redraw-*` are in its parent evidence directory.

Physical keyboard/IME composition and Windows Terminal compositor/font latency
are not proved by Unicode injection or these controlled ConPTY frame measures.
