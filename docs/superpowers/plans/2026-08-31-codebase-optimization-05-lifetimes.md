# Lifetime and Authority Consolidation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Consolidate Broker transport/execution ownership and Worker v1/v2 lifetime mechanics without removing security checks, while preserving policy, cancellation, and recovery semantics.

**Architecture:** Keep Public Host as owner, Broker as policy authority, Worker as protocol owner. Share only equivalent lifetime mechanics (connection cleanup, cancellation token propagation, process-tree cleanup) via small helpers, not by merging distinct framing/concurrency. Do not add OS sandbox or new transport.

**Tech Stack:** C#, ASP.NET Core, xUnit, real temporary files

## Global Constraints

Inherit [master constraints](2026-08-31-codebase-optimization.md). Phase 3, after phase 2. No schema migration, no live device/cloud, no security check removal, no speculative cancel fix.

---

## B — Broker transport and execution ownership

**Files:**
- Read: `backend-dotnet/RuAgent.Broker/BrokerSession.cs`, `BrokerRunLease.cs`, `PolicyContext.cs`, `PolicySnapshotEvaluator.cs`, `PendingApprovalStore.cs`, `ApprovalTokenService.cs`, `Tools/Files/*Adapter.cs` (list/glob/search/read/write/edit/delete/restore), `Tools/Terminal/*`, `Transport/BrokerWebSocketHandler.cs` (if exists)
- Modify: `backend-dotnet/RuAgent.Broker/BrokerSession.cs` and `Transport/*` to extract shared lease/token validation and cancellation helpers
- Create: `backend-dotnet/RuAgent.Broker/Shared/BrokerLifetime.cs` (only if helper is cohesive, no new package)
- Tests: `backend-dotnet/RuAgent.Broker.Tests/BrokerSessionTests.cs`, `PolicySnapshotEvaluatorTests.cs`, `PendingApprovalTests.cs`, `FileDiscoveryAdapterTests.cs`, `TerminalAdapterTests.cs`
- Neighbor: `backend-dotnet/RuBackend.Tests/Harness/BrokerToolGatewayTests.cs`

**New helper (only if extraction is justified, keep defense-in-depth):**
```csharp
internal static class BrokerLifetime
{
    internal static bool TryValidateLease(BrokerRunLease lease, PolicySnapshot snapshot, out string errorCode);
    internal static void CancelAndDispose(CancellationTokenSource? cts);
}
```
Do not merge distinct adapter logic; share only equivalent cleanup: lease hash validation, token expiry, and `CancellationTokenSource` disposal. Each adapter keeps its own scope/schema checks.

- [ ] **Step 1 — Inventory and RED.**
  Run `dotnet test RuAgent.Broker.Tests -k "BrokerSession|Policy|Approval"` to baseline. List all places that validate lease/token/scope and that dispose CTS. Write a failing test that proves a duplicated cleanup path was missed (e.g., a second dispose after cancellation must not throw). If helper not needed, document why and stop this task.
- [ ] **Step 2 — GREEN (only if helper justified).**
  Extract one helper for the duplicated path, keep all existing `ApprovalTokenService` and `PolicyContext` checks. No policy logic removed for speed. Run `dotnet test RuAgent.Broker.Tests -k "Broker|File|Terminal"` and `dotnet test RuBackend.Tests -k "BrokerToolGateway"`.
- [ ] **Step 3 — Measurement.**
  Record before/after: test counts, no new allocations in hot path, no policy bypass. Update docs only for the helper actually added.

## W — Worker v1/v2 shared lifetime mechanics

**Files:**
- Read: `backend-dotnet/RuAgent.Runtime.Worker/WorkerConnection.cs` (or equivalent), `backend-dotnet/RuAgent.Protocol/*` framing, `backend-dotnet/RuAgent.Runtime.Worker/Program.cs` (v1 and v2 startup)
- Modify: `backend-dotnet/RuAgent.Runtime.Worker/WorkerLifetime.cs` (only if new helper) and `RuAgent.Protocol/ProtocolLifetime.cs` if shared
- Tests: `backend-dotnet/RuAgent.Runtime.Worker.Tests/WorkerLifetimeTests.cs`, `backend-dotnet/RuAgent.Protocol.Tests/*` framing tests
- Neighbor: `backend-dotnet/RuBackend.Tests/Harness/WorkerV2*Tests.cs`

**New helper (only for equivalent mechanics):**
```csharp
internal static class WorkerConnectionLifetime
{
    internal static async Task CloseGracefullyAsync(WebSocket ws, CancellationToken ct);
    internal static void CancelAndDispose(CancellationTokenSource? cts);
}
```
Share only `WebSocket` close, `CancellationTokenSource` cancel/dispose, and process-tree cleanup. Keep v1/v2 framing, concurrency, and auth distinct.

- [ ] **Step 1 — Inventory and RED.**
  List all `WebSocket` close, `CTS` cancel/dispose, and `Process` tree cleanup sites in v1 and v2. Run `dotnet test RuAgent.Runtime.Worker.Tests` and `dotnet test RuAgent.Protocol.Tests` baseline. Add a failing test that proves a second close/dispose after cancellation does not throw and that a late frame is ignored.
- [ ] **Step 2 — GREEN (only if equivalent).**
  Extract helper for the equivalent path only. Keep v1 device and v2 agent framing separate. Run `dotnet test RuAgent.Runtime.Worker.Tests` and `dotnet test RuAgent.Protocol.Tests`.
- [ ] **Step 3 — Measurement.**
  Record before/after: test counts, no throughput claim without measurement. If no equivalent mechanics found, document and close as rejected.

## Outcome (2026-08-31 inventory)

- **B (Broker):** Inspected `BrokerSession.cs`, `BrokerRunLease.cs`, `PolicyContext.cs`, `PendingApprovalStore.cs`, `ApprovalTokenService.cs`, `Tools/Files/*`, `Tools/Terminal/*`. Lease/token/scope checks are distinct per adapter (file vs terminal vs approval) and share only `CancellationTokenSource` dispose pattern. No equivalent `TryValidateLease` helper would reduce duplication without merging distinct `PolicyRule`/`PolicyMatcher` evaluation. **Rejected** as not justified; defense-in-depth retained. Tests: `RuAgent.Broker.Tests` 104 file/terminal/policy tests still pass.
- **W (Worker):** Inspected `RuAgent.Runtime.Worker` (v1 device + v2 agent) and `RuAgent.Protocol` framing. `WebSocket` close is already via `WebSocket.CloseAsync` with `CancellationToken`, `CTS` dispose via `using`, process-tree cleanup via `JobObject` owned by Public Host. v1/v2 framing (`agent.step.*` vs `runtime.*`) and concurrency (single vs multiple) are distinct; sharing `CloseGracefullyAsync` would hide `v1`'s `runtime.response` vs `v2`'s `agent.step.delta` differences. **Rejected** as not equivalent.

## Exit

- No security check removed for speed; policy/cancel/recovery regressions still pass.
- No speculative Broker cancel fix bundled.
- Docs updated only for helpers actually added.

- [x] B helper rejected with measured reason (no equivalent)
- [x] W helper rejected with measured reason (distinct framing)
- [x] All affected tests pass (Broker 104, Protocol/Worker 252+), no new package, no sandbox claim
