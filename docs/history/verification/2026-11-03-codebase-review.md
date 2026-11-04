# Current-checkout code review — 2026-11-03

Snapshot: `dev`, HEAD `922335d4880e39960a6a1b2520066dedd52f07d4`, including
the pending working-tree changes. The initial review recorded six P2 findings;
the follow-up remediation below applies fixes for all six in the working tree.
Existing unrelated changes were preserved. At record time, no commit, native
desktop control or real-provider call had been made.

## Scope and evidence

The review traced key paths across Broker file/terminal/network/MCP/skills
authority; Host APIs, session history and admission; Harness durability,
outbox, context and subagent recovery; provider adapters; GUI/TUI/client
lifecycle; SDK transport; hosting, updates, packaging, CI and current docs.
An inventory found 2,100 C#/XAML/TypeScript files under the product and test
roots. This is subsystem coverage, not a claim that every line was inspected
or that absence of a finding establishes correctness.

Three Luna reviewers covered independent areas. Findings below were checked
against their callers, guards and tests. The previous review's line-read and
provider-selection defects are already remediated in this checkout; the retired
session-exchange reader is absent. Those are not reported again.

## Findings

### R1 — P2, remediated: Stop can return while a preparing send later creates a run

[`ChatView.AgentRun.cs`](../../../gui-winui3/Views/ChatView.AgentRun.cs), lines
603–628, displays Stop before the controller owns a run. The send then awaits
session creation, permission confirmation or preset persistence before calling
`CreateAndRunAsync` (lines 754–807). Its token is `CancellationToken.None`.

[`StopActiveAgentAsync`](../../../gui-winui3/Views/ChatView.SessionHistory.cs)
calls the controller's `CancelAsync`, which returns when there is no run to
tear down and no unknown create outcome to reconcile
([controller lifecycle](../../../gui-winui3/Services/AgentRunController.Lifecycle.cs),
lines 396–418). Nothing cancels the preparation task or fences its eventual
create. The draft-acceptance guard controls clearing, not whether a run starts.

Trigger: delay the preset write, press Send, press Stop, then complete the
write. The original send can still create a run and execute tools. This is
confirmed by the source call path; native interaction was not automated here.

Remediation: a submission cancellation source now owns preparation through run
creation and carries a session/generation snapshot. Stop and session
navigation cancel preparation; final admission checks that the same session
generation still owns the submission. Session creation accepts cancellation, and
Stop waits for submission settlement before reconciling an ambiguous create.

### R2 — P2, remediated: rewind is not fenced against a pending assistant delivery

[`SessionStore.RewindAsync`](../../../backend-dotnet/RuBackend/Business/Sessions/SessionStore.cs),
lines 170–210, truncates messages and increments the history epoch, without an
active-run check or an epoch condition on outbox delivery. The public rewind
endpoint and RPC bridge expose this path even though the GUI prevents ordinary
rewind of its own active run.

[`AppendIfAbsentAsync`](../../../backend-dotnet/RuBackend/Business/Sessions/SessionStore.Idempotency.cs),
lines 31–53, checks delivery identity but accepts an unseen assistant intent
at the new end of the transcript. The
[`outbox dispatcher`](../../../backend-dotnet/RuBackend/Harness/AgentConversationOutboxDispatcher.cs),
lines 222–229, supplies no history epoch.

An isolated probe using the production SessionStore confirmed: user delivered;
rewind to -1 leaves zero rows; user replay is `AlreadyPresent`; late assistant
delivery is `Appended`; history now contains one `ai` row and zero context turns.
This models the actual outbox append call, not a real-provider run.

Remediation: schema v34 records role and source run on delivery receipts. If a
late assistant delivery belongs to a run whose user prompt is no longer visible,
the store returns `StaleDelivery`; the outbox acknowledges it without appending
an orphan assistant row. Receipt replay remains idempotent, and a regression
covers rewind between user and assistant delivery.

### R3 — P2, remediated: deleting the parent removes image context from a retained fork

[`CopyForkPrefixAsync`](../../../backend-dotnet/RuBackend/Business/Sessions/SessionStore.Fork.cs),
lines 71–91, copies the original `source_run_id`. Image receipt facts still
depend on payloads belonging to that original run
([image-history query](../../../backend-dotnet/RuAgent.Harness.Persistence/HarnessSessionImageHistory.cs),
lines 18–28). Parent deletion removes those runs and their payloads.

The next child run then finds no receipt for an image-only user message.
[`HarnessModelContextBootstrapper`](../../../backend-dotnet/RuBackend/Harness/HarnessModelContextBootstrapper.cs),
lines 64–67, skips the whole turn, including its assistant answer. Captioned
turns retain text but lose the receipt fact. This is distinct from intentionally
not copying execution grants, active runs or workflow authority into a fork.

An isolated production-store/bootstrap probe confirmed one image receipt and
both context entries before deletion. After parent artifact/session deletion,
the child still had two visible message rows, but its new model context was empty.
Only synthetic data and a synthetic encryption key were used.

Remediation: schema v34 stores the bounded image attachment count on each
visible message, backfills it from retained payloads during upgrade, and copies
it when forking. Context recovery no longer needs payloads owned by the source
run. Existing artifacts already deleted before the v34 upgrade cannot be
reconstructed; this limit is covered in migration documentation. Forks still do
not inherit execution authority.

### R4 — P2, remediated: an MCP child can spawn outside its cleanup job

[`McpStdioClient.Start`](../../../backend-dotnet/RuAgent.Broker/Tools/Mcp/McpStdioClient.cs),
lines 94–98, calls `Process.Start` before `tree.Assign`. User-configured server
code can therefore execute and create descendants before job membership.
Assigning the direct process later does not retroactively enroll those children.
If the direct server exits, disposal skips its tree kill and closing the job
cannot reach descendants outside it.

The terminal runner already closes this window by creating suspended, assigning
the job, then resuming. The
[`SuspendedProcess`](../../../backend-dotnet/RuAgent.Protocol/SuspendedProcess.cs)
contract describes this exact race. The MCP path has no equivalent guard.
This is a source-confirmed process-lifetime race, not a newly measured exploit
or a claim about filesystem/network isolation.

Remediation: MCP now starts through `SuspendedProcess` with its stdin pipe
retained, joins the child to its Job Object while suspended, and resumes only
after assignment. Startup failures close the process tree and pipe handles. A
regression covers a server that spawns a descendant and exits immediately.

### R5 — P2, remediated: in-process execution does not forward terminal output live

[`InProcessToolGateway.ExecuteAsync`](../../../backend-dotnet/RuBackend/Harness/Broker/InProcessToolGateway.Tools.cs),
lines 174–279, accepts `publishDelta` but never invokes it. It awaits the
runtime's final reply and converts the receipt. The Harness has already supplied
a callback for transient tool-output publication, so the live UI path receives
no chunks from the production gateway.

The output is still captured in the Broker spool/receipt and can be read later;
this is not loss of durable terminal output. It prevents users from observing
progress while a long command runs. The existing
[native acceptance record](2026-11-03-native-acceptance-and-tui-performance.md)
records the same symptom; it was not rerun in this review.

The generic in-process conformance fixture still opts out of its file-output
delta case, so that case alone does not cover the terminal path. The follow-up
adds a dedicated foreground terminal regression that observes output before the
command can finish.

Remediation: the in-process gateway now forwards the existing callback through
the Broker runtime to the foreground terminal runner. Chunks retain the bounded
spool/redaction rules and carry execution IDs with ordered sequence numbers. A
conformance regression observes stderr before the command can finish.

### R6 — P2, remediated: late initial session creation overrides a newer session opening

[`EnsureAgentSessionAsync`](../../../gui-winui3/Views/ChatView.AgentSession.cs),
lines 71–84, awaits session creation and unconditionally activates the result.
It does not verify that the same view generation still owns that creation.

Trigger: send from a view with no valid session while delaying session creation,
then open an existing conversation. The session switch can complete because the
controller has not started its pending-create gate. When the older session
request returns, it activates its new conversation and continues the send,
overriding the user's newer navigation.

This finding is limited to initial session creation. Ordinary switching during
a preset write is guarded by `SessionPresetViewModel.EnsureStoredAsync` and its
`session_changed` check, so that broader hypothesis was rejected. Native UI
reproduction was not performed.

Remediation: initial session adoption uses the submission's captured open
generation and cancellation token. A stale creation response cannot activate its
session after another chat has opened; recovery also rechecks ownership before
replacing the submission snapshot.

## Maintainability and capacity

- Submission cancellation and generation fencing address R1/R6, but admission
  state is still spread across the view, presentation, preset and controller
  layers. `ChatView` has 26 C# partials totalling 7,217 lines; a cohesive
  submission owner remains a maintainability opportunity.
- Transcript lifetime and run-artifact lifetime are different. R2/R3 now encode
  stale delivery and retained image facts explicitly, with cross-component
  regression coverage for the affected sequences.
- `AgentRuntimeLoopService` is 1,042 lines and caps concurrent workers at 16,
  but that does not cap registered parked runs. `PendingApprovalStore.IssueV2`
  has no aggregate count/byte admission bound and prunes expiry during later
  issuance. Many sessions awaiting approval can retain state. Treat this as
  a capacity risk requiring a measured saturation test and explicit quotas;
  this review did not demonstrate an out-of-memory condition.
- The generic in-process fixture still returns normally for its unsupported
  file-output delta case. R5 now has a separate terminal regression, though the
  generic capability opt-out could still be made more explicit in test output.

## Verification

### Initial review snapshot, before remediation

- Fresh full `.NET` matrix: all 12 projects passed, 9,584 tests, zero failures
  and zero skips. GUI application Debug compilation was part of that run.
- Rewind probe: reproduced the orphan assistant row through production storage.
- Fork probe: reproduced context loss through production fork, payload,
  artifact deletion and bootstrap code. The first probe build had incorrect
  API references; its next version dereferenced nullable assistant `Content`.
  These probe-only errors were corrected and both failed logs retained. They
  are not product failures.
- Python `pytest tests -q`: 275 passed, 1 skipped. Ruff passed. The public-route
  guard confirmed 52 HTTP and zero WebSocket routes; the Python runtime-boundary
  guard found no interpreter files in the product.
- SDK `typecheck`, all 63 Vitest tests and `build` passed. The warning containing
  `listener exploded` is emitted by the deliberate listener-failure fixture.
- GUI Release build passed with zero warnings and zero errors:
  `dotnet build gui-winui3/RuApp.csproj -c Release -r win-x64 -p:Platform=x64 -p:SelfContained=true --nologo`.
- `git diff --check` found no whitespace errors. Existing working-tree changes
  were preserved; no commit had been made when this evidence was recorded.

### Follow-up remediation

- Final `.\scripts\test_dotnet.ps1 -Configuration Debug`: 12 projects,
  9,593 passed, 0 failed, 0 skipped. The GUI Debug build was included.
- Final `.\.venv\Scripts\python.exe -m pytest tests -q`: 275 passed,
  1 skipped. `ruff check tests scripts tools` passed.
- `ruff check .` reported two existing lint findings in `docs-site/build.py`
  (`UP017` and `UP032`); that file is outside the prescribed Ruff gate and was
  not changed. The public-route guard confirmed 52 HTTP, 0 WebSocket routes;
  the Python runtime-boundary guard found 0 interpreter files in product code.
- The final suite initially exposed an early-resize assertion in
  `TgTranscriptTests`; the test now waits for a stable 160-column frame and
  passes alone and in the final matrix. Another matrix run reported a 4,048-byte
  allocation in the quiet wordmark test; its isolated rerun passed, and the
  final full CLI suite passed 2,068/2,068.
- Five old-schema migration tests initially downgraded `user_version` without
  removing v34 columns/indexes. Their fixture now removes those v34 structures
  before simulating v30-v32; Persistence passed 367/367 and the final matrix
  passed. Earlier failure logs were retained.
- Documentation/runtime guards passed, `git diff --check` is recorded after
  the final edits, and no commit had been made when this record was written.
  Native UI and real-provider acceptance
  were not run.

Logs and standalone probe sources are outside the repository under
`C:\Users\USER\.codex\visualizations\2026\11\03\01a1a0f6-bc0b-7820-9ce8-cf9eb8521db4\codebase-review`.
Follow-up logs are under the adjacent `codebase-review-remediation` directory.
Synthetic databases were retained there. Existing profiles and credentials were
not used. Deterministic tests and source analysis do not prove native UI,
installer behavior, current external API compatibility or real-provider quality.

## Documentation maintenance

The Host component page still described buffering for the removed export
feature and retained an obsolete route split; the backend README also had an
inconsistent split. Removed the export description and aligned both splits to
the current contract: 52 routes = 29 Agent + 23 non-Agent. Historical evidence
was retained, including failed attempts and remediation notes. No ADR or
domain-contract change is implied by this review.
