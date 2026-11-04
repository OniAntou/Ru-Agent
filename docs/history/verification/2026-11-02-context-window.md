---
status: historical
date: 2026-11-02
---

# TUI and WinUI request context accounting

Working-tree evidence based on `922335d4`, preserving the preceding uncommitted
TUI activity work. Current behavior lives in [TUI architecture](../../architecture/tui.md),
[GUI context popup](../../architecture/gui.md#context-window-popup) and the
[Host event feed](../../architecture/host.md#run-event-feed).

## Reproduction and corrections

- Three TUI regressions failed before the fix: prompt budget replaced full window
  capacity, an overestimate froze the peak after lower provider input, and retained
  Anthropic Messages capacity was discarded (`context-window-tui-red.log`).
- Two GUI regressions failed before the fix: delayed estimates overwrote reported
  input and revived precompaction context (`context-window-gui-red.log`: 2,046
  passes, two failures).
- The native synthetic Host/SSE workflow reproduced **103,000 header tokens for
  a 52,000-token request** (`context-window-native-red-final.log`). Initial fixture
  probes timed out because the completion predicate depended on a transient text
  marker; the final predicate uses the actual end-of-turn footer. Those probe logs
  remain preserved outside the repository.
- Header occupancy now comes from a typed per-request `modelusage`/`json` payload;
  cumulative envelope input/output/total remain billing counters. The same count
  feeds the percentage: 52,000 / 128,000 = 40.625%, independent of the 120,000 prompt
  admission budget. Estimates are marked `~`; provider input may correct them down.
- Shared `RequestContextReading` separates durable event order from step-local
  transient cursors. Optional SSE `model_step_id` correlates request estimates and
  usage; stale estimates, retired steps and precompaction evidence cannot overwrite
  newer readings. New steps accept reset transient cursors. Missing input remains
  distinct from zero; cache buckets respect protocol defaults and explicit inclusive
  accounting. Legacy envelopes without identity remain readable.
- Fresh runs obtain pinned capacity/protocol through a bounded metadata read.
  Retained Chat Completions, Responses and Anthropic Messages use their snapshot
  window. Unknown capacity does not invent a percentage from prompt budget.
- Catalog refresh retains the previous published reading until its await completes,
  avoiding a temporary blank capacity. Usage missing prompt input retains only that
  request's estimate; otherwise it uses its prepared breakdown as an estimate or
  leaves occupancy unknown, rather than relabelling the previous step's count.

## Verification and intermediate failures

- The first focused TUI fix passed all four regressions, including the native
  workflow. A later focused run passed 38 tracker/chrome/metadata/workflow cases.
  Final workflow coverage includes all three supported protocols and cache semantics.
- GUI integration initially failed compilation because a new event subscription
  used the wrong handler name; corrected. The next run found an actual regression
  retaining old categories when legacy usage had no breakdown (2,047 passes, one
  failure). Retention now requires a matching nonempty estimate step identity.
- Client tests initially had five xUnit argument-binding failures from `int` test
  literals passed to nullable `long` arguments; corrected to `long` literals without
  changing product behavior or expected values.
- The first full matrix stopped at Broker: 1,426 passes, one failure. An immediately
  exiting MCP process could fail during job assignment, before the test's
  initialize-only exception assertion. The assertion now covers both valid failure
  points and still requires `mcp_server_unavailable`; runtime and authority are unchanged.
- The next matrix passed the first 11 projects, including CLI 2,076, Client 324 and
  Protocol 202. Host had 1,657 passes and one failed child-state assertion in
  `SubagentSchedulingTests` with remembered parent permission. Child terminal-code
  diagnostics were added without changing the required Completed state; a focused
  rerun passed all four scheduling cases. The original failure remains recorded;
  this alone does not establish its cause or fix the flake.
- Additional missing-input regressions failed twice (retained 50,000 tokens from
  the previous step). The per-request reader now drops that stale value and preserves
  the distinction between unknown, reported zero and a current prepared estimate.
- A new GUI saturation regression reproduced overflow when a very large prepared
  count was added to the draft before clamping. Clamping before addition restores
  the supported integer bound. The full GUI run then found a fixture error: the
  controller integration test used one-character step ids, which the real transient
  parser correctly rejected. The fixture now uses real `step_` plus 32-hex identities;
  parser validation and expected reading order were preserved. The next full GUI
  build/suite passed **2,051 tests**, zero failures/skips.
- Final matrix phases and documentation results are recorded after completion below.

## Final verification phases

All .NET runs were sequential under `Global\ru-test-matrix-run`; no lock bypass or
automatic retry was used. Commands used Debug and `--no-restore` / `-NoRestore`.

- `scripts/test_dotnet.ps1 -Configuration Debug -NoRestore` passed its first 11
  projects on the second run: Broker 1,427; CLI 2,076; Client 324; Harness Core 914;
  Harness Persistence 366; Hosting 42; Protocol 202; Runtime Adapters 329;
  Runtime Model 142; Session Interchange 45; Updates 25. The Host failure above
  stopped that command before GUI; this is not a single green full-matrix claim.
- After the final missing-input correction, the complete Client suite passed
  **326**, zero failures/skips (`context-window-client-final.log`). The final CLI
  tracker, metadata, meter, chrome and real native workflow checks passed **67**,
  zero failures/skips (`context-window-tui-final.log`), including all three protocols.
- `scripts/test_dotnet.ps1 -Configuration Debug -NoRestore -ProjectFilter gui-winui3`
  built the real WinUI app and passed **2,051**, zero failures/skips
  (`context-window-gui-final-2.log`), including real-controller event ordering,
  delayed estimates, compaction, reconnect cursors and overflow regression.
- `scripts/test_dotnet.ps1 -Configuration Debug -NoRestore -ProjectFilter RuBackend.Tests`
  passed the complete Host suite: **1,658**, zero failures/skips
  (`context-window-host-final.log`, 4m51s). Together these phases cover all 13
  projects. The earlier scheduling failure did not recur either in isolation or
  this complete Host rerun; its cause remains unconfirmed and the diagnostic stays.
- `.venv\Scripts\python.exe -m pytest tests/test_current_documentation.py tests/test_source_encoding.py tests/test_harness_routes.py tests/test_aspnetcore_route_matrix.py -q`
  passed **24** (`context-window-doc-contracts-final.log`). Current docs and links
  were reviewed; the glossary and admission/security contracts did not change.
- Existing advisory xUnit analyzer warnings remain in ProviderWebSearchHostTests,
  AgentSessionUiSourceContractTests and CustomizePageContractTests.
- SDK compatibility was checked in source: `RpcRunBridge` reads the extended
  envelope and projects the existing explicit RPC field set; the SDK consumes
  that projection without a strict raw-SSE schema. No SDK contract changed and
  its separate test/build suite was not rerun. Full Python, release/publish,
  physical-terminal, interactive WinUI and live-provider acceptance were not run.

Evidence logs are outside the repository in the session artifact directory under
`C:\Users\USER\.codex\visualizations\2026\10\31\01a1916b-98e4-7d21-be03-7426d5468f75`.
All runtime fixtures use synthetic local HTTP responses or isolated test stores.
No live provider, user-session mutation, desktop automation, installed-runtime
replacement, commit or push was performed. Interactive physical-terminal and WinUI
acceptance remain separate from deterministic tests and native headless rendering.
