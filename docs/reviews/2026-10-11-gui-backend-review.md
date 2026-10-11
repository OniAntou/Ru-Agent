# GUI and backend review — 2026-10-11

Snapshot: `dev`, `f42db02e`. This is a dated review, not a statement that these
findings remain open in later commits. No production code was changed in this
review. Temporary regression probes were removed after execution; their sources
and failing logs remain in local `artifacts/`.

## Follow-up fixes

The subsequent implementation addresses all seven findings from this snapshot:

| Finding | Change | Regression evidence |
| --- | --- | --- |
| Runtime discovery starvation | Polling schedules owned workers without waiting for model/tool work; a shared four-slot gate bounds execution. Deletion cancels the worker and shutdown awaits cleanup. | Held factory, new registration, duplicate polls, six queued runs and shutdown tests. |
| Stale adopted SSE settlement | Run/generation guards cover delayed initialization, cancellation, failures, refresh, terminal fallback and transient callbacks. | The formerly failing old-subscription/new-run regression and controller suite. |
| History reload/turn cleanup ownership | Session generation, run and assistant identity are checked at dispatched application and after the awaited reload. | Queued UI ownership tests; native session-switch acceptance remains unrun. |
| Deleted-run SSE leak | A missing durable row closes the response without a fabricated terminal snapshot and releases the hub lease. | Live HTTP deletion test, subscriber cleanup and subsequent 404. |
| Provider key probe identity | A separate probe pins provider/key edit, fetches that provider directly and guards removal; superseded results never update the current UI. | Held save/catalog tests, wrong-provider and removal-failure cases. |
| Stale Files root | A serial refresh controller retains only the newest root request; superseded results/errors and folder expansions are discarded. | Held root read, multiple switches, old read failure and clearing-workspace tests. |
| Export workspace loss | List and export share workspace resolution; export queries the requested session directly. | HTTP export checks for every supported writable format. |

The original evidence and verification below describe the pre-fix checkout.
Follow-up verification:

- Full `.NET` Debug matrix: **5,957 passed / 0 failed / 0 skipped**, all 12
  projects, including backend **1,049** and GUI **1,352**.
  Command: `scripts/test_dotnet.ps1 -Configuration Debug -NoRestore`.
  Log: `artifacts/gui-backend-fixes-dotnet.log`.
- Documentation guards: **19 passed**; command:
  `.venv/Scripts/python.exe -m pytest tests/test_current_documentation.py tests/test_guidance_route_counts.py tests/test_readme_counts.py -q`.
  Log: `artifacts/gui-backend-fixes-docs.log`.
- `git diff --check` passed. Debug test builds include the shipped WinUI source.
- The initial implementation matrix had one failing export test because its test
  inputs included unsupported writable formats. The test now enumerates the
  catalog's supported `CanWrite` formats and retains the workspace assertion.
  Original log: `artifacts/gui-backend-fixes-dotnet-initial.log`.
- During GUI iteration, existing cancellation tests caught a late-create cleanup
  regression: cleanup must capture the teardown generation after verifying CTS
  identity, rather than using the original creation generation. This was corrected.
  Source tests were aligned with the extracted key-probe and Files ownership seams.
  An intermediate disposed-field compile error was also corrected before the final matrix.
- No new native GUI automation, live provider calls or external-harness export
  acceptance was run. The full Python tooling suite and Release publish were not
  rerun for these C# lifecycle/query changes.

## Scope and evidence

The review traced GUI session selection/history restoration, run-controller
ownership, reply settlement, provider settings/catalog state, transcript scroll
handling and the Files panel. Backend inspection covered runtime scheduling,
outbox dispatch, run/session deletion, live SSE, provider selection/settings,
session persistence and export. Surrounding contracts, tests, CONTEXT.md and
architecture/operations documentation were checked. This is not an exhaustive
line-by-line review of every file or native acceptance of every GUI flow.

Three behavioral probes reproduced defects in current code. Four additional
findings follow from concrete asynchronous paths or missing data projection;
those four were not independently reproduced in the native GUI.

## Findings

### 1. P1 — A long run prevents later registrations from entering the runtime

`AgentRuntimeLoopService.StepOnceAsync` snapshots registrations, starts bounded
work and awaits `Task.WhenAll`. Each `DriveRunAsync` continues model/tool steps
until the run parks or terminates. `ExecuteAsync` therefore cannot dispatch the
outbox or discover newly registered runs while any member of the snapshot is
still running. The four-slot semaphore only helps runs already in that snapshot.

Reproduction: block run A in coordinator creation, register independent run B,
and wait two seconds. B never reaches coordinator creation although three slots
are free. The probe fails the required behavior.

Source: `backend-dotnet/RuBackend/Harness/AgentRuntimeLoopService.cs:484,517,937`.
Evidence: `artifacts/gui-backend-review-backend-probe.log`;
`artifacts/gui-backend-review-scheduling-probe.cs.txt`.

Fix direction: keep discovery/outbox polling independent of in-flight drive
tasks. Maintain one owned worker per run, bounded by a shared concurrency gate,
and observe completion/cancellation without a snapshot-wide barrier.

### 2. P1 — An old adopted SSE subscription marks the new run idle

`FollowAdoptedRunAsync` guards the release path by run id, but its cancellation
fallback and general exception handler enqueue unconditional status and busy
updates. After detaching A and adopting B, a delayed cancellation of A reaches
the fallback and sets `_isBusy = false` on B. This is not corrected by the
generation checks on ordinary activity/delta callbacks.

Reproduction: hold A's subscription, detach A, adopt B with another held
subscription, then release A. B remains `CurrentRun` but `IsBusy` becomes false.
The temporary GUI regression test fails. The create path uses the busy flag to
decide whether a new request may begin, so controller ownership can be replaced
while B is still active.

Source: `gui-winui3/Services/AgentRunController.cs:1735-1755`.
Evidence: `artifacts/gui-backend-review-gui-probe.log`;
`artifacts/gui-backend-review-gui-probe.patch`.

Fix direction: apply run id plus ownership generation checks inside every
deferred completion, cancellation and failure callback, including stream-end
settlement and terminal-snapshot fallback.

### 3. P1 — A turn-end history reload can repaint another session

`ReloadSessionHistoryAsync` checks session identity after fetching messages,
then awaits `LoadReplayedRunActivitiesAsync`. Its `Apply` callback does not
recheck identity or the session-open generation. Switching to B during A's
replay fetch lets A replace B's visible transcript. Returning early from the
reload would also need to stop `CompleteActiveAgentTurnAsync`: that caller
unconditionally clears the active assistant/run references after the await.

Source: `gui-winui3/Views/ChatView.SessionHistory.cs:208-210` and
`gui-winui3/Views/ChatView.AgentRun.cs:1267-1285`.
Evidence: asynchronous control-flow inspection; no native reproduction in this
review. The relevant awaits are real HTTP/replay work, not atomic operations.

Fix direction: capture session, run and generation at turn settlement; recheck
before dispatcher application and before caller cleanup/queue draining. Add
deferred-response tests that switch sessions at both await boundaries.

### 4. P2 — A live SSE request survives deletion of its run

The live loop in `GetEventsAsync` updates state only when `GetRunAsync` returns
a row. If session deletion removes that row, the loop retains the old
non-terminal snapshot and polls indefinitely. The artifact deletion transaction
marks runs cancelled and deletes them together, so SSE cannot rely on observing
an intermediate terminal row.

Reproduction: open a live feed, delete the session's run artifacts, and await
response completion. The request remains open until the test explicitly cancels
it. A detached client can retain a subscription and an in-flight Host request
after its session is gone.

Source: `backend-dotnet/RuBackend/Harness/AgentV1Endpoints.cs:451-458` and
`backend-dotnet/RuAgent.Harness.Persistence/HarnessSessionArtifactStore.cs:187`.
Evidence: `artifacts/gui-backend-review-sse-probe.log`;
`artifacts/gui-backend-review-sse-probe.patch`.

Fix direction: terminate the feed on a missing run with a documented bounded
closure signal; ensure subscriber cleanup executes. Test deletion while attached.

### 5. P2 — A key probe can use another provider's verdict to delete a key

`ProbeAndSaveModelsDevKeyAsync` captures provider A, awaits key persistence, then
refreshes the shared catalog controller and reads its current state. The user
can select B during the await; the controller correctly changes backend, but
the probe does not pin or recheck it. If B is unauthorized, the probe removes
A's key using `providerId`. On success it can also clear B's currently edited
password field using A's completed operation.

Source: `gui-winui3/Views/SettingsView.xaml.cs:809-835`;
provider selection at `:981-997`.
Evidence: control-flow inspection, including catalog generation/cancellation
handling. Native timing reproduction was not run.

Fix direction: bind the probe to provider identity and the entered-key revision.
Obtain a verdict for that provider explicitly and discard stale UI updates or
credential mutations when ownership changes.

### 6. P2 — Switching workspace during a Files refresh leaves the old tree

`SetWorkspace` starts a refresh, but `_isFilesLoading` causes a concurrent
refresh for B to return. The in-flight A request then appends A's rows without
checking `_workspacePath`, and no refresh for B is queued. The Files tab can
therefore show A's paths while the active session/workspace belongs to B.

Source: `gui-winui3/Controls/AgentSidePanelControl.Files.cs:35-79`.
Evidence: control-flow inspection; native reproduction was not run.

Fix direction: use a workspace generation/cancellation scope and replay the
latest requested refresh after an in-flight listing ends. Reject stale results
before adding rows.

### 7. P2 — Session export always loses a normal Ru session's workspace path

`WorkingDirectoryForAsync` reads `ISessionStore.ListAsync` and returns
`summary.WorkspacePath`. `SessionStore.ToSummary` does not fill any workspace
fields. Those fields are added separately by the public session-list endpoint,
which export does not call. The export therefore receives null even for a Ru
session that has a known Harness workspace, placing partner exports under an
unknown project instead of the session's actual project.

Source: `backend-dotnet/RuBackend/Business/SessionInterchangeEndpoints.cs:258-268`,
`backend-dotnet/RuBackend/Business/Sessions/SessionStore.Listing.cs:28` and the
workspace enrichment in `BusinessEndpoints.ListSessionsAsync`.
Evidence: data-flow inspection; not an external-harness acceptance test.

Fix direction: share the existing session/workspace resolution service with
export, resolving the requested session directly rather than scanning a capped
summary list.

## Verification

- Full .NET matrix attempt: Broker **941 passed**, then the CLI suite produced
  no completion after several minutes. The review stopped that run; this is an
  incomplete matrix, not a pass or a confirmed CLI product defect. Log:
  `artifacts/gui-backend-review-dotnet.log`.
- Temporary regression probes: **3 failed / 0 passed**, each with the expected
  behavior asserted against current production code. These are defect evidence,
  not failures hidden by rerunning the same tests.
- Documentation guards: **19 passed**, including a check after adding this
  report (`artifacts/gui-backend-review-final-docs.log`).
- Existing backend suite: **1,046 passed / 0 failed / 0 skipped**,
  `scripts/test_dotnet.ps1 -Configuration Debug -ProjectFilter RuBackend.Tests`
  (`artifacts/gui-backend-review-backend.log`).
- Existing GUI suite: **1,340 passed / 0 failed / 0 skipped**,
  `scripts/test_dotnet.ps1 -Configuration Debug -ProjectFilter gui-winui3`
  (`artifacts/gui-backend-review-gui.log`). Passing existing suites do not cover
  the three failing temporary regression probes above.
- No real-provider benchmark or new native GUI automation was run in this review.
  Previous scroll acceptance does not establish acceptance of these race cases.

## Implementation priorities

Fix runtime discovery starvation and run/session ownership races first. Add
behavioral tests using held model, HTTP and SSE responses, rather than relying
only on source-string assertions. Then close the deleted-run SSE path and bind
provider/File-panel work to their owning identity. Export workspace resolution
can follow those fixes.

The recurring GUI problem is fragmented ownership: the controller, view and
deferred callbacks each check identity differently. A shared immutable operation
scope carrying session, run and generation would reduce those gaps. Splitting
large files alone does not establish cancellation or ownership correctness.
