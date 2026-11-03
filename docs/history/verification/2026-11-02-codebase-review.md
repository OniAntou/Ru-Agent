# Current-checkout code review — 2026-11-02

Snapshot: `dev`, HEAD `922335d4`, including the pending working-tree changes.
This is review evidence, not a clean bill of health or an implementation report.
No production code was changed during this review.

The review covered Broker file/terminal/network/MCP/skills authority, Host/Harness
admission, cancellation, deletion, persistence and SSE, provider adapters and
context accounting, GUI/TUI/client ownership, interchange, updates, SDK transport,
CI and current component documentation. It was a source and contract review;
native GUI interaction, installer mutation and real-provider task quality were
not exercised.

## Findings

### R1 — P1: line-mode file reads can escape their authorized root during a junction swap

[`FileReadAdapter.Lines.cs`](../../../backend-dotnet/RuAgent.Broker/Tools/Files/FileReadAdapter.Lines.cs),
lines 17–26, resolves and classifies a path, then opens it by pathname. The helper
checks hard-link count but never compares the opened handle's final path against
the approved canonical path. Ordinary byte reads already do that in
[`FileReadAdapter.cs`](../../../backend-dotnet/RuAgent.Broker/Tools/Files/FileReadAdapter.cs), line 141.

`read_file` dispatches through this line-mode adapter. A concurrent process can
replace an in-root ancestor with a junction after resolution, causing an external
file to be returned without an external-read grant. This affects the structured
read boundary in Plan/Standard independently of the documented terminal policy.

An isolated fixture alternated an in-root directory and a junction to another
synthetic directory. The adapter returned `OUTSIDE-SYNTHETIC` after 482 reads and
52 swaps. The external-target fixture was outside the adapter root; no user data
or protected file was used. Its expected boundary assertion failed.

Fix direction: validate the opened handle before reading, using the same final-path
guard as byte reads. Add a race regression through the line-mode entry point.

### R2 — P2: GUI run admission omits the displayed provider/model intent

[`ChatView.AgentRun.cs`](../../../gui-winui3/Views/ChatView.AgentRun.cs), lines 754–761,
constructs `CreateAgentRunRequest` without `ProviderSelection`.
[`AgentApiClient.cs`](../../../backend-dotnet/RuAgent.Client/AgentApiClient.cs), line 72,
therefore uses the legacy run route. With no persisted intent,
[`ProviderIntentSettings.cs`](../../../backend-dotnet/RuBackend/Providers/ProviderIntentSettings.cs),
line 10, leaves current global settings authoritative when the Host pins selection.

The GUI's settings-saved event is local to its API service; a CLI or second GUI
can change global defaults while the first GUI still displays its earlier model.
Submitting there can run model B while the picker still shows A. The TUI already
sends explicit intent. This finding was traced through the actual callers and
pinning path; the two-client native GUI sequence was not automated.

Fix direction: snapshot the displayed backend/model at submission, send it through
the selected-run contract, and keep that intent across retries and reconciliation.

### R3 — P2: a late settings read overwrites a newer saved model choice

[`ChatModelPickerViewModel.cs`](../../../gui-winui3/ViewModels/ChatModelPickerViewModel.cs),
lines 190–198, accepts a settings response if its backend object is still attached.
It has no read generation tied to newer selection changes. A settings read that
started before a model save can arrive after the save and reset the picker to the
old model, even though the stored selection is new.

An isolated test delayed the settings response, saved `new-model`, then returned
the old snapshot. The save remained `new-model` while `ModelLabel` became
`old-model`; the expected retained-selection assertion failed. This is separate
from R2: one guards reads versus saves, the other binds run admission to UI intent.

Fix direction: invalidate or version outstanding reads when selection changes;
only the owning read may adopt settings and update grouped selection.

### R4 — P2: OpenCode database import applies its size limit after materialization

`backend-dotnet/RuAgent.SessionInterchange/OpenCodeDatabase.cs` (removed after this review),
lines 64–85, reads every message/part into JsonNodes and serializes the complete
graph before enforcing the 16 MiB import limit. Individual database rows and the
growing aggregate have no earlier byte bound. A sufficiently large selected
session can exhaust the CLI process before it can return the named refusal.

A synthetic supported-schema database with a 20 MiB JSON part allocated
138,487,504 bytes (132.1 MiB on the calling thread) before throwing
`opencode_session_too_large`. The allocation probe used 64 MiB as a diagnostic
threshold, not a newly proposed public import limit. The source also confirms
the allocation scales with the full selected session rather than the accepted cap.

Fix direction: check row sizes and cumulative UTF-8 size before constructing the
graph, serialize through a bounded writer, and add cancellation and row/count
bounds when reading a large database session.

## Verification and limits

- Fresh review checks: Python `pytest -q`: 268 passed, 1 skipped; Ruff passed;
  route guard: 54 HTTP and no WebSocket routes; Python runtime boundary passed.
- SDK: `typecheck`, all 63 Vitest tests, and `build` passed. The emitted listener
  warning belongs to a deliberate listener-failure test.
- Three additional isolated C# probes referenced the current product projects.
  All three failed their expected-correctness assertions, confirming R1, R3 and
  the allocation behavior in R4. They were kept outside the repository so this
  review does not silently add failing tests or alter production behavior.
- Baseline before review, on the same product source: all 13 .NET suites passed
  (9,648 tests); Release build and package Host smoke passed. That baseline was
  not rerun merely to repeat it and does not cover the new reproductions.
- Reviewed hypotheses excluded from findings include binary-content pinning for
  terminal launches (the contract binds path and intent), and holding the last
  context reading while preparing a new request (documented fallback behavior).

Local diagnostic artifacts are under
`C:\Users\USER\.codex\visualizations\2026\10\31\01a1916b-98e4-7d21-be03-7426d5468f75`:
`codebase-review-python.log`, `codebase-review-sdk.log`,
`codebase-review-probes.log`, and `review-probes/ReviewTests.cs`.
The synthetic race/database fixtures are there too; they contain no real secrets.

## Remediation — 2026-11-02

The user requested all findings fixed and removal of every external session
exchange format, explicitly including Antigravity.

- R1: line reads validate the opened handle's final path before reading bytes.
  A deterministic adapter test supplies a real mismatching handle after valid
  in-root resolution; matching handles, protected paths and hard-link denial stay
  covered. A fresh synthetic junction stress run completed 20,000 read attempts
  and 1,703 swaps with zero external reads. Original failing probes are preserved.
- R2: GUI sends capture displayed provider/model before asynchronous session
  admission and use the existing selected-run contract. Queued turns capture at
  enqueue; retries retain that snapshot. Missing selection preserves the draft
  and does not create a session.
- R3: settings reads are fenced by binding, read generation and local/authoritative
  selection versions, including reads started during a pending save and queued
  Settings callbacks. A successful manual save is immediately available to send,
  even before the initial settings read finishes. Catalog availability updates
  cannot own selection. The original delayed-read probe now keeps `new-model`.
- R4: the vulnerable reader and the entire external interchange library were
  removed, together with Host/Client/CLI/GUI entry points and their obsolete
  fixtures. Authenticated retired-route tests expect 405 (the existing OPTIONS
  wildcard), verify absent endpoint registrations and no session creation.
  Native Ru fork, rewind, history/search, delete and legacy-file migration remain;
  existing profiles and stored sessions were not modified.

The public contract now contains 52 HTTP routes and no WebSocket routes. The
solution's test matrix has 12 projects after removal of the interchange suite.
Independent scoped reviews approved the fixes and integration.

### Remediation verification

- .NET: the first ten projects passed all 5,836 tests. The matrix then stopped at
  a compile error in the new Host retirement fixture, which was moved to its
  owning partial test class. The subsequent full Host run passed 1,640 tests and
  failed only the new test's incorrect 404 expectation. The existing OPTIONS
  wildcard returns 405 for retired paths, as already covered for `/chat`.
  After correcting that expectation and adding endpoint-registration absence
  assertions, all 17 focused retirement/native-fork/migration/route tests passed.
  No production code changed between those Host runs. The full GUI suite passed
  2,095 tests. This covers 9,572 distinct .NET cases across all 12 projects; it is
  not a claim of one uninterrupted green matrix run.
- The 203-test focused GUI run and both fresh external probes passed. Earlier
  integration compile failures and the obsolete catalog-publish test setup were
  corrected before the final GUI run; their logs are retained.
- Python: `pytest tests -q` passed 268 tests with one skip. Ruff, the 52-route
  contract guard and Python runtime boundary guard passed.
- SDK: `typecheck`, all 63 Vitest tests and `build` passed. The listener warning
  comes from the deliberate listener-failure test.
- Release: `build.bat` completed all 11 stages. All three publish directories and
  their dependency manifests contain no `RuAgent.SessionInterchange` assembly.
  Packaged Host smoke checks authenticated status, the 53-skill user catalog
  without a workspace, retired import/export rejection (405), an empty native
  session listing (200), and graceful shutdown, using isolated temporary data.
- Unresolved acceptance observation: the first package smoke returned 503 on the
  skills read after status was ready; stderr was empty, and that smoke's cleanup
  removed stdout before additional diagnostics were captured. The unchanged
  package then passed a diagnostic rerun and 110 fresh isolated startup smokes
  (111 passes). The initial failure is preserved in `remediation-package-smoke.log`;
  subsequent logs are `remediation-package-diagnostic.log` and
  `remediation-package-startup-1.log` through `-110.log`. This does not establish
  the initial 503's cause or prove startup cannot fail intermittently; no
  speculative storage/policy change was made.
- Native GUI clicking and real-provider acceptance are separate and were not
  automated. Existing profiles were not used by the package smoke.

Local logs use the `remediation-` prefix in the evidence directory above.

Current operating instructions remain in [verification](../../operations/verification.md).
