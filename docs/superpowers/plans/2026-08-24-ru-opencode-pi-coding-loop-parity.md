# Ru OpenCode/Pi Coding-Loop Parity Implementation Program

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Status (2026-08-29): historical implementation program.** The selected Phase 1/2 foundation, Phase 3 terminal/reconciliation slice, Agent-only desktop cutover, and prompt-composer work are shipped on `main`. Managed Git, real-model acceptance, session/extension parity, and the OS-enforced sandbox remain future projects; this dated program is retained as an audit trail, not as the current operating contract.

**Goal:** Deliver the approved milestone-1 coding harness through four sequential, testable vertical slices without running two writable Agent loops for one run.

**Architecture:** A dependency-free `RuAgent.Harness.Core` owns run orchestration; a separate SQLite adapter is the only Harness persistence writer; ASP.NET Core exposes the authenticated API and owns Worker/Broker children; Broker remains the final policy and execution authority; Desktop and CLI are clients of the same API.

**Tech Stack:** .NET 10, ASP.NET Core, Microsoft.Data.Sqlite 10.0.11, xUnit, Python 3.11/3.13, pytest, Ruff, Mypy, WinUI 3, PowerShell, Windows Job Objects, SQLite WAL, AES-GCM, Windows DPAPI, SSE.

---

## Source of truth and status

- Approved design: `docs/superpowers/specs/2026-08-24-ru-opencode-pi-coding-loop-parity-design.md`.
- This document is the historical implementation program index. Current production behavior is documented in `docs/README.md`, `docs/architecture.md`, and `docs/operations/`.
- The current production contract is 54 HTTP plus 1 WebSocket route; the 13-route Agent API is shipped.
- Milestone 1 may not be called coding-loop parity until phase 4 and the real-model acceptance checklist are complete.
- Overall OpenCode/Pi parity is not claimed by this program. Session trees/compaction and extension ecosystems remain later milestones.
- The OS-enforced filesystem/network sandbox remains a separate project. Policy rules, path checks, analyzers, and `network_intent` are not sandboxing.

## Program plans

Execute these plans in order. Do not start a later plan while an earlier plan has failing checkpoint gates.

1. [Phase 1: Harness Foundation and Headless Read-Only Tracer](2026-08-24-ru-opencode-pi-coding-loop-parity-01-harness-foundation.md)
2. [Phase 2: Policy Authority and Complete File Tools](2026-08-24-ru-opencode-pi-coding-loop-parity-02-policy-file-tools.md)
3. [Phase 3: Terminal, Local Git, and Reconciliation](2026-08-24-ru-opencode-pi-coding-loop-parity-03-terminal-git.md)
4. [Phase 4: Desktop Cutover and Parity Acceptance](2026-08-24-ru-opencode-pi-coding-loop-parity-04-desktop-cutover.md)

Dependency flow:

```text
Phase 1: Core + store + Worker v2 + API/SSE + host/client + CLI tracer
    |
    v
Phase 2: immutable policy snapshots + trust + approvals + complete files.*
    |
    v
Phase 3: terminal streaming + analyzers + local Git + reconciliation
    |
    v
Phase 4: Desktop API client + legacy removal + docs + acceptance
```

## Approved-spec coverage matrix

| Approved design area | Owning phase | Completion evidence |
|---|---:|---|
| Core identities, run/tool states, scheduling, budgets, cancellation | 1 | Core state/coordinator/scheduler tests |
| SQLite WAL, transactional projections/events, encrypted payloads, history outbox | 1 | Persistence and session dedupe/crash tests |
| Worker one-step `AssistantStep` and Agent runtime | 1 | Python/C# golden protocol and one-call tests |
| Public Host process ownership, descriptor, mutex, idle shutdown | 1 | Hosting/Client Job Object and stale-descriptor tests |
| API v1, SSE cursor/reconnect, 54+2 route contract, CLI tracer | 1 | Endpoint/SSE/route/CLI contract tests |
| Workspace identity/trust and immutable preset snapshot | 1–2 | Trust binding plus complete snapshot tests |
| Ordered Standard/Full rules and approval snapshot replacement | 2 | Pure evaluator and cross-process ordering tests |
| Complete file discovery/mutation/delete/restore contract | 2 | Bounded/adversarial temp-workspace tests |
| Terminal environment, analyzers, streaming, timeout, process-tree cancel | 3 | Analyzer matrices and real child/grandchild tests |
| Local Git, dirty preservation, managed commit | 3 | Disposable-repository matrix |
| Dispatch crash ambiguity and reconciliation | 3 | Startup/cancel/reconcile state matrix |
| Desktop Agent cards/actions/attach/history/TTS boundary | 4 | WinUI unit/source/Harness tests |
| Agent-only/profile/legacy Worker-loop removal | 4 | Reference scans and Agent/volume regressions |
| ADR/current docs and truthful local-user/non-sandbox claims | 4 | Documentation contract tests |
| CLI and Desktop real-model seeded-bug acceptance | 4 | Separately approved recorded acceptance |
| Session trees/compaction; extensions/skills/subagents/MCP/LSP/web | Future milestones | Explicitly excluded from milestone 1 |
| OS-enforced filesystem/network sandbox | Separate project | Explicitly excluded; no policy-control relabeling |

## Fixed project boundaries

These names and dependency directions are shared by all four plans. Change them only through an explicit design amendment before implementation.

```text
RuAgent.Harness.Core
  BCL-only domain/application library
  references: none of ASP.NET, WinUI, SQLite, process, shell, Worker, Broker

RuAgent.Harness.Persistence
  SQLite, encryption, outbox, rule/workspace storage
  references: RuAgent.Harness.Core

RuAgent.Protocol
  private Broker v1/v2, Worker-facing and public Agent API wire DTOs
  references: no host/UI implementation

RuAgent.Hosting
  Public Host single instance, Worker/Broker launch, Job Object, descriptor
  references: RuAgent.Protocol

RuAgent.Client
  descriptor validation, start-or-attach, authenticated Agent HTTP/SSE client
  references: RuAgent.Protocol

RuBackend
  composition root, adapters, API/SSE, sole Harness-store writer
  references: Core, Persistence, Protocol, Hosting

RuAgent.Broker
  canonical resolution, policy authority, adapters, process trees
  references: Protocol

RuAgent.Cli
  thin client and renderer only
  references: Client, Protocol

RuApp
  thin Desktop client for Agent; ordinary Chat remains on Chat WebSocket
  references: Client, Protocol
```

Forbidden dependency edges:

- Core must not reference persistence, ASP.NET, WinUI, PowerShell, process APIs, or concrete Worker/Broker clients.
- CLI must not reference `RuBackend`, `RuAgent.Broker`, Python modules, or WinUI.
- Desktop and CLI must not connect directly to Worker or Broker.
- Broker must not write `agent_harness.db`, Chat sessions, memory, or UI state.
- Worker v2 must not execute tools, wait for receipts, request approval, persist Agent events, or own the outer loop.
- WinUI must not retain a second copy of Worker/Broker launch and Job Object logic after phase 4.

## Fixed type vocabulary

Use these names consistently across plans and code:

```csharp
public enum HarnessRunState
{
    Created,
    WaitingWorkspaceTrust,
    WaitingHistory,
    Running,
    WaitingApproval,
    WaitingUser,
    ReconciliationRequired,
    Completed,
    Failed,
    Cancelled,
    Interrupted,
}

public enum HarnessToolCallState
{
    Proposed,
    Allowed,
    AwaitingApproval,
    Denied,
    Executing,
    Succeeded,
    Failed,
    TimedOut,
    Cancelled,
    OutcomeUnknown,
}

public enum AgentPermissionPreset { Standard, FullAccess }
public enum PolicyDecision { Allow, Ask, Deny }
public enum ApprovalScope { Once, Run, Project }
public enum ReconcileOutcome { Applied, NotApplied, UnknownContinue }
public enum HistoryWaitKind { UserBeforeFirstStep, FinalBeforeCompletion }
```

Stable IDs are separate `readonly record struct` values in `HarnessIdentifiers.cs`: `WorkspaceId`, `SessionId`, `RunId`, `ModelStepId`, `ToolCallId`, `ExecutionId`, `ApprovalId`, `TrustRequestId`, `PolicyRuleId`, `EventId`, and `PayloadId`. Wire adapters serialize their `Value` as lowercase snake-case JSON fields.

The Core persistence boundary is a compare-and-swap commit, so event append, projection change, encrypted payload intent, and conversation outbox intent cannot drift:

```csharp
public sealed record HarnessCommit(
    RunId RunId,
    long ExpectedVersion,
    AgentRun NextRun,
    IReadOnlyList<HarnessEventIntent> Events,
    IReadOnlyList<HarnessPayloadIntent> Payloads,
    IReadOnlyList<ConversationOutboxIntent> ConversationOutbox);

public interface IHarnessEventStore
{
    Task<CreateRunResult> TryCreateRunAsync(NewAgentRun request, CancellationToken cancellationToken);
    Task<AgentRun?> GetRunAsync(RunId runId, CancellationToken cancellationToken);
    Task<HarnessCommitResult> CommitAsync(HarnessCommit commit, CancellationToken cancellationToken);
    IAsyncEnumerable<HarnessEventEnvelope> ReadEventsAsync(
        RunId runId,
        long afterSequence,
        CancellationToken cancellationToken);
}
```

The Core orchestration ports remain provider and transport neutral:

```csharp
public interface IModelStepRunner
{
    Task<AssistantStep> RunStepAsync(
        ModelStepRequest request,
        Func<AssistantDelta, ValueTask> publishDelta,
        CancellationToken cancellationToken);
}

public interface IToolGateway
{
    Task<ToolProposalDecision> EvaluateAsync(
        ToolProposal proposal,
        CancellationToken cancellationToken);

    Task<PreparedToolExecution> PrepareAsync(
        ApprovedToolExecution execution,
        CancellationToken cancellationToken);

    Task<ToolExecutionReceipt> ExecuteAsync(
        PreparedToolExecution execution,
        Func<ToolOutputChunk, ValueTask> publishDelta,
        CancellationToken cancellationToken);

    Task CancelAsync(ExecutionId executionId, CancellationToken cancellationToken);
}
```

`PrepareAsync` is side-effect free. It re-resolves canonical intent and may
return bounded private pre-dispatch payload intents (for example a delete
recovery copy). Core commits those payloads together with
`tool.execution_requested` before calling `ExecuteAsync`. Broker re-resolves
again at execution and fails with `workspace_changed` if the prepared intent
no longer matches.

## Fixed protocol and rollout rules

- Worker v1 stays at `/worker/v1` for ordinary Chat during migration.
- Agent Worker v2 is additive at `/worker/v2`, envelope version 2, with `agent.step.start`, `agent.step.delta`, `agent.step.done`, and `agent.step.cancel`.
- Broker protocol v1 remains supported until phase 4. Broker v2 adds run-lease policy/workspace binding and `tool.output.delta`; new v2 messages must be rejected by a v1 connection.
- Public API remains `/agent/v1/*`; its safe event envelope has protocol version `1`.
- `POST /agent/v1/runs` carries a bounded `idempotency_key` JSON field. The
  store binds its hash to the normalized create request: exact replay returns
  the existing run, while key reuse with a different session/workspace/prompt
  payload hash returns `idempotency_conflict`.
- `RuBackend:AgentHarness:Version` is the one whole-run flag with values `v1` or `v2`. It is snapshotted at run creation. No tool call within a run may cross versions.
- Phase 1 keeps the default at `v1`; phase 4 changes the default to `v2` only after Desktop and CLI acceptance, then removes the temporary branch.
- All 13 Agent HTTP routes are added together and canonical route data changes directly from 41+2 to 54+2. Do not publish an intermediate route count.
- Existing Chat, voice, VTS, observation, settings, memory, and session routes remain byte-compatible.

## Fixed persistence and history rules

- `DataPaths` gains computed `BusinessDataRoot` and `AgentHarnessDatabase` properties; do not add another positional constructor argument and break every existing fixture.
- `AgentHarnessDatabase` is `<business-data-root>\agent_harness.db`, never `memory.db`.
- SQLite WAL is enabled. ASP.NET Core is the only writer.
- In addition to the design's eight minimum tables, `agent_session_settings`
  stores each session's selected preset and monotonic selection version.
  A first Standard run may create version 1 atomically; Full Access requires a
  prior interactive session-preset change through the user-control policy API.
  Run creation must match the stored selection/version.
- Safe events never contain commands, arguments, paths not approved for display, code, diffs, stdout/stderr, provider bodies, environment values, credentials, or raw control metadata.
- Private payloads use AES-GCM. The randomly generated AES key is protected with DPAPI CurrentUser; database/key files receive current-user ACLs.
- Every payload intent declares a bounded kind/audience. Model-facing text, authenticated-client text, and persisted terminal text pass through known-secret streaming redaction before forwarding/encryption. Tests and docs retain the limitation that unknown user/project secrets cannot be guaranteed detectable.
- `RecoveryOpaque` payloads are byte-exact encrypted internal data: they are never returned by the public payload endpoint, sent to the model, rendered by clients, or redacted/mutated. Only the active run's one-time Broker restore path may consume them.
- Chat history receives exactly one visible user message and one final visible assistant message per completed Agent run.
- After the visible user-message outbox acknowledgement, Host captures the existing bounded session-context turns as an encrypted immutable base-context payload for that run. Core assembles each provider-neutral model request from that base snapshot plus run-local assistant/tool-result payloads in durable sequence order; UI and Worker never reconstruct context.
- Context assembly never silently drops durable entries. Phase 3 adds pre-step provider-size estimation and a bounded continuation summary for a true context-limit terminal outcome.
- Extend the existing session message compatibly:

```csharp
public sealed record SessionMessage(
    string Role,
    string Content,
    string? SourceMessageId = null,
    string? SourceRunId = null);

public enum SessionAppendOutcome
{
    Appended,
    AlreadyPresent,
    SessionNotFound,
    InvalidMessage,
}

Task<SessionAppendOutcome> AppendIfAbsentAsync(
    string sessionId,
    SessionMessage message,
    CancellationToken cancellationToken);
```

Existing session JSON without source fields must continue to load.

## Fixed execution and safety rules

- One session has at most one active run; one trusted workspace has at most one active Agent run.
- Default budget window: 32 model steps and 64 tool calls. Approval waits do not consume budget. Exhaustion enters `WaitingUser`; Continue opens a new window.
- Independent read-only calls may run concurrently. File mutations, terminal calls, and Git mutations remain serialized in model order.
- Core persists `tool.execution_requested` before Broker dispatch and persists the final receipt before another model step.
- Read-only transport retry: at most two attempts with the same `execution_id`. Mutations are never automatically retried after dispatch.
- Cancellation/timeout closes the complete process tree. Ambiguous mutation outcomes enter `ReconciliationRequired`; the original `execution_id` is never dispatched again.
- `UnknownContinue` requires one durable read-only inspection receipt before Broker accepts another mutation.
- Standard and Full Access execute as the current Windows user. Neither is a filesystem or network sandbox.
- Full Access never bypasses initial workspace trust or protocol/credential/output/cancellation invariants.
- No implicit reset, clean, force checkout, whole-tree stage, push, or PR workflow.
- Heavy suites run sequentially in `D:\LLM`.
- Deterministic tests must not mutate live microphone, speaker, volume, desktop, or VTube Studio state.

## Commit and worktree discipline

- Work on the user-authorized branch only. At program creation that branch is `main`.
- Before each task, run `git status --short --branch`; preserve unrelated user changes.
- Each RED/GREEN task ends with a focused conventional commit after its stated tests pass.
- Never use `git reset --hard`, `git clean`, force checkout, broad staging, or `git add -A`.
- Stage only files named by the current task and inspect `git diff --cached --stat` plus `git diff --cached` before committing.
- Do not run heavyweight suites concurrently.

## Phase completion evidence

Each phase must leave:

- all new focused tests green;
- all touched existing suites green;
- current operational docs truthful for the shipped/default behavior;
- historical specs/plans retained as history, not linked as current behavior;
- no placeholder markers (`TODO`, `TBD`, `FIXME`, `NotImplementedException`) in the phase's production files;
- a clean or fully disclosed worktree;
- a checkpoint commit whose message is listed in that phase plan.

Final deterministic verification is specified in phase 4. Real Desktop/model acceptance is a separate, explicit checkpoint and must not be inferred from unit or Harness tests.
