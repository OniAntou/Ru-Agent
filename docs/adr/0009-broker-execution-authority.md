---
status: accepted
date: 2026-08-29
current_extension: ADR-0011 model autonomy and workspace trust boundary
  (`0011-model-autonomy-and-workspace-trust-boundary.md`, accepted
  2026-09-24).
---

# RuAgentBroker is the single tool execution authority

> Current execution profile: [ADR 0011](0011-model-autonomy-and-workspace-trust-boundary.md)
> keeps Broker as the sole execution authority and binds its canonical profile
> fallback to the explicit workspace trust decision. This ADR's original
> rationale and migration history remain unchanged.

Ru had two parallel tool implementations: the in-process `HarnessToolGateway`
(~4,300 lines inside the ASP.NET Core web host) and the process-isolated
Broker adapters, forcing manual parity between them. We decided that every
model-visible tool call (`read`, `write`, `edit`, `bash`)
executes only in RuAgentBroker, reached through the existing `IToolGateway`
seam (`Evaluate → Prepare → Execute → Cancel`); the Host keeps auth, policy
projection, approvals, and recovery persistence only.

The Broker is also the only authority for **session-scoped** Agent
permissions. It owns the in-memory `SessionDirectoryGrantRegistry` and
the typed pending-approval reasons (`external_directory_required`,
`terminal_delete_requires_approval`,
`terminal_external_mutation_requires_approval`,
`terminal_timeout_requires_approval`, `web_approval_required`,
`terminal_unknown_executable`).
Host and UI never persist or duplicate those grants; the GUI and CLI
only render the server-selected reason and forward a parameterless
approve.

## Considered options

- Full Broker (chosen): one seam, one enforcement place, real process
  isolation; every tool call pays one loopback round-trip.
- Hybrid (rejected): files/terminal via Broker, web kept in the Host —
  keeps two execution paths alive, the root cause of the duplication.
- Reverse (rejected): keep the gateway and drop the Broker path —
  contradicts ADR-0001's "Broker is the execution boundary" and loses
  process isolation.

## Consequences

- Preimage hashes for file mutations are Broker-captured (existing
  `FileHashService`) and Host-persisted (`FileRecoveryStore`); recovery
  artifacts move through a run-scoped handoff directory, never through
  model proposals. The invariant "Host-captured preimage" becomes
  "Broker-captured, Host-persisted".
- `HarnessToolGateway` and the duplicate `RuBackend/Agent/CapabilityProfile.cs`
  are deleted; Broker policy (ADR-0007 profiles, ADR-0008 workspace
  coordination) applies unchanged.
- Migration is strangler-ordered: conformance tests first, then read-only
  file tools, mutations, terminal, web.

- Migration status (updated 2026-09-03, cutover complete):
  - `BrokerSession` supports multiple concurrent run leases (per-lease
    close), and
    `ToolExecutionService` resolves every workspace-bound adapter against
    the lease's canonical root — the precondition for multiple concurrent
    runs on one Broker process.
  - A shared `ToolGatewayConformance` suite (in
    `RuAgent.Harness.Core.Tests/Conformance/`) locks one behavior contract
    across adapters; both the in-process gateway and `BrokerToolGateway`
    subclassed it. Twenty-three facts run against the Broker adapter,
    including the migrated regression facts (preimage never in model
    proposals, canonical `is_regex`, `read` with `op=help` read-only lifecycle).
  - All V2-wire tool families are conformance-locked: read-only file ops,
    `write` with `op=write`/`edit`/`op=delete`/`op=restore` (Broker-captured preimage), and
    `bash` with `mode=run`/`mode=shell` (analyzer-safe forms allow; unrecognized forms ask).
  - The Host projects its `PolicySnapshot` onto the wire via
    `PolicySnapshotMapper` (hash-identical to the Core canonicalizer) and
    `AgentRuntimeCoordinatorFactory` opens a per-run Broker lease through
    `BrokerToolGatewayFactory`; gateway disposal is tied to run teardown in
    `AgentRuntimeLoopService`. The in-process `HarnessToolGateway` and its
    fallback branch are deleted — a missing or unconfigured Broker descriptor
    fails closed (`broker_gateway_unavailable`) instead of silently
    downgrading to the Host path.
  - Approval bridging: the Host approval endpoint forwards the resolution to
    the Broker (`approval.respond`) before resuming the run and the resumed
    proposal executes with the returned one-time `authorization_token`
    (60 s). Approvals never persist reusable rules; the token or the
    session-directory grant is the whole authorization, and the Broker lease
    rebind remains future work.
  - Web wiring: web options are defined once on the Host, forwarded to the
    Broker process through the environment, and the v2 lease enables the
    standalone `web_search`/`web_fetch` tools only when enabled. Web tools always ask;
    explicit rules still deny first. Live-network acceptance
    stays separate per docs/operations/verification.md.
  - The v2 lease advertises the full authoritative tool set (16 bindings:
    system, help, notes, files, terminal) plus web when enabled.
  - `RuBackend/Agent/CapabilityProfile.cs` is intentionally kept: it is the
    v1 wire contract of `AgentBrokerClient` (chat/agent profiles), not a
    duplicate of the Broker's three-profile enum.
