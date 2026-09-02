# RuAgentBroker is the single tool execution authority

Ru had two parallel tool implementations: the in-process `HarnessToolGateway`
(~4,300 lines inside the ASP.NET Core web host) and the process-isolated
Broker adapters, forcing manual parity between them. We decided that every
model-visible tool call (`files.*`, `terminal.*`, `web.*`, `help.docs`)
executes only in RuAgentBroker, reached through the existing `IToolGateway`
seam (`Evaluate → Prepare → Execute → Cancel`); the Host keeps auth, policy
projection, approvals, and recovery persistence only.

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

## Migration status (2026-09-02)

- A shared `ToolGatewayConformance` suite (in
  `RuAgent.Harness.Core.Tests/Conformance/`) locks one behavior contract
  across adapters; both the in-process gateway and `BrokerToolGateway`
  subclass it. Twenty facts run against each adapter.
- Migrated and conformance-locked over the V2 wire flow: read-only file
  tools, `files.write/edit/delete/restore` (Broker-captured preimage),
  and `terminal.run/shell` (analyzer-safe forms allow; unrecognized forms
  ask). Broker fallback capabilities now advertise these ten bindings.
- `BrokerSession` now supports multiple concurrent run leases (per-run
  decision/preparation registries, per-lease close), and
  `ToolExecutionService` resolves every workspace-bound adapter against
  the lease's canonical root (per-root tool sets) instead of the legacy
  environment root — the precondition for multiple concurrent runs on one
  Broker process.
- Remaining cutover legs, in order:
  1. Snapshot handoff: map the Host `PolicySnapshot` to
     `BrokerPolicySnapshotWire` and open/close a per-run Broker lease in
     `AgentRuntimeCoordinatorFactory.Create` (the single production
     constructor of the tool gateway), with gateway disposal tied to
     `AgentRuntimeLoopService.Remove`.
  2. Approval bridging: `/agent/v1/runs/{runId}/approvals/{approvalId}`
     must forward `approval.respond` to the Broker and execute with the
     returned `authorization_token` (the adapter already accepts either
     token on `tool.execute`).
  3. Web wiring: `BrokerHost` needs `WebOptions` plumbed from the Host so
     `web.search/web.fetch` advertise; live-network acceptance stays
     separate per docs/operations/verification.md.
  4. Delete `HarnessToolGateway` and `RuBackend/Agent/CapabilityProfile.cs`
     and move the Host-gateway regression tests onto the conformance
     suite.
